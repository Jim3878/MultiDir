using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MultiDir.Views;

/// <summary>One row of a <see cref="Flyout"/>. Null in a list = separator.</summary>
public sealed class FlyoutItem
{
    public required string Header { get; init; }
    public FrameworkElement? Icon { get; init; }
    public string? ToolTip { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Dim { get; init; }
    /// <summary>Left click (for a row without <see cref="Children"/>).</summary>
    public Action? Click { get; init; }
    public Action? MiddleClick { get; init; }
    /// <summary>Non-null = a submenu (built when it opens).</summary>
    public Func<List<FlyoutItem?>>? Children { get; init; }
    /// <summary>Right-click menu of this row, shown on top while the menus stay open (like Chrome's bookmark menus).</summary>
    public Func<List<FlyoutItem?>>? ContextItems { get; init; }

    /// <summary>Identity across <see cref="Flyout.Refresh"/> (e.g. the bookmark the row stands for): an open submenu stays open.</summary>
    public object? Key { get; init; }

    /// <summary>Non-null = the row can be dragged; returns what the drag carries.</summary>
    public Func<IDataObject>? DragData { get; init; }
    /// <summary>Something is dragged over the row: what would a drop at <see cref="FlyoutDropZone"/> do (None = not here)?
    /// The point is the cursor on screen (px), e.g. for a hint.</summary>
    public Func<DragEventArgs, FlyoutDropZone, Point, DragDropEffects>? DragOver { get; init; }
    public Action<DragEventArgs, FlyoutDropZone>? Drop { get; init; }
}

/// <summary>Where on a row a drag would land: above it, onto it (rows with a submenu), or below it.</summary>
public enum FlyoutDropZone { Before, Into, After }

/// <summary>
/// Lightweight cascading menus built on popups that never capture the mouse, so several can be open at once:
/// a drop-down, its submenus, and a right-click menu on top of them. (WPF's ContextMenu can only be one at a time:
/// opening a second one closes the first.)
/// Closes on a click elsewhere in the app, when the app loses focus, or with Esc (one layer at a time).
/// </summary>
public static class Flyout
{
    private static readonly List<Popup> Levels = new(); // drop-down + submenus, outermost first
    private static Popup? _context;                      // right-click layer on top
    private static UIElement? _anchor;
    private static UIElement? _closedByAnchorClick;
    private static DateTime _closedAt;
    private static bool _hooked;
    private static readonly DispatcherTimer SubmenuTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private static Action? _submenuAction;

    public static bool IsOpen => Levels.Count > 0;

    /// <summary>Opens a drop-down under <paramref name="anchor"/>. Clicking the same anchor again just closes it.</summary>
    public static void ShowBelow(UIElement anchor, List<FlyoutItem?> items) => ShowBelow(anchor, () => items);

    /// <summary>Same, built by <paramref name="build"/> — which <see cref="Refresh"/> calls again when the content changed.</summary>
    public static void ShowBelow(UIElement anchor, Func<List<FlyoutItem?>> build)
    {
        bool toggledOff = _closedByAnchorClick == anchor && DateTime.Now - _closedAt < TimeSpan.FromSeconds(1);
        _closedByAnchorClick = null;
        if (toggledOff) return;
        CloseAll();
        Hook();
        _anchor = anchor;
        Levels.Add(Open(build, anchor, PlacementMode.Bottom, level: 0));
    }

    public static void CloseAll()
    {
        CloseContext();
        CloseFrom(0);
        _anchor = null;
    }

    private static void CloseFrom(int level)
    {
        SubmenuTimer.Stop();
        while (Levels.Count > level)
        {
            Levels[^1].IsOpen = false;
            Levels.RemoveAt(Levels.Count - 1);
        }
    }

    private static void CloseContext()
    {
        if (_context != null) _context.IsOpen = false;
        _context = null;
    }

    // ---------- building ----------

    /// <summary>The element the drop-down hangs under (null = closed).</summary>
    public static UIElement? Anchor => IsOpen ? _anchor : null;

    /// <summary>
    /// Rebuilds the open drop-down and submenus in place (e.g. after a drag reordered them), so they stay open
    /// showing the new state. <paramref name="newAnchor"/>: the drop-down's anchor was replaced by this one.
    /// A submenu stays open when its row (same <see cref="FlyoutItem.Key"/>) is still there.
    /// </summary>
    public static void Refresh(UIElement? newAnchor = null)
    {
        if (!IsOpen) return;
        CloseContext();
        if (newAnchor != null) { _anchor = newAnchor; Levels[0].PlacementTarget = newAnchor; }
        for (int k = 0; k < Levels.Count; k++)
        {
            if (k > 0)
            {
                var oldKey = (Levels[k].PlacementTarget as FrameworkElement)?.Tag is FlyoutItem { Key: { } key } ? key : null;
                var newRow = oldKey == null ? null : PanelOf(Levels[k - 1]).Children.OfType<Border>()
                    .FirstOrDefault(r => r.Tag is FlyoutItem { Children: not null } it && Equals(it.Key, oldKey));
                if (newRow == null) { CloseFrom(k); return; }
                Levels[k].PlacementTarget = newRow;
                Levels[k].Tag = ((FlyoutItem)newRow.Tag).Children;
            }
            Fill(PanelOf(Levels[k]), ((Func<List<FlyoutItem?>>)Levels[k].Tag)(), k, context: false);
        }
    }

