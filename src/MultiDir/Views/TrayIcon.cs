using Forms = System.Windows.Forms;

namespace MultiDir.Views;

/// <summary>
/// Closing the window keeps MultiDir in the tray, so opening a folder later is instant and lands as a tab.
/// The tray menu is the way to really quit.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private bool _hintShown;

    public TrayIcon(Action show, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("開啟 MultiDir", null, (_, _) => show());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("結束", null, (_, _) => exit());
        _icon = new Forms.NotifyIcon
        {
            Text = "MultiDir",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
    }

    /// <summary>The MultiDir icon at the tray's size (16 px at 100 %, larger on high-DPI screens).</summary>
    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/MultiDir.ico")).Stream;
            return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        catch { return System.Drawing.SystemIcons.Application; }
    }

    /// <summary>Tells the user once per run where the window went.</summary>
    public void ShowHiddenHint()
    {
        if (_hintShown) return;
        _hintShown = true;
        _icon.ShowBalloonTip(3000, "MultiDir 仍在執行", "已縮到系統匣，開資料夾會直接變成分頁。要完全結束請在這個圖示按右鍵 →「結束」。", Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
