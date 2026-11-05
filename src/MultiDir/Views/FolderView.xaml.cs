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
    }

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FolderTab.ViewMode): ApplyViewMode(); break;
            case nameof(FolderTab.Segments): UpdateCrumbs(); ScrollToTop(); break;
        }
    }

    // ---------- view modes ----------

    /// <summary>Only swaps panel / template / container style; the data and the ListView stay the same.</summary>
    private void ApplyViewMode()
    {
        if (_tab == null) return;
        var mode = _tab.ViewMode;
        LargeBtn.IsChecked = mode == ViewMode.Large;
        ListBtn.IsChecked = mode == ViewMode.List;
        SmallBtn.IsChecked = mode == ViewMode.Small;

        if (mode == ViewMode.List)
        {
            List.ItemTemplate = null;
            List.ItemContainerStyle = (Style)Application.Current.Resources["RowItemStyle"];
            List.ItemsPanel = (ItemsPanelTemplate)Resources["StackPanelTemplate"];
            List.View = (GridView)Resources["DetailsView"];
        }
        else
        {
            List.View = null;
            List.ItemContainerStyle = (Style)Application.Current.Resources["TileItemStyle"];
            List.ItemsPanel = (ItemsPanelTemplate)Resources["WrapPanelTemplate"];
            List.ItemTemplate = (DataTemplate)Resources[mode == ViewMode.Large ? "LargeTemplate" : "SmallTemplate"];
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
        ViewMode[] order = [ViewMode.Small, ViewMode.List, ViewMode.Large];
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
        ShellDragDrop.DoDragDrop(Window.GetWindow(this)!, paths);
        e.Handled = true;
    }

    private void List_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = false;
        if (_deferredSelect != null && e.ChangedButton == MouseButton.Left)
        {
            List.SelectedItems.Clear();
            List.SelectedItem = _deferredSelect;
        }
        _deferredSelect = null;
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
