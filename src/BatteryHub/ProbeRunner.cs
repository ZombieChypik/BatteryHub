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
        line("== HID-устройства с VID 0x1D57 ==");
        var seconds = 10;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--probe-seconds", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(args[i + 1], out seconds);
            }
        }

        var devices = DeviceList.Local.GetHidDevices(X11DongleSource.VendorId).ToList();
        if (devices.Count == 0)
        {
            line("  Не найдено ни одного HID-устройства с VID 0x1D57.");
            line("  (Донгл подключён? Мышь может работать в Bluetooth-режиме — тогда донгл не нужен.)");
            return;
        }

        foreach (var device in devices)
        {
            line($"  PID=0x{device.ProductID:X4}  «{device.GetProductName()}»  maxInput={device.GetMaxInputReportLength()}  maxFeature={device.GetMaxFeatureReportLength()}");
        }
        line("");

        var dongles = devices.Where(d => d.ProductID == X11DongleSource.ProductIdDongle).ToList();
        if (dongles.Count == 0)
        {
            line("  Донгл (PID 0xFA60) не найден — читать отчёты не из чего.");
            return;
        }

        line($"== Чтение входных отчётов {seconds} с (пошевелите мышь, если ничего не приходит) ==");
        var streams = new List<(HidDevice Device, HidStream Stream)>();
        foreach (var device in dongles)
        {
            if (device.TryOpen(out var stream))
            {
                stream.ReadTimeout = 200;
                streams.Add((device, stream));
            }
            else
            {
                line($"  Не удалось открыть интерфейс: {Tail(device.DevicePath)}");
            }
        }

        try
        {
            var buffer = new byte[128];
            var reports = 0;
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
                    catch (Exception ex)
                    {
                        line($"  [{paths[i]}] чтение прервано: {ex.Message}");
                        streams.RemoveAt(i);
                        paths.RemoveAt(i);
                        i--;
                        continue;
                    }

                    if (n > 0)
                    {
                        reports++;
                        var hex = string.Join(" ", buffer.Take(Math.Min(n, 16)).Select(b => $"{b:X2}"));
                        line($"  [{paths[i]}] {hex}");
                        var pct = ParseBattery(buffer, n);
                        if (pct.HasValue && !batterySeen)
                        {
                            batterySeen = true;
                            line($"  ^^ ЗАРЯД РАСПОЗНАН: {pct}%");
                        }
                    }
                }
            }
            line($"Всего отчётов за {seconds} с: {reports}. Заряд распознан: {batterySeen}.");
            if (!batterySeen)
            {
                line("Подсказка: пошевелите мышью/кликните — мышь шлёт отчёт о заряде только когда бодрствует.");
            }
        }
        finally
        {
            foreach (var (_, stream) in streams)
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
