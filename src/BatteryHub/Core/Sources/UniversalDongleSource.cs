using HidSharp;

namespace BatteryHub.Core.Sources;

/// <summary>
/// Универсальный источник 2.4G-донглов: сам ищет приёмники любых производителей.
/// Для известных (Attack Shark X11) распознаёт заряд по сигнатуре отчёта;
/// для остальных показывает подключение без данных — их формат заряда можно
/// добавить по логу `--probe` (сигнатуры в одном месте, пополняются легко).
/// Bluetooth-устройства здесь не участвуют: их заряд Windows отдаёт сама.
/// </summary>
public sealed class UniversalDongleSource : IDisposable
{
    public const int X11VendorId = 0x1D57;
    public const int X11ProductIdDongle = 0xFA60;
    public const int X11ProductIdWired = 0xFA55;
    public const string X11DeviceId = "x11-dongle";
    public const string X11DefaultName = "Attack Shark X11";

    private static readonly byte[] X11BatterySignature = { 0x03, 0x55, 0x40, 0x01 };

    /// <summary>Сколько держать интерфейсы донглов открытыми (одна сессия чтения).</summary>
    private static readonly TimeSpan StreamSession = TimeSpan.FromSeconds(60);

    /// <summary>Пауза между сессиями чтения / пересканирования.</summary>
    private static readonly TimeSpan RescanDelay = TimeSpan.FromSeconds(10);

    /// <summary>Ключевые слова в имени: так выглядят приёмники wireless-устройств.</summary>
    private static readonly string[] ReceiverHints =
    {
        "wireless", "2.4g", "receiver", "dongle", "radio", "приёмник", "радио",
    };

    private readonly DeviceRegistry _registry;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private volatile bool _pokeRequested;

    // X11: бодрствование и последний известный заряд
    private DateTime _lastReportUtc = DateTime.MinValue;
    private int? _lastKnownPct;
    private DateTime _lastWakePublishUtc = DateTime.MinValue;

    // ранее показанные универсальные донглы (для снятия, когда донгл вытащили)
    private readonly HashSet<string> _activeGeneric = new();

    public UniversalDongleSource(DeviceRegistry registry)
    {
        _registry = registry;
    }

    public void Start()
    {
        if (_thread != null)
        {
            return;
        }
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => RunLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "Dongle reader",
        };
        _thread.Start();

