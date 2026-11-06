using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace MultiDir.Services;

/// <summary>
/// Catches what the registry can't: "open file location" / "show in folder" (explorer /select and
/// SHOpenFolderAndSelectItems, used by Chrome, download managers…) and Win+E always open an Explorer window directly.
/// While MultiDir is the default folder opener, every NEW Explorer window is hidden the moment it appears, its folder
/// and selected files are read through the Shell windows API, the window is closed, and the folder opens as a
/// MultiDir tab with the same files selected.
/// Left alone: virtual folders (Control Panel, Recycle Bin, network, phones…), windows opened while Shift is held,
/// and windows MultiDir itself hands to Explorer.
/// </summary>
public static class ExplorerTakeover
{
    private static IntPtr _hook;
    private static WinEventDelegate? _callback; // kept alive: the hook calls back into it
    private static Action<IReadOnlyList<string>>? _open;
    private static DateTime _letThroughUntil;
    private static readonly HashSet<IntPtr> Handled = new();

    // This PC / Quick access / Home (what Win+E or a plain explorer.exe opens) → a 本機 tab.
    private static readonly string[] ThisPcLike =
    [
        "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", // This PC
        "::{679f85cb-0220-4080-b29b-5540cc05aab6}", // Quick access
        "::{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", // Home (Win11)
    ];

    public static bool IsActive => _hook != IntPtr.Zero;

    /// <summary>Starts watching. <paramref name="open"/> gets OpenExternal-style args ("folder", "/select,file", "::thispc").</summary>
    public static void Start(Action<IReadOnlyList<string>> open)
    {
        _open = open;
        if (_hook != IntPtr.Zero) return;
        _callback = OnWinEvent;
        _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        SingleInstance.Trace($"explorer takeover {(_hook != IntPtr.Zero ? "on" : "FAILED")}");
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>The next Explorer window(s) within 3 s are meant for Explorer (MultiDir passed a virtual folder on).</summary>
    public static void LetThrough() => _letThroughUntil = DateTime.Now.AddSeconds(3);

    private static void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero || !IsExplorerFolderWindow(hwnd) || !Handled.Add(hwnd)) return;
        if (DateTime.Now < _letThroughUntil || (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) return; // Shift = I want Explorer
        // Hide at once (no flash); it is shown again if it turns out to be a folder we leave to Explorer.
        ShowWindow(hwnd, SW_HIDE);
        var started = DateTime.Now;
        DateTime? readySince = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            var state = Inspect(hwnd);
            bool timedOut = DateTime.Now - started > TimeSpan.FromSeconds(4);
            if (state == null && !timedOut) { readySince = null; return; } // still navigating
            // /select applies the selection just after navigation completes: give it a moment, then read again.
            readySince ??= DateTime.Now;
            if (state != null && state.Selected.Count == 0 && !timedOut && DateTime.Now - readySince < TimeSpan.FromMilliseconds(250)) return;
            timer.Stop();
            Handled.RemoveWhere(h => !IsWindow(h));
            if (state is { } s && Decide(s) is { } args)
            {
                try { s.Window.Quit(); } catch { PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
                SingleInstance.Trace($"explorer takeover: {string.Join(" ", args)}");
                _open?.Invoke(args);
            }
            else ShowWindow(hwnd, SW_SHOW); // not ours after all: give the window back
        };
        timer.Start();
    }

    private sealed record WindowState(dynamic Window, string Location, List<string> Selected);

    /// <summary>The Explorer window's folder and selection once it has finished navigating; null while still loading.</summary>
    private static WindowState? Inspect(IntPtr hwnd)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows)!)!;
            int count = shell.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic? w = shell.Item(i);
                if (w == null || (IntPtr)(long)w.HWND != hwnd) continue;
                if ((int)w.ReadyState != 4 /* READYSTATE_COMPLETE */ || (bool)w.Busy) return null;
                dynamic? doc = w.Document;
                if (doc == null) return null;
                string location = doc.Folder.Self.Path;
                var selected = new List<string>();
                dynamic items = doc.SelectedItems();
                int n = items.Count;
                for (int k = 0; k < n; k++) selected.Add((string)items.Item(k).Path);
                return new WindowState(w, location, selected);
            }
        }
        catch { /* window gone or not ready yet */ }
        return null;
    }

    /// <summary>What MultiDir should open instead, or null to leave the window to Explorer.</summary>
    private static List<string>? Decide(WindowState s)
    {
        if (ThisPcLike.Any(id => string.Equals(id, s.Location, StringComparison.OrdinalIgnoreCase))) return ["::thispc"];
        if (s.Location.StartsWith("::") || !System.IO.Directory.Exists(s.Location)) return null; // virtual folder
        // A selection made by /select (or "show in folder") → select the same files in MultiDir.
        var files = s.Selected.Where(p => !string.IsNullOrEmpty(p) && !p.StartsWith("::")).ToList();
        return files.Count > 0 ? files.Select(f => "/select," + f).ToList() : [s.Location];
    }

    private static bool IsExplorerFolderWindow(IntPtr hwnd)
    {
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        return cls.ToString() == "CabinetWClass" && GetParent(hwnd) == IntPtr.Zero;
    }

    // ---------- interop ----------

    private static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private const uint EVENT_OBJECT_SHOW = 0x8002, WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    private const int OBJID_WINDOW = 0, SW_HIDE = 0, SW_SHOW = 5, VK_SHIFT = 0x10;
    private const uint WM_CLOSE = 0x10;

    private delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
