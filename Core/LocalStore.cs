using System.IO;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace CrashLens.Core;

public sealed class LocalStore : IDisposable
{
    private readonly object _gate = new();
    private readonly object _diskGate = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly List<CrashEvent> _events = new();
    private readonly ConcurrentQueue<CrashEvent> _pending = new();
    private readonly System.Threading.Timer _flushTimer;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private bool _disposed;

    public string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashLens");
    public string EventsFile => Path.Combine(DataDirectory, "events.jsonl");
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public AppSettings Settings { get; private set; }
    public event Action<CrashEvent>? EventAdded;

    public LocalStore()
    {
        Directory.CreateDirectory(DataDirectory);
        Settings = LoadSettings();
        if (File.Exists(EventsFile))
        {
            foreach (string line in File.ReadLines(EventsFile))
            {
                try
                {
                    CrashEvent? entry = JsonSerializer.Deserialize<CrashEvent>(line);
                    if (entry != null && !string.IsNullOrEmpty(entry.Key) && _keys.Add(entry.Key))
                        _events.Add(entry);
                }
                catch (JsonException) { /* Ignore partial/corrupted last lines after power loss. */ }
            }
        }
        _flushTimer = new System.Threading.Timer(_ => Flush(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new AppSettings();
        }
        catch (Exception) { /* Fall back to safe defaults. */ }
        return new AppSettings();
    }

    public void SaveSettings()
    {
        lock (_diskGate)
        {
            string temporary = SettingsFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Settings, _jsonOptions), new UTF8Encoding(false));
            File.Move(temporary, SettingsFile, overwrite: true);
        }
    }

    public bool Add(CrashEvent entry)
    {
        lock (_gate)
        {
            if (!_keys.Add(entry.Key)) return false;
            _events.Add(entry);
        }
        _pending.Enqueue(entry);
        EventAdded?.Invoke(entry);
        return true;
    }

    public List<CrashEvent> Snapshot()
    {
        lock (_gate) return _events.ToList();
    }

    public void Flush()
    {
        lock (_diskGate)
        {
            var batch = new List<CrashEvent>();
            while (_pending.TryDequeue(out CrashEvent? e)) batch.Add(e);
            if (batch.Count == 0) return;
            try
            {
                using var stream = new FileStream(EventsFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                foreach (CrashEvent entry in batch)
                    writer.WriteLine(JsonSerializer.Serialize(entry));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            catch (Exception)
            {
                foreach (CrashEvent entry in batch) _pending.Enqueue(entry);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer.Dispose();
        Flush();
    }
}
