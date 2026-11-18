namespace BatteryHub.Core;

public enum DeviceKind
{
    Mouse,
    Keyboard,
    Headset,
    Gamepad,
    Other,
}

public enum DeviceSource
{
    Bluetooth,
    X11Dongle,
    X11Wired,
}

/// <summary>Снимок состояния устройства для UI.</summary>
public sealed class DeviceStatus
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DeviceKind Kind { get; init; }
    public DeviceSource Source { get; init; }
    public int? BatteryPct { get; init; }
    public bool IsCharging { get; init; }
    public bool NeedsWake { get; init; }
    public DateTime? LastUpdateUtc { get; init; }
    public double? EtaHours { get; init; }
}

/// <summary>
/// Потокобезопасный реестр устройств: источники публикуют состояние, UI подписывается на Changed.
/// </summary>
public sealed class DeviceRegistry
{
    private sealed class Entry
    {
        public string Name = "";
        public DeviceKind Kind = DeviceKind.Other;
        public DeviceSource Source = DeviceSource.Bluetooth;
        public int? BatteryPct;
        public bool IsCharging;
        public bool NeedsWake;
        public DateTime? LastUpdateUtc;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly StateStore _store = new();

    /// <summary>Событие может прилетать из любого потока — подписчик обязан маршалировать сам.</summary>
    public event Action? Changed;

    /// <summary>
    /// Классификация типа устройства по имени: сначала явные слова-типы,
    /// затем характерные бренды/модели (список по Wireless-Device-Battery-Monitor).
    /// </summary>
    public static DeviceKind ClassifyKind(string name)
    {
        var n = name.ToLowerInvariant();

        // 1. Явные слова-типы
        if (ContainsAny(n, "mouse", "мышь", "мышк", "m720", "g502", "g304", "g pro"))
        {
            return DeviceKind.Mouse;
        }
        if (ContainsAny(n, "keyboard", "клавиатур"))
        {
            return DeviceKind.Keyboard;
        }
        if (ContainsAny(n, "headphone", "headset", "earbuds", "earphone", "airpods", "buds",
            "beats", "наушник", "гарнитур", "cloud mix", "arctis", "nova pro", "inzone",
            "astro a50", "void", "quantum", "moondrop", "link 380", "link 390"))
        {
            return DeviceKind.Headset;
        }
        if (ContainsAny(n, "gamepad", "controller", "xbox", "dualsense", "dualshock",
            "gamesir", "8bitdo", "flydigi", "геймпад", "джойстик", "stadia", "switch pro"))
        {
            return DeviceKind.Gamepad;
        }

        // 2. Бренды-мыши (специализируются на мышах)
        if (ContainsAny(n, "attack shark", "attackshark", "pulsar", "atk", "vxe", "lamzu",
            "g-wolves", "gwolves", "wlmouse", "vaxee", "zygen", "finalmouse", "ninjutso",
            "incott", "kysona", "sprime", "wraith", "razer", "zowie", "glorious", "logitech g",
            "g305", "g102", "superglide"))
        {
            return DeviceKind.Mouse;
        }

        // 3. Бренды-клавиатуры
        if (ContainsAny(n, "keychron", "royal kludge", "rk ", "machenike", "mchose"))
        {
            return DeviceKind.Keyboard;
        }

        return DeviceKind.Other;
    }

    private static bool ContainsAny(string source, params string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (source.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public void Update(
        string id,
        DeviceSource source,
        string name,
        DeviceKind kind,
        int? batteryPct = null,
        bool? charging = null,
        bool? needsWake = null)
    {
        bool changed;
        lock (_lock)
        {
            var existed = _entries.TryGetValue(id, out var entry);
            if (!existed)
            {
                entry = new Entry { Name = name };
                _entries[id] = entry;
            }

            var before = (entry.Source, entry.Name, entry.Kind, entry.BatteryPct, entry.IsCharging, entry.NeedsWake);

            entry.Source = source;
            if (!string.IsNullOrWhiteSpace(name))
            {
                entry.Name = name;
            }
            entry.Kind = kind;

            if (batteryPct.HasValue && batteryPct.Value is >= 0 and <= 100)
            {
                entry.BatteryPct = batteryPct;
                entry.NeedsWake = false;
                _store.AddSample(id, new Sample(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), batteryPct.Value));
            }
            if (charging.HasValue)
            {
                entry.IsCharging = charging.Value;
            }
            if (needsWake.HasValue)
            {
                entry.NeedsWake = needsWake.Value;
            }

            var after = (entry.Source, entry.Name, entry.Kind, entry.BatteryPct, entry.IsCharging, entry.NeedsWake);
            changed = !existed || before != after;
            if (changed)
            {
                entry.LastUpdateUtc = DateTime.UtcNow;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    public void Remove(string id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _entries.Remove(id);
        }
        if (removed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Убирает все Bluetooth-устройства, кроме перечисленных (сверка после полного ре-поллинга).</summary>
    public void PruneBluetoothExcept(HashSet<string> keepIds)
    {
        List<string> toRemove;
        lock (_lock)
        {
            toRemove = _entries
                .Where(kv => kv.Value.Source == DeviceSource.Bluetooth && !keepIds.Contains(kv.Key))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var id in toRemove)
            {
                _entries.Remove(id);
            }
        }
        if (toRemove.Count > 0)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Последний известный замер устройства (по истории).</summary>
    public (int Pct, DateTime Utc)? GetLastKnownSample(string deviceId)
    {
        lock (_lock)
        {
            return _store.GetLastKnownSample(deviceId);
        }
    }

    /// <summary>Отсортированный снимок: сначала заряжающиеся, затем по заряду (больше — выше), без данных — в конце.</summary>
    public IReadOnlyList<DeviceStatus> GetSnapshot()
    {
        List<DeviceStatus> snapshot;
        lock (_lock)
        {
            snapshot = _entries.Select(kv =>
            {
                var entry = kv.Value;
                var samples = _store.GetSamples(kv.Key);
                var eta = entry.IsCharging
                    ? null
                    : BatteryEstimator.EstimateHours(samples, entry.BatteryPct ?? 0, false);
                return new DeviceStatus
                {
                    Id = kv.Key,
                    Name = entry.Name,
                    Kind = entry.Kind,
                    Source = entry.Source,
                    BatteryPct = entry.BatteryPct,
                    IsCharging = entry.IsCharging,
                    NeedsWake = entry.NeedsWake,
                    LastUpdateUtc = entry.LastUpdateUtc,
                    EtaHours = eta,
                };
            }).ToList();
        }

        return snapshot
            .OrderByDescending(d => d.IsCharging)
            .ThenByDescending(d => d.BatteryPct.HasValue ? d.BatteryPct.Value : -1)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Flush() => _store.Flush(force: true);
}
