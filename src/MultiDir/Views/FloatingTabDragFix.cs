using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace MultiDir.Views;

/// <summary>
/// AvalonDock ignores a tab drag inside a floating window that holds a single document, so such a tab
/// could never be dragged back into the main window. We turn that gesture into a title-bar drag of the
/// floating window (WM_NCLBUTTONDOWN/HTCAPTION): AvalonDock then shows its normal dock overlay and the
/// tab can be dropped onto any pane, exactly like dragging the floating window's title bar.
/// </summary>
public static class FloatingTabDragFix
{
    private static Point? _downPos;
    private static LayoutDocumentTabItem? _downTab;

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(LayoutDocumentTabItem), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnDown), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(LayoutDocumentTabItem), UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(OnMove), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(LayoutDocumentTabItem), UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler((_, _) => Reset()), handledEventsToo: true);
    }

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        var tab = (LayoutDocumentTabItem)sender;
        if (!IsSoleDocumentInFloatingWindow(tab)) { Reset(); return; }
        _downTab = tab;
        _downPos = tab.PointToScreen(e.GetPosition(tab));
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        if (_downTab == null || _downPos == null || sender != _downTab) return;
        if (e.LeftButton != MouseButtonState.Pressed) { Reset(); return; }

        var now = _downTab.PointToScreen(e.GetPosition(_downTab));
        if (Math.Abs(now.X - _downPos.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _downPos.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        // Window.GetWindow would return the MAIN window (AvalonDock keeps floating content logically parented
        // to the DockingManager); the HWND that actually hosts the tab is the floating window's.
        var source = PresentationSource.FromVisual(_downTab) as HwndSource;
        Reset();
        if (source == null) return;
        var hwnd = source.Handle;
        e.Handled = true;
        Mouse.Capture(null);
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    private static bool IsSoleDocumentInFloatingWindow(LayoutDocumentTabItem tab)
    {
        if (tab.Model is not LayoutDocument doc) return false;
        var floating = doc.FindParent<LayoutDocumentFloatingWindow>();
        if (floating == null) return false;
        return floating.Descendents().OfType<LayoutDocument>().Count() == 1;
    }

    private static void Reset()
    {
        _downTab = null;
        _downPos = null;
    }

    private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
