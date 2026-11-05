using System.IO;

namespace MultiDir.Services;

/// <summary>Where MultiDir keeps its settings and logs. Automation instances redirect this to a temp folder.</summary>
public static class AppPaths
{
    public static string DataDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiDir");
}
