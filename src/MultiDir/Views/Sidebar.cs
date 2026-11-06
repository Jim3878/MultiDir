using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MultiDir.Models;
using MultiDir.Services;

namespace MultiDir.Views;

/// <summary>
/// Left sidebar (decision #10): 書籤 / 樹狀目錄 / 歷史, each section collapsible, the whole bar can be hidden.
/// The folder tree follows the focused pane (expands to it without collapsing anything, decision #18) unless pinned.
/// Every folder row is a drop target (move/copy like anywhere else); clicking navigates the focused pane,
/// middle-click opens a new tab.
/// </summary>
public sealed class Sidebar : DockPanel
{
    private readonly TreeView _bookmarks = NewTree();
    private readonly TreeView _tree = NewTree();
    private readonly StackPanel _history = new();
    private readonly ToggleButton _pin;
    private readonly Dictionary<string, FrameworkElement> _sectionBodies = new();
    private readonly Dictionary<string, TextBlock> _sectionChevrons = new();
    private readonly HashSet<Bookmark> _expandedBookmarkFolders = new();
    private int _syncVersion;
    private string? _lastSyncPath;

    /// <summary>(path, newTab) — the host decides which tab/pane.</summary>
    public event Action<string, bool>? OpenRequested;

    /// <summary>Set by the host: bookmark actions/menus are shared with the bookmark bar.</summary>
    public BookmarkBar? BookmarkBar { get; set; }

    public Sidebar()
    {
        Background = (Brush)Application.Current.FindResource("Bg");
        LastChildFill = true;

        _pin = new ToggleButton
        {
            Content = "\uE718", ToolTip = "鎖定樹狀目錄（不跟著目前分頁移動）",
            Style = (Style)Application.Current.FindResource("ToolButton"), Width = 22, Height = 20, FontSize = 11,
        };
        _pin.Checked += (_, _) => _pin.ToolTip = "已鎖定：按一下恢復跟著目前分頁移動";
        _pin.Unchecked += (_, _) =>
        {
            _pin.ToolTip = "鎖定樹狀目錄（不跟著目前分頁移動）";
            if (_lastSyncPath != null) SyncTo(_lastSyncPath); // catch up right away
        };

        var bookmarks = Section("bookmarks", "書籤", new ScrollViewer { Content = _bookmarks, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var history = Section("history", "歷史", new ScrollViewer { Content = _history, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        // No horizontal scrolling: BringIntoView would shift the tree sideways and cut off icons; long names just get clipped.
        var tree = Section("tree", "樹狀目錄", new ScrollViewer { Content = _tree, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                                                 HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, _pin);
        SetDock(bookmarks, Dock.Top);
        SetDock(history, Dock.Bottom);
        Children.Add(bookmarks);
        Children.Add(history);
        Children.Add(tree);

        BuildFolderRoots();
        RebuildBookmarks();
        RebuildHistory();
        BookmarkStore.Changed += RebuildBookmarks;
        HistoryStore.Changed += () => Dispatcher.BeginInvoke(RebuildHistory, DispatcherPriority.Background);

        foreach (var t in new[] { _bookmarks, _tree })
        {
            t.AllowDrop = true;
            t.DragEnter += Tree_DragOver;
            t.DragOver += Tree_DragOver;
            t.DragLeave += Tree_DragLeave;
            t.Drop += Tree_Drop;
            t.PreviewMouseLeftButtonDown += Tree_PreviewMouseLeftButtonDown;
            t.PreviewMouseMove += Tree_PreviewMouseMove;
            t.MouseLeftButtonUp += Tree_MouseLeftButtonUp;
            t.MouseUp += Tree_MouseUp;
            t.MouseRightButtonUp += Tree_MouseRightButtonUp;
        }
        _history.AllowDrop = true;
        _history.DragEnter += History_DragOver;
        _history.DragOver += History_DragOver;
        _history.DragLeave += (_, e) => { SetHot(null); ShellDragDrop.Leave(); };
        _history.Drop += History_Drop;
        IsVisibleChanged += (_, _) => { if (IsVisible && _lastSyncPath != null) SyncTo(_lastSyncPath); };
        _dragHoverTimer.Tick += (_, _) => { _dragHoverTimer.Stop(); if (_dragHoverItem != null) _dragHoverItem.IsExpanded = true; };
    }

    // ---------- persisted state ----------

    public bool Pinned { get => _pin.IsChecked == true; set => _pin.IsChecked = value; }

    /// <summary>Which sections are collapsed (saved with the session).</summary>
    public List<string> CollapsedSections
    {
        get => _sectionBodies.Where(kv => kv.Value.Visibility != Visibility.Visible).Select(kv => kv.Key).ToList();
        set { foreach (var (key, _) in _sectionBodies) SetSection(key, !value.Contains(key)); }
    }

    // ---------- sections ----------

    private static TreeView NewTree()
    {
        var tree = new TreeView
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4, 0, 4, 4),
            Foreground = (Brush)Application.Current.FindResource("Text"),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
        // The section's ScrollViewer does the scrolling, but the TreeView's own (inner, never scrolling) one
        // swallows the mouse wheel: hand the wheel to the section instead.
        tree.PreviewMouseWheel += (_, e) =>
        {
            if (e.Handled) return;
            for (var d = VisualTreeHelper.GetParent(tree); d != null; d = VisualTreeHelper.GetParent(d))
                if (d is ScrollViewer outer)
                {
                    outer.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta / 3.0);
                    e.Handled = true;
                    return;
                }
        };
        return tree;
    }

    private FrameworkElement Section(string key, string title, FrameworkElement body, FrameworkElement? extra = null)
    {
        var chevron = new TextBlock { Text = "\uE70D", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 8,
                                      Foreground = (Brush)Application.Current.FindResource("TextDim"), VerticalAlignment = VerticalAlignment.Center, Width = 14 };
        var header = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(6, 6, 4, 2), Cursor = Cursors.Hand };
        if (extra != null) { SetDock(extra, Dock.Right); header.Children.Add(extra); }
        header.Children.Add(chevron);
        header.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold,
                                            Foreground = (Brush)Application.Current.FindResource("TextDim"), VerticalAlignment = VerticalAlignment.Center });
        header.MouseLeftButtonUp += (_, e) =>
        {
            if (extra != null && IsInside(e.OriginalSource, extra)) return;
            SetSection(key, _sectionBodies[key].Visibility != Visibility.Visible);
        };
        _sectionBodies[key] = body;
        _sectionChevrons[key] = chevron;

        var panel = new DockPanel();
        SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(body);
        return panel;
    }

