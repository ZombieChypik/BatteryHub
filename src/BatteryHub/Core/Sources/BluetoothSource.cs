using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;

namespace BatteryHub.Core.Sources;

/// <summary>
/// Публикует заряд подключённых Bluetooth-устройств через WinRT DeviceWatcher
/// (свойство System.Devices.BatteryLife) + периодический полный ре-поллинг.
/// Если служба Bluetooth остановлена (радио выключено) — источник полностью спит:
/// попытки перечисления при мёртвой службе висят по 15 секунд и качают систему.
/// </summary>
public sealed class BluetoothSource : IDisposable
{
    public const string Selector = "System.Devices.Aep.IsConnected:=System.StructuredQueryType.Boolean#True";

    public static readonly string[] Properties =
    {
        "System.ItemNameDisplay",
        "System.Devices.BatteryLife",
        "System.Devices.ChargingState",
        "System.Devices.Aep.IsConnected",
        "System.Devices.Aep.ContainerId",
        "System.Devices.Aep.Category",
    };

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private readonly DeviceRegistry _registry;
    private readonly object _watcherLock = new();
    private DeviceWatcher? _watcher;
    private System.Threading.Timer? _pollTimer;
    private System.Threading.Timer? _serviceTimer;
    private volatile bool _started;
    private volatile bool _btAvailable;

    public BluetoothSource(DeviceRegistry registry)
    {
        _registry = registry;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;

        // раз в 15 с проверяем службу Bluetooth: появилась — включаем watcher и поллинг
        _serviceTimer = new System.Threading.Timer(
            _ => CheckService(), null, TimeSpan.Zero, TimeSpan.FromSeconds(15));
        // сам поллинг стартует только когда служба Bluetooth работает
        _pollTimer = new System.Threading.Timer(
            _ => SafePoll(), null, PollInterval, PollInterval);
    }

    private void CheckService()
    {
        var available = IsBluetoothServiceRunning();
        if (available == _btAvailable)
        {
            return;
        }
        _btAvailable = available;
        if (available)
        {
            RestartWatcher();
            SafePoll();
        }
        else
        {
            lock (_watcherLock)
            {
                DisposeWatcher();
            }
        }
    }

    private static bool IsBluetoothServiceRunning()
    {
        try
        {
            return Native.QueryServiceRunning("bthserv");
        }
        catch
        {
            return false;
        }
    }

