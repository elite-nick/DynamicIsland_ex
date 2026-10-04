using System.IO;
using System.Windows;

namespace DynamicIsland;

public partial class App : Application
{
    Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "DynamicIsland.SingleInstance", out bool fresh);
        // started by the island it has just replaced: that one is on its way out
        if (!fresh && e.Args.Contains(Updater.Restarted))
        {
            try { fresh = _mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { fresh = true; }
        }
        if (!fresh)
        {
            Shutdown();
            return;
        }
        Updater.CleanUp();

        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) =>
        {
            Log(a.Exception);
            a.Handled = true;
        };
        new MainWindow().Show();
    }

    public static void Log(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "DynamicIsland.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}
