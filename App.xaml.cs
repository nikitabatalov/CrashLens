using System.IO;
using CrashLens.Core;
using Forms = System.Windows.Forms;
using System.Windows;
using System.Drawing;

namespace CrashLens;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _registeredSignal;
    private LocalStore? _store;
    private EventCaptureService? _capture;
    private MainWindow? _window;
    private Forms.NotifyIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _singleInstance = new Mutex(initiallyOwned: true, name: @"Local\CrashLens-NikitaBatalov", createdNew: out bool first);
        if (!first)
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(@"Local\CrashLensShow-NikitaBatalov");
                existing.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { /* Initial instance is still launching. */ }
            Shutdown();
            return;
        }
        try
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CrashLensShow-NikitaBatalov");
            _registeredSignal = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, timedOut) =>
            {
                if (!timedOut) Dispatcher.BeginInvoke(ShowWindow);
            }, null, -1, executeOnlyOnce: false);
            _store = new LocalStore();
            if (_store.Settings.AutoStart)
            {
                try { StartupManager.SetEnabled(true); }
                catch (Exception) { /* User may set it later in Settings. */ }
            }
            _capture = new EventCaptureService(_store);
            _window = new MainWindow(_store, _capture);
            MainWindow = _window;
            SetupTray();

            bool minimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            if (!minimized) _window.Show();
            _capture.Start();
            _store.EventAdded += NotifyAboutNewIncident;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Не удалось запустить CrashLens:\n{ex.Message}", "CrashLens",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть CrashLens", null, (_, _) => Dispatcher.BeginInvoke(ShowWindow));
        menu.Items.Add("Обновить журнал", null, (_, _) =>
        {
            if (_capture != null) _ = _capture.BackfillAsync(true);
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.BeginInvoke(ExitApplication));

        _tray = new Forms.NotifyIcon
        {
            Text = "CrashLens — монитор ошибок Windows",
            ContextMenuStrip = menu,
            Visible = false
        };
        try
        {
            var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/CrashLens.ico"));
            if (stream != null)
            {
                using Stream iconStream = stream.Stream;
                using var icon = new Icon(iconStream);
                _tray.Icon = (Icon)icon.Clone();
            }
            else _tray.Icon = SystemIcons.Shield;
        }
        catch (Exception) { _tray.Icon = SystemIcons.Shield; }
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowWindow);
        _tray.Visible = true;
    }

    private void NotifyAboutNewIncident(CrashEvent item)
    {
        if (item.Severity != "Critical" || _store?.Settings.ShowCriticalNotifications != true) return;
        if (DateTimeOffset.UtcNow - item.TimeUtc > TimeSpan.FromMinutes(2)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_tray == null) return;
            _tray.BalloonTipTitle = item.Category == "BSOD" ? "CrashLens · Синий экран" : "CrashLens · Критическое событие";
            _tray.BalloonTipText = $"{item.CodeLabel}: {item.Title}";
            _tray.BalloonTipIcon = Forms.ToolTipIcon.Warning;
            _tray.ShowBalloonTip(3500);
        });
    }

    private void ShowWindow() => _window?.ShowFromTray();

    private void ExitApplication()
    {
        _window?.ExitCompletely();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_store != null) _store.EventAdded -= NotifyAboutNewIncident;
            _capture?.Dispose();
            _store?.Dispose();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Icon?.Dispose();
                _tray.ContextMenuStrip?.Dispose();
                _tray.Dispose();
            }
        }
        finally
        {
            try { _singleInstance?.ReleaseMutex(); }
            catch (ApplicationException) { /* Not the owner in a second instance. */ }
            _registeredSignal?.Unregister(null);
            _showSignal?.Dispose();
            _singleInstance?.Dispose();
            base.OnExit(e);
        }
    }
}
