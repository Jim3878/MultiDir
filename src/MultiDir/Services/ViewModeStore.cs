using System.IO;
using System.Text.Json;

namespace MultiDir.Services;

/// <summary>Values are stored as numbers in viewmodes.json: only ever append.</summary>
public enum ViewMode { Large, List, Small, ExtraLarge }

/// <summary>Remembers the view mode per folder (decision #8). Stored in our own settings, never desktop.ini.</summary>
public static class ViewModeStore
{
    private static string FilePath => Path.Combine(AppPaths.DataDir, "viewmodes.json");

    // Loaded on first use, so AppPaths.DataDir can be redirected first (automation instances).
    private static Dictionary<string, ViewMode> Modes => _modes ??= LoadFile();
    private static Dictionary<string, ViewMode>? _modes;
    private static Timer? _saveTimer;

    public static ViewMode DefaultMode { get; set; } = ViewMode.List;

    public static ViewMode Get(string folder) =>
        Modes.TryGetValue(Normalize(folder), out var m) ? m : DefaultMode;

    public static void Set(string folder, ViewMode mode)
    {
        lock (Modes) Modes[Normalize(folder)] = mode;
        // Debounce writes; switching modes must stay instant.
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ => Save(), null, 1000, Timeout.Infinite);
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string json;
            lock (Modes) json = JsonSerializer.Serialize(Modes);
            File.WriteAllText(FilePath, json);
        }
        catch { /* non-critical */ }
    }

    private static string Normalize(string folder) => folder.TrimEnd('\\').ToUpperInvariant();

    private static Dictionary<string, ViewMode> LoadFile()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, ViewMode>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* corrupt file → start fresh */ }
        return new();
    }
}

public enum SortKey { Name, Modified, Type, Size }

/// <summary>Remembers the list sort per folder (like Explorer), next to the view mode. sorts.json.</summary>
public static class SortStore
{
    private static string FilePath => Path.Combine(AppPaths.DataDir, "sorts.json");
    private static Dictionary<string, string> Sorts => _sorts ??= LoadFile();
    private static Dictionary<string, string>? _sorts;
    private static Timer? _saveTimer;

    public static (SortKey Key, bool Descending) Get(string folder)
    {
        lock (Sorts)
            if (Sorts.TryGetValue(Normalize(folder), out var s) && s.Split('|') is [var k, var d] && Enum.TryParse<SortKey>(k, out var key))
                return (key, d == "desc");
        return (SortKey.Name, false);
    }

    public static void Set(string folder, SortKey key, bool descending)
    {
        lock (Sorts)
        {
            if (key == SortKey.Name && !descending) Sorts.Remove(Normalize(folder)); // default: nothing to remember
            else Sorts[Normalize(folder)] = $"{key}|{(descending ? "desc" : "asc")}";
        }
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ => Save(), null, 1000, Timeout.Infinite);
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string json;
            lock (Sorts) json = JsonSerializer.Serialize(Sorts);
            File.WriteAllText(FilePath, json);
        }
        catch { /* non-critical */ }
    }

    private static string Normalize(string folder) => folder.TrimEnd('\\').ToUpperInvariant();

    private static Dictionary<string, string> LoadFile()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }
}