        // сразу публикуем последний известный заряд X11 из истории —
        // виджет показывает его до первого отчёта бодрствующей мыши
        try
        {
            var last = _registry.GetLastKnownSample(X11DeviceId);
            if (last.HasValue)
            {
                _lastKnownPct = last.Value.Pct;
                _lastReportUtc = last.Value.Utc;
                _registry.Update(X11DeviceId, DeviceSource.X11Dongle, X11DefaultName, DeviceKind.Mouse,
                    batteryPct: last.Value.Pct, charging: false, needsWake: IsAsleep());
            }
        }
        catch
        {
        }
    }

    /// <summary>Просит поток немедленно пере-перечислить донглы и ждать отчёты.</summary>
    public void Poke()
    {
        _pokeRequested = true;
    }

    private void RunLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                RunOnce(token);
            }
            catch
            {
                // пересчитаем устройства на следующей итерации
            }

            _pokeRequested = false;

            // пауза до следующего пересканирования: Poke прерывает её мгновенно.
            for (var i = 0; i < RescanDelay.TotalSeconds && !_pokeRequested && !token.IsCancellationRequested; i++)
            {
                token.WaitHandle.WaitOne(1000);
            }
        }
    }

    private void RunOnce(CancellationToken token)
    {
        var devices = DeviceList.Local
            .GetHidDevices()
            .ToList();

        var wired = devices.Any(d => d.VendorID == X11VendorId && d.ProductID == X11ProductIdWired);

        // Проводное X11: зарядка, донгл не читаем — иначе состояние мигает
        if (wired)
        {
            _registry.Update(X11DeviceId, DeviceSource.X11Wired, X11DefaultName, DeviceKind.Mouse,
                batteryPct: null, charging: true, needsWake: false);
            return;
        }

        // профили: известные донглы + универсальные кандидаты по имени
        var profiles = new List<(string Id, string Name, DeviceKind Kind, Func<byte[], int, int?> Parse)>();

        var x11 = devices.Where(d => d.VendorID == X11VendorId && d.ProductID == X11ProductIdDongle).ToList();
        if (x11.Count > 0)
        {
            profiles.Add((X11DeviceId, X11DefaultName, DeviceKind.Mouse, ParseX11Battery));
        }

        var generic = new List<(string Id, string Name, List<HidDevice> Devices)>();
        foreach (var group in devices.GroupBy(d => (d.VendorID, d.ProductID)))
        {
            if (group.Key.VendorID == X11VendorId)
            {
                continue; // X11 уже разобран
            }
            var name = GetFriendlyName(group.First());
            var n = name.ToLowerInvariant();
            if (!ReceiverHints.Any(h => n.Contains(h)))
            {
                continue;
            }
            var id = $"dongle:{group.Key.VendorID:X4}:{group.Key.ProductID:X4}";
            var kind = DeviceRegistry.ClassifyKind(name);
            profiles.Add((id, name, kind, ParseUnknown));
            generic.Add((id, name, group.ToList()));
        }

        // донглы, которые пропали из системы — снять с учёта
        var presentGeneric = generic.Select(g => g.Id).ToHashSet();
        foreach (var stale in _activeGeneric.Where(id => !presentGeneric.Contains(id)).ToList())
        {
            _registry.Remove(stale);
            _activeGeneric.Remove(stale);
        }

        // публикация состояния всех найденных донглов
        foreach (var (id, name, kind, _) in profiles)
        {
            var isX11 = id == X11DeviceId;
            _registry.Update(id, DeviceSource.X11Dongle, name, kind,
                batteryPct: isX11 && _lastKnownPct.HasValue ? _lastKnownPct : null,
                charging: false,
                needsWake: isX11 && IsAsleep());
            if (!isX11)
            {
                _activeGeneric.Add(id);
            }
        }

        if (x11.Count == 0 && !devices.Any(d => d.VendorID == X11VendorId && d.ProductID == X11ProductIdWired))
        {
            _registry.Remove(X11DeviceId);
        }

        if (!profiles.Any())
        {
            return;
        }

        // читаем отчёты со всех интерфейсов всех найденных донглов
        var targets = x11.Cast<HidDevice>()
            .Concat(generic.SelectMany(g => g.Devices))
            .ToList();

        var streams = new List<(HidDevice Device, HidStream Stream, Func<byte[], int, int?> Parse, string Id)>();
        foreach (var device in targets)
        {
            var id = device.VendorID == X11VendorId && device.ProductID == X11ProductIdDongle
                ? X11DeviceId
                : $"dongle:{device.VendorID:X4}:{device.ProductID:X4}";
            var profile = profiles.FirstOrDefault(p => p.Id == id);
            if (profile.Id == null || !device.TryOpen(out var stream))
            {
                continue;
            }
            stream.ReadTimeout = 1000;
            streams.Add((device, stream, profile.Parse, profile.Id));
        }

        try
        {
            var buffer = new byte[Math.Max(64, targets.Count > 0 ? targets.Max(d => d.GetMaxInputReportLength()) : 64)];
            var deadline = DateTime.UtcNow + StreamSession;
            while (!token.IsCancellationRequested && streams.Count > 0 && DateTime.UtcNow < deadline)
            {
                if (_pokeRequested)
                {
                    break;
                }

                var x11HadData = false;
                for (var i = 0; i < streams.Count; i++)
                {
                    int n;
                    try
                    {
                        n = streams[i].Stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }
                    catch
                    {
                        streams.RemoveAt(i);
                        i--;
                        continue;
                    }

                    if (n > 0)
                    {
                        var pct = streams[i].Parse(buffer, n);
                        if (pct.HasValue)
                        {
                            x11HadData = true;
                            _lastReportUtc = DateTime.UtcNow;
                            _lastKnownPct = pct;
                            _registry.Update(X11DeviceId, DeviceSource.X11Dongle, X11DefaultName, DeviceKind.Mouse,
                                batteryPct: pct, charging: false, needsWake: false);
                        }
                    }
                }

                if (!x11HadData && streams.Any(s => s.Id == X11DeviceId))
                {
                    PublishWakeHint();
                }
            }
        }
        finally
        {
            foreach (var (_, stream, _, _) in streams)
            {
                try
                {
                    stream.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    private bool IsAsleep()
    {
        if (_lastKnownPct == null)
        {
            return true;
        }
        return DateTime.UtcNow - _lastReportUtc > TimeSpan.FromSeconds(60);
    }

    private void PublishWakeHint()
    {
        if (DateTime.UtcNow - _lastWakePublishUtc < TimeSpan.FromSeconds(30))
        {
            return;
        }
        _lastWakePublishUtc = DateTime.UtcNow;
        _registry.Update(X11DeviceId, DeviceSource.X11Dongle, X11DefaultName, DeviceKind.Mouse,
            batteryPct: null, charging: false, needsWake: IsAsleep());
    }

    private static int? ParseX11Battery(byte[] buffer, int length)
    {
        var limit = Math.Min(length, 8) - X11BatterySignature.Length - 1;
        for (var offset = 0; offset <= limit; offset++)
        {
            var match = true;
            for (var i = 0; i < X11BatterySignature.Length; i++)
            {
                if (buffer[offset + i] != X11BatterySignature[i])
                {
                    match = false;
                    break;
                }
            }
            if (match && buffer[offset + X11BatterySignature.Length] <= 100)
            {
                return buffer[offset + X11BatterySignature.Length];
            }
        }
        return null;
    }

    /// <summary>Формат заряда неизвестен — данных нет, пока не добавим сигнатуру.</summary>
    private static int? ParseUnknown(byte[] buffer, int length)
    {
        return null;
    }

    private static string GetFriendlyName(HidDevice? device)
    {
        if (device != null)
        {
            try
            {
                var name = device.GetProductName();
                if (!string.IsNullOrWhiteSpace(name) && name != "Unknown")
                {
                    return name;
                }
            }
            catch
            {
            }
        }
        return "2.4G устройство";
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _thread?.Join(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
        _cts?.Dispose();
        _cts = null;
        _thread = null;
    }
}
