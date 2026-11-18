using System.Runtime.InteropServices;

namespace BatteryHub;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    private const int AttachParentProcess = -1;
    public const string SingleInstanceMutexName = "Local\\BatteryHub.Instance";
    public const string ShowPanelEventName = "Local\\BatteryHub.ShowPanel";

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            TryWriteCrashLog(e.ExceptionObject as Exception ?? new Exception("Unknown"));

        if (args.Any(a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase)))
        {
            return await ProbeRunner.RunAsync(args);
        }

        try
        {
            using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool firstInstance);
            if (!firstInstance)
            {
                // Второй запуск: показать панель у уже работающего экземпляра.
                try
                {
                    using var showEvent = System.Threading.EventWaitHandle.OpenExisting(ShowPanelEventName);
                    showEvent.Set();
                }
                catch
                {
                    // первый экземпляр ещё не создал событие — не критично
                }
                return 0;
            }

            ApplicationConfiguration.Initialize();
            L10n.Load();
            AppSettings.Load();
            if (AppSettings.HasSettingsFile)
            {
                L10n.Set(AppSettings.Lang); // settings.json — приоритет над lang.txt
            }
            Application.ThreadException += (_, e) => TryWriteCrashLog(e.Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.Run(new TrayApp());
            return 0;
        }
        catch (Exception ex)
        {
            TryWriteCrashLog(ex);
            return 1;
        }
    }

    public static void TryWriteCrashLog(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryHub");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:u}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n");
        }
        catch
        {
        }
    }

    /// <summary>Пытается подключить вывод к родительской консоли (для режима --probe).</summary>
    public static void TryAttachConsole()
    {
        try
        {
            AttachConsole(AttachParentProcess);
        }
        catch
        {
            // нет консоли — вывод пойдёт в лог-файл
        }
    }

    public static void TryFreeConsole()
    {
        try
        {
            FreeConsole();
        }
        catch
        {
        }
    }
}
