using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace MultiDir.Views;

/// <summary>
/// Thin accent frame around the focused pane — where bookmarks, history, the sidebar and folders opened from
/// outside will land. Shown only when there is more than one pane to choose from.
/// </summary>
public static class FocusFrame
{
    private static readonly List<WeakReference<FrameAdorner>> Adorners = new();
    private static LayoutDocumentPane? _active;

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(LayoutDocumentPaneControl), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => Attach((LayoutDocumentPaneControl)s)));
    }

    /// <summary>Marks <paramref name="pane"/> as the focused one (null = none).</summary>
    public static void SetActive(LayoutDocumentPane? pane)
    {
        _active = pane;
        Refresh();
    }

    /// <summary>Re-evaluates every frame (also after panes are added/removed).</summary>
    public static void Refresh()
    {
        Adorners.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in Adorners) if (w.TryGetTarget(out var a)) a.InvalidateVisual();
    }

    private static void Attach(LayoutDocumentPaneControl control)
    {
        var layer = AdornerLayer.GetAdornerLayer(control);
        if (layer == null || layer.GetAdorners(control)?.OfType<FrameAdorner>().Any() == true) return;
        var adorner = new FrameAdorner(control);
        layer.Add(adorner);
        Adorners.Add(new WeakReference<FrameAdorner>(adorner));
        control.Unloaded += (_, _) => AdornerLayer.GetAdornerLayer(control)?.Remove(adorner);
        Refresh();
    }

    private sealed class FrameAdorner : Adorner
    {
        private readonly LayoutDocumentPaneControl _pane;
        private static readonly Pen Pen = new((Brush)Application.Current.FindResource("Accent"), 2);

        public FrameAdorner(LayoutDocumentPaneControl pane) : base(pane)
        {
            _pane = pane;
            IsHitTestVisible = false; // purely visual: never steals clicks or drops
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_pane.Model is not LayoutDocumentPane model || model != _active) return;
            // Only meaningful with a choice of panes (floating windows count).
            if ((model.Root as LayoutRoot)?.Descendents().OfType<LayoutDocumentPane>().Count(p => p.ChildrenCount > 0) is not > 1) return;
            var r = new Rect(_pane.RenderSize);
            r.Inflate(-1, -1);
            dc.DrawRoundedRectangle(null, Pen, r, 3, 3);
        }
    }
}
