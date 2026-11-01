using Microsoft.Win32;

namespace CrashLens.Core;

public static class StartupManager
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key is null) throw new InvalidOperationException("Не найден раздел реестра автозагрузки Windows.");
        if (enabled)
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Путь к приложению неизвестен.");
            // The project is intended to be published as a single executable.
            key.SetValue("CrashLens", $"\"{exe}\" --minimized", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("CrashLens", throwOnMissingValue: false);
        }
    }

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue("CrashLens") is string value && value.Length > 0;
    }
}
