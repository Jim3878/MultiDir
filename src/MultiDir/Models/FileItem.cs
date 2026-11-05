using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using MultiDir.Services;

namespace MultiDir.Models;

/// <summary>One entry in a folder listing (file, folder or drive).</summary>
public sealed class FileItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private bool _thumbnailRequested;

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsDrive { get; init; }
    public DateTime? Modified { get; init; }
    public long? Size { get; init; }

    public string Extension => IsDirectory ? "" : Path.GetExtension(Name);

    public string TypeName => IsDrive ? "磁碟" : IsDirectory ? "資料夾" : ShellIcons.GetTypeName(Extension);

    public string SizeText => Size is { } s && !IsDirectory ? FormatSize(s) : "";

    public string ModifiedText => Modified?.ToString("yyyy/MM/dd HH:mm") ?? "";

    /// <summary>16px shell icon, cached per extension.</summary>
    public ImageSource? SmallIcon => ShellIcons.GetIcon(this, large: false);

    /// <summary>
    /// Large-mode image. Returns the 32px icon until the async thumbnail arrives.
    /// Only realized (visible) items ever touch this, so only they get thumbnails.
    /// </summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnail != null) return _thumbnail;
            if (!_thumbnailRequested)
            {
                _thumbnailRequested = true;
                ThumbnailService.Request(this, img =>
                {
                    _thumbnail = img;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
                });
            }
            return ShellIcons.GetIcon(this, large: true);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}
