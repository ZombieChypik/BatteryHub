using Microsoft.Win32;

namespace BatteryHub;

/// <summary>
/// Закрепление значка в трее на панели задач (возле индикатора языка РУС/ENG).
/// Windows 11 хранит размещение значков в HKCU\Control Panel\NotifyIconSettings\&lt;id&gt;,
/// где &lt;id&gt; — хеш пути exe, а значение IsPromoted: 1 — на панели задач, 0 — в скрытых.
/// </summary>
public static class TrayPromotion
{
    private const string Root = @"Control Panel\NotifyIconSettings";

    /// <summary>
    /// Ищет ключ нашего exe и ставит IsPromoted=1 (значок на панели задач).
    /// Возвращает true, если ключ найден и значок закреплён (или уже был).
    /// Ключ появляется после первого показа значка в трее — возможны повторные попытки.
    /// </summary>
    public static bool TryPromote()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return true; // нечего искать
        }
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Root, writable: true);
            if (root == null)
            {
                return false;
            }
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name, writable: true);
                if (key == null)
                {
                    continue;
                }
                if (!string.Equals(key.GetValue("ExecutablePath") as string, exe, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (key.GetValue("IsPromoted") is int i && i == 1)
                {
                    return true;
                }
                key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                return true;
            }
        }
        catch
        {
            // нет доступа — значок закрепится вручную через настройки Windows
        }
        return false;
    }

    /// <summary>Промотирует с повторными попытками: ключ реестра появляется с задержкой после показа значка.</summary>
    public static void PromoteWithRetry()
    {
        Task.Run(async () =>
        {
            for (var i = 0; i < 8; i++)
            {
                if (TryPromote())
                {
                    return;
                }
                await Task.Delay(800);
            }
        });
    }
}