    private static class Native
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr sc, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatus(IntPtr service, out SERVICE_STATUS status);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
        }

        public static bool QueryServiceRunning(string name)
        {
            var sc = OpenSCManager(null, null, 0x0001 /* SC_MANAGER_CONNECT */);
            if (sc == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                var svc = OpenService(sc, name, 0x0004 /* SERVICE_QUERY_STATUS */);
                if (svc == IntPtr.Zero)
                {
                    return false;
                }
                try
                {
                    return QueryServiceStatus(svc, out var status)
                        && status.CurrentState == 0x00000004; // SERVICE_RUNNING
                }
                finally
                {
                    CloseServiceHandle(svc);
                }
            }
            finally
            {
                CloseServiceHandle(sc);
            }
        }
    }

    /// <summary>Немедленный полный ре-поллинг (пункт меню «Обновить сейчас»).</summary>
    public void RefreshNow()
    {
        if (_started && _btAvailable)
        {
            SafePoll();
        }
    }

    private void RestartWatcher()
    {
        if (!_btAvailable)
        {
            return;
        }
        lock (_watcherLock)
        {
            DisposeWatcher();
            var watcher = DeviceInformation.CreateWatcher(
                Selector, Properties, DeviceInformationKind.AssociationEndpoint);
            watcher.Added += OnAdded;
            watcher.Updated += OnUpdated;
            watcher.Removed += OnRemoved;
            watcher.Stopped += OnStopped;
            _watcher = watcher;
            try
            {
                watcher.Start();
            }
            catch
            {
                // watcher перезапустится на следующем ре-поллинге
                _watcher = null;
            }
        }
    }

    private void OnStopped(DeviceWatcher sender, object args)
    {
        // watcher мог остановиться сам (например, при недоступности стека) —
        // перезапускаем только если служба Bluetooth реально работает
        Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
        {
            if (_started && _btAvailable)
            {
                RestartWatcher();
            }
        });
    }

    private void OnAdded(DeviceWatcher sender, DeviceInformation info)
    {
        PublishDevice(info.Id, info.Properties);
    }

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        var props = args.Properties;
        if (props.TryGetValue("System.Devices.Aep.IsConnected", out var connectedObj) &&
            connectedObj is bool connected && !connected)
        {
            RemoveByDeviceId(args.Id);
            return;
        }
        PublishDevice(args.Id, props);
    }

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        RemoveByDeviceId(args.Id);
    }

    private void RemoveByDeviceId(string deviceId)
    {
        _registry.Remove(ContainerKeyForDeviceId(deviceId));
    }

    private void PublishDevice(string deviceId, IReadOnlyDictionary<string, object> props)
    {
        if (!IsBluetoothDevice(deviceId, props))
        {
            return;
        }

        props.TryGetValue("System.ItemNameDisplay", out var nameObj);
        props.TryGetValue("System.Devices.Aep.ContainerId", out var containerObj);
        props.TryGetValue("System.Devices.BatteryLife", out var batteryObj);
        props.TryGetValue("System.Devices.ChargingState", out var chargingObj);

        var name = nameObj as string;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Bluetooth-устройство";
        }

        var key = ContainerKeyFromDeviceId(deviceId, containerObj);
        _registry.Update(
            key,
            DeviceSource.Bluetooth,
            name,
            DeviceRegistry.ClassifyKind(name),
            batteryPct: ToPct(batteryObj),
            charging: ToBool(chargingObj));
    }

    /// <summary>Полный ре-поллинг — источник истины: убирает отвалившиеся, добавляет пропущенные.</summary>
    private async void SafePoll()
    {
        if (!_btAvailable)
        {
            return;
        }
        try
        {
            var found = await DeviceInformation.FindAllAsync(Selector, Properties, DeviceInformationKind.AssociationEndpoint)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(15));

            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var device in found)
            {
                if (!IsBluetoothDevice(device.Id, device.Properties))
                {
                    continue;
                }

                device.Properties.TryGetValue("System.ItemNameDisplay", out var nameObj);
                device.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var containerObj);
                device.Properties.TryGetValue("System.Devices.BatteryLife", out var batteryObj);
                device.Properties.TryGetValue("System.Devices.ChargingState", out var chargingObj);

                var name = nameObj as string;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = "Bluetooth-устройство";
                }

                var key = ContainerKeyFromDeviceId(device.Id, containerObj);
                keep.Add(key);
                _registry.Update(
                    key,
                    DeviceSource.Bluetooth,
                    name,
                    DeviceRegistry.ClassifyKind(name),
                    batteryPct: ToPct(batteryObj),
                    charging: ToBool(chargingObj));
            }

            _registry.PruneBluetoothExcept(keep);

            if (_started && _watcher == null)
            {
                RestartWatcher();
            }
        }
        catch
        {
            // стек Bluetooth мог быть временно недоступен — следующий поллинг повторит
        }
    }

    private static bool IsBluetoothDevice(string deviceId, IReadOnlyDictionary<string, object> props)
    {
        if (deviceId.StartsWith("Bluetooth", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (props.TryGetValue("System.Devices.Aep.Category", out var category))
        {
            if (category is IEnumerable<string> list)
            {
                foreach (var c in list)
                {
                    if (c?.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return true;
                    }
                }
            }
            else if (category?.ToString()?.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }
        return false;
    }

    private static bool? ToBool(object? value)
    {
        try
        {
            return value switch
            {
                null => null,
                bool b => b,
                int i => i != 0,
                uint u => u != 0,
                byte by => by != 0,
                _ => bool.TryParse(value.ToString(), out var parsed) ? parsed : null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static int? ToPct(object? value)
    {
        if (value == null)
        {
            return null;
        }
        try
        {
            return value switch
            {
                uint u when u <= 100 => (int)u,
                int i when i >= 0 && i <= 100 => i,
                ushort us when us <= 100 => us,
                byte b when b <= 100 => b,
                long l when l >= 0 && l <= 100 => (int)l,
                _ => int.TryParse(value.ToString(), out var parsed) && parsed is >= 0 and <= 100
                    ? parsed
                    : null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string ContainerKeyFromDeviceId(string deviceId, object? containerObj)
    {
        var container = containerObj?.ToString();
        return string.IsNullOrWhiteSpace(container)
            ? ContainerKeyForDeviceId(deviceId)
            : "bt-container:" + container;
    }

    private static string ContainerKeyForDeviceId(string deviceId)
    {
        // Ключ стабилен для всех endpoint'ов одного устройства (адрес внутри id).
        var colon = deviceId.IndexOf('#');
        var rest = colon >= 0 && colon + 1 < deviceId.Length ? deviceId[(colon + 1)..] : deviceId;
        var dash = rest.IndexOf('-');
        if (dash > 0)
        {
            rest = rest[..dash];
        }
        return "bt:" + rest;
    }

    private void DisposeWatcher()
    {
        if (_watcher != null)
        {
            try
            {
                _watcher.Added -= OnAdded;
                _watcher.Updated -= OnUpdated;
                _watcher.Removed -= OnRemoved;
                _watcher.Stopped -= OnStopped;
                _watcher.Stop();
            }
            catch
            {
            }
            _watcher = null;
        }
    }

    public void Dispose()
    {
        _started = false;
        _serviceTimer?.Dispose();
        _pollTimer?.Dispose();
        lock (_watcherLock)
        {
            DisposeWatcher();
        }
    }
}
