using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using MultiDir.Models;

namespace MultiDir.Services;

/// <summary>
/// One entry on the bookmark bar: a bookmarked folder, or (Chrome-style) a bookmark folder holding more entries.
/// </summary>
public sealed class Bookmark
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>Non-null = this is a bookmark folder (may be empty). Older bookmarks.json files simply have none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Bookmark>? Children { get; set; }

    [JsonIgnore] public bool IsFolder => Children != null;

    [JsonIgnore]
    public ImageSource? Icon => IsFolder ? null : ShellIcons.GetIcon(new FileItem
    {
        Name = Name, FullPath = Path, IsDirectory = true, IsDrive = Path.Length <= 3 && Path.EndsWith(":\\"),
    }, large: false);

    [JsonIgnore] public bool Exists => !IsFolder && Directory.Exists(Path);

    /// <summary>All bookmarked folders inside (recursively), for "全部在新分頁開啟".</summary>
    public IEnumerable<Bookmark> AllBookmarks() =>
        Children?.SelectMany(c => c.IsFolder ? c.AllBookmarks() : [c]) ?? [];

    public bool Contains(Bookmark other) => Children?.Any(c => c == other || c.Contains(other)) == true;
}

/// <summary>Chrome-style bookmark bar contents (decision: 我的最愛放工具列). Stored in bookmarks.json.</summary>
public static class BookmarkStore
{
    private static string FilePath => System.IO.Path.Combine(AppPaths.DataDir, "bookmarks.json");
    private static List<Bookmark>? _items;

    /// <summary>Top level of the bar. Loaded on first use, so automation instances can redirect AppPaths.DataDir first.</summary>
    public static List<Bookmark> Items => _items ??= Load();

    /// <summary>Raised after every change (the bar and the ☆ buttons refresh from it).</summary>
    public static event Action? Changed;

    /// <summary>Raised after a bookmark was added (anywhere: ☆, drag & drop, a dragged tab…) — the bar flashes it.</summary>
    public static event Action<Bookmark>? Added;

    public static bool Contains(string path) => Find(path) != null;

    /// <summary>The bookmark for a folder path, anywhere in the tree.</summary>
    public static Bookmark? Find(string path) =>
        All(Items).FirstOrDefault(b => !b.IsFolder && string.Equals(Normalize(b.Path), Normalize(path), StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a folder (no duplicates) to <paramref name="parent"/> (null = the bar) at <paramref name="index"/> (-1 = end).</summary>
    public static void Add(string path, int index = -1, Bookmark? parent = null)
    {
        if (path.Length == 0 || Contains(path)) return;
        var trimmed = path.TrimEnd('\\');
        var name = System.IO.Path.GetFileName(trimmed);
        if (name.Length == 0) name = trimmed; // drive root: "C:"
        var bookmark = new Bookmark { Name = name, Path = path };
        Insert(bookmark, ListOf(parent), index);
        Save();
        Added?.Invoke(bookmark);
    }

    public static Bookmark AddFolder(string name, int index = -1, Bookmark? parent = null)
    {
        var folder = new Bookmark { Name = name, Children = new() };
        Insert(folder, ListOf(parent), index);
        Save();
        return folder;
    }

    /// <summary>Removes a bookmark or a whole bookmark folder.</summary>
    public static void Remove(Bookmark bookmark)
    {
        if (ParentList(bookmark)?.Remove(bookmark) == true) Save();
    }

    public static void Rename(Bookmark bookmark, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == bookmark.Name) return;
        bookmark.Name = name;
        Save();
    }

    /// <summary>
    /// Moves an entry into <paramref name="parent"/> (null = the bar) at <paramref name="index"/> (-1 = end).
    /// Index is in terms of the target list before removal, like a drop position.
    /// </summary>
    public static void Move(Bookmark bookmark, int index, Bookmark? parent = null)
    {
        if (parent == bookmark || (parent != null && bookmark.Contains(parent))) return; // never into itself
        var from = ParentList(bookmark);
        var to = ListOf(parent);
        if (from == null) return;
        int old = from.IndexOf(bookmark);
        if (from == to && index > old) index--;
        from.RemoveAt(old);
        Insert(bookmark, to, index);
        Save();
    }

    /// <summary>The list that holds <paramref name="bookmark"/> (the bar or a folder's children).</summary>
    public static List<Bookmark>? ParentList(Bookmark bookmark) =>
        Items.Contains(bookmark) ? Items : All(Items).FirstOrDefault(f => f.Children?.Contains(bookmark) == true)?.Children;

    public static Bookmark? ParentFolder(Bookmark bookmark) =>
        All(Items).FirstOrDefault(f => f.Children?.Contains(bookmark) == true);

    private static List<Bookmark> ListOf(Bookmark? parent) => parent?.Children ?? Items;

    private static void Insert(Bookmark b, List<Bookmark> list, int index)
    {
        if (index < 0 || index > list.Count) list.Add(b); else list.Insert(index, b);
    }

    private static IEnumerable<Bookmark> All(IEnumerable<Bookmark> list) =>
        list.SelectMany(b => b.IsFolder ? All(b.Children!).Prepend(b) : [b]);

    // Readable file (Chinese names stay Chinese), in case someone edits or backs it up by hand.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Normalize(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;

    private static void Save()
    {
        Changed?.Invoke();
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items, JsonOptions));
        }
        catch { /* non-critical */ }
    }

    private static List<Bookmark> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<Bookmark>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* corrupt → empty bar */ }
        return new();
    }
}
