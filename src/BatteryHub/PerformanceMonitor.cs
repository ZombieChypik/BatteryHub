using System.Runtime.InteropServices;

namespace BatteryHub;

/// <summary>
/// Периодически отдаёт неиспользуемую память системе и собирает мусор,
/// чтобы хаб в диспетчере задач занимал ~10–25 МБ вместо ~160.
/// Страницы всё равно останутся в файле подкачки и вернутся мгновенно при обращении.
/// </summary>
public sealed class PerformanceMonitor
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, int minimumWorkingSetSize,
        int maximumWorkingSetSize);

    private System.Windows.Forms.Timer? _timer;

    public void Start()
    {
        _timer = new System.Windows.Forms.Timer { Interval = 5 * 60_000 }; // раз в 5 минут
        _timer.Tick += (_, _) => Trim();
        _timer.Start();

        // первый стрим после того, как улягутся стартовые аллокации
        var first = new System.Windows.Forms.Timer { Interval = 30_000 };
        first.Tick += (_, _) =>
        {
            first.Stop();
            first.Dispose();
            Trim();
        };
        first.Start();
    }

    private void Trim()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }
        catch
        {
            // не критично, если не удалось
        }
    }
}
