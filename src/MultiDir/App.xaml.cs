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
        bool automation = IsAutomation = e.Args.Contains("--automation");
        var folders = e.Args.Where(a => !a.StartsWith('-')).ToList();
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
        SingleInstance.Trace("startup: before MainWindow");
        var window = new MainWindow();
        SingleInstance.Trace("startup: MainWindow constructed");
        window.ContentRendered += (_, _) => SingleInstance.Trace("startup: first frame rendered");
        MainWindow = window;
        // Quit only via the main window (tray "結束"); hidden floating windows must not keep the process alive.
        ShutdownMode = ShutdownMode.OnMainWindowClose;

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
        else
        {
            window.TrayIcon = new Views.TrayIcon(window.ShowFromTray, window.ExitApp);
        }
        window.Show();
        SingleInstance.Trace("startup: shown");
        if (automation) AutomationServer.Start(window);
        // Folders passed on the command line open as new tabs in the focused pane.
        window.OpenExternal(folders, activate: false);
        Instance?.Listen(paths => window.Dispatcher.BeginInvoke(() => window.OpenExternal(paths)));
    }

    /// <summary>Set by <see cref="Program.Main"/> when this process owns the single-instance lock.</summary>
    public static SingleInstance? Instance { get; set; }

    /// <summary>Test instance: must never touch system-wide state such as the registry.</summary>
    public static bool IsAutomation { get; private set; }

    protected override void OnExit(ExitEventArgs e)
    {
        SingleInstance.Trace("exit: OnExit");
        ViewModeStore.Save();
        HistoryStore.Save();
        Instance?.Dispose();
        (MainWindow as MainWindow)?.TrayIcon?.Dispose();
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
