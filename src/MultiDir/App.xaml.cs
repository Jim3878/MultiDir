using System.IO;
using System.Windows;
using MultiDir.Services;

namespace MultiDir;

public partial class App : Application
{
    private static string LogPath => Path.Combine(AppPaths.DataDir, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool automation = e.Args.Contains("--automation");
        // Test instances must never read or write the user's real settings.
        if (automation) AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "MultiDir-automation");
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            // An automation instance must never pop dialogs onto the user's screen; the log is enough.
            if (!automation)
                MessageBox.Show(args.Exception.Message, "MultiDir 發生錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);

        Views.FloatingTabDragFix.Register();
        var window = new MainWindow();
        MainWindow = window;

        // --automation: test instance driven over a named pipe. It opens off-screen and never takes focus,
        // so testing does not interfere with the user's own mouse, keyboard or windows.
        if (automation)
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 100;
            window.Top = SystemParameters.VirtualScreenTop;
            window.Width = 1400;
            window.Height = 860;
        }
        window.Show();
        if (automation) AutomationServer.Start(window);
        // Folders passed on the command line open as new tabs in the focused pane.
        foreach (var arg in e.Args.Where(a => !a.StartsWith('-')))
            window.OpenTab(arg);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ViewModeStore.Save();
        base.OnExit(e);
    }

    private static void Log(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch { }
    }
}
