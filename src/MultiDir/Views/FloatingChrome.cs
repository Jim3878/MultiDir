using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace MultiDir.Views;

/// <summary>
/// Chrome-style floating windows: no title bar, just the tab strip. Drag the empty part of the strip to move the
/// window, double-click it to maximize/restore; ─ □ ✕ sit at the right end of the strip.
/// </summary>
public static class FloatingChrome
{
    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(LayoutDocumentFloatingWindowControl), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => StripTitleBar((LayoutDocumentFloatingWindowControl)s)));
        EventManager.RegisterClassHandler(typeof(LayoutDocumentPaneControl), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => AttachPane((LayoutDocumentPaneControl)s)));
        EventManager.RegisterClassHandler(typeof(LayoutDocumentPaneControl), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(Pane_PreviewMouseLeftButtonDown));
    }

    // ---------- no title bar ----------

    private static void StripTitleBar(LayoutDocumentFloatingWindowControl window)
    {
        if (window.Template?.FindName("Header", window) is FrameworkElement header)
        {
            header.Visibility = Visibility.Collapsed;
            if (VisualTreeHelper.GetParent(header) is Grid grid && Grid.GetRow(header) is var row && row < grid.RowDefinitions.Count)
            {
                // The theme gives the title row a fixed minimum height, so collapsing the header alone leaves an empty band.
                grid.RowDefinitions[row].MinHeight = 0;
                grid.RowDefinitions[row].Height = new GridLength(0);
            }
        }
        // The theme's WindowChrome reserves a caption strip at the top; without a title bar that would swallow
        // clicks on the tabs. Keep only the resize border.
        if (WindowChrome.GetWindowChrome(window) is { } chrome && chrome.CaptionHeight != 0)
        {
            var copy = (WindowChrome)chrome.Clone();
            copy.CaptionHeight = 0;
            WindowChrome.SetWindowChrome(window, copy);
        }
    }

    private static LayoutDocumentFloatingWindowControl? WindowOf(LayoutDocumentPaneControl pane) =>
        pane.Model is LayoutDocumentPane model && model.FindParent<LayoutDocumentFloatingWindow>() is { } fw
            ? Application.Current.Windows.OfType<LayoutDocumentFloatingWindowControl>().FirstOrDefault(w => w.Model == fw)
            : null;

    // ---------- drag the empty strip to move, double-click to maximize ----------

    private static void Pane_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pane = (LayoutDocumentPaneControl)sender;
        if (WindowOf(pane) is not { } window || !OnEmptyStrip(pane, e)) return;
        e.Handled = true;
        if (e.ClickCount == 2) { ToggleMaximize(window); return; }
        if (PresentationSource.FromVisual(window) is not HwndSource src) return;
        Mouse.Capture(null);
        // Hand the press to Windows as a title-bar press: it runs the normal move loop
        // (snap, multi-monitor, and our re-dock / bookmark-bar drop zones all keep working).
        ReleaseCapture();
        SendMessage(src.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    /// <summary>On the tab strip row, but not on a tab or one of our buttons.</summary>
    private static bool OnEmptyStrip(LayoutDocumentPaneControl pane, MouseButtonEventArgs e)
    {
        var strip = FindDescendant<DocumentPaneTabPanel>(pane);
        if (strip == null) return false;
        var y = e.GetPosition(pane).Y;
        var stripBottom = strip.TranslatePoint(new Point(0, strip.ActualHeight), pane).Y;
        if (y < 0 || y > stripBottom) return false;
        for (var d = e.OriginalSource as DependencyObject; d != null && d != pane; d = VisualTreeHelper.GetParent(d))
            if (d is TabItem or LayoutDocumentTabItem or System.Windows.Controls.Primitives.ButtonBase) return false;
        return true;
    }

    private static void ToggleMaximize(Window w) =>
        w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ---------- ─ □ ✕ at the right of the strip ----------

    private static void AttachPane(LayoutDocumentPaneControl pane)
    {
        if (WindowOf(pane) == null) return; // main-window panes keep their usual look
        var layer = AdornerLayer.GetAdornerLayer(pane);
        if (layer == null || layer.GetAdorners(pane)?.OfType<CaptionButtons>().Any() == true) return;
        layer.Add(new CaptionButtons(pane));
        // Keep tabs (and the ＋ after them) clear of ─ □ ✕.
        if (FindDescendant<DocumentPaneTabPanel>(pane) is { } strip) strip.Margin = new Thickness(0, 0, 96, 0);
    }

    private sealed class CaptionButtons : Adorner
    {
        private readonly LayoutDocumentPaneControl _pane;
        private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal };

        public CaptionButtons(LayoutDocumentPaneControl pane) : base(pane)
        {
            _pane = pane;
            _panel.Children.Add(Make("", "最小化", w => w.WindowState = WindowState.Minimized));
            _panel.Children.Add(Make("", "最大化 / 還原", ToggleMaximize));
            _panel.Children.Add(Make("", "關閉視窗（關閉其中的分頁）", w =>
            {
                if (w is LayoutFloatingWindowControl { Model: LayoutFloatingWindow model })
                    foreach (var doc in model.Descendents().OfType<LayoutDocument>().ToList()) doc.Close();
            }));
            AddVisualChild(_panel);
            // Re-arrange only when the spot actually moved: invalidating on every LayoutUpdated would loop forever.
            pane.LayoutUpdated += (_, _) => { if ((Target() - _pos).Length > 0.5) InvalidateArrange(); };
        }

        private Button Make(string glyph, string tip, Action<Window> act)
        {
            var b = new Button { Content = glyph, ToolTip = tip, Width = 30, Height = 22, FontSize = 10,
                                 Style = (Style)Application.Current.FindResource("ToolButton") };
            b.Click += (_, _) => { if (WindowOf(_pane) is { } w) act(w); };
            return b;
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _panel;

        protected override Size MeasureOverride(Size constraint)
        {
            _panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return base.MeasureOverride(constraint);
        }

        private Point _pos = new(-1, -1);

        /// <summary>Right end of the tab strip, left of AvalonDock's ▾ tab-list button.</summary>
        private Point Target()
        {
            var strip = FindDescendant<DocumentPaneTabPanel>(_pane);
            double h = strip?.ActualHeight ?? 22;
            double x = _pane.ActualWidth - _panel.DesiredSize.Width - 22;
            double y = strip != null && strip.IsVisible ? strip.TranslatePoint(new Point(0, 0), _pane).Y + (h - _panel.DesiredSize.Height) / 2 : 0;
            return new Point(Math.Max(0, x), Math.Max(0, y));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _pos = Target();
            _panel.Arrange(new Rect(_pos, _panel.DesiredSize));
            return finalSize;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindDescendant<T>(c) is { } d) return d;
        }
        return null;
    }

    private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
}
