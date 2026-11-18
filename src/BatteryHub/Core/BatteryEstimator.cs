namespace BatteryHub.Core;

/// <summary>Оценка оставшегося времени работы по скорости разряда из истории замеров.</summary>
public static class BatteryEstimator
{
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromHours(12);
    private static readonly TimeSpan MinSpan = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Линейная оценка по последним замерам. Возвращает часы (0.1–72) или null, если данных мало.
    /// </summary>
    public static double? EstimateHours(IReadOnlyList<Sample> samples, int currentPct, bool charging)
    {
        if (charging || samples.Count < 3)
        {
            return null;
        }

        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - HistoryWindow.TotalMilliseconds;
        var recent = samples.Where(s => s.UnixMs >= cutoff).ToList();
        if (recent.Count < 3)
        {
            return null;
        }

        var spanMs = recent[^1].UnixMs - recent[0].UnixMs;
        if (spanMs < MinSpan.TotalMilliseconds)
        {
            return null;
        }

        var spanHours = spanMs / 3600000.0;
        var drop = recent[0].Pct - recent[^1].Pct;
        if (drop < 2)
        {
            return null; // нет измеримого разряда (свежая запись или заряжалось)
        }

        var ratePerHour = drop / spanHours; // % в час
        if (ratePerHour <= 0.01 || ratePerHour > 60)
        {
            return null; // нереалистичная скорость
        }

        var eta = currentPct / ratePerHour;
        return Math.Clamp(eta, 0.1, 72);
    }

    /// <summary>«≈ 6 ч 40 мин» или «—».</summary>
    public static string FormatEta(double? etaHours)
    {
        if (etaHours is not { } hours)
        {
            return "—";
        }

        var totalMinutes = (int)Math.Round(hours * 60, MidpointRounding.AwayFromZero);
        var h = totalMinutes / 60;
        var m = totalMinutes % 60;
        if (h == 0)
        {
            return $"≈ {m} мин";
        }
        return m == 0 ? $"≈ {h} ч" : $"≈ {h} ч {m} мин";
    }
}
