using System.IO;
using System.Text.Json;

namespace MultiDir.Services;

public enum ViewMode { Large, List, Small }

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
