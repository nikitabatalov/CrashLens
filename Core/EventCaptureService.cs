using System.Diagnostics.Eventing.Reader;

namespace CrashLens.Core;

public sealed class EventCaptureService : IDisposable
{
    private readonly LocalStore _store;
    private readonly List<EventLogWatcher> _watchers = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly CancellationTokenSource _cancel = new();
    private bool _disposed;
    public event Action<string>? StatusChanged;

    // Native Windows event subscriptions; no process polling or full-log polling loop.
    private const string SystemQuery = "*[System[(Level=1 or Level=2 or Level=3 or EventID=41 or EventID=6008 or EventID=1001)]]";
    private const string ApplicationQuery = "*[System[(Level=1 or Level=2 or EventID=1000 or EventID=1001 or EventID=1002 or EventID=1026)]]";

    public EventCaptureService(LocalStore store) => _store = store;

    public void Start()
    {
        foreach ((string channel, string query) in new[] { ("System", SystemQuery), ("Application", ApplicationQuery) })
        {
            try
            {
                var watcher = new EventLogWatcher(new EventLogQuery(channel, PathType.LogName, query));
                watcher.EventRecordWritten += (_, e) =>
                {
                    using EventRecord? record = e.EventRecord;
                    if (e.EventException != null)
                    {
                        StatusChanged?.Invoke($"Поток {channel}: {e.EventException.Message}");
                        return;
                    }
                    if (record == null) return;
                    CrashEvent? item = CrashDecoder.FromRecord(record, channel);
                    if (item != null) _store.Add(item);
                };
                watcher.Enabled = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Нет доступа к журналу {channel}: {ex.Message}");
            }
        }
        StatusChanged?.Invoke(_watchers.Count == 2
            ? "Мониторинг активен · System + Application"
            : $"Подключено журналов: {_watchers.Count}/2. Проверьте права доступа.");
        _ = BackfillAsync(false);
    }

    public async Task BackfillAsync(bool manual)
    {
        if (_disposed) return;
        try
        {
            await _scanLock.WaitAsync(_cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        try
        {
            // Re-read overlap prevents losing events written between the last scan and a restart.
            DateTimeOffset cutoff = manual
                ? DateTimeOffset.UtcNow.AddDays(-30)
                : _store.Settings.LastScanUtc is DateTimeOffset previous
                    ? previous.AddMinutes(-5) : DateTimeOffset.UtcNow.AddDays(-30);
            int imported = await Task.Run(() =>
            {
                int added = 0;
                foreach ((string channel, string query) in new[] { ("System", SystemQuery), ("Application", ApplicationQuery) })
                {
                    try
                    {
                        var logQuery = new EventLogQuery(channel, PathType.LogName, query) { ReverseDirection = true };
                        using var reader = new EventLogReader(logQuery);
                        // Bounded per launch to avoid heavy history imports on very large logs.
                        for (int i = 0; i < 4000 && !_cancel.IsCancellationRequested; i++)
                        {
                            using EventRecord? record = reader.ReadEvent();
                            if (record == null) break;
                            if (record.TimeCreated.HasValue && record.TimeCreated.Value.ToUniversalTime() < cutoff.UtcDateTime)
                                break;
                            CrashEvent? item = CrashDecoder.FromRecord(record, channel);
                            if (item != null && _store.Add(item)) added++;
                        }
                    }
                    catch (Exception ex)
                    {
                        StatusChanged?.Invoke($"История {channel}: {ex.Message}");
                    }
                }
                return added;
            }, _cancel.Token).ConfigureAwait(false);
            if (!_cancel.IsCancellationRequested)
            {
                _store.Settings.LastScanUtc = DateTimeOffset.UtcNow;
                _store.SaveSettings();
                _store.Flush();
                StatusChanged?.Invoke($"Мониторинг активен · восстановлено событий: {imported}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusChanged?.Invoke($"Не удалось обновить историю: {ex.Message}"); }
        finally { _scanLock.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancel.Cancel();
        foreach (var watcher in _watchers)
        {
            watcher.Enabled = false;
            watcher.Dispose();
        }
        _watchers.Clear();
        // Do not dispose scan semaphore yet: an in-progress scan may still release it.
    }
}
