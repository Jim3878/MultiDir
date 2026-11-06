using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace MultiDir.Services;

/// <summary>A newer release on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string PageUrl);

/// <summary>
/// Update check against the GitHub releases of Jim3878/MultiDir. Only builds made by the release workflow carry a
/// real version number; development builds (version 0.0.0) never check, so tools/run.ps1 builds are left alone.
/// Installing = download the release zip, unpack it next to us, quit, and let a small script copy it over this
/// folder and start the new version.
/// </summary>
public static class Updater
{
    private const string Repo = "Jim3878/MultiDir";
    private const string AssetName = "MultiDir-win-x64.zip";

    public static Version Current { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Development builds have no version and never update themselves.</summary>
    public static bool Enabled => Current > new Version(0, 0, 0, 0) && !App.IsAutomation;

    public static string CurrentText => $"v{Current.Major}.{Current.Minor}.{Current.Build}";

    private static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"MultiDir/{CurrentText}");
        return http;
    }

    /// <summary>The latest release if it is newer than this build; null when up to date or offline.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        if (!Enabled) return null;
        try
        {
            using var http = Http();
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            if (Normalize(latest) <= Normalize(Current)) return null;
            var asset = root.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString() == AssetName);
            if (asset.ValueKind != JsonValueKind.Object) return null;
            return new UpdateInfo(latest, tag, asset.GetProperty("browser_download_url").GetString()!,
                                  root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases");
        }
        catch (Exception ex)
        {
            SingleInstance.Trace("update check failed: " + ex.Message);
            return null;
        }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>
    /// Downloads and unpacks <paramref name="update"/>, then starts the script that replaces this folder once we
    /// have exited. The caller quits right after (the session is saved on the way out).
    /// </summary>
    public static async Task PrepareAsync(UpdateInfo update, IProgress<string>? progress = null)
    {
        var work = Path.Combine(Path.GetTempPath(), "MultiDir-update");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        var zip = Path.Combine(work, AssetName);
        var staged = Path.Combine(work, "new");

        progress?.Report("下載中…");
        using (var http = Http())
        using (var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(zip);
            await response.Content.CopyToAsync(file);
        }
        progress?.Report("解壓縮中…");
        ZipFile.ExtractToDirectory(zip, staged);
        if (!File.Exists(Path.Combine(staged, "MultiDir.exe"))) throw new InvalidDataException("更新檔裡找不到 MultiDir.exe");

        // ASCII only: Windows PowerShell 5.1 reads a BOM-less script in the ANSI code page.
        var install = AppContext.BaseDirectory.TrimEnd('\\');
        var script = Path.Combine(work, "apply-update.ps1");
        var log = Path.Combine(work, "apply-update.log");
        var sb = new StringBuilder();
        sb.AppendLine("param([int]$ProcessId, [string]$Source, [string]$Target, [string]$Log)");
        sb.AppendLine("try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue } catch {}");
        sb.AppendLine("Start-Sleep -Milliseconds 300");
        sb.AppendLine("robocopy $Source $Target /E /R:5 /W:1 /NFL /NDL /NP > $Log");
        sb.AppendLine("Start-Process -FilePath (Join-Path $Target 'MultiDir.exe')");
        File.WriteAllText(script, sb.ToString(), Encoding.ASCII);

        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList =
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                "-ProcessId", Environment.ProcessId.ToString(), "-Source", staged, "-Target", install, "-Log", log,
            },
            UseShellExecute = false, CreateNoWindow = true,
        });
    }
}
