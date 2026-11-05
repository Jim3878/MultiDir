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

    public static ImageSource? GetIcon(FileItem item, bool large)
    {
        string key;
        bool useAttributes;
        if (item.IsDrive) { key = item.FullPath; useAttributes = false; }
        else if (item.IsDirectory) { key = "<dir>"; useAttributes = true; }
        else if (PerFile.Contains(item.Extension)) { key = item.FullPath; useAttributes = false; }
        else { key = item.Extension.Length == 0 ? "<noext>" : item.Extension; useAttributes = true; }

        key = (large ? "L|" : "S|") + key;
        return Cache.GetOrAdd(key, _ => Load(item.IsDirectory && !item.IsDrive ? "folder" : item.FullPath,
            item.IsDirectory, large, useAttributes));
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
