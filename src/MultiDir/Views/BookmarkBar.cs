using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MultiDir.Services;

namespace MultiDir.Views;

/// <summary>
/// Chrome-style bookmark bar with bookmark folders.
/// Bookmark: click = open in the active tab, Ctrl/middle-click = new tab, right-click = 開啟 / 新分頁 / 重新命名 / 移除.
/// Folder: click = drop-down (nested submenus), middle-click = open all in new tabs, right-click = 全部開啟 / 重新命名 / 刪除.
/// Dragging: a folder onto empty bar space adds it; files onto a bookmark move them there; bookmarks reorder along the bar;
/// anything dropped onto the middle of a folder goes into it, and hovering 0.5 s opens it to drop into a subfolder.
/// </summary>
public sealed class BookmarkBar : DockPanel
{
    private const string ReorderFormat = "MultiDir.Bookmark";
    private static readonly Brush FolderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x39));

    private readonly OverflowPanel _panel = new() { ClipToBounds = true };
    private readonly Button _overflow;
    private readonly TextBlock _empty;
    private readonly Border _insertMark;
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };

    private Point _downAt;
    private Bookmark? _downOn;
    private Button? _dropHot;

    /// <summary>(path, newTab) — the host decides which tab/pane.</summary>
    public event Action<string, bool>? OpenRequested;

    public BookmarkBar()
    {
        Background = Brushes.Transparent; // hit-testable, so the empty area accepts drops and right-clicks
        AllowDrop = true;
        LastChildFill = true;

        _overflow = new Button
        {
            Content = "\uE76C\uE76C", // »
            ToolTip = "其他書籤",
            Style = (Style)Application.Current.FindResource("ToolButton"),
            FontSize = 9, Visibility = Visibility.Collapsed,
        };
        _overflow.Click += (_, _) => ShowOverflowMenu();
        SetDock(_overflow, Dock.Right);
        Children.Add(_overflow);

        _empty = new TextBlock
        {
            Text = "把資料夾拖到這裡，或按功能列的 ☆ 加入書籤；右鍵可新增書籤資料夾",
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            IsHitTestVisible = false,
        };
        _insertMark = new Border { Width = 2, Background = (Brush)Application.Current.FindResource("Accent"), Visibility = Visibility.Collapsed };
        _overlay.Children.Add(_insertMark);

        var host = new Grid();
        host.Children.Add(_empty);
        host.Children.Add(_panel);
        host.Children.Add(_overlay);
        Children.Add(host);

        // Right-click on empty bar space.
        ContextMenu = DarkMenu();
        ContextMenu.Items.Add(Item("新增資料夾…", () => NewFolder(null)));

        _panel.OverflowChanged += () =>
            _overflow.Visibility = _panel.HiddenChildren.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        BookmarkStore.Changed += Rebuild;
        BookmarkStore.Added += b => Dispatcher.BeginInvoke(() => Flash(b), System.Windows.Threading.DispatcherPriority.Loaded);
        Rebuild();

        _hoverTimer.Tick += HoverTimer_Tick;
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); CloseDropPopup(); };

        DragEnter += Bar_DragOver;
        DragOver += Bar_DragOver;
        DragLeave += Bar_DragLeave;
        Drop += Bar_Drop;
    }

    // ---------- bar buttons ----------

    private void Rebuild()
    {
        _panel.Children.Clear();
        foreach (var b in BookmarkStore.Items) _panel.Children.Add(CreateButton(b));
        _empty.Visibility = BookmarkStore.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Button CreateButton(Bookmark b)
    {
        var text = new TextBlock { Text = b.Name, MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
        if (!b.IsFolder && !b.Exists) { text.Opacity = 0.5; text.FontStyle = FontStyles.Italic; }
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(IconFor(b));
        content.Children.Add(text);

        var button = new Button
        {
            Content = content, Tag = b, Height = 24, Margin = new Thickness(1, 0, 1, 0), Padding = new Thickness(6, 0, 6, 0),
            Style = (Style)Application.Current.FindResource("CrumbButton"),
            ToolTip = b.IsFolder ? $"{b.Name}（{b.AllBookmarks().Count()} 個書籤）" : b.Exists ? b.Path : b.Path + "（找不到這個資料夾）",
            ContextMenu = BarMenu(b),
        };
        button.Click += (_, _) =>
        {
            if (b.IsFolder) ShowFolderDropDown(button, b);
            else Open(b, newTab: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        };
        button.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) { OpenMiddle(b); e.Handled = true; }
            if (e.ChangedButton == MouseButton.Left) { _downAt = e.GetPosition(this); _downOn = b; }
        };
        button.PreviewMouseMove += (_, e) => StartReorderDrag(button, e);
        return button;
    }

    private static FrameworkElement IconFor(Bookmark b) => b.IsFolder
        ? new TextBlock { Text = "\uE8B7", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 14,
                          Foreground = FolderBrush, Width = 16, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        : new Image { Source = b.Icon, Width = 16, Height = 16 };

    // ---------- menus ----------
    // One action list per entry feeds both the bar's right-click ContextMenu and the drop-down's right-click flyout.

    private sealed record MenuAction(string Header, Action Run, bool Enabled = true);

    private List<MenuAction?> BookmarkActions(Bookmark b)
    {
        var list = new List<MenuAction?>
        {
            new("開啟", () => Open(b, false)),
            new("在新分頁開啟", () => Open(b, true)),
            null,
            new("重新命名…", () => RenameEntry(b)),
        };
        if (BookmarkStore.ParentFolder(b) != null) list.Add(new("移到書籤列", () => BookmarkStore.Move(b, -1)));
        list.Add(new("移除", () => BookmarkStore.Remove(b)));
        return list;
    }

    private List<MenuAction?> FolderActions(Bookmark folder)
    {
        int n = folder.AllBookmarks().Count(x => x.Exists);
        var list = new List<MenuAction?>
        {
            new($"全部在新分頁開啟（{n}）", () => OpenAll(folder), n > 0),
            null,
            new("新增資料夾…", () => NewFolder(folder)),
            new("重新命名…", () => RenameEntry(folder)),
        };
        if (BookmarkStore.ParentFolder(folder) != null) list.Add(new("移到書籤列", () => BookmarkStore.Move(folder, -1)));
        list.Add(new("刪除", () => DeleteFolder(folder)));
        return list;
    }

    private static ContextMenu DarkMenu() => new() { Style = (Style)Application.Current.FindResource("DarkMenu") };

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header, Style = (Style)Application.Current.FindResource("DarkMenuItem") };
        item.Click += (_, e) => { action(); e.Handled = true; };
        return item;
    }

    /// <summary>Right-click menu of a button on the bar (built when it opens, so it reflects the current tree).</summary>
    /// <summary>Same right-click menu for a bookmark/folder shown elsewhere (the sidebar's 書籤 section).</summary>
    public ContextMenu CreateMenu(Bookmark b) => BarMenu(b);

    /// <summary>Open a bookmark (or, for a folder with <paramref name="newTab"/>, everything in it) — used by the sidebar.</summary>
    public void Activate(Bookmark b, bool newTab)
    {
        if (b.IsFolder) { if (newTab) OpenAll(b); }
        else Open(b, newTab);
    }

    /// <summary>Folder icon / shell icon used for a bookmark entry.</summary>
    public static FrameworkElement CreateIcon(Bookmark b) => IconFor(b);

    private ContextMenu BarMenu(Bookmark b)
    {
        var menu = DarkMenu();
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            foreach (var a in b.IsFolder ? FolderActions(b) : BookmarkActions(b))
            {
                if (a == null) { menu.Items.Add(new Separator { Style = (Style)Application.Current.FindResource("DarkMenuSeparator") }); continue; }
                var item = Item(a.Header, a.Run);
                item.IsEnabled = a.Enabled;
                menu.Items.Add(item);
            }
        };
        menu.Items.Add(new MenuItem()); // placeholder so the menu opens; replaced in Opened
        return menu;
    }

    private static List<FlyoutItem?> ToFlyout(List<MenuAction?> actions) =>
        actions.Select(a => a == null ? null : new FlyoutItem { Header = a.Header, Click = a.Run, Enabled = a.Enabled }).ToList();

    /// <summary>Drop-down rows for a folder's contents: bookmarks, and nested folders as submenus.</summary>
    private List<FlyoutItem?> Entries(IEnumerable<Bookmark> list)
    {
        var rows = list.Select(c => (FlyoutItem?)(c.IsFolder
            ? new FlyoutItem
            {
                Header = c.Name, Icon = IconFor(c),
                Children = () => Entries(c.Children!),
                MiddleClick = () => OpenAll(c),
                ContextItems = () => ToFlyout(FolderActions(c)),
            }
            : new FlyoutItem
            {
                Header = c.Name, Icon = IconFor(c), ToolTip = c.Exists ? c.Path : c.Path + "（找不到這個資料夾）", Dim = !c.Exists,
                Click = () => Open(c, (Keyboard.Modifiers & ModifierKeys.Control) != 0),
                MiddleClick = () => Open(c, true),
                ContextItems = () => ToFlyout(BookmarkActions(c)),
            })).ToList();
        if (rows.Count == 0) rows.Add(new FlyoutItem { Header = "（空的）", Enabled = false });
        return rows;
    }

    private void ShowFolderDropDown(FrameworkElement anchor, Bookmark folder) =>
        Flyout.ShowBelow(anchor, Entries(folder.Children!));

    private void ShowOverflowMenu() =>
        Flyout.ShowBelow(_overflow, Entries(_panel.HiddenChildren.OfType<Button>().Select(b => (Bookmark)b.Tag)));

    // ---------- actions ----------

    private void Open(Bookmark b, bool newTab)
    {
        if (b.IsFolder) return;
        if (!b.Exists)
        {
            if (App.IsAutomation || Window.GetWindow(this) is not { } owner) return;
            if (MessageBox.Show(owner, $"找不到資料夾：\n{b.Path}\n\n要移除這個書籤嗎？", "書籤", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                BookmarkStore.Remove(b);
            return;
        }
        OpenRequested?.Invoke(b.Path, newTab);
    }

    private void OpenMiddle(Bookmark b)
    {
        if (b.IsFolder) OpenAll(b); else Open(b, newTab: true);
    }

    private void OpenAll(Bookmark folder)
    {
        foreach (var b in folder.AllBookmarks().Where(x => x.Exists)) OpenRequested?.Invoke(b.Path, true);
    }

    private void NewFolder(Bookmark? parent)
    {
        if (App.IsAutomation || Window.GetWindow(this) is not { } owner) return;
        if (RenameDialog.Prompt(owner, "新增書籤資料夾", "新資料夾") is { } name) BookmarkStore.AddFolder(name, -1, parent);
    }

    private void RenameEntry(Bookmark b)
    {
        if (App.IsAutomation || Window.GetWindow(this) is not { } owner) return;
        if (RenameDialog.Prompt(owner, b.IsFolder ? "重新命名書籤資料夾" : "重新命名書籤", b.Name) is { } name) BookmarkStore.Rename(b, name);
    }

    private void DeleteFolder(Bookmark folder)
    {
        int n = folder.AllBookmarks().Count();
        if (n > 0)
        {
            if (App.IsAutomation || Window.GetWindow(this) is not { } owner) return;
            if (MessageBox.Show(owner, $"要刪除書籤資料夾「{folder.Name}」和裡面的 {n} 個書籤嗎？\n（只刪除書籤，不會動到真正的資料夾）",
                    "刪除書籤資料夾", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }
        BookmarkStore.Remove(folder);
    }

    // ---------- automation ----------

    /// <summary>Automation: open the top-level bookmark at <paramref name="index"/> (a folder opens all of it in new tabs).</summary>
    public void OpenAt(int index, bool newTab)
    {
        var b = BookmarkStore.Items[index];
        if (b.IsFolder) OpenAll(b); else Open(b, newTab);
    }

    /// <summary>Automation: the bar as a tree; entries that went into » are marked.</summary>
    public string Describe()
    {
        var lines = new List<string>();
        void Walk(IEnumerable<Bookmark> list, int depth)
        {
            foreach (var b in list)
            {
                var hidden = depth == 0 && _panel.HiddenChildren.OfType<Button>().Any(x => x.Tag == b) ? "  (in »)" : "";
                lines.Add($"{new string(' ', 2 + depth * 2)}{(depth == 0 ? BookmarkStore.Items.IndexOf(b) + ": " : "- ")}" +
                          (b.IsFolder ? $"[{b.Name}]" : $"{b.Name} | {b.Path}") + hidden);
                if (b.IsFolder) Walk(b.Children!, depth + 1);
            }
        }
        Walk(BookmarkStore.Items, 0);
        return string.Join(Environment.NewLine, lines);
    }

    // ---------- drag: reorder ----------

    private void StartReorderDrag(Button button, MouseEventArgs e)
    {
        if (_downOn == null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _downAt;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var b = _downOn;
        _downOn = null;
        // Internal format only: dragging a bookmark must never move the real folder.
        DragDrop.DoDragDrop(button, new DataObject(ReorderFormat, b), DragDropEffects.Move);
        EndDrag();
    }

    private void EndDrag()
    {
        DragHint.Hide();
        HideInsertMark();
        SetDropHot(null);
        _hoverTimer.Stop();
        _hoverTarget = null;
        CloseDropPopup();
    }

    // ---------- drag over the bar ----------

    /// <summary>What a drag carries that the bar can take: a bookmark being moved, or folders to bookmark.</summary>
    private static (Bookmark? Moving, List<string> Folders) Payload(DragEventArgs e)
    {
        if (e.Data.GetData(ReorderFormat) is Bookmark moving) return (moving, new());
        try { return (null, e.Data.GetData(DataFormats.FileDrop) is string[] paths ? paths.Where(Directory.Exists).ToList() : new()); }
        catch { return (null, new()); }
    }

    private static bool CanTake((Bookmark? Moving, List<string> Folders) p, Bookmark? into) =>
        p.Moving != null ? into == null || (into != p.Moving && !p.Moving.Contains(into)) : p.Folders.Count > 0;

    /// <summary>The middle half of a folder button means "into the folder"; its edges mean "next to it".</summary>
    private bool IsIntoFolder(Button button, DragEventArgs e) => button.Tag is Bookmark { IsFolder: true } && IsMiddle(button, e);

    /// <summary>The middle half of a button = "onto it"; its outer quarters = "next to it" (insert a bookmark there).</summary>
    private static bool IsMiddle(Button button, DragEventArgs e)
    {
        var x = e.GetPosition(button).X;
        return x > button.ActualWidth * 0.25 && x < button.ActualWidth * 0.75;
    }

    /// <summary>Files or folders dropped onto the middle of a bookmark go INTO that folder (move/copy like anywhere else).</summary>
    private static bool IsOntoBookmark(Button? button, DragEventArgs e, (Bookmark? Moving, List<string> Folders) payload, out Bookmark target)
    {
        target = null!;
        if (payload.Moving != null || button is not { Tag: Bookmark { IsFolder: false, Exists: true } b } || !IsMiddle(button, e)) return false;
        target = b;
        return true;
    }

    private void Bar_DragOver(object sender, DragEventArgs e)
    {
        _closeTimer.Stop();
        var target = ButtonAt(e);
        var payload = Payload(e);
        e.Handled = true;

        if (target is { Tag: Bookmark { IsFolder: true } folder } && IsIntoFolder(target, e))
        {
            HideInsertMark();
            SetDropHot(CanTake(payload, folder) ? target : null);
            e.Effects = CanTake(payload, folder) ? (payload.Moving != null ? DragDropEffects.Move : DragDropEffects.Link) : DragDropEffects.None;
            Hover(target, () => OpenDropPopup(target, folder));
            ShellDragDrop.Leave();
            if (CanTake(payload, folder))
                Hint(e, "\uE8B7", payload.Moving != null ? $"移到書籤資料夾「{folder.Name}」" : $"加入書籤資料夾「{folder.Name}」", "停 0.5 秒可展開，放進子資料夾");
            return;
        }
        Hover(null, null);

        if (IsOntoBookmark(target, e, payload, out var b))
        {
            HideInsertMark();
            SetDropHot(target);
            ShellDragDrop.Over(e, this, b.Path); // also shows "移動到「X」" / "複製到「X」"
            return;
        }
        SetDropHot(null);
        ShellDragDrop.Leave();
        if (CanTake(payload, null))
        {
            ShowInsertMark(InsertIndex(e));
            e.Effects = payload.Moving != null ? DragDropEffects.Move : DragDropEffects.Link;
            Hint(e, payload.Moving != null ? "\uE8CB" : "\uE734", payload.Moving != null ? "移動書籤" : "加入書籤列");
        }
        else
        {
            HideInsertMark();
            e.Effects = DragDropEffects.None;
        }
    }

    private void Bar_DragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(this);
        if (p.X >= 0 && p.Y >= 0 && p.X < ActualWidth && p.Y < ActualHeight) return;
        HideInsertMark();
        SetDropHot(null);
        ShellDragDrop.Leave();
        Hover(null, null);
        _closeTimer.Start(); // the drop-down survives the short trip from the bar into it
    }

    private void Bar_Drop(object sender, DragEventArgs e)
    {
        var target = ButtonAt(e);
        var payload = Payload(e);
        int index = InsertIndex(e);
        bool into = target != null && IsIntoFolder(target, e);
        e.Handled = true;
        EndDrag();

        if (into && target!.Tag is Bookmark folder) { DropInto(payload, folder); return; }
        if (IsOntoBookmark(target, e, payload, out var b)) { ShellDragDrop.Drop(e, this, b.Path); return; }
        if (payload.Moving != null) { BookmarkStore.Move(payload.Moving, index); return; }
        if (payload.Folders.Count > 0) { foreach (var f in payload.Folders) BookmarkStore.Add(f, index++); return; }
    }

    private void Hint(DragEventArgs e, string glyph, string title, string? detail = null) =>
        DragHint.Show(PointToScreen(e.GetPosition(this)), glyph, title, detail);

    private static void DropInto((Bookmark? Moving, List<string> Folders) payload, Bookmark folder)
    {
        if (!CanTake(payload, folder)) return;
        if (payload.Moving != null) BookmarkStore.Move(payload.Moving, -1, folder);
        else foreach (var f in payload.Folders) BookmarkStore.Add(f, -1, folder);
    }

    private Button? ButtonAt(DragEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is Button btn && btn.Tag is Bookmark) return btn;
        return null;
    }

    /// <summary>Is a screen point (px) over the bar? (used while dragging a tab, which is not an OLE drag)</summary>
    public bool ContainsScreenPoint(Point screenPx)
    {
        if (!IsVisible) return false;
        var p = PointFromScreen(screenPx);
        return p.X >= 0 && p.Y >= -4 && p.X < ActualWidth && p.Y < ActualHeight + 4;
    }

    /// <summary>A dragged tab hovers the bar (null = it left): insert line where it would go + hint beside the cursor.</summary>
    public void ShowTabHover(Point? screenPx, IReadOnlyList<string> paths)
    {
        if (screenPx is not { } pt) { HideInsertMark(); DragHint.Hide(); return; }
        if (paths.Count == 0) { HideInsertMark(); DragHint.Show(pt, "", "「本機」不能加入書籤", leftOfCursor: true); return; }
        ShowInsertMark(InsertIndexAt(_panel.PointFromScreen(pt).X));
        // Left of the cursor: the tab's floating window hangs below-right of it and would hide the hint.
        DragHint.Show(pt, "", paths.Count == 1 ? "放開即加入書籤" : $"放開即加入 {paths.Count} 個書籤", string.Join("、", paths), leftOfCursor: true);
    }

    /// <summary>Feedback after adding: the new bookmark's button (or » if it didn't fit) lights up briefly.</summary>
    private void Flash(Bookmark b)
    {
        var button = _panel.Children.OfType<Button>().FirstOrDefault(x => x.Tag == b);
        FrameworkElement target = button != null && !_panel.HiddenChildren.Contains(button) ? button
            : BookmarkStore.ParentFolder(b) is { } folder && _panel.Children.OfType<Button>().FirstOrDefault(x => x.Tag == folder) is { } fb ? fb
            : _overflow;
        if (target is not Control control) return;
        var brush = ((SolidColorBrush)Application.Current.FindResource("Accent")).Clone();
        control.Background = brush;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1400))
        { BeginTime = TimeSpan.FromMilliseconds(300) };
        fade.Completed += (_, _) => control.ClearValue(BackgroundProperty);
        brush.BeginAnimation(Brush.OpacityProperty, fade);
    }

    /// <summary>Adds folders as bookmarks at the bar position under a screen point (px).</summary>
    public void AddAt(Point screenPx, IEnumerable<string> folders)
    {
        int index = InsertIndexAt(_panel.PointFromScreen(screenPx).X);
        foreach (var f in folders) BookmarkStore.Add(f, index++);
    }

    /// <summary>Insert position from the cursor: before the first visible button whose middle is right of it.</summary>
    private int InsertIndex(DragEventArgs e) => InsertIndexAt(e.GetPosition(_panel).X);

    private int InsertIndexAt(double x)
    {
        var visible = VisibleButtons();
        for (int i = 0; i < visible.Count; i++)
        {
            var left = visible[i].TranslatePoint(new Point(0, 0), _panel).X;
            if (x < left + visible[i].ActualWidth / 2) return i;
        }
        return visible.Count;
    }

    private List<Button> VisibleButtons() => _panel.Children.OfType<Button>().Where(b => !_panel.HiddenChildren.Contains(b)).ToList();

    private void ShowInsertMark(int index)
    {
        var visible = VisibleButtons();
        double x = index < visible.Count ? visible[index].TranslatePoint(new Point(0, 0), _panel).X - 1
                 : visible.Count > 0 ? visible[^1].TranslatePoint(new Point(visible[^1].ActualWidth, 0), _panel).X + 1 : 2;
        Canvas.SetLeft(_insertMark, Math.Max(0, x - 1));
        Canvas.SetTop(_insertMark, 3);
        _insertMark.Height = Math.Max(0, ActualHeight - 6);
        _insertMark.Visibility = Visibility.Visible;
    }

    private void HideInsertMark() => _insertMark.Visibility = Visibility.Collapsed;

    private void SetDropHot(Button? button)
    {
        if (_dropHot == button) return;
        _dropHot?.ClearValue(BackgroundProperty);
        _dropHot = button;
        if (button != null) button.Background = (Brush)Application.Current.FindResource("Selection");
    }

    // ---------- hover 0.5 s → open ----------

    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private object? _hoverTarget;
    private Action? _hoverAction;

    private void Hover(object? target, Action? action)
    {
        if (target == _hoverTarget) return;
        _hoverTarget = target;
        _hoverAction = action;
        _hoverTimer.Stop();
        if (target != null) _hoverTimer.Start();
    }

    private void HoverTimer_Tick(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        _hoverAction?.Invoke();
    }

    // ---------- drop-down used while dragging ----------
    // A normal ContextMenu grabs the mouse, which breaks an ongoing drag, so dragging uses this light popup instead:
    // it shows one folder level at a time; hovering a subfolder for 0.5 s steps into it.

    private Popup? _dropPopup;
    private StackPanel? _dropRows;
    private Bookmark? _dropFolder;

    private void OpenDropPopup(FrameworkElement anchor, Bookmark folder)
    {
        if (_dropPopup == null)
        {
            _dropRows = new StackPanel { MinWidth = 200 };
            var border = new Border
            {
                Background = (Brush)Application.Current.FindResource("Surface"), BorderBrush = (Brush)Application.Current.FindResource("Border"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(3), Child = _dropRows, AllowDrop = true,
            };
            border.DragOver += Popup_DragOver;
            border.DragEnter += Popup_DragOver;
            border.DragLeave += (_, _) => _closeTimer.Start();
            border.Drop += Popup_Drop;
            _dropPopup = new Popup { Child = border, AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Bottom, Focusable = false };
        }
        _dropPopup.PlacementTarget = anchor;
        ShowDropLevel(folder);
        _dropPopup.IsOpen = true;
    }

    private void ShowDropLevel(Bookmark folder)
    {
        _dropFolder = folder;
        _rowHot = null;
        _dropRows!.Children.Clear();
        var path = new List<string>();
        for (var f = folder; f != null; f = BookmarkStore.ParentFolder(f)) path.Insert(0, f.Name);
        _dropRows.Children.Add(DropRow($"放進「{string.Join(" › ", path)}」", folder, header: true));
        foreach (var sub in folder.Children!.Where(c => c.IsFolder)) _dropRows.Children.Add(DropRow(sub.Name, sub, header: false));
        foreach (var b in folder.Children!.Where(c => !c.IsFolder))
            _dropRows.Children.Add(new TextBlock { Text = "   " + b.Name, Opacity = 0.5, Margin = new Thickness(8, 3, 8, 3), IsHitTestVisible = false });
    }

    private Border DropRow(string text, Bookmark folder, bool header)
    {
        var row = new DockPanel();
        if (!header)
            row.Children.Add(new TextBlock { Text = "\uE76C", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 9,
                                             VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
        if (!header) SetDock(row.Children[0], Dock.Right);
        row.Children.Add(IconFor(folder));
        row.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                                         Foreground = (Brush)Application.Current.FindResource(header ? "TextDim" : "Text") });
        return new Border { Child = row, Tag = folder, Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(3), Background = Brushes.Transparent };
    }

    private Border? RowAt(DragEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is Border { Tag: Bookmark } row) return row;
        return null;
    }

    private Border? _rowHot;

    private void Popup_DragOver(object sender, DragEventArgs e)
    {
        _closeTimer.Stop();
        e.Handled = true;
        var row = RowAt(e);
        var target = row?.Tag as Bookmark ?? _dropFolder;
        var payload = Payload(e);
        bool ok = target != null && CanTake(payload, target);
        e.Effects = ok ? (payload.Moving != null ? DragDropEffects.Move : DragDropEffects.Link) : DragDropEffects.None;
        if (ok && sender is FrameworkElement popupContent)
            DragHint.Show(popupContent.PointToScreen(e.GetPosition(popupContent)), "\uE8B7", $"放進「{target!.Name}」");
        else DragHint.Hide();

        if (_rowHot != row)
        {
            _rowHot?.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            _rowHot = row;
            if (row != null && ok) row.Background = (Brush)Application.Current.FindResource("Selection");
        }
        // Hovering a subfolder row steps into it.
        Hover(row != null && target != _dropFolder ? row : null, target != null && target != _dropFolder ? () => ShowDropLevel(target) : null);
    }

    private void Popup_Drop(object sender, DragEventArgs e)
    {
        var target = RowAt(e)?.Tag as Bookmark ?? _dropFolder;
        var payload = Payload(e);
        e.Handled = true;
        EndDrag();
        if (target != null) DropInto(payload, target);
    }

    private void CloseDropPopup()
    {
        if (_dropPopup != null) _dropPopup.IsOpen = false;
        _rowHot = null;
        _dropFolder = null;
    }

    /// <summary>Horizontal panel that hides children that don't fully fit (they go into the » menu).</summary>
    private sealed class OverflowPanel : Panel
    {
        public List<UIElement> HiddenChildren { get; } = new();
        public event Action? OverflowChanged;

        protected override Size MeasureOverride(Size available)
        {
            double h = 0;
            foreach (UIElement c in InternalChildren)
            {
                c.Measure(new Size(double.PositiveInfinity, available.Height));
                h = Math.Max(h, c.DesiredSize.Height);
            }
            return new Size(double.IsInfinity(available.Width) ? InternalChildren.Cast<UIElement>().Sum(c => c.DesiredSize.Width) : available.Width, h);
        }

        protected override Size ArrangeOverride(Size final)
        {
            var before = HiddenChildren.Count;
            HiddenChildren.Clear();
            double x = 0;
            foreach (UIElement c in InternalChildren)
            {
                var w = c.DesiredSize.Width;
                if (HiddenChildren.Count == 0 && x + w <= final.Width)
                {
                    c.Arrange(new Rect(x, (final.Height - c.DesiredSize.Height) / 2, w, c.DesiredSize.Height));
                    x += w;
                }
                else
                {
                    c.Arrange(new Rect(0, 0, 0, 0));
                    HiddenChildren.Add(c);
                }
            }
            if (before != HiddenChildren.Count) Dispatcher.BeginInvoke(() => OverflowChanged?.Invoke());
            else OverflowChanged?.Invoke();
            return final;
        }
    }
}
