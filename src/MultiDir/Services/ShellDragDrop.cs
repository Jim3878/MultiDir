using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace MultiDir.Services;

/// <summary>
/// Drag &amp; drop is delegated to the Windows Shell so behaviour matches Explorer exactly (decisions #7, #19):
/// same-drive move / cross-drive copy, Shift/Ctrl overrides, the right-drag menu (incl. 7-Zip etc.),
/// "移動到 X" drop descriptions, conflict dialogs, progress and Ctrl+Z undo.
/// MultiDir only decides <em>which folder</em> is the target.
/// </summary>
public static class ShellDragDrop
{
    /// <summary>The data object of a drag that started inside MultiDir (lets the shell run the copy asynchronously).</summary>
    private static ComDataObject? _ownDrag;

    // ---------- drag source ----------

    /// <summary>Starts a modal shell drag of the given items (all must share one parent folder).</summary>
    public static void DoDragDrop(Window owner, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var data = CreateDataObject(paths);
        if (data == null) return;
        _ownDrag = data;
        try
        {
            var hwnd = new WindowInteropHelper(owner).Handle;
            // Default shell drop source + drag image built from the data object.
            SHDoDragDrop(hwnd, data, IntPtr.Zero, DROPEFFECT_COPY | DROPEFFECT_MOVE | DROPEFFECT_LINK, out _);
        }
        finally
        {
            _ownDrag = null;
            Marshal.ReleaseComObject(data);
            // Drag ended anywhere (dropped outside, Esc…): make sure our side let go of the drag image and target.
            if (_helperEntered || _target != null) Leave();
            Views.DragHint.Hide();
        }
    }