    private static StackPanel PanelOf(Popup popup) => (StackPanel)((ScrollViewer)((Border)popup.Child).Child).Content;

    private static void Fill(StackPanel panel, List<FlyoutItem?> items, int level, bool context)
    {
        panel.Children.Clear();
        foreach (var item in items) panel.Children.Add(item == null ? SeparatorRow() : Row(item, level, context));
    }

    private static Popup Open(Func<List<FlyoutItem?>> build, UIElement target, PlacementMode placement, int level, bool context = false)
    {
        var panel = new StackPanel { MinWidth = 180 };
        Fill(panel, build(), level, context);
        var border = new Border
        {
            Background = (Brush)Application.Current.FindResource("Surface"),
            BorderBrush = (Brush)Application.Current.FindResource("Border"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(3),
            Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 600 },
        };
        // A popup inherits fonts from what opened it (the ‹‹ button uses the icon font at 8 pt): always use normal text.
        TextElement.SetFontFamily(border, SystemFonts.MessageFontFamily);
        TextElement.SetFontSize(border, 12);
        TextElement.SetFontWeight(border, FontWeights.Normal);
        // A click anywhere in the drop-down / submenus dismisses the right-click layer on top of them.
        if (!context) border.PreviewMouseDown += (_, _) => CloseContext();
        var popup = new Popup
        {
            Child = border, PlacementTarget = target, Placement = placement, StaysOpen = true, Tag = build,
            AllowsTransparency = true, Focusable = false, PopupAnimation = PopupAnimation.None,
            HorizontalOffset = placement == PlacementMode.Right ? -2 : 0, VerticalOffset = placement == PlacementMode.Right ? -4 : 0,
        };
        popup.IsOpen = true;
        return popup;
    }

    private static UIElement SeparatorRow() =>
        new Border { Height = 1, Background = (Brush)Application.Current.FindResource("Border"), Margin = new Thickness(6, 3, 6, 3) };

