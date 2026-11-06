using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MultiDir.Models;

namespace MultiDir.Services;

/// <summary>System icons via SHGetFileInfo, cached per extension so huge folders stay cheap.</summary>
public static class ShellIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase);

    // Extensions whose icon is per-file rather than per-type.
    private static readonly HashSet<string> PerFile = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".ico", ".url", ".appref-ms" };

    /// <summary>The icon, loaded right away if it is not cached yet (fine for a handful of icons).</summary>
    public static ImageSource? GetIcon(FileItem item, bool large)
    {
        var (key, path, useAttributes) = KeyOf(item, large);
        return Cache.GetOrAdd(key, _ => Load(path, item.IsDirectory, large, useAttributes));
    }

    /// <summary>
    /// For folder listings: a cached icon comes back at once; otherwise it is loaded on a background thread
    /// (SHGetFileInfo can take several ms per file, e.g. resolving a .lnk) and <paramref name="whenReady"/> runs on
    /// the UI thread with it. Meanwhile returns the generic icon for the type if that is already known, else null.
    /// </summary>
    public static ImageSource? GetIconAsync(FileItem item, bool large, Action<ImageSource?> whenReady)
    {
        var (key, path, useAttributes) = KeyOf(item, large);
        if (Cache.TryGetValue(key, out var icon)) return icon;
        lock (Pending)
        {
            if (Pending.TryGetValue(key, out var waiting)) waiting.Add(whenReady);
            else
            {
                Pending[key] = [whenReady];
                Queue.Add((key, path, item.IsDirectory, large, useAttributes));
                EnsureWorker();
            }
        }
        // A per-file icon (.exe, .lnk…) can show the type's generic icon until its own arrives.
        return !item.IsDirectory && PerFile.Contains(item.Extension)
               && Cache.TryGetValue((large ? "L|" : "S|") + item.Extension, out var generic) ? generic : null;
    }

    private static (string Key, string Path, bool UseAttributes) KeyOf(FileItem item, bool large)
    {
        string key;
        bool useAttributes;
        if (item.IsDrive) { key = item.FullPath; useAttributes = false; }
        else if (item.IsDirectory) { key = "<dir>"; useAttributes = true; }
        else if (PerFile.Contains(item.Extension)) { key = item.FullPath; useAttributes = false; }
        else { key = item.Extension.Length == 0 ? "<noext>" : item.Extension; useAttributes = true; }
        return ((large ? "L|" : "S|") + key, item.IsDirectory && !item.IsDrive ? "folder" : item.FullPath, useAttributes);
    }

    // ---------- background loading ----------

    private static readonly Dictionary<string, List<Action<ImageSource?>>> Pending = new();
    private static readonly System.Collections.Concurrent.BlockingCollection<(string Key, string Path, bool IsDir, bool Large, bool UseAttributes)> Queue = new();
    private static Thread? _worker;

    private static void EnsureWorker()
    {
        if (_worker != null) return;
        var dispatcher = Application.Current.Dispatcher;
        _worker = new Thread(() =>
        {
            foreach (var job in Queue.GetConsumingEnumerable())
            {
                var icon = Cache.GetOrAdd(job.Key, _ => Load(job.Path, job.IsDir, job.Large, job.UseAttributes));
                List<Action<ImageSource?>> callbacks;
                lock (Pending) { callbacks = Pending.Remove(job.Key, out var c) ? c : []; }
                // Background priority: icons fill in after the frame is on screen, never delaying it.
                dispatcher.BeginInvoke(() => { foreach (var cb in callbacks) cb(icon); }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }) { IsBackground = true, Name = "Shell icons" };
        _worker.SetApartmentState(ApartmentState.STA); // the shell expects COM to be initialized
        _worker.Start();
    }

    public static string GetTypeName(string extension)
    {
        if (extension.Length == 0) return "檔案";
        return TypeNames.GetOrAdd(extension, ext =>
        {
            var info = new SHFILEINFO();
            SHGetFileInfo("x" + ext, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf(info),
                SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
            return string.IsNullOrEmpty(info.szTypeName) ? ext.TrimStart('.').ToUpperInvariant() : info.szTypeName;
        });
    }

    private static ImageSource? Load(string path, bool isDir, bool large, bool useAttributes)
    {
        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON);
        if (useAttributes) flags |= SHGFI_USEFILEATTRIBUTES;
        uint attr = isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        if (SHGetFileInfo(path, attr, ref info, (uint)Marshal.SizeOf(info), flags) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DestroyIcon(info.hIcon); }
    }

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_SMALLICON = 0x1,
        SHGFI_USEFILEATTRIBUTES = 0x10, SHGFI_TYPENAME = 0x400;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
