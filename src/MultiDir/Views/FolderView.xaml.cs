using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MultiDir.Models;
using MultiDir.Services;
using MultiDir.ViewModels;

namespace MultiDir.Views;

public partial class FolderView : UserControl
{
    private FolderTab? _tab;

    public FolderView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as FolderTab);
        List.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(ColumnHeader_Click));
        // Keep ☆ in sync when bookmarks change anywhere (bar right-click → 移除, another pane's star…).
        // Subscribed only while loaded, so closed tabs aren't kept alive by the static store.
        Action onBookmarks = UpdateStar;
        Loaded += (_, _) => { BookmarkStore.Changed += onBookmarks; UpdateStar(); };
        Unloaded += (_, _) => BookmarkStore.Changed -= onBookmarks;
    }

    private void Star_Click(object sender, RoutedEventArgs e)
    {
        if (_tab == null || _tab.Path == FolderTab.ThisPc) return;
        if (BookmarkStore.Find(_tab.Path) is { } existing) BookmarkStore.Remove(existing);
        else BookmarkStore.Add(_tab.Path);
    }

    private void UpdateStar()
    {
        bool can = _tab != null && _tab.Path != FolderTab.ThisPc;
        bool on = can && BookmarkStore.Contains(_tab!.Path);
        StarBtn.IsEnabled = can;
        StarBtn.Content = on ? "\uE735" : "\uE734";
        StarBtn.Foreground = on ? new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x39))
                           : can ? (Brush)FindResource("Text") : new SolidColorBrush(Color.FromRgb(0x55, 0x58, 0x5E));
        StarBtn.ToolTip = on ? "移除書籤" : "加入書籤";
    }

    /// <summary>Middle-click on a folder → the host opens it in a new tab in the same pane.</summary>
    public event Action<FolderView, string>? OpenInNewTabRequested;

    public FolderTab? Tab => _tab;

    /// <summary>Height of the pane toolbar (← → ↑ / address / filter / view) — part of the re-dock drop zone.</summary>
    public double ToolbarHeight => Toolbar.ActualHeight;

    private void Attach(FolderTab? tab)
    {
        if (_tab != null) _tab.PropertyChanged -= Tab_PropertyChanged;
        _tab = tab;
        if (tab == null) return;
        tab.PropertyChanged += Tab_PropertyChanged;
        ApplyViewMode();
        UpdateCrumbs();
        UpdateStar();
    }

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FolderTab.ViewMode): ApplyViewMode(); break;
            case nameof(FolderTab.SortKey) or nameof(FolderTab.SortDescending): UpdateSortHeaders(); break;
            case nameof(FolderTab.Segments): UpdateCrumbs(); UpdateStar(); ScrollToTop(); break;
        }
    }

    // ---------- sorting (list mode column headers) ----------

    private static readonly (string Label, SortKey Key)[] Columns =
        [("名稱", SortKey.Name), ("修改日期", SortKey.Modified), ("類型", SortKey.Type), ("大小", SortKey.Size)];

    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (_tab == null || e.OriginalSource is not GridViewColumnHeader { Column: { } column }) return;
        int i = (List.View as GridView)?.Columns.IndexOf(column) ?? -1;
        if (i < 0 || i >= Columns.Length) return;
        _tab.SortBy(Columns[i].Key);
        e.Handled = true;
    }

    /// <summary>Header text with ▲/▼ on the sorted column.</summary>
    private void UpdateSortHeaders()
    {
        if (_tab == null || List.View is not GridView grid) return;
        for (int i = 0; i < grid.Columns.Count && i < Columns.Length; i++)
            grid.Columns[i].Header = Columns[i].Key == _tab.SortKey
                ? $"{Columns[i].Label}  {(_tab.SortDescending ? "▼" : "▲")}"
                : Columns[i].Label;
    }

    // ---------- view modes ----------

    /// <summary>Only swaps panel / template / container style; the data and the ListView stay the same.</summary>
    private void ApplyViewMode()
    {
        if (_tab == null) return;
        var mode = _tab.ViewMode;
        ExtraLargeBtn.IsChecked = mode == ViewMode.ExtraLarge;
        LargeBtn.IsChecked = mode == ViewMode.Large;
        ListBtn.IsChecked = mode == ViewMode.List;
        SmallBtn.IsChecked = mode == ViewMode.Small;

        if (mode == ViewMode.List)
        {
            List.ItemTemplate = null;
            List.ItemContainerStyle = (Style)Application.Current.Resources["RowItemStyle"];
            List.ItemsPanel = (ItemsPanelTemplate)Resources["StackPanelTemplate"];
            List.View = (GridView)Resources["DetailsView"];
            UpdateSortHeaders();
        }
        else
        {
            List.View = null;
            List.ItemContainerStyle = (Style)Application.Current.Resources["TileItemStyle"];
            List.ItemsPanel = (ItemsPanelTemplate)Resources["WrapPanelTemplate"];
            List.ItemTemplate = (DataTemplate)Resources[mode switch
            {
                ViewMode.ExtraLarge => "ExtraLargeTemplate",
                ViewMode.Large => "LargeTemplate",
                _ => "SmallTemplate",
            }];
        }
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (_tab == null) return;
        _tab.ViewMode = Enum.Parse<ViewMode>((string)((FrameworkElement)sender).Tag);
        ApplyViewMode(); // re-sync toggle states even when the mode didn't change
    }

    private void List_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_tab == null || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        // Ctrl+wheel: up = bigger (Small → List → Large), down = smaller.
        ViewMode[] order = [ViewMode.Small, ViewMode.List, ViewMode.Large, ViewMode.ExtraLarge];
        int i = Array.IndexOf(order, _tab.ViewMode) + (e.Delta > 0 ? 1 : -1);
        _tab.ViewMode = order[Math.Clamp(i, 0, order.Length - 1)];
        e.Handled = true;
    }

    // ---------- navigation ----------

    private void Back_Click(object sender, RoutedEventArgs e) => _tab?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => _tab?.GoForward();
    private void Up_Click(object sender, RoutedEventArgs e) => _tab?.GoUp();
    private void Crumb_Click(object sender, RoutedEventArgs e) => _tab?.Navigate((string)((Button)sender).Tag);

    private void UpdateCrumbs()
    {
        if (_tab == null) return;
        Crumbs.ItemsSource = _tab.Segments
            .Select((s, i) => new Crumb(s.Label, s.Path, i == 0 ? Visibility.Collapsed : Visibility.Visible))
            .ToList();
    }

    private sealed record Crumb(string Label, string Path, Visibility SeparatorVisibility);

    private void CrumbHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_tab == null || IsEditingPath) return;
        IsEditingPath = true;
        PathBox.Text = _tab.Path;
        PathBox.Visibility = Visibility.Visible;
        Crumbs.Visibility = Visibility.Hidden;
        e.Handled = true;

        // Take focus only after this click has finished activating the pane (AvalonDock moves focus on
        // activation); otherwise the box would lose focus immediately and close again.
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsEditingPath) return;
            PathBox.Focus();
            PathBox.SelectAll();
            // Clicking somewhere that can't take focus (toolbar, another pane's chrome…) must also commit.
            if (Window.GetWindow(this) is { } w) w.PreviewMouseDown += Window_PreviewMouseDownWhileEditing;
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>True while the breadcrumb is in text-edit mode (the host must not steal focus then).</summary>
    public bool IsEditingPath { get; private set; }

    private void Window_PreviewMouseDownWhileEditing(object sender, MouseButtonEventArgs e)
    {
        if (PathBox.IsMouseOver) return;
        EndPathEdit(commit: true);
    }

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { EndPathEdit(commit: true); List.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndPathEdit(commit: false); List.Focus(); e.Handled = true; }
    }

    /// <summary>Leaving the box (click elsewhere, Tab…) also goes to the typed path; only Esc cancels.</summary>
    private void PathBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Only a real hand-over of focus ends editing; ignore the box never having had focus yet.
        if (PathBox.IsKeyboardFocusWithin) return;
        EndPathEdit(commit: true);
    }

    private void EndPathEdit(bool commit)
    {
        if (!IsEditingPath) return; // already ended (Enter/Esc, then the focus loss that follows)
        IsEditingPath = false;
        if (Window.GetWindow(this) is { } w) w.PreviewMouseDown -= Window_PreviewMouseDownWhileEditing;
        PathBox.Visibility = Visibility.Collapsed;
        Crumbs.Visibility = Visibility.Visible;
        if (commit && _tab != null && !string.Equals(PathBox.Text.Trim(), _tab.Path, StringComparison.OrdinalIgnoreCase))
            _tab.Navigate(PathBox.Text);
    }

    private void ScrollToTop()
    {
        if (List.Items.Count > 0) List.ScrollIntoView(List.Items[0]);
    }

    // ---------- items ----------

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (ItemFromSource(e.OriginalSource) is { } item) Open(item);
    }

    private void List_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) { _tab?.GoBack(); e.Handled = true; return; }
        if (e.ChangedButton == MouseButton.XButton2) { _tab?.GoForward(); e.Handled = true; return; }
        if (e.ChangedButton == MouseButton.Middle && ItemFromSource(e.OriginalSource) is { IsDirectory: true } dir)
        {
            OpenInNewTabRequested?.Invoke(this, dir.FullPath);
            e.Handled = true;
        }
    }

    private void Open(FileItem item)
    {
        if (item.IsDirectory) { _tab?.Navigate(item.FullPath); return; }
        try { Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "無法開啟", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static FileItem? ItemFromSource(object source)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ListViewItem lvi) return lvi.DataContext as FileItem;
            if (d is ListView) return null;
        }
        return null;
    }

    // ---------- type-to-filter (decision #13) ----------

    private void List_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_tab == null || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        _tab.Filter += e.Text;
        e.Handled = true;
    }

    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_tab == null) return;
        switch (e.Key)
        {
            case Key.Back when _tab.Filter.Length > 0:
                _tab.Filter = _tab.Filter[..^1];
                e.Handled = true;
                break;
            case Key.Back:
                _tab.GoBack();
                e.Handled = true;
                break;
            case Key.Escape when _tab.Filter.Length > 0:
                _tab.Filter = "";
                e.Handled = true;
                break;
            case Key.Enter when List.SelectedItem is FileItem item:
                Open(item);
                e.Handled = true;
                break;
            case Key.Left when (Keyboard.Modifiers & ModifierKeys.Alt) != 0:
            case Key.System when e.SystemKey == Key.Left:
                _tab.GoBack(); e.Handled = true; break;
            case Key.System when e.SystemKey == Key.Right:
                _tab.GoForward(); e.Handled = true; break;
            case Key.System when e.SystemKey == Key.Up:
                _tab.GoUp(); e.Handled = true; break;
            case Key.F5:
                _tab.Refresh(); e.Handled = true; break;
            case Key.Apps:
            case Key.System when e.SystemKey == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0:
            case Key.F10 when (Keyboard.Modifiers & ModifierKeys.Shift) != 0:
                ShowContextMenuForKeyboard(); e.Handled = true; break;
            case Key.F2 when List.SelectedItem is FileItem { IsDrive: false } renameItem:
                Rename(renameItem); e.Handled = true; break;
            case Key.Delete when !App.IsAutomation && SelectedPaths() is { Count: > 0 } toDelete:
                // Same as Explorer: 資源回收筒 with confirmation; Shift+Delete deletes permanently (also confirmed).
                using (var cm = ShellContextMenu.ForItems(toDelete))
                    cm?.InvokeVerb("delete", Hwnd(), _tab.Path, shift: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                e.Handled = true; break;
        }
    }

    private void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _tab != null) { _tab.Filter = ""; List.Focus(); e.Handled = true; }
        else if (e.Key is Key.Down or Key.Enter) { List.Focus(); e.Handled = true; }
    }

    /// <summary>Called when the pane is activated; never steal focus from something the user just clicked in this pane.</summary>
    public void FocusList()
    {
        if (IsEditingPath || IsKeyboardFocusWithin) return;
        List.Focus();
    }

    // ---------- drag source (decision #7: drag & drop is THE way to move files) ----------

    private Point _dragStart;
    private bool _dragArmed;
    private FileItem? _deferredSelect;

    private void List_PreviewMouseButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemFromSource(e.OriginalSource);
        _dragged = false;
        _dragArmed = item != null;
        _dragStart = e.GetPosition(List);
        _deferredSelect = null;

        // WPF collapses a multi-selection on mouse-down, which would make dragging several files impossible.
        // If the click lands on an already-selected item, keep the selection and decide on mouse-up instead.
        if (item != null && e.ChangedButton == MouseButton.Left && e.ClickCount == 1 &&
            Keyboard.Modifiers == ModifierKeys.None && List.SelectedItems.Contains(item) && List.SelectedItems.Count > 1)
        {
            _deferredSelect = item;
            List.Focus();
            e.Handled = true;
        }
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || _tab == null) return;
        if (e.LeftButton != MouseButtonState.Pressed && e.RightButton != MouseButtonState.Pressed) { _dragArmed = false; return; }
        var delta = e.GetPosition(List) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragArmed = false;
        _deferredSelect = null;
        var paths = List.SelectedItems.Cast<FileItem>().Where(i => !i.IsDrive).Select(i => i.FullPath).ToList();
        if (paths.Count == 0) return;
        _dragged = true;
        ShellDragDrop.DoDragDrop(Window.GetWindow(this)!, paths);
        e.Handled = true;
    }

    private void List_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = false;
        if (e.ChangedButton == MouseButton.Right)
        {
            if (!_dragged && !IsInside<System.Windows.Controls.Primitives.ScrollBar>(e.OriginalSource)
                && !IsInside<GridViewColumnHeader>(e.OriginalSource)) ShowContextMenu(e);
            _dragged = false;
            e.Handled = true;
            return;
        }
        if (_deferredSelect != null && e.ChangedButton == MouseButton.Left)
        {
            List.SelectedItems.Clear();
            List.SelectedItem = _deferredSelect;
        }
        _deferredSelect = null;
    }

    // ---------- native context menu (the real Explorer menu) ----------

    private bool _dragged;

    /// <summary>Automation: the last menu built (test instances never show it on screen).</summary>
    public static (FolderView View, ShellContextMenu Menu, List<string> Paths)? LastAutomationMenu { get; private set; }

    private IntPtr Hwnd() => PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource src ? src.Handle : IntPtr.Zero;

    private List<string> SelectedPaths() =>
        List.SelectedItems.Cast<FileItem>().Select(i => i.FullPath).ToList();

    private void ShowContextMenu(MouseButtonEventArgs e)
    {
        var item = ItemFromSource(e.OriginalSource);
        if (item != null && !List.SelectedItems.Contains(item))
        {
            List.SelectedItems.Clear();
            List.SelectedItem = item;
        }
        else if (item == null)
        {
            List.SelectedItems.Clear(); // empty area → the folder's own menu (新增 / 貼上 / 內容)
        }
        var screen = List.PointToScreen(e.GetPosition(List));
        OpenMenu(item != null, (int)screen.X, (int)screen.Y);
    }

    private static bool IsInside<T>(object source) where T : DependencyObject
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is T) return true;
        return false;
    }

    private void ShowContextMenuForKeyboard()
    {
        // Like Explorer: at the focused/selected item, else at the top-left of the list.
        var anchor = (List.SelectedItem is { } sel ? List.ItemContainerGenerator.ContainerFromItem(sel) as FrameworkElement : null) ?? List;
        var screen = anchor.PointToScreen(new Point(16, anchor.ActualHeight > 40 ? 16 : anchor.ActualHeight / 2));
        OpenMenu(List.SelectedItems.Count > 0, (int)screen.X, (int)screen.Y);
    }

    private void OpenMenu(bool forItems, int x, int y)
    {
        if (_tab == null) return;
        bool extended = (Keyboard.Modifiers & ModifierKeys.Shift) != 0; // Shift+右鍵: 複製路徑、在此開啟 PowerShell…
        var paths = forItems ? SelectedPaths() : new List<string>();
        if (!forItems && _tab.Path == FolderTab.ThisPc) return; // "本機" has no folder background menu worth showing
        var menu = forItems ? ShellContextMenu.ForItems(paths, extended) : ShellContextMenu.ForFolderBackground(_tab.Path, extended);
        if (menu == null) return;
        menu.AddCustomTop(CustomItems(forItems ? paths : [_tab.Path], forItems));

        if (App.IsAutomation)
        {
            LastAutomationMenu?.Menu.Dispose();
            LastAutomationMenu = (this, menu, paths);
            return;
        }
        try
        {
            if (PresentationSource.FromVisual(this) is not System.Windows.Interop.HwndSource src) return;
            int id = menu.Track(src, x, y);
            if (id > 0) ExecuteMenuCommand(menu, id, paths, x, y);
        }
        finally { menu.Dispose(); }
    }

    /// <summary>Host hook: the other panes a folder can be opened in (label + open action), for 「在其他窗格開啟 ▸」.</summary>
    public static Func<FolderView, IReadOnlyList<(string Label, Action<string> Open)>>? OtherPanes { get; set; }

    /// <summary>MultiDir's own entries at the top of the native menu.</summary>
    private List<ShellContextMenu.CustomItem> CustomItems(List<string> paths, bool forItems)
    {
        var list = new List<ShellContextMenu.CustomItem>();
        var items = List.SelectedItems.Cast<FileItem>().ToList();
        if (forItems && items.Count > 0 && items.All(i => i.IsDirectory))
        {
            var folders = items.Select(i => i.FullPath).ToList();
            list.Add(new("在新分頁開啟", () => { foreach (var f in folders) OpenInNewTabRequested?.Invoke(this, f); }));
            var panes = OtherPanes?.Invoke(this) ?? [];
            list.Add(panes.Count == 0
                ? new("在其他窗格開啟", Enabled: false)
                : new("在其他窗格開啟", Children: panes.Select(p => new ShellContextMenu.CustomItem(p.Label, () => { foreach (var f in folders) p.Open(f); })).ToList()));
        }
        if (paths.Count > 0 && paths.All(p => p.Length > 0))
            list.Add(new(paths.Count > 1 ? $"複製路徑（{paths.Count} 個）" : "複製路徑", () =>
            {
                try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); } catch { /* clipboard busy */ }
            }));
        return list;
    }

    /// <summary>
    /// Runs a chosen menu command. Opening folders and renaming stay inside MultiDir (navigate / new tab / rename box);
    /// everything else is executed by the shell exactly as Explorer would.
    /// </summary>
    public void ExecuteMenuCommand(ShellContextMenu menu, int id, List<string> paths, int x = 0, int y = 0)
    {
        if (_tab == null) return;
        if (menu.RunCustom(id)) return; // one of MultiDir's own items
        var verb = menu.Verb(id)?.ToLowerInvariant();
        var items = List.SelectedItems.Cast<FileItem>().ToList();
        bool allFolders = items.Count > 0 && items.All(i => i.IsDirectory);
        switch (verb)
        {
            case "open" or "explore" when allFolders && items.Count == 1:
                _tab.Navigate(items[0].FullPath);
                return;
            case "open" or "explore" or "opennewwindow" or "opennewtab" when allFolders:
                foreach (var dir in items) OpenInNewTabRequested?.Invoke(this, dir.FullPath);
                return;
            case "rename" when items.Count == 1 && !items[0].IsDrive:
                Rename(items[0]);
                return;
        }
        try
        {
            menu.Invoke(id, Hwnd(), _tab.Path == FolderTab.ThisPc ? null : _tab.Path, x, y,
                shift: (Keyboard.Modifiers & ModifierKeys.Shift) != 0, control: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        }
        catch (Exception ex) { SingleInstance.Trace($"menu invoke failed ({verb}): {ex.Message}"); }
    }

    private void Rename(FileItem item)
    {
        if (App.IsAutomation || Window.GetWindow(this) is not { } owner) return; // never pop a dialog from a test instance
        RenameDialog.Run(owner, item.FullPath);
        List.Focus();
    }

    // ---------- drop targets ----------

    private ListViewItem? _dropHot;

    /// <summary>Folder under the cursor wins; otherwise the pane's own folder ("本機" itself is not a target).</summary>
    private string? DropFolder(DragEventArgs e)
    {
        var item = ItemFromSource(e.OriginalSource);
        if (item is { IsDirectory: true }) return item.FullPath;
        return _tab is { Path: var p } && p != FolderTab.ThisPc ? p : null;
    }

    private void SetDropHot(ListViewItem? lvi)
    {
        if (_dropHot == lvi) return;
        _dropHot?.ClearValue(BackgroundProperty);
        _dropHot = lvi;
        _dropHot?.SetValue(BackgroundProperty, Application.Current.Resources["Selection"]);
    }

    private static ListViewItem? ContainerFromSource(object source)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ListViewItem { DataContext: FileItem { IsDirectory: true } } lvi) return lvi;
            if (d is ListView) return null;
        }
        return null;
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        SetDropHot(ContainerFromSource(e.OriginalSource));
        ShellDragDrop.Over(e, List, DropFolder(e));
    }

    private void List_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when crossing child elements; only leave when the pointer really left the list.
        var p = e.GetPosition(List);
        if (p.X >= 0 && p.Y >= 0 && p.X < List.ActualWidth && p.Y < List.ActualHeight) return;
        SetDropHot(null);
        ShellDragDrop.Leave();
    }

    private void List_Drop(object sender, DragEventArgs e)
    {
        SetDropHot(null);
        ShellDragDrop.Drop(e, List, DropFolder(e));
    }

    private static string? CrumbFolder(DragEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is Button { Tag: string path }) return path == FolderTab.ThisPc ? null : path;
        return null;
    }

    private void Crumbs_DragOver(object sender, DragEventArgs e) => ShellDragDrop.Over(e, Crumbs, CrumbFolder(e));

    private void Crumbs_DragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(Crumbs);
        if (p.X >= 0 && p.Y >= 0 && p.X < Crumbs.ActualWidth && p.Y < Crumbs.ActualHeight) return;
        ShellDragDrop.Leave();
    }

    private void Crumbs_Drop(object sender, DragEventArgs e) => ShellDragDrop.Drop(e, Crumbs, CrumbFolder(e));
}
