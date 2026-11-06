using System.IO;
using System.Text.Json;

namespace MultiDir.Services;

/// <summary>Recently visited folders for the sidebar's 歷史 section (most recent first, no duplicates). history.json.</summary>
public static class HistoryStore
{
    private const int Max = 50;
    private static string FilePath => Path.Combine(AppPaths.DataDir, "history.json");
    private static List<string>? _items;
    private static Timer? _saveTimer;

    public static IReadOnlyList<string> Items => List;
    private static List<string> List => _items ??= Load();

    public static event Action? Changed;

    public static void Add(string path)
    {
        if (string.IsNullOrEmpty(path)) return; // "本機" is not history
        var key = Normalize(path);
        List.RemoveAll(p => string.Equals(Normalize(p), key, StringComparison.OrdinalIgnoreCase));
        List.Insert(0, path);
        if (List.Count > Max) List.RemoveRange(Max, List.Count - Max);
        Changed?.Invoke();
        ScheduleSave();
    }

    public static void Remove(string path)
    {
        if (List.RemoveAll(p => string.Equals(Normalize(p), Normalize(path), StringComparison.OrdinalIgnoreCase)) == 0) return;
        Changed?.Invoke();
        ScheduleSave();
    }

    public static void Clear()
    {
        List.Clear();
        Changed?.Invoke();
        ScheduleSave();
    }

    private static string Normalize(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;

    private static void ScheduleSave()
    {
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ => Save(), null, 1000, Timeout.Infinite);
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            string json;
            lock (List) json = JsonSerializer.Serialize(List);
            File.WriteAllText(FilePath, json);
        }
        catch { /* non-critical */ }
    }

    private static List<string> Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }
}
