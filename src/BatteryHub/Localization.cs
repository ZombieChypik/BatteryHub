namespace BatteryHub;

/// <summary>Локализация интерфейса: «ru» / «en». Язык сохраняется в lang.txt рядом с настройками.</summary>
public static class L10n
{
    public static string Lang { get; private set; } = "ru";

    private static readonly string LangFile = System.IO.Path.Combine(
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryHub"),
        "lang.txt");

    public static void Load()
    {
        try
        {
            if (File.Exists(LangFile))
            {
                Lang = File.ReadAllText(LangFile).Trim() == "en" ? "en" : "ru";
            }
        }
        catch
        {
        }
    }

    public static void Set(string lang)
    {
        Lang = lang == "en" ? "en" : "ru";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LangFile)!);
            File.WriteAllText(LangFile, Lang);
        }
        catch
        {
        }
    }

    private static readonly Dictionary<string, string> Ru = new()
    {
        ["menu.open"] = "Открыть панель",
        ["menu.refresh"] = "Обновить сейчас",
        ["menu.display"] = "Отображение",
        ["menu.widget"] = "Виджет на панели задач (мышке + точка заряда)",
        ["menu.tray"] = "Значок в трее (панель или скрытые)",
        ["menu.placement"] = "Настройка расположения значка Windows…",
        ["menu.color"] = "Цвет",
        ["menu.customColor"] = "Другой цвет…",
        ["menu.lang"] = "Язык / Language",
        ["menu.autostart"] = "Запускать при входе в Windows",
        ["menu.bt"] = "Bluetooth-настройки",
        ["menu.diag"] = "Диагностика (для связи с разработчиком)",
        ["menu.exit"] = "Выход",
        ["balloon.trayhint"] = "Значок в трее включён. Чтобы поставить его на панель задач: правый клик → «Настройка расположения значка Windows…» → тумблер «Battery Hub».",
        ["balloon.low"] = "Низкий заряд: {0} — {1}%",
        ["flyout.refreshing"] = "Запрашиваем свежие данные…",
        ["flyout.updated"] = "Обновлено {0}",
        ["flyout.collecting"] = "Собираем данные…",
        ["conn.bt"] = "Bluetooth",
        ["conn.dongle"] = "2.4G донгл",
        ["conn.wired"] = "USB-кабель · зарядка",
        ["status.charging"] = "⚡ Заряжается",
        ["status.wake"] = "Пошевелите мышь, чтобы обновить заряд",
        ["status.nodata"] = "Нет данных о заряде",
        ["status.empty"] = "Нет беспроводных устройств.\nПошевелите мышь (она шлёт заряд, когда бодрствует)\nили подключите Bluetooth-устройство.",
        ["footer.bt"] = "Bluetooth-настройки",
        ["tooltip.empty"] = "Нет данных — пошевелите мышь или подключите устройство",
        ["eta.minutes"] = "≈ {0} мин",
        ["eta.hoursOnly"] = "≈ {0} ч",
        ["eta.hours"] = "≈ {0} ч {1} мин",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["menu.open"] = "Open panel",
        ["menu.refresh"] = "Refresh now",
        ["menu.display"] = "Display",
        ["menu.widget"] = "Taskbar widget (mouse + charge dot)",
        ["menu.tray"] = "Tray icon (taskbar or hidden)",
        ["menu.placement"] = "Windows icon placement settings…",
        ["menu.color"] = "Color",
        ["menu.customColor"] = "Custom color…",
        ["menu.lang"] = "Язык / Language",
        ["menu.autostart"] = "Start with Windows",
        ["menu.bt"] = "Bluetooth settings",
        ["menu.diag"] = "Diagnostics (to report to developer)",
        ["menu.exit"] = "Exit",
        ["balloon.trayhint"] = "Tray icon enabled. To pin it to the taskbar: right-click → «Windows icon placement settings…» → toggle «Battery Hub».",
        ["balloon.low"] = "Low battery: {0} — {1}%",
        ["flyout.refreshing"] = "Fetching fresh data…",
        ["flyout.updated"] = "Updated {0}",
        ["flyout.collecting"] = "Collecting data…",
        ["conn.bt"] = "Bluetooth",
        ["conn.dongle"] = "2.4G dongle",
        ["conn.wired"] = "USB cable · charging",
        ["status.charging"] = "⚡ Charging",
        ["status.wake"] = "Move the mouse to update the battery",
        ["status.nodata"] = "No battery data",
        ["status.empty"] = "No wireless devices.\nWake the mouse (it reports battery when awake)\nor connect a Bluetooth device.",
        ["footer.bt"] = "Bluetooth settings",
        ["tooltip.empty"] = "No data — wake the mouse or connect a device",
        ["eta.minutes"] = "≈ {0}m",
        ["eta.hoursOnly"] = "≈ {0}h",
        ["eta.hours"] = "≈ {0}h {1}m",
    };

    public static string T(string key)
    {
        var dict = Lang == "en" ? En : Ru;
        return dict.TryGetValue(key, out var value) ? value : Ru.TryGetValue(key, out var f) ? f : key;
    }

    public static string F(string key, params object[] args)
    {
        return string.Format(T(key), args);
    }

    /// <summary>Локализованное имя цвета из палитры AppSettings.Palette.</summary>
    public static string ColorName(string name)
    {
        return name switch
        {
            "White" => Lang == "en" ? "White" : "Белый",
            "Black" => Lang == "en" ? "Black" : "Чёрный",
            "Red" => Lang == "en" ? "Red" : "Красный",
            "Orange" => Lang == "en" ? "Orange" : "Оранжевый",
            "Yellow" => Lang == "en" ? "Yellow" : "Жёлтый",
            "Green" => Lang == "en" ? "Green" : "Зелёный",
            "Cyan" => Lang == "en" ? "Cyan" : "Голубой",
            "Blue" => Lang == "en" ? "Blue" : "Синий",
            "Purple" => Lang == "en" ? "Purple" : "Фиолетовый",
            "Pink" => Lang == "en" ? "Pink" : "Розовый",
            _ => name,
        };
    }
}
