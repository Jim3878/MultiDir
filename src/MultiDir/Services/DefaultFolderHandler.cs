using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MultiDir.Services;

/// <summary>
/// Makes MultiDir the program that opens folders and drives (double-click on the desktop, "open folder"
/// from other apps…). Per-user only (HKCU\Software\Classes), fully reversible, no admin rights needed.
/// Virtual folders (Control Panel, Recycle Bin, This PC via Win+E) stay with Explorer.
/// </summary>
public static class DefaultFolderHandler
{
    private const string Marker = "MultiDir";
    private const string PrevDefault = "MultiDirPreviousDefault";

    private static string ShellKey(string openKey) => openKey[..openKey.LastIndexOf('\\')];
    private static readonly string[] Keys = [@"Software\Classes\Directory\shell\open", @"Software\Classes\Drive\shell\open"];

    public static bool IsRegistered
    {
        get
        {
            // Ours AND the default verb points at it (a registration from an older build only had the first part).
            using var key = Registry.CurrentUser.OpenSubKey(Keys[0]);
            using var shell = Registry.CurrentUser.OpenSubKey(ShellKey(Keys[0]));
            return key?.GetValue(Marker) != null && shell?.GetValue("") as string == "open";
        }
    }

    public static void Register()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("找不到 MultiDir 執行檔路徑");
        foreach (var path in Keys)
        {
            // Windows sets HKCR\Directory\shell (and Drive\shell) default verb to "none", which makes a double-click
            // fall through to Folder\shell\open (Explorer). Make "open" the default verb for this user.
            using (var shell = Registry.CurrentUser.CreateSubKey(ShellKey(path)))
            {
                if (shell.GetValue(PrevDefault) == null) shell.SetValue(PrevDefault, shell.GetValue("") as string ?? "");
                shell.SetValue("", "open");
            }
            using var open = Registry.CurrentUser.CreateSubKey(path);
            open.SetValue(Marker, exe); // marks the key as ours, so Unregister never deletes someone else's
            using var command = open.CreateSubKey("command");
            command.SetValue("", $"\"{exe}\" \"%1\"");
        }
        Notify();
    }

    public static void Unregister()
    {
        foreach (var path in Keys)
        {
            bool ours;
            using (var key = Registry.CurrentUser.OpenSubKey(path)) ours = key?.GetValue(Marker) != null;
            if (!ours) continue;
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            // Put the user's previous default verb back (usually: none of their own → Windows' "none").
            using var shell = Registry.CurrentUser.OpenSubKey(ShellKey(path), writable: true);
            if (shell?.GetValue(PrevDefault) is string prev)
            {
                if (prev.Length == 0) shell.DeleteValue("", throwOnMissingValue: false); else shell.SetValue("", prev);
                shell.DeleteValue(PrevDefault, throwOnMissingValue: false);
            }
        }
        Notify();
    }

    /// <summary>
    /// Cleans a folder argument from the shell: "%1" for a drive arrives as <c>C:"</c> (the quote escapes),
    /// and anything that is not a real folder is handed back to Explorer.
    /// </summary>
    public static string? NormalizeArgument(string arg)
    {
        arg = arg.Trim().Trim('"');
        if (arg.Length == 2 && arg[1] == ':') arg += "\\";
        return Directory.Exists(arg) ? arg : null;
    }

    private static void Notify() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
}
