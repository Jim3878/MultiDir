using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MultiDir.Services;

/// <summary>One queued copy / move: some files and folders into one folder.</summary>
public sealed class FileJob
{
    public required bool Move { get; init; }
    public required IReadOnlyList<string> Sources { get; init; }
    public required string Destination { get; init; }
    public bool Running { get; internal set; }

    public string Describe()
    {
        var what = Sources.Count == 1 ? $"「{Path.GetFileName(Sources[0].TrimEnd('\\'))}」" : $"{Sources.Count} 個項目";
        var to = Path.GetFileName(Destination.TrimEnd('\\'));
        return $"{(Move ? "移動" : "複製")} {what} 到「{(to.Length > 0 ? to : Destination)}」";
    }
}

/// <summary>
/// Copies and moves run one after another instead of all at once (several big copies at the same time
/// thrash the disk and all finish late). Each job is done by the Windows shell (IFileOperation), so the
/// progress window, "replace or skip" questions, the recycle bin and Ctrl+Z undo all behave like Explorer.
/// </summary>
public static class FileOperationQueue
{
    private static readonly List<FileJob> Jobs = new(); // [0] is running when Running is set
    private static readonly object Lock = new();
    private static Thread? _worker;
    private static readonly AutoResetEvent Wake = new(false);

    /// <summary>Raised on the UI thread whenever the queue changes.</summary>
    public static event Action? Changed;

    public static IReadOnlyList<FileJob> Snapshot() { lock (Lock) return Jobs.ToList(); }

    /// <summary>Queues a copy / move of <paramref name="sources"/> into <paramref name="destination"/>.</summary>
    public static void Enqueue(IReadOnlyList<string> sources, string destination, bool move)
    {
        // Nothing to do: moving items into the folder they are already in, or a folder into itself.
        var list = sources.Where(s => s.Length > 0
                                      && !(move && SameFolder(Path.GetDirectoryName(s.TrimEnd('\\')), destination))
                                      && !IsInside(destination, s)).ToList();
        if (list.Count == 0 || !Directory.Exists(destination)) return;
        lock (Lock) Jobs.Add(new FileJob { Move = move, Sources = list, Destination = destination });
        EnsureWorker();
        Wake.Set();
        RaiseChanged();
    }

    /// <summary>Drops a job that has not started yet (the running one is cancelled from its progress window).</summary>
    public static void Cancel(FileJob job)
    {
        lock (Lock) { if (job.Running || !Jobs.Remove(job)) return; }
        RaiseChanged();
    }

    private static bool SameFolder(string? a, string b) =>
        a != null && string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    private static bool IsInside(string path, string folder)
    {
        var f = folder.TrimEnd('\\') + "\\";
        return (path.TrimEnd('\\') + "\\").StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    private static void RaiseChanged()
    {
        var app = Application.Current;
        if (app == null) return;
        if (app.Dispatcher.CheckAccess()) Changed?.Invoke();
        else app.Dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    private static void EnsureWorker()
    {
        if (_worker != null) return;
        var owner = Application.Current?.MainWindow is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;
        _worker = new Thread(() => Loop(owner)) { IsBackground = true, Name = "File operations" };
        _worker.SetApartmentState(ApartmentState.STA); // IFileOperation shows its own windows: needs STA
        _worker.Start();
    }

    private static void Loop(IntPtr owner)
    {
        while (true)
        {
            FileJob? job;
            lock (Lock)
            {
                job = Jobs.FirstOrDefault();
                if (job != null) job.Running = true;
            }
            if (job == null) { Wake.WaitOne(); continue; }
            RaiseChanged();
            try { Run(job, owner); }
            catch (Exception ex) { SingleInstance.Trace($"file job failed ({job.Describe()}): {ex.Message}"); }
            lock (Lock) Jobs.Remove(job);
            RaiseChanged();
        }
    }

    private static void Run(FileJob job, IntPtr owner)
    {
        var type = Type.GetTypeFromCLSID(CLSID_FileOperation)!;
        var op = (IFileOperation)Activator.CreateInstance(type)!;
        try
        {
            uint flags = FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR | FOFX_ADDUNDORECORD;
            // Copying into the folder it is already in makes "名稱 - 複製", as Explorer does.
            if (!job.Move && job.Sources.Any(s => SameFolder(Path.GetDirectoryName(s.TrimEnd('\\')), job.Destination)))
                flags |= FOF_RENAMEONCOLLISION;
            // A test instance never shows anything on the user's screen.
            if (App.IsAutomation) flags |= FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI;
            op.SetOperationFlags(flags);
            if (owner != IntPtr.Zero && !App.IsAutomation) op.SetOwnerWindow(owner);
            var dest = Item(job.Destination);
            foreach (var source in job.Sources)
            {
                var item = Item(source);
                if (job.Move) op.MoveItem(item, dest, null, IntPtr.Zero);
                else op.CopyItem(item, dest, null, IntPtr.Zero);
            }
            op.PerformOperations();
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80270000 /* COPYENGINE_E_USER_CANCELLED */ || (uint)ex.HResult == 0x800704C7) { }
        finally { Marshal.ReleaseComObject(op); }
    }

    private static IShellItem Item(string path)
    {
        var iid = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item));
        return item;
    }

    // ---------- interop ----------

    private static readonly Guid CLSID_FileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private const uint FOF_RENAMEONCOLLISION = 0x8, FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMMKDIR = 0x200,
        FOF_NOERRORUI = 0x400, FOFX_ADDUNDORECORD = 0x20000000;

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        uint Advise(IntPtr pfops);
        void Unadvise(uint dwCookie);
        void SetOperationFlags(uint dwOperationFlags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
        void SetProgressDialog(IntPtr popd);
        void SetProperties(IntPtr pproparray);
        void SetOwnerWindow(IntPtr hwndOwner);
        void ApplyPropertiesToItem(IShellItem psiItem);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);
        void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IntPtr pfopsItem);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);
        void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IntPtr pfopsItem);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);
        void DeleteItem(IShellItem psiItem, IntPtr pfopsItem);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);
        void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName,
                     [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, IntPtr pfopsItem);
        void PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
}
