using HidSharp;

namespace BatteryHub.Core.Sources;

/// <summary>
/// Читает заряд Attack Shark X11 через 2.4G-донгл (USB VID 0x1D57, PID 0xFA60).
/// Мочь сама шлёт входной HID-отчёт со сигнатурой 03 55 40 01 и процентом в следующем байте,
/// но только когда бодрствует. Проводное подключение — PID 0xFA55 (детект зарядки).
/// </summary>
public sealed class X11DongleSource : IDisposable
{
    public const int VendorId = 0x1D57;
    public const int ProductIdDongle = 0xFA60;
    public const int ProductIdWired = 0xFA55;
    public const string DefaultName = "Attack Shark X11";
    public const string DeviceId = "x11-dongle";

    private static readonly byte[] BatterySignature = { 0x03, 0x55, 0x40, 0x01 };

    /// <summary>Сколько держать интерфейсы донгла открытыми (одна сессия чтения).</summary>
    private static readonly TimeSpan StreamSession = TimeSpan.FromSeconds(60);

    /// <summary>Пауза между сессиями чтения / пересканирования.</summary>
    private static readonly TimeSpan RescanDelay = TimeSpan.FromSeconds(10);

    private readonly DeviceRegistry _registry;
    private Thread? _thread;
    private CancellationTokenSource? _cts;

    private DateTime _lastReportUtc = DateTime.MinValue;
    private int? _lastKnownPct;
    private DateTime _lastWakePublishUtc = DateTime.MinValue;
    private volatile bool _pokeRequested;

    public X11DongleSource(DeviceRegistry registry)
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
            Name = "X11 HID reader",
        };
        _thread.Start();

        // сразу публикуем последний известный заряд из истории —
        // значок показывает его до первого отчёта бодрствующей мыши
        try
        {
            var last = _registry.GetLastKnownSample(DeviceId);
            if (last.HasValue)
            {
                _lastKnownPct = last.Value.Pct;
                _lastReportUtc = last.Value.Utc;
                _registry.Update(DeviceId, DeviceSource.X11Dongle, DefaultName, DeviceKind.Mouse,
                    batteryPct: last.Value.Pct, charging: false, needsWake: IsAsleep());
            }
        }
        catch
        {
        }
    }

    /// <summary>Просит поток чтения немедленно пере-перечислить донгл и ждать отчёты (клик по панели).</summary>
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
            // Раньше здесь был цикл «пересканировать каждую секунду» — из-за него
            // приложение бесконечно переоткрывало интерфейсы донгла и дёргало UI.
            for (var i = 0; i < RescanDelay.TotalSeconds && !_pokeRequested && !token.IsCancellationRequested; i++)
            {
                token.WaitHandle.WaitOne(1000);
            }
        }
    }

    /// <summary>
    /// Открывает все интерфейсы донгла и держит их открытыми до минуты (StreamSession),
    /// читая отчёты; затем закрывает и через RescanDelay пересканирует заново.
    /// </summary>
    private void RunOnce(CancellationToken token)
    {
        var devices = DeviceList.Local
            .GetHidDevices(VendorId)
            .Where(d => d.ProductID == ProductIdDongle || d.ProductID == ProductIdWired)
            .ToList();

        var wired = devices.Any(d => d.ProductID == ProductIdWired);
        var dongles = devices.Where(d => d.ProductID == ProductIdDongle).ToList();

        if (dongles.Count == 0 && !wired)
        {
            _registry.Remove(DeviceId);
            return;
        }

        var name = GetFriendlyName(dongles.Count > 0 ? dongles[0] : null);

        // Проводное подключение: зарядка, процент недоступен. Донгл при этом НЕ читаем —
        // иначе каждые ~70 с состояние «заряжается ↔ не заряжается» мигает:
        // значок перерисовывается, а у виджета меняется ширина (молния ⚡) и
        // закрывается открытое меню правого клика.
        if (wired)
        {
            _registry.Update(DeviceId, DeviceSource.X11Wired, name, DeviceKind.Mouse,
                batteryPct: null, charging: true, needsWake: false);
            return;
        }

        _registry.Update(DeviceId, DeviceSource.X11Dongle, name, DeviceKind.Mouse,
            batteryPct: null, charging: false, needsWake: IsAsleep());

        if (dongles.Count == 0)
        {
            return;
        }

        var streams = new List<HidStream>();
        var buffer = new byte[Math.Max(64, dongles.Max(d => d.GetMaxInputReportLength()))];
        var deadline = DateTime.UtcNow + StreamSession;

        foreach (var device in dongles)
        {
            if (device.TryOpen(out var stream))
            {
                stream.ReadTimeout = 1000;
                streams.Add(stream);
            }
        }

        try
        {
            while (!token.IsCancellationRequested && streams.Count > 0 && DateTime.UtcNow < deadline)
            {
                if (_pokeRequested)
                {
                    break; // немедленное пересканирование (клик по панели / переподключение)
                }

                var hadData = false;
                for (var i = 0; i < streams.Count; i++)
                {
                    int n;
                    try
                    {
                        n = streams[i].Read(buffer, 0, buffer.Length);
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }
                    catch
                    {
                        // этот интерфейс умер (не все интерфейсы донгла читаются) — выкидываем только его
                        streams.RemoveAt(i);
                        i--;
                        continue;
                    }

                    if (n > 0)
                    {
                        hadData = true;
                        TryParseBattery(buffer, n, name);
                    }
                }

                if (!hadData)
                {
                    PublishWakeHint(name);
                }
            }
        }
        finally
        {
            foreach (var stream in streams)
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
            return true; // заряд ещё неизвестен
        }
        return DateTime.UtcNow - _lastReportUtc > TimeSpan.FromSeconds(60);
    }

    private void PublishWakeHint(string name)
    {
        if (DateTime.UtcNow - _lastWakePublishUtc < TimeSpan.FromSeconds(30))
        {
            return;
        }
        _lastWakePublishUtc = DateTime.UtcNow;
        _registry.Update(DeviceId, DeviceSource.X11Dongle, name, DeviceKind.Mouse,
            batteryPct: null, charging: false, needsWake: IsAsleep());
    }

    private void TryParseBattery(byte[] buffer, int length, string name)
    {
        // Сигнатура может начинаться с байта 0 или 1 (в зависимости от того,
        // добавляет ли ОС байт report ID в начало отчёта).
        var searchLimit = Math.Min(length, 8) - BatterySignature.Length - 1;
        for (var offset = 0; offset <= searchLimit; offset++)
        {
            var match = true;
            for (var i = 0; i < BatterySignature.Length; i++)
            {
                if (buffer[offset + i] != BatterySignature[i])
                {
                    match = false;
                    break;
                }
            }
            if (!match)
            {
                continue;
            }

            var pct = buffer[offset + BatterySignature.Length];
            if (pct <= 100)
            {
                _lastReportUtc = DateTime.UtcNow;
                _lastKnownPct = pct;
                _registry.Update(DeviceId, DeviceSource.X11Dongle, name, DeviceKind.Mouse,
                    batteryPct: pct, charging: false, needsWake: false);
            }
            return;
        }
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
        return DefaultName;
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
