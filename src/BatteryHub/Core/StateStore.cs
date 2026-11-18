using System.Text.Json;

namespace BatteryHub.Core;

/// <summary>Замер заряда устройства.</summary>
public sealed record Sample(long UnixMs, int Pct);

/// <summary>Хранит историю замеров в %LocalAppData%\BatteryHub\state.json.</summary>
public sealed class StateStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryHub");
    private static readonly string FilePath = Path.Combine(Dir, "state.json");

    private readonly object _lock = new();
    private Dictionary<string, List<Sample>> _samples = new();
    private bool _dirty;
    private DateTime _lastSaveUtc = DateTime.MinValue;

    public StateStore()
    {
        Load();
    }

    public List<Sample> GetSamples(string deviceId)
    {
        lock (_lock)
        {
            return _samples.TryGetValue(deviceId, out var list) ? new List<Sample>(list) : new List<Sample>();
        }
    }

    /// <summary>Последний известный замер — для показа «последнего известного» сразу после запуска.</summary>
    public (int Pct, DateTime Utc)? GetLastKnownSample(string deviceId)
    {
        lock (_lock)
        {
            if (_samples.TryGetValue(deviceId, out var list) && list.Count > 0)
            {
                var last = list[^1];
                return (last.Pct, DateTime.UnixEpoch.AddMilliseconds(last.UnixMs).ToUniversalTime());
            }
            return null;
        }
    }

    public void AddSample(string deviceId, Sample sample)
    {
        lock (_lock)
        {
            if (!_samples.TryGetValue(deviceId, out var list))
            {
                list = new List<Sample>();
                _samples[deviceId] = list;
            }

            if (list.Count > 0)
            {
                var last = list[^1];
                if (last.Pct == sample.Pct &&
                    sample.UnixMs - last.UnixMs < TimeSpan.FromMinutes(5).TotalMilliseconds)
                {
                    return; // без изменений — не дублируем замеры
                }
            }

            list.Add(sample);
            Prune(list);
            _dirty = true;
        }
    }

    private static void Prune(List<Sample> list)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - TimeSpan.FromDays(7).TotalMilliseconds;
        list.RemoveAll(s => s.UnixMs < cutoff);
    }

    public void Flush(bool force = false)
    {
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }
            if (!force && DateTime.UtcNow - _lastSaveUtc < TimeSpan.FromSeconds(30))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = FilePath + ".tmp";
                var options = new JsonSerializerOptions { WriteIndented = false };
                File.WriteAllText(tmp, JsonSerializer.Serialize(_samples, options));
                File.Move(tmp, FilePath, overwrite: true);
                _dirty = false;
                _lastSaveUtc = DateTime.UtcNow;
            }
            catch
            {
                // журнал не критичен — приложение продолжит работу
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }
            var json = File.ReadAllText(FilePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, List<Sample>>>(json);
            if (data != null)
            {
                lock (_lock)
                {
                    _samples = data;
                    foreach (var list in _samples.Values)
                    {
                        Prune(list);
                    }
                }
            }
        }
        catch
        {
            lock (_lock)
            {
                _samples = new Dictionary<string, List<Sample>>();
            }
        }
    }
}
