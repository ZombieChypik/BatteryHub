using System.Text;
using BatteryHub.Core.Sources;
using HidSharp;
using Windows.Devices.Enumeration;

namespace BatteryHub;

/// <summary>Режим --probe: диагностика HID-донгла X11 и Bluetooth-заряда, вывод в консоль/лог.</summary>
public static class ProbeRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        Program.TryAttachConsole();
        var log = new StringBuilder();
        void Line(string s)
        {
            log.AppendLine(s);
            try
            {
                Console.WriteLine(s);
            }
            catch
            {
                // вывода в консоль нет — всё попадёт в лог-файл
            }
        }

        try
        {
            Line($"BatteryHub probe — {DateTime.Now:u}");
            Line($"OS: {Environment.OSVersion.VersionString}, {Environment.Is64BitOperatingSystem}x64");
            Line("");

            ProbeHid(args, Line);
            Line("");
            await ProbeBluetoothAsync(Line);
            Line("");
            Line("Готово.");
        }
        catch (Exception ex)
        {
            Line($"ОШИБКА: {ex}");
        }
        finally
        {
            TryWriteLog(log.ToString());
            Program.TryFreeConsole();
        }
        return 0;
    }

    private static void ProbeHid(string[] args, Action<string> line)
    {
        var seconds = 10;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--probe-seconds", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(args[i + 1], out seconds);
            }
        }

        // Универсальная диагностика: все HID-устройства (любой производитель),
        // чтобы определить, передаёт ли донгл произвольной мыши заряд.
        line("== Все HID-устройства ==");
        var devices = DeviceList.Local.GetHidDevices()
            .GroupBy(d => (d.VendorID, d.ProductID))
            .ToList();
        if (devices.Count == 0)
        {
            line("  HID-устройства не найдены.");
            return;
        }

        foreach (var group in devices)
        {
            var first = group.First();
            var isX11 = first.VendorID == UniversalDongleSource.X11VendorId;
            var tag = isX11 ? "  [X11]" : "";
            var name = first.GetProductName();
            line($"  VID={first.VendorID:X4} PID={first.ProductID:X4}{tag} «{name}» интерфейсов: {group.Count()}");
            foreach (var d in group.Take(8))
            {
                line($"      maxInput={d.GetMaxInputReportLength()} maxFeature={d.GetMaxFeatureReportLength()} {Tail(d.DevicePath)}");
            }
        }
        line("");

        line($"== Чтение входных отчётов {seconds} с со ВСЕХ открытых интерфейсов ==");
        line("   (пошевелите мышью и покликайте — так появляются отчёты)");
        var streams = new List<(HidDevice Device, HidStream Stream, Dictionary<string, int> Unique)>();
        foreach (var device in DeviceList.Local.GetHidDevices())
        {
            if (device.GetMaxInputReportLength() == 0)
            {
                continue;
            }
            if (device.TryOpen(out var stream))
            {
                stream.ReadTimeout = 200;
                streams.Add((device, stream, new Dictionary<string, int>()));
            }
        }
        line($"  Открыто интерфейсов: {streams.Count} (остальные — системные/недоступные)");
        line("");

        try
        {
            var buffer = new byte[256];
            var batterySeen = false;
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            var paths = streams.Select(s => Tail(s.Device.DevicePath)).ToList();
            while (DateTime.UtcNow < deadline)
            {
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
                        // этот интерфейс умер — выкидываем только его
                        streams.RemoveAt(i);
                        paths.RemoveAt(i);
                        i--;
                        continue;
                    }

                    if (n <= 0)
                    {
                        continue;
                    }

                    var hex = string.Join(" ", buffer.Take(Math.Min(n, 16)).Select(b => $"{b:X2}"));
                    var uniq = streams[i].Unique;
                    uniq.TryGetValue(hex, out var count);
                    uniq[hex] = count + 1;

                    if (count == 0 && uniq.Values.Sum() <= 60)
                    {
                        line($"  [{paths[i]}] {hex}");
                    }

                    var pct = ParseBattery(buffer, n);
                    if (pct.HasValue && !batterySeen)
                    {
                        batterySeen = true;
                        line($"  ^^ ЗАРЯД X11 РАСПОЗНАН: {pct}%");
                    }
                }
            }

            // сводка: уникальные отчёты каждого интерфейса — тут и будет формат заряда
            line("");
            line("== Сводка по интерфейсам (уникальные отчёты) ==");
            foreach (var (device, _, uniq) in streams)
            {
                var path = Tail(device.DevicePath);
                line($"  [{path}] VID={device.VendorID:X4} PID={device.ProductID:X4}: отчётов {uniq.Values.Sum()}, уникальных {uniq.Count}");
                foreach (var kv in uniq.OrderByDescending(k => k.Value).Take(12))
                {
                    line($"      {kv.Key}  x{kv.Value}");
                }
            }
            if (!batterySeen)
            {
                line("");
                line("Подсказка: если среди уникальных отчётов есть подозрительный «постоянный» отчёт");
                line("(например, меняющийся при разряде мыши) — это и есть заряд; пришлите лог разработчику.");
            }
        }
        finally
        {
            foreach (var (_, stream, _) in streams)
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

        ProbeFeatureReports(line);
    }

    /// <summary>Пробует читать feature-отчёты (некоторые донглы отвечают зарядом на GetFeature).</summary>
    private static void ProbeFeatureReports(Action<string> line)
    {
        line("");
        line("== Feature-отчёты (GetFeature) ==");
        var attempts = 0;
        foreach (var device in DeviceList.Local.GetHidDevices())
        {
            if (device.GetMaxFeatureReportLength() == 0)
            {
                continue;
            }
            if (!device.TryOpen(out var stream))
            {
                continue;
            }

            var path = Tail(device.DevicePath);
            try
            {
                for (byte id = 0; id <= 8 && attempts < 120; id++)
                {
                    var buffer = new byte[device.GetMaxFeatureReportLength()];
                    buffer[0] = id;
                    try
                    {
                        stream.GetFeature(buffer);
                        var hex = string.Join(" ", buffer.Take(Math.Min(buffer.Length, 16)).Select(b => $"{b:X2}"));
                        line($"  [{path}] feature 0x{id:X2} -> {hex}");
                        attempts++;
                    }
                    catch
                    {
                        attempts++;
                    }
                }
            }
            finally
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
        if (attempts == 0)
        {
            line("  Нет устройств с feature-отчётами.");
        }
    }

    private static int? ParseBattery(byte[] buffer, int length)
    {
        int[] sig = { 0x03, 0x55, 0x40, 0x01 };
        var limit = Math.Min(length, 8) - sig.Length - 1;
        for (var offset = 0; offset <= limit; offset++)
        {
            var match = true;
            for (var i = 0; i < sig.Length; i++)
            {
                if (buffer[offset + i] != sig[i])
                {
                    match = false;
                    break;
                }
            }
            if (match && buffer[offset + sig.Length] <= 100)
            {
                return buffer[offset + sig.Length];
            }
        }
        return null;
    }

    private static async Task ProbeBluetoothAsync(Action<string> line)
    {
        line("== Bluetooth-устройства (подключённые AEP) ==");
        try
        {
            var found = await DeviceInformation.FindAllAsync(
                BluetoothSource.Selector, BluetoothSource.Properties, DeviceInformationKind.AssociationEndpoint);
            line($"Найдено {found.Count} подключённых конечных устройств:");
            foreach (var device in found)
            {
                device.Properties.TryGetValue("System.ItemNameDisplay", out var nameObj);
                device.Properties.TryGetValue("System.Devices.BatteryLife", out var batteryObj);
                device.Properties.TryGetValue("System.Devices.ChargingState", out var chargingObj);
                device.Properties.TryGetValue("System.Devices.Aep.Category", out var categoryObj);

                var name = nameObj as string ?? "(без имени)";
                var battery = batteryObj != null ? batteryObj + "%" : "нет данных";
                var categories = categoryObj is IEnumerable<string> list
                    ? string.Join(",", list)
                    : categoryObj?.ToString() ?? "";
                var bt = device.Id.StartsWith("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                         categories.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);
                line($"  {(bt ? "[BT]" : "[--]")} «{name}» заряд={battery} зарядка={chargingObj} категории=[{categories}] id={Tail(device.Id)}");
            }
        }
        catch (Exception ex)
        {
            line($"Ошибка опроса Bluetooth: {ex.Message}");
        }
    }

    private static string Tail(string s)
    {
        s = s.Replace("{", "").Replace("}", "");
        return s.Length <= 60 ? s : "…" + s[^60..];
    }

    private static void TryWriteLog(string content)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryHub");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "probe.log"), content);
        }
        catch
        {
        }
    }
}
