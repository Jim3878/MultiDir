using System.IO;
using System.Text.Json;

namespace MultiDir.Services;

/// <summary>Per-tab state the dock layout does not know about. Keyed by the tab's ContentId.</summary>
public sealed class TabState
{
    public string Path { get; set; } = "";
    public string[] Back { get; set; } = [];
    public string[] Forward { get; set; } = [];
}

public sealed class SessionState
{
    public Dictionary<string, TabState> Tabs { get; set; } = new();
    public string? ActiveTab { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// Full session restore: the AvalonDock layout (splits, tab order, floating windows) goes to layout.xml,
/// everything else (each tab's folder + history, window placement) to session.json.
/// </summary>
public static class SessionStore
{
    public static string LayoutPath => Path.Combine(AppPaths.DataDir, "layout.xml");
    private static string StatePath => Path.Combine(AppPaths.DataDir, "session.json");

    public static SessionState? LoadState()
    {
        try
        {
            if (File.Exists(StatePath) && File.Exists(LayoutPath))
                return JsonSerializer.Deserialize<SessionState>(File.ReadAllText(StatePath));
        }
        catch { /* corrupt → default layout */ }
        return null;
    }

    /// <summary>Writes both files via temp files, so a crash mid-save never leaves a half-written session.</summary>
    public static void Save(SessionState state, Action<TextWriter> writeLayout)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            using (var w = new StreamWriter(LayoutPath + ".tmp")) writeLayout(w);
            File.WriteAllText(StatePath + ".tmp", JsonSerializer.Serialize(state));
            File.Move(LayoutPath + ".tmp", LayoutPath, overwrite: true);
            File.Move(StatePath + ".tmp", StatePath, overwrite: true);
        }
        catch { /* non-critical: next save retries */ }
    }
}