    private static Border Row(FlyoutItem item, int level, bool context)
    {
        var dock = new DockPanel();
        if (item.Children != null)
        {
            var arrow = new TextBlock { Text = "\uE76C", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 9,
                                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            DockPanel.SetDock(arrow, Dock.Right);
            dock.Children.Add(arrow);
        }
        dock.Children.Add(new Border { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), Child = item.Icon });
        dock.Children.Add(new TextBlock { Text = item.Header, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360,
                                          FontStyle = item.Dim ? FontStyles.Italic : FontStyles.Normal });

        var row = new Border
        {
            // The 2 px top/bottom border is the insert line while dragging (transparent otherwise).
            Child = dock, Padding = new Thickness(8, 2, 8, 2), BorderThickness = new Thickness(0, 2, 0, 2), BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(3), Background = Brushes.Transparent, Tag = item,
            ToolTip = item.ToolTip, Opacity = !item.Enabled ? 0.4 : item.Dim ? 0.6 : 1,
        };
        TextElement.SetForeground(row, (Brush)Application.Current.FindResource("Text"));
        if (item.DragOver != null) AttachDropTarget(row, item, level);
        if (!item.Enabled) return row;
        if (item.DragData != null) AttachDragSource(row, item);

        var hover = (Brush)Application.Current.FindResource("SurfaceHover");
        row.MouseEnter += (_, _) =>
        {
            row.Background = hover;
            if (context) return;
            // Hovering a folder opens its submenu; hovering anything else closes deeper submenus.
            SubmenuTimer.Stop();
            _submenuAction = item.Children != null ? () => OpenSubmenu(item, row, level) : () => CloseFrom(level + 1);
            SubmenuTimer.Tick -= SubmenuTick;
            SubmenuTimer.Tick += SubmenuTick;
            SubmenuTimer.Start();
        };
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (item.Children != null) { if (!context) OpenSubmenu(item, row, level); return; }
            CloseAll();
            item.Click?.Invoke();
        };
        row.MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle && item.MiddleClick != null) { e.Handled = true; CloseAll(); item.MiddleClick(); }
        };
        row.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (context || item.ContextItems == null) return;
            CloseContext();
            var contextItems = item.ContextItems();
            _context = Open(() => contextItems, row, PlacementMode.MousePoint, level, context: true);
        };
        return row;
    }

    // ---------- drag & drop on rows (e.g. reordering bookmarks inside a folder) ----------

    private static void AttachDragSource(Border row, FlyoutItem item)
    {
        Point? down = null;
        row.PreviewMouseLeftButtonDown += (_, e) => down = e.GetPosition(row);
        row.PreviewMouseLeftButtonUp += (_, _) => down = null;
        row.PreviewMouseMove += (_, e) =>
        {
            if (down is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(row) - start;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            down = null;
            SubmenuTimer.Stop();
            // The menus stay open during the drag (popups without mouse capture), so rows elsewhere can take the drop.
            DragDrop.DoDragDrop(row, item.DragData!(), DragDropEffects.Move | DragDropEffects.Link | DragDropEffects.Copy);
            DragHint.Hide(); // the menus stay open: whoever changed their content refreshes them (Flyout.Refresh)
        };
    }

    private static void AttachDropTarget(Border row, FlyoutItem item, int level)
    {
        row.AllowDrop = true;
        var accent = (Brush)Application.Current.FindResource("Accent");
        var selection = (Brush)Application.Current.FindResource("Selection");

        FlyoutDropZone ZoneOf(DragEventArgs e)
        {
            double y = e.GetPosition(row).Y, h = Math.Max(1, row.ActualHeight);
            if (item.Children != null && y > h * 0.25 && y < h * 0.75) return FlyoutDropZone.Into;
            return y < h / 2 ? FlyoutDropZone.Before : FlyoutDropZone.After;
        }
        void Clear()
        {
            row.BorderBrush = Brushes.Transparent;
            row.Background = Brushes.Transparent;
            row.BorderThickness = new Thickness(0, 2, 0, 2);
            row.Padding = new Thickness(8, 2, 8, 2);
        }

        void Over(object _, DragEventArgs e)
        {
            e.Handled = true;
            var zone = ZoneOf(e);
            var effect = item.DragOver!(e, zone, row.PointToScreen(e.GetPosition(row)));
            e.Effects = effect;
            Clear();
            if (effect == DragDropEffects.None) return;
            if (zone == FlyoutDropZone.Into)
            {
                row.Background = selection;
                // Hovering a folder row opens its submenu, to drop deeper.
                if (!(Levels.Count > level + 1 && Levels[level + 1].PlacementTarget == row))
                {
                    _submenuAction = () => OpenSubmenu(item, row, level);
                    SubmenuTimer.Tick -= SubmenuTick;
                    SubmenuTimer.Tick += SubmenuTick;
                    if (!SubmenuTimer.IsEnabled) SubmenuTimer.Start();
                }
            }
            else
            {
                SubmenuTimer.Stop();
                row.BorderBrush = accent;
                row.BorderThickness = zone == FlyoutDropZone.Before ? new Thickness(0, 2, 0, 0) : new Thickness(0, 0, 0, 2);
                row.Padding = zone == FlyoutDropZone.Before ? new Thickness(8, 2, 8, 4) : new Thickness(8, 4, 8, 2);
            }
        }
        row.DragEnter += Over;
        row.DragOver += Over;
        row.DragLeave += (_, _) => { Clear(); DragHint.Hide(); };
        row.Drop += (_, e) =>
        {
            e.Handled = true;
            var zone = ZoneOf(e);
            Clear();
            SubmenuTimer.Stop();
            bool ok = item.DragOver!(e, zone, row.PointToScreen(e.GetPosition(row))) != DragDropEffects.None;
            DragHint.Hide();
            if (!ok) return;
            item.Drop?.Invoke(e, zone);
        };
    }

    private static void SubmenuTick(object? sender, EventArgs e)
    {
        SubmenuTimer.Stop();
        _submenuAction?.Invoke();
    }

    private static void OpenSubmenu(FlyoutItem item, Border row, int level)
    {
        SubmenuTimer.Stop();
        if (Levels.Count > level + 1 && Levels[level + 1].PlacementTarget == row) return; // already open
        CloseContext();
        CloseFrom(level + 1);
        Levels.Add(Open(item.Children!, row, PlacementMode.Right, level + 1));
    }

    // ---------- dismissal ----------

    private static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        // Popups are separate windows: a mouse-down in any app Window is by definition outside the menus.
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (!IsOpen) return;
            // Clicking the anchor that opened the menu toggles it closed (its Click must not reopen it).
            _closedByAnchorClick = _anchor != null && IsInside(e.OriginalSource, _anchor) ? _anchor : null;
            _closedAt = DateTime.Now;
            CloseAll();
        }), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (!IsOpen || e.Key != Key.Escape) return;
            e.Handled = true;
            if (_context != null) CloseContext(); else CloseFrom(Levels.Count - 1);
        }), handledEventsToo: true);
        Application.Current.Deactivated += (_, _) => CloseAll();
    }

    private static bool IsInside(object source, UIElement anchor)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d == anchor) return true;
        return false;
    }
}
