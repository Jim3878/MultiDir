using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using MultiDir.Models;

namespace MultiDir.Services;

/// <summary>One folder on the bookmark bar.</summary>
public sealed class Bookmark
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    [JsonIgnore]
    public ImageSource? Icon => ShellIcons.GetIcon(new FileItem
    {
        Name = Name, FullPath = Path, IsDirectory = true, IsDrive = Path.Length <= 3 && Path.EndsWith(":\\"),
    }, large: false);

    [JsonIgnore]
    public bool Exists => Directory.Exists(Path);
}

/// <summary>Chrome-style bookmark bar contents (decision: 我的最愛放工具列). Stored in bookmarks.json.</summary>
public static class BookmarkStore
{
    private static string FilePath => System.IO.Path.Combine(AppPaths.DataDir, "bookmarks.json");
    private static ObservableCollection<Bookmark>? _items;

    /// <summary>Loaded on first use, so automation instances can redirect AppPaths.DataDir first.</summary>
    public static ObservableCollection<Bookmark> Items => _items ??= Load();

    public static event Action? Changed;

    public static bool Contains(string path) => Find(path) != null;

    public static Bookmark? Find(string path) =>
        Items.FirstOrDefault(b => string.Equals(Normalize(b.Path), Normalize(path), StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a folder (no duplicates). <paramref name="index"/> = position on the bar, default the end.</summary>
    public static void Add(string path, int index = -1)
    {
        if (path.Length == 0 || Contains(path)) return;
        var trimmed = path.TrimEnd('\\');
        var name = System.IO.Path.GetFileName(trimmed);
        if (name.Length == 0) name = trimmed; // drive root: "C:"
        var bookmark = new Bookmark { Name = name, Path = path };
        if (index < 0 || index > Items.Count) Items.Add(bookmark); else Items.Insert(index, bookmark);
        Save();
    }

    public static void Remove(Bookmark bookmark)
    {
        if (Items.Remove(bookmark)) Save();
    }

    public static void Rename(Bookmark bookmark, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == bookmark.Name) return;
        bookmark.Name = name;
        int i = Items.IndexOf(bookmark);
        if (i >= 0) { Items.RemoveAt(i); Items.Insert(i, bookmark); } // refresh the bar
        Save();
    }

    public static void Move(Bookmark bookmark, int newIndex)
    {
        int old = Items.IndexOf(bookmark);
        if (old < 0) return;
        newIndex = Math.Clamp(newIndex > old ? newIndex - 1 : newIndex, 0, Items.Count - 1);
        if (newIndex == old) return;
        Items.Move(old, newIndex);
        Save();
    }

    private static string Normalize(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;

    private static void Save()
    {
        Changed?.Invoke();
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items.ToList()));
        }
        catch { /* non-critical */ }
    }

    private static ObservableCollection<Bookmark> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return new ObservableCollection<Bookmark>(JsonSerializer.Deserialize<List<Bookmark>>(File.ReadAllText(FilePath)) ?? new());
        }
        catch { /* corrupt → empty bar */ }
        return new ObservableCollection<Bookmark>();
    }
}
