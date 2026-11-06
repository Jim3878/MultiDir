using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiDir.Services;

/// <summary>
/// One MultiDir per user. A second launch (e.g. double-clicking a folder while MultiDir runs) hands its
/// folders to the running instance over a pipe and exits, so folders open as tabs instead of new windows.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>Lets automation tests run their own single-instance pair without touching the user's MultiDir.</summary>
    public const string TestIdVariable = "MULTIDIR_INSTANCE_ID";
    private static readonly string Id = "MultiDir.Instance." + (Environment.GetEnvironmentVariable(TestIdVariable) ?? Environment.UserName);
    private readonly Mutex _mutex;
    private CancellationTokenSource? _cts;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Returns the owner object if this is the first instance; otherwise forwards <paramref name="paths"/> and returns null.</summary>
    public static SingleInstance? TryAcquire(IReadOnlyList<string> paths)
    {
        var mutex = new Mutex(true, Id, out bool created);
        if (created) return new SingleInstance(mutex);
        mutex.Dispose();
        Forward(paths);
        return null;
    }

    private static void Forward(IReadOnlyList<string> paths)
    {
        AllowSetForegroundWindow(ASFW_ANY); // let the running instance come to the front
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", Id, PipeDirection.Out);
                pipe.Connect(500);
                Trace($"connected (attempt {attempt})");
                using var w = new StreamWriter(pipe, new UTF8Encoding(false));
                w.WriteLine(paths.Count == 0 ? "" : string.Join("\n", paths));
                Trace("sent");
                return;
            }
            catch (Exception ex) { Trace("retry: " + ex.GetType().Name); Thread.Sleep(100); } // first instance still starting up
        }
    }

    /// <summary>Starts listening; <paramref name="onOpen"/> is called on a background thread with the forwarded paths.</summary>
    public void Listen(Action<string[]> onOpen)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync(token);
                    using var r = new StreamReader(pipe, Encoding.UTF8);
                    var text = await r.ReadToEndAsync(token);
                    Trace("received");
                    onOpen(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { /* client went away */ }
            }
        }, token);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _mutex.Dispose();
    }

    /// <summary>
    /// Startup / hand-off timing log (a few lines per launch) so slow starts can be diagnosed:
    /// %AppData%\MultiDir\startup.log, or a temp file for test instances. Kept under ~200 KB.
    /// </summary>
    public static void Trace(string what)
    {
        try
        {
            bool test = Environment.GetEnvironmentVariable(TestIdVariable) != null || Environment.GetCommandLineArgs().Contains("--automation");
            var file = test
                ? Path.Combine(Path.GetTempPath(), "multidir-instance-trace.txt")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiDir", "startup.log");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (File.Exists(file) && new FileInfo(file).Length > 200_000) File.Delete(file);
            File.AppendAllText(file, $"{DateTime.Now:MM-dd HH:mm:ss.fff} pid {Environment.ProcessId}: {what}{Environment.NewLine}");
        }
        catch { }
    }

    private const int ASFW_ANY = -1;
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
