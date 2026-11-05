using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MultiDir.Models;

namespace MultiDir.Services;

/// <summary>
/// Async shell thumbnails (IShellItemImageFactory) with bounded parallelism and a small LRU cache.
/// The UI never waits: callers show the icon first and get a callback on the UI thread.
/// </summary>
public static class ThumbnailService
{
    public const int Size = 96;
    private const int CacheCapacity = 3000;

    private static readonly SemaphoreSlim Gate = new(4);
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image)>> Map = new();
    private static readonly LinkedList<(string Key, ImageSource Image)> Lru = new();

    public static void Request(FileItem item, Action<ImageSource> onReady)
    {
        if (item.IsDrive) return;
        string key = item.FullPath + "|" + item.Modified?.Ticks;
        if (TryGet(key, out var cached)) { onReady(cached); return; }

        var dispatcher = Application.Current.Dispatcher;
        _ = Task.Run(async () =>
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var img = Load(item.FullPath);
                if (img == null) return;
                Put(key, img);
                _ = dispatcher.BeginInvoke(() => onReady(img));
            }
            catch { /* thumbnail is best-effort */ }
            finally { Gate.Release(); }
        });
    }

    private static bool TryGet(string key, out ImageSource image)
    {
        lock (CacheLock)
        {
            if (Map.TryGetValue(key, out var node))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }
        image = null!;
        return false;
    }

    private static void Put(string key, ImageSource image)
    {
        lock (CacheLock)
        {
            if (Map.ContainsKey(key)) return;
            Map[key] = Lru.AddFirst((key, image));
            while (Map.Count > CacheCapacity)
            {
                var last = Lru.Last!;
                Lru.RemoveLast();
                Map.Remove(last.Value.Key);
            }
        }
    }

    private static ImageSource? Load(string path)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null)
            return null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new SIZE { cx = Size, cy = Size }, SIIGBF_RESIZETOFIT, out hbm) != 0 || hbm == IntPtr.Zero)
                return null;
            return FromHBitmap(hbm);
        }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Copies a 32bpp shell HBITMAP keeping alpha (CreateBitmapSourceFromHBitmap drops it).</summary>
    private static BitmapSource? FromHBitmap(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[w * h * 4];
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hbm, 0, (uint)h, pixels, ref bmi, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }

        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { hasAlpha = true; break; }
        if (!hasAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
        src.Freeze();
        return src;
    }

    private const int SIIGBF_RESIZETOFIT = 0x0;

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int c, out BITMAP pv);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
}
