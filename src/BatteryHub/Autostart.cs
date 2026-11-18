using Microsoft.Win32;

namespace BatteryHub;

/// <summary>Автозапуск при входе в Windows через ключ HKCU\...\Run.</summary>
public static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BatteryHub";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                return;
            }
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    key.SetValue(ValueName, $"\"{exe}\"");
                }
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // нет прав на HKCU Run — крайне маловероятно
        }
    }

    /// <summary>Включает автозапуск, если он ещё не настроен (пользователь согласился при первом запуске).</summary>
    public static void EnsureEnabled()
    {
        if (!IsEnabled())
        {
            Set(true);
        }
    }
}