    private void SetSection(string key, bool open)
    {
        _sectionBodies[key].Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _sectionChevrons[key].Text = open ? "\uE70D" : "\uE76C";
    }

    private static TreeViewItem Node(string text, FrameworkElement icon, object tag, string? tooltip = null, bool dim = false)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        icon.Margin = new Thickness(0, 0, 6, 0);
        header.Children.Add(icon);
        header.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Opacity = dim ? 0.5 : 1,
                                            FontStyle = dim ? FontStyles.Italic : FontStyles.Normal });
        return new TreeViewItem { Header = header, Tag = tag, ToolTip = tooltip, Style = (Style)Application.Current.FindResource("DarkTreeItem") };
    }

    private static Image FolderIcon(string path, bool drive = false) => new()
    {
        Source = ShellIcons.GetIcon(new FileItem { Name = path, FullPath = path, IsDirectory = true, IsDrive = drive }, large: false),
        Width = 16, Height = 16,
    };

    // ---------- 書籤 ----------

    private void RebuildBookmarks()
    {
        _bookmarks.Items.Clear();
        foreach (var b in BookmarkStore.Items) _bookmarks.Items.Add(BookmarkNode(b));
        if (BookmarkStore.Items.Count == 0)
            _bookmarks.Items.Add(new TextBlock { Text = "（還沒有書籤）", Opacity = 0.5, Margin = new Thickness(18, 2, 0, 2), Foreground = (Brush)Application.Current.FindResource("TextDim") });
    }

    private TreeViewItem BookmarkNode(Bookmark b)
    {
        var node = Node(b.Name, BookmarkBar.CreateIcon(b), b,
                        b.IsFolder ? null : b.Exists ? b.Path : b.Path + "（找不到這個資料夾）", dim: !b.IsFolder && !b.Exists);
        if (b.IsFolder)
        {
            foreach (var c in b.Children!) node.Items.Add(BookmarkNode(c));
            node.IsExpanded = _expandedBookmarkFolders.Contains(b);
            node.Expanded += (_, e) => { if (e.OriginalSource == node) _expandedBookmarkFolders.Add(b); };
            node.Collapsed += (_, e) => { if (e.OriginalSource == node) _expandedBookmarkFolders.Remove(b); };
        }
        return node;
    }

    // ---------- 樹狀目錄 ----------

    /// <summary>Folder node state. Children load lazily (a placeholder child shows the expander until then).</summary>
    private sealed class FolderNode
    {
        public required string Path { get; init; }
        public Task? Loading;
    }

    private const string Placeholder = "…";

    private void BuildFolderRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _tree.Items.Add(FolderTreeNode(profile, Path.GetFileName(profile), FolderIcon(profile)));
        foreach (var d in DriveInfo.GetDrives())
        {
            string label = d.Name.TrimEnd('\\');
            try { if (d.IsReady && d.VolumeLabel.Length > 0) label = $"{d.VolumeLabel} ({label})"; } catch { }
            _tree.Items.Add(FolderTreeNode(d.Name, label, FolderIcon(d.Name, drive: true)));
        }
    }

    private TreeViewItem FolderTreeNode(string path, string name, FrameworkElement icon)
    {
        var node = Node(name, icon, new FolderNode { Path = path }, path);
        node.Items.Add(Placeholder);
        node.Expanded += (_, e) => { if (e.OriginalSource == node) _ = EnsureChildren(node); };
        return node;
    }

    private Task EnsureChildren(TreeViewItem node)
    {
        var info = (FolderNode)node.Tag;
        if (info.Loading != null) return info.Loading;
        return info.Loading = LoadChildren(node, info.Path);
    }

    private async Task LoadChildren(TreeViewItem node, string path)
    {
        var dirs = await Task.Run(() =>
        {
            try
            {
                var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
                return new DirectoryInfo(path).EnumerateDirectories("*", options)
                    // Same rule as the file list: hide only Explorer's "protected OS" items (Hidden AND System).
                    .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != (FileAttributes.Hidden | FileAttributes.System))
                    .Select(d => d.FullName)
                    .OrderBy(p => Path.GetFileName(p), Comparer<string>.Create(StrCmpLogicalW))
                    .ToList();
            }
            catch { return new List<string>(); }
        });
        node.Items.Clear();
        var folderIcon = ShellIcons.GetIcon(new FileItem { Name = "x", FullPath = "x", IsDirectory = true }, large: false);
        foreach (var d in dirs) node.Items.Add(FolderTreeNode(d, Path.GetFileName(d), new Image { Source = folderIcon, Width = 16, Height = 16 }));
    }

    /// <summary>
    /// Follow the focused pane: expand the tree down to <paramref name="path"/> and select it, without collapsing anything.
    /// Ignored while pinned (the latest path is remembered so unpinning catches up).
    /// </summary>
    public async void SyncTo(string path)
    {
        _lastSyncPath = path;
        if (Pinned || !IsVisible) return; // hidden sidebar: catch up when it is shown again
        int version = ++_syncVersion;
        if (string.IsNullOrEmpty(path))
        {
            if (_tree.SelectedItem is TreeViewItem sel) sel.IsSelected = false;
            return;
        }

        // Deepest root containing the path (the user folder beats C:\).
        var root = _tree.Items.OfType<TreeViewItem>()
            .Where(r => IsUnder(path, ((FolderNode)r.Tag).Path))
            .OrderByDescending(r => ((FolderNode)r.Tag).Path.Length)
            .FirstOrDefault();
        if (root == null) return;

        var current = root;
        var rootPath = ((FolderNode)root.Tag).Path.TrimEnd('\\');
        var rest = path.TrimEnd('\\').Length > rootPath.Length ? path.TrimEnd('\\')[(rootPath.Length + 1)..] : "";
        foreach (var segment in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            await EnsureChildren(current);
            if (version != _syncVersion) return; // a newer sync took over
            current.IsExpanded = true;
            var next = current.Items.OfType<TreeViewItem>().FirstOrDefault(c =>
                string.Equals(Path.GetFileName(((FolderNode)c.Tag).Path), segment, StringComparison.OrdinalIgnoreCase));
            if (next == null) break; // e.g. a protected folder that the tree doesn't list
            current = next;
        }
        current.IsSelected = true;
        await Dispatcher.InvokeAsync(() => current.BringIntoView(), DispatcherPriority.Loaded);
    }

    /// <summary>Automation: selected tree path and the expanded chain.</summary>
    public string Describe()
    {
        string Sel(TreeView t) => t.SelectedItem is TreeViewItem { Tag: FolderNode n } ? n.Path : "-";
        return $"pinned={Pinned} collapsed=[{string.Join(",", CollapsedSections)}] tree.selected={Sel(_tree)}" + Environment.NewLine +
               "history: " + string.Join(" | ", HistoryStore.Items.Take(8));
    }

    private static bool IsUnder(string path, string root)
    {
        var p = path.TrimEnd('\\') + "\\";
        var r = root.TrimEnd('\\') + "\\";
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    // ---------- 歷史 ----------

    private void RebuildHistory()
    {
        _history.Children.Clear();
        foreach (var path in HistoryStore.Items)
        {
            var name = Path.GetFileName(path.TrimEnd('\\'));
            if (name.Length == 0) name = path;
            var parent = Path.GetDirectoryName(path.TrimEnd('\\')) ?? "";
            bool exists = Directory.Exists(path);
            var row = new DockPanel();
            row.Children.Add(FolderIcon(path, drive: path.Length <= 3));
            ((FrameworkElement)row.Children[0]).Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = exists ? 1 : 0.5, FontStyle = exists ? FontStyles.Normal : FontStyles.Italic,
                Inlines =
                {
                    new System.Windows.Documents.Run(name),
                    new System.Windows.Documents.Run("  " + parent) { Foreground = (Brush)Application.Current.FindResource("TextDim"), FontSize = 11 },
                },
            });
            var border = new Border { Child = row, Tag = path, Padding = new Thickness(20, 2, 6, 2), Background = Brushes.Transparent, CornerRadius = new CornerRadius(3),
                                      ToolTip = path, Margin = new Thickness(4, 0, 4, 0) };
            border.MouseEnter += (_, _) => { if (_hot != border) border.Background = (Brush)Application.Current.FindResource("SurfaceHover"); };
            border.MouseLeave += (_, _) => { if (_hot != border) border.Background = Brushes.Transparent; };
            border.MouseLeftButtonUp += (_, _) => OpenRequested?.Invoke(path, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
            border.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { OpenRequested?.Invoke(path, true); e.Handled = true; } };
            border.ContextMenu = HistoryMenu(path);
            _history.Children.Add(border);
        }
        if (HistoryStore.Items.Count == 0)
            _history.Children.Add(new TextBlock { Text = "（還沒有瀏覽紀錄）", Opacity = 0.5, Margin = new Thickness(22, 2, 0, 4), Foreground = (Brush)Application.Current.FindResource("TextDim") });
    }

    private ContextMenu HistoryMenu(string path)
    {
        var menu = new ContextMenu { Style = (Style)Application.Current.FindResource("DarkMenu") };
        MenuItem Item(string header, Action a)
        {
            var mi = new MenuItem { Header = header, Style = (Style)Application.Current.FindResource("DarkMenuItem") };
            mi.Click += (_, _) => a();
            return mi;
        }
        menu.Items.Add(Item("開啟", () => OpenRequested?.Invoke(path, false)));
        menu.Items.Add(Item("在新分頁開啟", () => OpenRequested?.Invoke(path, true)));
        menu.Items.Add(new Separator { Style = (Style)Application.Current.FindResource("DarkMenuSeparator") });
        menu.Items.Add(Item("從歷史移除", () => HistoryStore.Remove(path)));
        menu.Items.Add(Item("清除全部歷史", HistoryStore.Clear));
        return menu;
    }

    // ---------- mouse on trees ----------

    private static TreeViewItem? ItemAt(object source)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ToggleButton) return null; // the expander chevron
            if (d is TreeViewItem tvi) return tvi;
        }
        return null;
    }

    private static string? FolderPathOf(TreeViewItem item) => item.Tag switch
    {
        FolderNode n => n.Path,
        Bookmark { IsFolder: false } b => b.Path,
        _ => null,
    };

    private Point _downAt;
    private TreeViewItem? _downItem;

    private void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _downItem = ItemAt(e.OriginalSource);
        _downAt = e.GetPosition(this);
    }

    private void Tree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_downItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _downAt;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _downItem;
        _downItem = null;
        // Tree folders can be dragged like any folder (into a pane, onto a bookmark…). Bookmarks are shortcuts: not dragged.
        if (item.Tag is FolderNode n && Window.GetWindow(this) is { } owner) ShellDragDrop.DoDragDrop(owner, [n.Path]);
    }

    private void Tree_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = ItemAt(e.OriginalSource);
        if (item == null || item != _downItem) { _downItem = null; return; } // not a click (drag, or press elsewhere)
        _downItem = null;
        bool newTab = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (item.Tag is Bookmark b)
        {
            if (b.IsFolder) item.IsExpanded = !item.IsExpanded; // a bookmark folder just opens/closes here
            else BookmarkBar?.Activate(b, newTab);
        }
        else if (FolderPathOf(item) is { } path) OpenRequested?.Invoke(path, newTab);
        e.Handled = true;
    }

    private void Tree_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || ItemAt(e.OriginalSource) is not { } item) return;
        e.Handled = true;
        if (item.Tag is Bookmark b) BookmarkBar?.Activate(b, newTab: true);
        else if (FolderPathOf(item) is { } path) OpenRequested?.Invoke(path, true);
    }

    private void Tree_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(e.OriginalSource) is not { } item) return;
        e.Handled = true;
        if (item.Tag is Bookmark b)
        {
            if (BookmarkBar?.CreateMenu(b) is { } menu) { menu.PlacementTarget = item; menu.IsOpen = true; }
            return;
        }
        if (item.Tag is FolderNode n) ShowShellMenu(n.Path, e);
    }

    /// <summary>The real Explorer menu for a tree folder; opening it stays inside MultiDir.</summary>
    private void ShowShellMenu(string path, MouseButtonEventArgs e)
    {
        if (App.IsAutomation || PresentationSource.FromVisual(this) is not HwndSource src) return; // tests never show menus
        using var menu = ShellContextMenu.ForItems([path], extended: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        if (menu == null) return;
        var screen = PointToScreen(e.GetPosition(this));
        int id = menu.Track(src, (int)screen.X, (int)screen.Y);
        if (id <= 0) return;
        switch (menu.Verb(id)?.ToLowerInvariant())
        {
            case "open" or "explore": OpenRequested?.Invoke(path, false); return;
            case "opennewwindow" or "opennewtab": OpenRequested?.Invoke(path, true); return;
            case "rename":
                if (Window.GetWindow(this) is { } owner) RenameDialog.Run(owner, path);
                return;
        }
        try { menu.Invoke(id, src.Handle, Path.GetDirectoryName(path), (int)screen.X, (int)screen.Y); }
        catch (Exception ex) { SingleInstance.Trace($"sidebar menu failed: {ex.Message}"); }
    }

    // ---------- drop targets ----------

    private readonly DispatcherTimer _dragHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private TreeViewItem? _dragHoverItem;
    private Border? _hot;

    private void SetHot(Border? row)
    {
        if (_hot == row) return;
        _hot?.ClearValue(Border.BackgroundProperty);
        if (_hot != null && _hot.Tag is string) _hot.Background = Brushes.Transparent; // history rows have a local background
        _hot = row;
        if (row != null) row.Background = (Brush)Application.Current.FindResource("Selection");
    }

    private static Border? RowOf(TreeViewItem item) => item.Template?.FindName("row", item) as Border;

    private static List<string> DroppedFolders(DragEventArgs e)
    {
        try { return e.Data.GetData(DataFormats.FileDrop) is string[] p ? p.Where(Directory.Exists).ToList() : new(); }
        catch { return new(); }
    }

    private void Tree_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var item = ItemAt(e.OriginalSource);
        // Hovering a collapsed node for 0.5 s expands it (like Explorer), so you can drill down while dragging.
        if (item != _dragHoverItem)
        {
            _dragHoverItem = item;
            _dragHoverTimer.Stop();
            if (item is { IsExpanded: false, HasItems: true }) _dragHoverTimer.Start();
        }

        bool bookmarksTree = sender == _bookmarks;
        if (bookmarksTree && (item == null || item.Tag is Bookmark { IsFolder: true }) && DroppedFolders(e).Count > 0)
        {
            // Folders onto the 書籤 section (or a bookmark folder) = new bookmarks, like the bar.
            SetHot(item != null ? RowOf(item) : null);
            ShellDragDrop.Leave();
            e.Effects = DragDropEffects.Link;
            var into = item?.Tag as Bookmark;
            DragHint.Show(((UIElement)sender).PointToScreen(e.GetPosition((UIElement)sender)), "\uE734",
                          into != null ? $"加入書籤資料夾「{into.Name}」" : "加入書籤");
            return;
        }
        var target = item != null ? FolderPathOf(item) : null;
        if (target != null && Directory.Exists(target))
        {
            SetHot(RowOf(item!));
            ShellDragDrop.Over(e, (UIElement)sender, target);
        }
        else
        {
            SetHot(null);
            ShellDragDrop.Leave();
            e.Effects = DragDropEffects.None;
        }
    }

    private void Tree_DragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition((UIElement)sender);
        var fe = (FrameworkElement)sender;
        if (p.X >= 0 && p.Y >= 0 && p.X < fe.ActualWidth && p.Y < fe.ActualHeight) { ShellDragDrop.LeaveSoon(); return; }
        SetHot(null);
        _dragHoverTimer.Stop();
        _dragHoverItem = null;
        ShellDragDrop.Leave();
    }

    private void Tree_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var item = ItemAt(e.OriginalSource);
        SetHot(null);
        _dragHoverTimer.Stop();
        _dragHoverItem = null;
        if (sender == _bookmarks && (item == null || item.Tag is Bookmark { IsFolder: true }) && DroppedFolders(e) is { Count: > 0 } folders)
        {
            ShellDragDrop.Leave();
            var parent = item?.Tag as Bookmark;
            foreach (var f in folders) BookmarkStore.Add(f, -1, parent);
            return;
        }
        if (item != null && FolderPathOf(item) is { } target && Directory.Exists(target)) ShellDragDrop.Drop(e, (UIElement)sender, target);
        else ShellDragDrop.Leave();
    }

    private void History_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var row = HistoryRowAt(e.OriginalSource);
        if (row is { Tag: string path } && Directory.Exists(path))
        {
            SetHot(row);
            ShellDragDrop.Over(e, _history, path);
        }
        else
        {
            SetHot(null);
            ShellDragDrop.Leave();
            e.Effects = DragDropEffects.None;
        }
    }

    private void History_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var row = HistoryRowAt(e.OriginalSource);
        SetHot(null);
        if (row is { Tag: string path } && Directory.Exists(path)) ShellDragDrop.Drop(e, _history, path);
        else ShellDragDrop.Leave();
    }

    private Border? HistoryRowAt(object source)
    {
        for (var d = source as DependencyObject; d != null && d != _history; d = VisualTreeHelper.GetParent(d))
            if (d is Border { Tag: string } b) return b;
        return null;
    }

    private static bool IsInside(object source, DependencyObject ancestor)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d == ancestor) return true;
        return false;
    }
}
