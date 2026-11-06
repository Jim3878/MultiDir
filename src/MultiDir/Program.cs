using MultiDir.Services;

namespace MultiDir;

/// <summary>
/// Custom entry point: the single-instance check runs before WPF is loaded at all, so opening a folder while
/// MultiDir is already running costs only a bare process start + one pipe message.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        SingleInstance.Trace($"main {string.Join(' ', args)} (process started {System.Diagnostics.Process.GetCurrentProcess().StartTime:HH:mm:ss.fff})");
        bool automation = args.Contains("--automation");
        // Test instances stay separate from the user's MultiDir, unless a test opts in with its own instance id.
        bool single = !automation || Environment.GetEnvironmentVariable(SingleInstance.TestIdVariable) != null;
        if (args.Contains("--exit"))
        {
            // Ask the running MultiDir to quit (session is saved); does nothing if none is running.
            if (SingleInstance.TryAcquire([SingleInstance.ExitCommand]) is { } none) none.Dispose();
            return 0;
        }
        if (single)
        {
            var folders = args.Where(a => !a.StartsWith('-')).ToList();
            App.Instance = SingleInstance.TryAcquire(folders);
            if (App.Instance == null) { SingleInstance.Trace("exit"); return 0; } // handed over to the running MultiDir
        }

        var app = new App();
        app.InitializeComponent();
        int code = app.Run();
        SingleInstance.Trace("exit: process ending");
        return code;
    }
}
