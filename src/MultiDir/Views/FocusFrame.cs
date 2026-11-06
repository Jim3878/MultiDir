using System.Windows;
using System.Windows.Media;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace MultiDir.Views;

/// <summary>
/// Marks the focused pane — where bookmarks, history, the sidebar and folders opened from outside will land —
/// by giving its folder view a slightly lighter background. Only when there is more than one pane to choose from.
/// </summary>
public static class FocusFrame
{
    private static readonly List<WeakReference<LayoutDocumentPaneControl>> Panes = new();
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

    /// <summary>Re-evaluates every pane (also after panes are added/removed or tabs moved).</summary>
    public static void Refresh()
    {
        Panes.RemoveAll(w => !w.TryGetTarget(out _));
        // Only meaningful with a choice of panes (floating windows count).
        bool choice = (_active?.Root as LayoutRoot)?.Descendents().OfType<LayoutDocumentPane>().Count(p => p.ChildrenCount > 0) > 1;
        foreach (var w in Panes)
        {
            if (!w.TryGetTarget(out var control) || control.Model is not LayoutDocumentPane model) continue;
            bool? on = choice ? model == _active : null;
            foreach (var doc in model.Children)
                if (doc.Content is FolderView view) view.SetActivePane(on);
        }
    }

    private static void Attach(LayoutDocumentPaneControl control)
    {
        if (!Panes.Any(w => w.TryGetTarget(out var c) && c == control))
            Panes.Add(new WeakReference<LayoutDocumentPaneControl>(control));
        Refresh();
    }
}
