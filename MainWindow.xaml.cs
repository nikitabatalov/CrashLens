using System.IO;
using CrashLens.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CrashLens;

public partial class MainWindow : Window
{
    private readonly LocalStore _store;
    private readonly EventCaptureService _capture;
    private readonly DispatcherTimer _refreshTimer;
    private CrashEvent? _selected;
    private bool _ready;
    private bool _reallyClose;
    private int _refreshQueued;
    public ObservableCollection<CrashEvent> VisibleEvents { get; } = new();

    public MainWindow(LocalStore store, EventCaptureService capture)
    {
        _store = store;
        _capture = capture;
        InitializeComponent();
        DataContext = this;
        AutoStartCheck.IsChecked = _store.Settings.AutoStart;
        NotificationsCheck.IsChecked = _store.Settings.ShowCriticalNotifications;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            Interlocked.Exchange(ref _refreshQueued, 0);
            RefreshView();
        };
        _store.EventAdded += OnEventAdded;
        _capture.StatusChanged += OnStatusChanged;
        _ready = true;
        RefreshView();
    }

    private void OnEventAdded(CrashEvent _)
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_reallyClose)
            {
                _refreshTimer.Stop();
                _refreshTimer.Start();
            }
        }, DispatcherPriority.Background);
    }

    private void OnStatusChanged(string text)
    {
        Dispatcher.BeginInvoke(() =>
        {
            MonitorStatusText.Text = text;
            MonitorDot.Fill = new System.Windows.Media.SolidColorBrush(
                text.Contains("Нет доступа", StringComparison.OrdinalIgnoreCase)
                    ? System.Windows.Media.Color.FromRgb(255, 175, 118)
                    : System.Windows.Media.Color.FromRgb(107, 224, 187));
        });
    }

    private void RefreshView()
    {
        if (!_ready || _reallyClose) return;
        List<CrashEvent> all = _store.Snapshot();
        TotalCountText.Text = all.Count.ToString("N0");
        CriticalCountText.Text = all.Count(x => x.Severity == "Critical").ToString("N0");
        BsodCountText.Text = all.Count(x => x.Category == "BSOD").ToString("N0");
        WeekCountText.Text = all.Count(x => x.TimeUtc >= DateTimeOffset.UtcNow.AddDays(-7)).ToString("N0");

        string period = (PeriodCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        DateTimeOffset? since = period switch
        {
            "day" => new DateTimeOffset(DateTime.Today).ToUniversalTime(),
            "week" => DateTimeOffset.UtcNow.AddDays(-7),
            "month" => DateTimeOffset.UtcNow.AddDays(-30),
            "year" => DateTimeOffset.UtcNow.AddYears(-1),
            _ => null
        };
        string severity = (SeverityCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        string search = SearchBox.Text?.Trim() ?? "";
        IEnumerable<CrashEvent> query = all;
        if (since.HasValue) query = query.Where(e => e.TimeUtc >= since.Value);
        if (severity != "all") query = query.Where(e => e.Severity == severity);
        if (search.Length > 0)
        {
            query = query.Where(e => e.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Code.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Provider.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.EventId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        // UI cap only; the saved local history is not truncated.
        List<CrashEvent> latest = query.OrderByDescending(x => x.TimeUtc).Take(2000).ToList();
        string? selectedKey = _selected?.Key;
        VisibleEvents.Clear();
        foreach (CrashEvent entry in latest) VisibleEvents.Add(entry);
        ListCountText.Text = latest.Count == 2000 ? "2000 последних записей" : $"{latest.Count} записей";

        CrashEvent? match = latest.FirstOrDefault(e => e.Key == selectedKey) ?? latest.FirstOrDefault();
        if (match is not null) EventsGrid.SelectedItem = match;
        else { _selected = null; ShowDetails(null); }
    }

    private void ShowDetails(CrashEvent? entry)
    {
        bool selected = entry != null;
        DetailCategoryText.Text = entry?.Category.ToUpperInvariant() ?? "ВЫБЕРИТЕ СОБЫТИЕ";
        DetailCodeText.Text = entry?.CodeLabel ?? "—";
        DetailTitleText.Text = entry?.Title ?? "Здесь появятся подробности";
        DetailExplanationText.Text = entry?.Explanation ?? "Выберите запись в журнале слева, чтобы увидеть код, объяснение и источник.";
        DetailSourceText.Text = selected ? $"{entry!.Provider}\nКанал: {entry.Channel} · Event ID {entry.EventId}" : "—";
        DetailTimestampText.Text = selected ? $"{entry!.TimeLabel}\nRecord ID: {entry.RecordId}" : "—";
        DetailMessageText.Text = entry?.Message ?? "—";
        XmlTextBox.Text = entry?.RawXml ?? "";
        GoogleButton.IsEnabled = selected;
        CopyButton.IsEnabled = selected;
        DumpButton.IsEnabled = selected && !string.IsNullOrWhiteSpace(entry!.DumpPath) && File.Exists(entry.DumpPath);
    }

    private void EventsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _selected = EventsGrid.SelectedItem as CrashEvent;
        ShowDetails(_selected);
    }

    private void Filter_Changed(object sender, EventArgs e)
    {
        if (_ready) RefreshView();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        MonitorStatusText.Text = "Сканирование истории за 30 дней...";
        await _capture.BackfillAsync(true);
        RefreshView();
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Экспорт событий CrashLens",
            Filter = "CSV файл (*.csv)|*.csv",
            FileName = $"CrashLens-{DateTime.Now:yyyy-MM-dd}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            writer.WriteLine("Дата;Уровень;Категория;Источник;Журнал;Event ID;Код;Название;Объяснение;Сообщение");
            foreach (CrashEvent item in _store.Snapshot().OrderByDescending(x => x.TimeUtc))
            {
                writer.WriteLine(string.Join(";", new[]
                {
                    Csv(item.TimeLabel), Csv(item.SeverityLabel), Csv(item.Category), Csv(item.Provider), Csv(item.Channel),
                    Csv(item.EventId.ToString()), Csv(item.Code), Csv(item.Title), Csv(item.Explanation), Csv(item.Message)
                }));
            }
            System.Windows.MessageBox.Show(this, "Журнал успешно экспортирован в CSV.", "CrashLens", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError("Не удалось экспортировать журнал", ex); }
    }

    private static string Csv(string s)
    {
        // Prevent spreadsheet formula injection when opened in Excel.
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }

    private static void OpenPath(string path) => Process.Start(new ProcessStartInfo
    {
        FileName = path,
        UseShellExecute = true
    });

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { OpenPath(_store.DataDirectory); }
        catch (Exception ex) { ShowError("Не удалось открыть папку", ex); }
    }

    private void OpenEventViewer_Click(object sender, RoutedEventArgs e)
    {
        try { OpenPath("eventvwr.msc"); }
        catch (Exception ex) { ShowError("Не удалось открыть Event Viewer", ex); }
    }

    private void OpenDumpFolder_Click(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
        try
        {
            if (Directory.Exists(path)) OpenPath(path);
            else System.Windows.MessageBox.Show(this, "Папка Minidump не найдена. Windows могла не создавать минидампы либо они были очищены.",
                "CrashLens", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError("Не удалось открыть дампы", ex); }
    }

    private void GoogleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        string query = _selected.Category == "BSOD" && !string.IsNullOrEmpty(_selected.Code)
            ? $"Windows BSOD {_selected.Code} {_selected.Title}"
            : $"Windows event {_selected.EventId} {_selected.Provider} {_selected.Code} {_selected.Title}";
        try { OpenPath("https://www.google.com/search?q=" + Uri.EscapeDataString(query)); }
        catch (Exception ex) { ShowError("Не удалось открыть браузер", ex); }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            System.Windows.Clipboard.SetText($"CrashLens by Nikita Batalov\n{_selected.TimeLabel}\n"
                + $"{_selected.Title} ({_selected.CodeLabel})\n{_selected.Category}\n"
                + $"{_selected.Provider} · Event ID {_selected.EventId}\n\n{_selected.Explanation}\n\n{_selected.Message}");
        }
        catch (Exception ex) { ShowError("Не удалось скопировать данные", ex); }
    }

    private void DumpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || !File.Exists(_selected.DumpPath)) return;
        try { Process.Start("explorer.exe", $"/select,\"{_selected.DumpPath}\""); }
        catch (Exception ex) { ShowError("Не удалось открыть путь к дампу", ex); }
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        try
        {
            bool enabled = AutoStartCheck.IsChecked == true;
            StartupManager.SetEnabled(enabled);
            _store.Settings.AutoStart = enabled;
            _store.SaveSettings();
        }
        catch (Exception ex)
        {
            AutoStartCheck.IsChecked = StartupManager.IsEnabled();
            ShowError("Не удалось изменить автозагрузку", ex);
        }
    }

    private void Notifications_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _store.Settings.ShowCriticalNotifications = NotificationsCheck.IsChecked == true;
        try { _store.SaveSettings(); }
        catch (Exception ex) { ShowError("Не удалось сохранить настройки", ex); }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyClose) return;
        e.Cancel = true;
        Hide();
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void ExitCompletely()
    {
        _reallyClose = true;
        _store.EventAdded -= OnEventAdded;
        _capture.StatusChanged -= OnStatusChanged;
        _refreshTimer.Stop();
        Close();
    }

    private void ShowError(string title, Exception error)
    {
        System.Windows.MessageBox.Show(this, error.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
