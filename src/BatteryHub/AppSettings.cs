using System.Text.Json;

namespace BatteryHub;

public enum DisplayMode
{
    /// <summary>Виджет на панели задач: мышка с точкой-маркером заряда рядом с часами.</summary>
    TaskbarWidget,
    /// <summary>Классический трей-значок (виден на панели или в скрытых — выбор в настройках Windows).</summary>
    TrayIcon,
}

/// <summary>Настройки: %LocalAppData%\BatteryHub\settings.json.</summary>
public static class AppSettings
{
    private static readonly object Lock = new();
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryHub");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static DisplayMode Mode { get; private set; } = DisplayMode.TaskbarWidget;

    /// <summary>Язык интерфейса: «ru» / «en».</summary>
    public static string Lang { get; private set; } = "ru";

    /// <summary>Был ли найден файл настроек при загрузке (для приоритета над lang.txt).</summary>
    public static bool HasSettingsFile { get; private set; }

    /// <summary>Глобальный цвет хаба: модели в панели, значок, меню.</summary>
    public static string GlobalColor { get; private set; } = "White";

    /// <summary>Палитра кастомизации (меню «Цвет»).</summary>
    public static readonly string[] Palette =
    {
        "White", "Black", "Red", "Orange", "Yellow", "Green", "Cyan", "Blue", "Purple", "Pink",
    };

    public static void SetLanguage(string lang)
    {
        lock (Lock)
        {
            Lang = lang == "en" ? "en" : "ru";
            L10n.Set(Lang);
            Save();
        }
    }

    public static Color GetColor() => FromName(GlobalColor);

    /// <summary>Имя глобального цвета (для сравнения и хранения).</summary>
    public static string GetColorName() => GlobalColor;

    /// <summary>Сменить глобальный цвет всего хаба.</summary>
    public static void SetGlobalColor(string colorName)
    {
        lock (Lock)
        {
            GlobalColor = colorName;
            Save();
        }
    }

    /// <summary>Произвольный цвет из палитры Windows: хранится как «#RRGGBB».</summary>
    public static void SetCustomColor(Color color)
    {
        SetGlobalColor($"#{color.R:X2}{color.G:X2}{color.B:X2}");
    }

    /// <summary>Текущий цвет — произвольный (не из палитры)?</summary>
    public static bool IsCustomColor => GlobalColor.StartsWith('#');

    public static Color FromName(string name)
    {
        return name switch
        {
            "Black" => Color.FromArgb(46, 48, 52),
            "Red" => Color.FromArgb(206, 84, 78),
            "Orange" => Color.FromArgb(230, 145, 60),
            "Yellow" => Color.FromArgb(226, 190, 70),
            "Green" => Color.FromArgb(96, 190, 122),
            "Cyan" => Color.FromArgb(80, 196, 210),
            "Blue" => Color.FromArgb(90, 140, 230),
            "Purple" => Color.FromArgb(150, 110, 220),
            "Pink" => Color.FromArgb(226, 120, 160),
            _ => TryParseHex(name) ?? Color.FromArgb(236, 239, 242), // White / «#RRGGBB»
        };
    }

    private static Color? TryParseHex(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] != '#' ||
            (name.Length != 7 && name.Length != 9))
        {
            return null;
        }
        try
        {
            return ColorTranslator.FromHtml(name);
        }
        catch
        {
            return null;
        }
    }

    public static void SetMode(DisplayMode mode)
    {
        lock (Lock)
        {
            Mode = mode;
        }
        Save();
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }
            var dto = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(FilePath));
            if (dto == null)
            {
                return;
            }
            lock (Lock)
            {
                HasSettingsFile = true;
                Mode = dto.Mode == "TrayIcon" ? DisplayMode.TrayIcon : DisplayMode.TaskbarWidget;
                Lang = dto.Lang == "en" ? "en" : "ru";
                GlobalColor = string.IsNullOrEmpty(dto.GlobalColor) ? "White" : dto.GlobalColor;
            }
        }
        catch
        {
            // повреждённый файл — значения по умолчанию
        }
    }

    public static void Save()
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Dir);
                var dto = new SettingsDto(
                    Mode == DisplayMode.TrayIcon ? "TrayIcon" : "Widget",
                    Lang,
                    GlobalColor);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(dto));
            }
        }
        catch
        {
        }
    }

    private sealed record SettingsDto(string Mode, string Lang, string GlobalColor);
}
