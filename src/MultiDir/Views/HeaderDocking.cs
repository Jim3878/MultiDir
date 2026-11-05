using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace MultiDir.Views;

/// <summary>
/// Browser-style re-docking: while a floating window is being dragged, hovering any pane's
/// <b>tab strip or pane toolbar</b> highlights it, and releasing there moves the floating tab(s) into that pane.
/// AvalonDock's own (small) cross targets keep working; this only adds a much bigger, obvious drop zone.
/// </summary>
public sealed class HeaderDocking
{
    private readonly DockingManager _dock;
    private readonly FrameworkElement _highlight;
    private readonly HashSet<IntPtr> _hooked = new();
    private bool _moving;

    public HeaderDocking(DockingManager dock, FrameworkElement highlight)
    {
        _dock = dock;
        _highlight = highlight;
        EventManager.RegisterClassHandler(typeof(LayoutDocumentFloatingWindowControl), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => Attach((Window)s)));
    }

    /// <summary>The pane header (tab strip + pane toolbar) under a screen point, or null.</summary>
    public (LayoutDocumentPane Pane, Rect Bounds)? ZoneAt(Point screenPx)
    {
        foreach (var control in Descendants<LayoutDocumentPaneControl>(_dock))
        {
            if (!control.IsVisible || control.Model is not LayoutDocumentPane pane) continue;
            var local = control.PointFromScreen(screenPx);
            double headerBottom = HeaderBottom(control);
            if (local.X < 0 || local.Y < 0 || local.X > control.ActualWidth || local.Y > headerBottom) continue;
            var topLeft = control.TranslatePoint(new Point(0, 0), _dock);
            return (pane, new Rect(topLeft.X, topLeft.Y, control.ActualWidth, headerBottom));
        }
        return null;
    }

    /// <summary>Bottom of the tab strip plus the selected tab's FolderView toolbar, in pane coordinates.</summary>
    private static double HeaderBottom(LayoutDocumentPaneControl control)
    {
        double bottom = 30;
        if (Descendants<DocumentPaneTabPanel>(control).FirstOrDefault() is { } tabs)
            bottom = tabs.TranslatePoint(new Point(0, tabs.ActualHeight), control).Y;
        var view = Descendants<FolderView>(control).FirstOrDefault(v => v.IsVisible);
        return bottom + (view?.ToolbarHeight ?? 32);
    }

    // ---------- floating window move loop ----------

    private void Attach(Window window)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source || !_hooked.Add(source.Handle)) return;
        source.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            switch (msg)
            {
                case WM_ENTERSIZEMOVE:
                    _moving = true;
                    break;
                case WM_MOVING:
                case WM_MOVE:
                    if (_moving) ShowHighlight(ZoneAt(Cursor()));
                    break;
                case WM_EXITSIZEMOVE:
                    if (!_moving) break;
                    _moving = false;
                    var zone = ZoneAt(Cursor());
                    ShowHighlight(null);
                    if (zone != null && window is LayoutFloatingWindowControl { Model: LayoutFloatingWindow model })
                    {
                        var docs = model.Descendents().OfType<LayoutDocument>().ToList();
                        // Let AvalonDock finish its own drop first; if it already docked the tab, do nothing.
                        window.Dispatcher.BeginInvoke(() => DockInto(docs, zone.Value.Pane));
                    }
                    break;
            }
            return IntPtr.Zero;
        });
    }

    private void DockInto(List<LayoutDocument> docs, LayoutDocumentPane pane)
    {
        if (pane.Root != _dock.Layout) return;
        LayoutDocument? last = null;
        foreach (var doc in docs.Where(d => d.FindParent<LayoutDocumentFloatingWindow>() != null))
        {
            (doc.Parent as ILayoutContainer)?.RemoveChild(doc);
            pane.Children.Add(doc);
            last = doc;
        }
        if (last == null) return;
        _dock.Layout.CollectGarbage(); // closes the now-empty floating window
        last.IsActive = true;
    }

    private void ShowHighlight((LayoutDocumentPane Pane, Rect Bounds)? zone)
    {
        if (zone == null) { _highlight.Visibility = Visibility.Collapsed; return; }
        var r = zone.Value.Bounds;
        Canvas.SetLeft(_highlight, r.X);
        Canvas.SetTop(_highlight, r.Y);
        _highlight.Width = r.Width;
        _highlight.Height = r.Height;
        _highlight.Visibility = Visibility.Visible;
    }

    private static Point Cursor()
    {
        GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    private const int WM_MOVE = 0x0003, WM_MOVING = 0x0216, WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
}
