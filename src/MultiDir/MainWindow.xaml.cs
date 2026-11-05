using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;
using AvalonDock.Themes;
using MultiDir.Services;
using MultiDir.ViewModels;
using MultiDir.Views;

namespace MultiDir;

public partial class MainWindow : Window
{
    /// <summary>Recently closed tabs for Ctrl+Shift+T: path + the pane it lived in.</summary>
    private readonly Stack<(string Path, LayoutDocumentPane? Pane)> _closed = new();
    private LayoutDocumentPane? _lastPane;
    private int _nextTabId;

    /// <summary>Big "drop on a pane's tab strip / toolbar" re-dock zone for floating windows.</summary>
    public HeaderDocking HeaderDocking { get; }
    private LayoutDocument? _lastDoc;

    public MainWindow()
    {
        InitializeComponent();
        Dock.Theme = new Vs2013DarkTheme();
        HeaderDocking = new HeaderDocking(Dock, HeaderDropHighlight);

        // Default layout (decision #6): Q-dir style four-grid.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var start = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            FolderTab.ThisPc,
        };
        var docs = start.Select(p => CreateDocument(new FolderTab(p))).ToList();
        ApplyPreset(4, docs.Select((d, i) => (d, i)).ToList());
        docs[0].IsActive = true;
    }

    // ---------- documents (tabs) ----------

    private LayoutDocument CreateDocument(FolderTab tab)
    {
        var view = new FolderView { DataContext = tab };
        // ContentId = stable per-tab id (used by automation and, later, by session save/restore).
        var doc = new LayoutDocument { Content = view, Title = tab.Title, ToolTip = PathLabel(tab.Path), ContentId = (++_nextTabId).ToString() };
        tab.Navigated += (_, _) =>
        {
            doc.Title = tab.Title;
            doc.ToolTip = PathLabel(tab.Path);
            if (doc == _lastDoc) UpdateWindowTitle(tab);
        };
        view.OpenInNewTabRequested += (v, path) => OpenTab(path, doc.Parent as LayoutDocumentPane, activate: false);
        return doc;
    }

    /// <summary>Opens a folder as a new tab in the given pane (default: the last focused pane).</summary>
    public LayoutDocument OpenTab(string path, LayoutDocumentPane? pane = null, bool activate = true)
    {
        pane = UsablePane(pane) ?? UsablePane(_lastPane) ?? Dock.Layout.Descendents().OfType<LayoutDocumentPane>().First();
        var doc = CreateDocument(new FolderTab(path));
        pane.Children.Add(doc);
        if (activate) doc.IsActive = true;
        return doc;
    }

    private LayoutDocumentPane? UsablePane(LayoutDocumentPane? pane) =>
        pane != null && pane.Root == Dock.Layout ? pane : null;

    private LayoutDocument? ActiveDocument =>
        Dock.Layout.ActiveContent as LayoutDocument ?? (_lastDoc?.Root == Dock.Layout ? _lastDoc : null);

    private void Dock_ActiveContentChanged(object? sender, EventArgs e)
    {
        if (Dock.Layout.ActiveContent is not LayoutDocument doc) return;
        _lastDoc = doc;
        _lastPane = doc.Parent as LayoutDocumentPane ?? _lastPane;
        if (doc.Content is FolderView { Tab: { } tab } view)
        {
            UpdateWindowTitle(tab);
            Dispatcher.BeginInvoke(view.FocusList, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void UpdateWindowTitle(FolderTab tab) => Title = $"{PathLabel(tab.Path)} — MultiDir";

    private static string PathLabel(string path) => path == FolderTab.ThisPc ? "本機" : path;

    private void Dock_DocumentClosed(object? sender, DocumentClosedEventArgs e)
    {
        if (e.Document.Content is FolderView { Tab: { } tab })
            _closed.Push((tab.Path, _lastPane));
    }

    // ---------- browser-style shortcuts (the only ones we design) ----------

    private void NewTab_Executed(object sender, ExecutedRoutedEventArgs e) =>
        OpenTab(FolderTab.ThisPc, ActiveDocument?.Parent as LayoutDocumentPane);

    private void CloseTab_Executed(object sender, ExecutedRoutedEventArgs e) => ActiveDocument?.Close();

    private void ReopenTab_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_closed.Count == 0) return;
        var (path, pane) = _closed.Pop();
        OpenTab(path, pane);
    }

    /// <summary>Middle-click on a tab header closes it, like a browser.</summary>
    private void Dock_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        for (var d = e.OriginalSource as DependencyObject; d != null; d = ParentOf(d))
        {
            if (d is LayoutDocumentTabItem { Model: LayoutDocument doc })
            {
                doc.Close();
                e.Handled = true;
                return;
            }
            if (d is FolderView) return; // middle-click inside the file list is handled there
        }
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    // ---------- tab headers as drop targets (hover 0.5 s switches to the tab) ----------

    private readonly System.Windows.Threading.DispatcherTimer _tabHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private LayoutDocument? _tabHoverDoc;

    private static LayoutDocument? TabDocFromSource(object source)
    {
        for (var d = source as DependencyObject; d != null; d = ParentOf(d))
        {
            if (d is LayoutDocumentTabItem { Model: LayoutDocument doc }) return doc;
            if (d is FolderView) return null;
        }
        return null;
    }

    private void Dock_DragOver(object sender, DragEventArgs e)
    {
        // FolderView handles its own area; anything reaching here is chrome (tab headers, splitters…).
        var doc = TabDocFromSource(e.OriginalSource);
        if (doc != _tabHoverDoc)
        {
            _tabHoverDoc = doc;
            _tabHoverTimer.Stop();
            if (doc != null && !doc.IsSelected)
            {
                _tabHoverTimer.Tick -= TabHover_Tick;
                _tabHoverTimer.Tick += TabHover_Tick;
                _tabHoverTimer.Start();
            }
        }
        string? folder = doc?.Content is FolderView { Tab.Path: var p } && p != FolderTab.ThisPc ? p : null;
        ShellDragDrop.Over(e, Dock, folder);
    }

    private void TabHover_Tick(object? sender, EventArgs e)
    {
        _tabHoverTimer.Stop();
        if (_tabHoverDoc != null) _tabHoverDoc.IsSelected = true;
    }

    private void Dock_DragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(Dock);
        if (p.X >= 0 && p.Y >= 0 && p.X < Dock.ActualWidth && p.Y < Dock.ActualHeight) return;
        _tabHoverTimer.Stop();
        _tabHoverDoc = null;
        ShellDragDrop.Leave();
    }

    private void Dock_Drop(object sender, DragEventArgs e)
    {
        _tabHoverTimer.Stop();
        var doc = TabDocFromSource(e.OriginalSource);
        _tabHoverDoc = null;
        string? folder = doc?.Content is FolderView { Tab.Path: var p } && p != FolderTab.ThisPc ? p : null;
        ShellDragDrop.Drop(e, Dock, folder);
    }

    // ---------- layout presets ----------

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        int count = int.Parse((string)((FrameworkElement)sender).Tag);
        // Keep every open tab; tabs from pane k go to preset pane k % count.
        var panes = Dock.Layout.Descendents().OfType<LayoutDocumentPane>().ToList();
        var docs = new List<(LayoutDocument, int)>();
        for (int i = 0; i < panes.Count; i++)
            foreach (var d in panes[i].Children.OfType<LayoutDocument>().ToList())
                docs.Add((d, i));
        // Floating tabs come back into the grid too.
        foreach (var d in Dock.Layout.Descendents().OfType<LayoutDocument>().Where(d => docs.All(x => x.Item1 != d)).ToList())
            docs.Add((d, 0));

        var active = ActiveDocument;
        ApplyPreset(count, docs);
        if (active != null) active.IsActive = true;
    }

    private void ApplyPreset(int count, List<(LayoutDocument Doc, int PaneIndex)> docs)
    {
        foreach (var (doc, _) in docs)
            (doc.Parent as ILayoutContainer)?.RemoveChild(doc);

        var panes = Enumerable.Range(0, count).Select(_ => new LayoutDocumentPane()).ToList();
        LayoutDocumentPaneGroup group;
        if (count == 4)
        {
            group = new LayoutDocumentPaneGroup { Orientation = Orientation.Horizontal };
            var left = new LayoutDocumentPaneGroup { Orientation = Orientation.Vertical };
            var right = new LayoutDocumentPaneGroup { Orientation = Orientation.Vertical };
            left.Children.Add(panes[0]); left.Children.Add(panes[2]);
            right.Children.Add(panes[1]); right.Children.Add(panes[3]);
            group.Children.Add(left); group.Children.Add(right);
        }
        else
        {
            group = new LayoutDocumentPaneGroup { Orientation = Orientation.Horizontal };
            foreach (var p in panes) group.Children.Add(p);
        }

        foreach (var (doc, index) in docs)
            panes[index % count].Children.Add(doc);

        // A preset never leaves an empty pane: fill gaps with "本機".
        foreach (var p in panes.Where(p => p.ChildrenCount == 0))
            p.Children.Add(CreateDocument(new FolderTab(FolderTab.ThisPc)));

        var root = new LayoutRoot();
        root.RootPanel = new LayoutPanel(group);
        Dock.Layout = root;
        _lastPane = panes[0];
    }
}