    private static ComDataObject? CreateDataObject(IReadOnlyList<string> paths)
    {
        var pidls = new IntPtr[paths.Count];
        try
        {
            for (int i = 0; i < paths.Count; i++)
                if (SHParseDisplayName(paths[i], IntPtr.Zero, out pidls[i], 0, out _) != 0) return null;
            if (SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out var array) != 0) return null;
            try
            {
                Guid bhid = BHID_DataObject, iid = IID_IDataObject;
                array.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out var obj);
                return obj as ComDataObject;
            }
            finally { Marshal.ReleaseComObject(array); }
        }
        finally
        {
            foreach (var p in pidls) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    // ---------- drop target ----------

    private static IOleDropTarget? _target;
    private static string? _targetFolder;
    private static IDropTargetHelper? _helper;
    private static bool _helperEntered;

    /// <summary>
    /// Call from DragEnter/DragOver. <paramref name="folder"/> is the folder under the cursor
    /// (null = nothing droppable here). Switching folders mid-drag re-enters the new shell target.
    /// </summary>
    public static void Over(DragEventArgs e, UIElement element, string? folder)
    {
        var pt = ScreenPoint(e, element);
        e.Effects = (DragDropEffects)OverCore(ComData(e.Data), pt, (int)e.KeyStates,
            (int)e.AllowedEffects, Hwnd(element), folder);
        e.Handled = true;
        // "移動到 X" / "複製到 X" next to the cursor (decision: 拖曳中游標即時顯示).
        if (folder != null) Views.DragHint.ShowDrop(new Point(pt.x, pt.y), e.Effects, folder, ((int)e.KeyStates & MK_RBUTTON) != 0);
        else Views.DragHint.Hide();
    }

    /// <summary>
    /// For DragLeave events that may only be crossing child elements: leave unless another DragOver arrives very
    /// soon. (A fast drag out of the window reports its last position still inside, and a missed leave keeps the
    /// shell's drag image stuck on the screen.)
    /// </summary>
    public static void LeaveSoon()
    {
        var seen = _overCount;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_overCount == seen && (_helperEntered || _target != null)) Leave();
        };
        timer.Start();
    }

    private static int _overCount;

    public static void Leave()
    {
        Views.DragHint.Hide();
        if (_helperEntered) { Helper()?.DragLeave(); _helperEntered = false; }
        ReleaseTarget(sendLeave: true);
        _targetFolder = null;
    }

    public static void Drop(DragEventArgs e, UIElement element, string? folder)
    {
        Views.DragHint.Hide();
        string[]? paths = null;
        try { paths = e.Data.GetData(DataFormats.FileDrop) as string[]; } catch { /* not files */ }
        e.Effects = (DragDropEffects)DropCore(ComData(e.Data), ScreenPoint(e, element), (int)e.KeyStates,
            (int)e.AllowedEffects, (int)e.Effects, Hwnd(element), folder, paths);
        e.Handled = true;
    }

    /// <summary>Key state (incl. which mouse button) from the last DragEnter/DragOver.</summary>
    private static int _lastKeys;

    private static int OverCore(ComDataObject? data, POINTL pt, int keys, int allowed, IntPtr hwnd, string? folder)
    {
        _lastKeys = keys;
        _overCount++;
        int effect = allowed;

        var helper = Helper();
        if (!_helperEntered && helper != null && data != null && hwnd != IntPtr.Zero)
        {
            var p = new POINT { x = pt.x, y = pt.y };
            helper.DragEnter(hwnd, data, ref p, effect);
            _helperEntered = true;
        }

        if (!string.Equals(folder, _targetFolder, StringComparison.OrdinalIgnoreCase))
        {
            ReleaseTarget(sendLeave: true);
            _targetFolder = folder;
            _target = folder != null && data != null ? GetDropTarget(folder) : null;
            if (_target != null) _target.DragEnter(data!, keys, pt, ref effect);
            else effect = DROPEFFECT_NONE;
        }
        else if (_target != null) _target.DragOver(keys, pt, ref effect);
        else effect = DROPEFFECT_NONE;

        if (_helperEntered && helper != null)
        {
            var p = new POINT { x = pt.x, y = pt.y };
            helper.DragOver(ref p, effect);
        }
        return effect;
    }

    private static int DropCore(ComDataObject? data, POINTL pt, int keysAtDrop, int allowed, int lastEffect, IntPtr hwnd, string? folder,
                                IReadOnlyList<string>? paths)
    {
        // Do NOT send another DragOver here: at drop time the mouse button is already released, and the
        // shell decides "left-drag = just do it / right-drag = show menu" from the key state of the LAST
        // DragOver. Only re-target (with the remembered button state) if the folder changed at the last moment.
        if (!string.Equals(folder, _targetFolder, StringComparison.OrdinalIgnoreCase))
            lastEffect = OverCore(data, pt, _lastKeys, allowed, hwnd, folder);

        if (_helperEntered)
        {
            var p = new POINT { x = pt.x, y = pt.y };
            if (data != null) Helper()?.Drop(data, ref p, lastEffect);
            _helperEntered = false;
        }

        // A plain left-drag copy / move of real files into a real folder goes to MultiDir's queue, so big
        // copies run one after another. The shell already decided copy vs move (same drive, Shift/Ctrl).
        // Right-drag (its menu), links, and anything virtual (zip folders, phones…) stay with the shell.
        if ((_lastKeys & MK_RBUTTON) == 0 && lastEffect is DROPEFFECT_COPY or DROPEFFECT_MOVE
            && folder != null && Directory.Exists(folder)
            && paths is { Count: > 0 } && paths.All(p => File.Exists(p) || Directory.Exists(p)))
        {
            ReleaseTarget(sendLeave: true);
            _targetFolder = null;
            FileOperationQueue.Enqueue(paths, folder, move: lastEffect == DROPEFFECT_MOVE);
            // Never report "moved" to the drag source: it would delete the originals itself before the queue ran.
            return lastEffect == DROPEFFECT_MOVE ? DROPEFFECT_NONE : DROPEFFECT_COPY;
        }

        int effect = DROPEFFECT_NONE;
        if (_target != null && data != null)
        {
            effect = allowed;
            _target.Drop(data, keysAtDrop, pt, ref effect);
        }
        ReleaseTarget(sendLeave: false);
        _targetFolder = null;
        return effect;
    }

    /// <summary>
    /// Automation: runs a complete left- or right-button drop of <paramref name="paths"/> onto
    /// <paramref name="folder"/> through exactly the same code path as a real drag (enter → over → drop
    /// with the button already released), without touching the real mouse.
    /// </summary>
    public static int TestDrop(IReadOnlyList<string> paths, string folder, bool rightButton, int modifierKeys = 0)
    {
        var data = CreateDataObject(paths) ?? throw new InvalidOperationException("無法建立資料物件（路徑是否存在、是否同一個資料夾？）");
        _ownDrag = data;
        try
        {
            int allowed = DROPEFFECT_COPY | DROPEFFECT_MOVE | DROPEFFECT_LINK;
            int keys = (rightButton ? MK_RBUTTON : MK_LBUTTON) | modifierKeys;
            var pt = new POINTL();
            OverCore(data, pt, keys, allowed, IntPtr.Zero, folder);
            int last = OverCore(data, pt, keys, allowed, IntPtr.Zero, folder);
            return DropCore(data, pt, modifierKeys, allowed, last, IntPtr.Zero, folder, paths);
        }
        finally
        {
            _ownDrag = null;
            Marshal.ReleaseComObject(data);
        }
    }

    public const int MK_LBUTTON = 1, MK_RBUTTON = 2, MK_SHIFT = 4, MK_CONTROL = 8;

    private static void ReleaseTarget(bool sendLeave)
    {
        if (_target == null) return;
        if (sendLeave) _target.DragLeave();
        Marshal.ReleaseComObject(_target);
        _target = null;
    }

    private static IOleDropTarget? GetDropTarget(string folder)
    {
        try
        {
            var iidItem = IID_IShellItem;
            if (SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iidItem, out var item) != 0) return null;
            try
            {
                Guid bhid = BHID_SFUIObject, iid = IID_IDropTarget;
                item.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out var obj);
                return obj as IOleDropTarget;
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch { return null; }
    }

    /// <summary>
    /// Gets the real OLE data object. For our own drags we hand the shell its own object
    /// (so it can copy asynchronously); otherwise unwrap WPF's DataObject, falling back to the wrapper.
    /// </summary>
    private static ComDataObject? ComData(IDataObject data)
    {
        if (_ownDrag != null) return _ownDrag;
        try
        {
            var inner = typeof(DataObject).GetField("_innerData", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(data);
            var com = inner?.GetType().GetField("_innerData", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(inner);
            if (com is ComDataObject c) return c;
        }
        catch { }
        return data as ComDataObject;
    }

    private static POINTL ScreenPoint(DragEventArgs e, UIElement element)
    {
        var p = element.PointToScreen(e.GetPosition(element));
        return new POINTL { x = (int)p.X, y = (int)p.Y };
    }

    private static IntPtr Hwnd(UIElement element) =>
        PresentationSource.FromVisual(element) is HwndSource src ? src.Handle : IntPtr.Zero;

    private static IDropTargetHelper? Helper()
    {
        if (_helper != null) return _helper;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("4657278A-411B-11D2-839A-00C04FD918D0"));
            _helper = type != null ? Activator.CreateInstance(type) as IDropTargetHelper : null;
        }
        catch { _helper = null; }
        return _helper;
    }

    // ---------- interop ----------

    private const int DROPEFFECT_NONE = 0, DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2, DROPEFFECT_LINK = 4;

    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid IID_IDropTarget = new("00000122-0000-0000-C000-000000000046");
    private static readonly Guid IID_IDataObject = new("0000010e-0000-0000-C000-000000000046");
    private static readonly Guid BHID_SFUIObject = new("3981e225-f559-11d3-8e3a-00c04f6837d5");
    private static readonly Guid BHID_DataObject = new("b8c0bd9f-ed24-455c-83e6-d5390c4fe8c4");

    [StructLayout(LayoutKind.Sequential)] private struct POINTL { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleDropTarget
    {
        [PreserveSig] int DragEnter(ComDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect);
        [PreserveSig] int DragOver(int grfKeyState, POINTL pt, ref int pdwEffect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(ComDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect);
    }

    [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTargetHelper
    {
        [PreserveSig] int DragEnter(IntPtr hwndTarget, ComDataObject pDataObject, ref POINT ppt, int dwEffect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int DragOver(ref POINT ppt, int dwEffect);
        [PreserveSig] int Drop(ComDataObject pDataObject, ref POINT ppt, int dwEffect);
        [PreserveSig] int Show([MarshalAs(UnmanagedType.Bool)] bool fShow);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint cidl, IntPtr[] rgpidl, out IShellItemArray ppsiItemArray);

    [DllImport("shell32.dll")]
    private static extern int SHDoDragDrop(IntPtr hwnd, ComDataObject pdtobj, IntPtr pdsrc, int dwEffect, out int pdwEffect);
}
