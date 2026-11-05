using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace MultiDir.Views;

/// <summary>
/// Browser-style "+" right after the last tab of every document pane (main window and floating windows).
/// Implemented as an adorner so AvalonDock's pane template stays untouched.
/// </summary>
public static class NewTabButtons
{
    public static void Register(Action<LayoutDocumentPane> openTab)
    {
        EventManager.RegisterClassHandler(typeof(LayoutDocumentPaneControl), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => Attach((LayoutDocumentPaneControl)s, openTab)));
    }

    private static void Attach(LayoutDocumentPaneControl control, Action<LayoutDocumentPane> openTab)
    {
        var layer = AdornerLayer.GetAdornerLayer(control);
        if (layer == null) return;
        if (layer.GetAdorners(control)?.OfType<PlusAdorner>().Any() == true) return;
        layer.Add(new PlusAdorner(control, openTab));
    }

    private sealed class PlusAdorner : Adorner
    {
        private readonly LayoutDocumentPaneControl _pane;
        private readonly Button _button;
        private Point _pos;

        public PlusAdorner(LayoutDocumentPaneControl pane, Action<LayoutDocumentPane> openTab) : base(pane)
        {
            _pane = pane;
            _button = new Button
            {
                Content = "", // Segoe MDL2 "Add"
                ToolTip = "新增分頁",
                Width = 24, Height = 22, FontSize = 11,
                Style = (Style)Application.Current.FindResource("ToolButton"),
            };
            _button.Click += (_, _) => { if (_pane.Model is LayoutDocumentPane m) openTab(m); };
            AddVisualChild(_button);
            pane.LayoutUpdated += (_, _) => Reposition();
            pane.Unloaded += (_, _) => AdornerLayer.GetAdornerLayer(pane)?.Remove(this);
        }

        /// <summary>Just after the right edge of the last visible tab, never past the tab strip.</summary>
        private void Reposition()
        {
            var strip = FindDescendant<DocumentPaneTabPanel>(_pane);
            if (strip == null || !strip.IsVisible) { SetVisibility(false); return; }

            double right = 0;
            foreach (UIElement child in strip.Children)
            {
                if (child is not FrameworkElement fe || !fe.IsVisible || fe.ActualWidth <= 0) continue;
                right = Math.Max(right, fe.TranslatePoint(new Point(fe.ActualWidth, 0), strip).X);
            }
            right = Math.Min(right, Math.Max(0, strip.ActualWidth - _button.Width));
            var origin = strip.TranslatePoint(new Point(right + 2, (strip.ActualHeight - _button.Height) / 2), _pane);

            SetVisibility(true);
            if ((origin - _pos).Length < 0.5) return;
            _pos = origin;
            InvalidateArrange();
        }

        private void SetVisibility(bool visible)
        {
            var v = visible ? Visibility.Visible : Visibility.Collapsed;
            if (_button.Visibility != v) _button.Visibility = v;
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _button;

        protected override Size MeasureOverride(Size constraint)
        {
            _button.Measure(new Size(_button.Width, _button.Height));
            return base.MeasureOverride(constraint);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _button.Arrange(new Rect(_pos, new Size(_button.Width, _button.Height)));
            return finalSize;
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) return t;
                if (FindDescendant<T>(child) is { } d) return d;
            }
            return null;
        }
    }
}
