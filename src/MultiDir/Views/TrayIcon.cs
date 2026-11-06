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
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
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
