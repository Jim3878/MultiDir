using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
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
    private LayoutDocumentPane? _lastDockedPane; // last focused pane inside the main window (not floating)
    private int _nextTabId;

    /// <summary>Big "drop on a pane's tab strip / toolbar" re-dock zone for floating windows.</summary>
    public HeaderDocking HeaderDocking { get; }
    private LayoutDocument? _lastDoc;

    public MainWindow()
    {
        InitializeComponent();
        SingleInstance.Trace("startup: InitializeComponent");
        Dock.Theme = new Vs2013DarkTheme();
        SingleInstance.Trace("startup: theme");
        HeaderDocking = new HeaderDocking(Dock, HeaderDropHighlight)
        {
            IsOverBookmarkBar = pt => Bookmarks.ContainsScreenPoint(pt),
            BookmarkBarHover = (pt, docs) => Bookmarks.ShowTabHover(pt,
                docs.Select(d => d.Content is FolderView { Tab.Path: var p } ? p : "").Where(p => p.Length > 0).ToList()),
            DroppedOnBookmarkBar = TabsDroppedOnBookmarkBar,
        };
        NewTabButtons.Register(pane => OpenTab(FolderTab.ThisPc, pane));
        FocusFrame.Register();
        // Panes come and go (splits, closing the last tab, presets): keep the frame's "more than one pane" rule current.
        Dock.LayoutChanged += (_, _) =>
        {
            Dock.Layout.Updated += (_, _) => { FocusFrame.Refresh(); RefreshTabTitles(); };
            FocusFrame.Refresh();
            RefreshTabTitles();
        };

        SidePanel.BookmarkBar = Bookmarks;
        FolderView.OtherPanes = OtherPanesFor;
        if (!TryRestoreSession()) ApplyDefaultLayout();
        SingleInstance.Trace("startup: layout");
        UpdateDefaultHandlerButton();
        FileOperationQueue.Changed += UpdateQueueButton;

        // Session is saved on close, plus every 30 s so a crash or power loss loses little.
        Closing += (_, e) =>
        {
            SaveSession();
            if (TrayIcon == null || _exiting) return;
            e.Cancel = true; // closing the window keeps MultiDir in the tray (decision: default folder handler)
            HideToTray();
        };
        var autosave = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        autosave.Tick += (_, _) => SaveSession();
        autosave.Start();
    }

    private void ApplyDefaultLayout()
    {
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

    // ---------- default folder handler: tray + folders from other launches ----------

    public TrayIcon? TrayIcon { get; set; }
    private bool _exiting;
    private List<Window> _hiddenFloating = new();

    private void HideToTray()
    {
        // Floating tab windows go (and come back) with the main window.
        _hiddenFloating = Application.Current.Windows.OfType<Window>().Where(w => w != this && w.IsVisible).ToList();
        foreach (var w in _hiddenFloating) w.Hide();
        Hide();
        TrayIcon?.ShowHiddenHint();
    }

    public void ShowFromTray()
    {
        if (!IsVisible)
        {
            Show();
            foreach (var w in _hiddenFloating) w.Show();
            _hiddenFloating.Clear();
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!App.IsAutomation) Activate(); // an off-screen test window must never take the user's focus
    }

    public void ExitApp()
    {
        _exiting = true;
        Close();
    }

    /// <summary>
    /// Folders handed to MultiDir from outside (command line, folder double-click, another launch):
    /// each becomes a new tab in the last focused pane. Non-folders (virtual shell locations) go to Explorer.
    /// </summary>
    public void OpenExternal(IReadOnlyList<string> args, bool activate = true)
    {
        if (activate) ShowFromTray();
        var select = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i].Trim();
            // explorer-style "/select,<file>" (also split over two args): open its folder and select it.
            if (arg.StartsWith("/select", StringComparison.OrdinalIgnoreCase))
            {
                var file = arg.Length > 8 ? arg[8..] : i + 1 < args.Count ? args[++i] : "";
                if ((file = file.Trim().Trim('"')).Length > 0) select.Add(file);
                continue;
            }
            if (arg == "::thispc") { OpenTab(FolderTab.ThisPc); continue; }
            if (DefaultFolderHandler.NormalizeArgument(arg) is { } folder) OpenTab(folder);
            else if (arg.Trim('"').Length > 0)
            {
                ExplorerTakeover.LetThrough(); // we hand this one to Explorer on purpose
                try { System.Diagnostics.Process.Start("explorer.exe", $"\"{arg.Trim('"')}\""); } catch { }
            }
        }
        // One tab per folder, with all of its files selected.
        foreach (var group in select.GroupBy(f => Path.GetDirectoryName(f.TrimEnd('\\')) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(group.Key)) continue;
            var doc = OpenTab(group.Key);
            (doc.Content as FolderView)?.SelectWhenLoaded(group.Select(f => Path.GetFileName(f.TrimEnd('\\'))).ToList());
        }
        SingleInstance.Trace("tabs opened");
        Dispatcher.BeginInvoke(() => SingleInstance.Trace("rendered"), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private static readonly string NL = Environment.NewLine;

    // ---------- copy / move queue ----------

    private void UpdateQueueButton()
    {
        var jobs = FileOperationQueue.Snapshot();
        QueueBtn.Visibility = jobs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (jobs.Count == 0) { Flyout.CloseAll(); return; }
        int waiting = jobs.Count(j => !j.Running);
        var running = jobs.FirstOrDefault(j => j.Running);
        QueueBtn.Content = running == null ? $"⧉ {waiting} 個複製工作等待中"
            : waiting == 0 ? $"⧉ {(running.Move ? "移動" : "複製")}中"
            : $"⧉ {(running.Move ? "移動" : "複製")}中 · 另有 {waiting} 個排隊";
        QueueBtn.ToolTip = string.Join(Environment.NewLine, jobs.Select(j => (j.Running ? "▶ " : "· ") + j.Describe()));
    }

    /// <summary>The queue: the running job (its progress window has 取消) and the waiting ones (click = drop it).</summary>
    private void QueueBtn_Click(object sender, RoutedEventArgs e)
    {
        var rows = new List<FlyoutItem?>();
        foreach (var job in FileOperationQueue.Snapshot())
        {
            var j = job;
            rows.Add(j.Running
                ? new FlyoutItem { Header = "▶ " + j.Describe(), ToolTip = "進行中：在 Windows 的進度視窗可以暫停或取消", Dim = true }
                : new FlyoutItem { Header = "等待：" + j.Describe(), ToolTip = "按一下取消這個工作", Click = () => FileOperationQueue.Cancel(j) });
        }
        Flyout.ShowBelow(QueueBtn, rows);
    }

    private void DefaultHandler_Click(object sender, RoutedEventArgs e)
    {
        if (App.IsAutomation) return;
        if (DefaultFolderHandler.IsRegistered)
        {
            if (MessageBox.Show(this, "要取消 MultiDir 為預設的資料夾開啟工具嗎？" + NL + "資料夾會改回用檔案總管開啟。", "MultiDir",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            DefaultFolderHandler.Unregister();
            ExplorerTakeover.Stop();
        }
        else
        {
            if (MessageBox.Show(this, "要把 MultiDir 設為預設的資料夾開啟工具嗎？" + NL + NL +
                    "• 在桌面或其他程式開啟資料夾、磁碟時，會變成 MultiDir 的新分頁" + NL +
                    "• 只影響你的帳號，隨時可以再按這個按鈕取消" + NL +
                    "• 「開啟檔案所在位置」與 Win+E 也會改開 MultiDir，並選取該檔案" + NL +
                    "• 控制台、資源回收筒等特殊資料夾仍由檔案總管開啟；按住 Shift 開啟則一律用檔案總管" + NL + NL +
                    "註冊的程式位置：" + NL + Environment.ProcessPath, "MultiDir",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            DefaultFolderHandler.Register();
            ExplorerTakeover.Start(paths => OpenExternal(paths));
        }
        UpdateDefaultHandlerButton();
    }

    private void UpdateDefaultHandlerButton()
    {
        bool on = DefaultFolderHandler.IsRegistered;
        DefaultHandlerBtn.Content = on ? "✓ 預設開啟工具" : "設為預設開啟工具";
        DefaultHandlerBtn.IsEnabled = !App.IsAutomation;
        DefaultHandlerBtn.ToolTip = on ? "MultiDir 是目前的資料夾開啟工具，按一下取消" : "讓資料夾、磁碟預設用 MultiDir 開啟";
    }

    /// <summary>
    /// Panes other than <paramref name="view"/>'s, labelled by their current tab (「Downloads」, 「src（浮動）」…),
    /// each opening a folder as a new active tab there. Used by 「在其他窗格開啟 ▸」.
    /// </summary>
    private IReadOnlyList<(string Label, Action<string> Open)> OtherPanesFor(FolderView view)
    {
        var own = Dock.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(d => d.Content == view)?.Parent;
        var panes = Dock.Layout.Descendents().OfType<LayoutDocumentPane>().Where(p => p != own && p.ChildrenCount > 0).ToList();
        var labels = panes.Select(p =>
        {
            var title = (p.SelectedContent ?? p.Children[0]).Title;
            return p.FindParent<LayoutDocumentFloatingWindow>() != null ? $"{title}（浮動）" : title;
        }).ToList();
        // Same title in two panes: number them so they can be told apart.
        for (int i = 0; i < labels.Count; i++)
            if (labels.Count(l => l == labels[i]) > 1) labels[i] = $"{labels[i]}（{i + 1}）";
        return panes.Select((p, i) => (labels[i], (Action<string>)(path => OpenTab(path, p)))).ToList();
    }

    /// <summary>
    /// A tab dragged onto the bookmark bar: bookmark its folder there, then put the tab back where it came from
    /// (it was only floated by the drag).
    /// </summary>
    internal void TabsDroppedOnBookmarkBar(List<LayoutDocument> docs, Point screenPx)
    {
        Bookmarks.AddAt(screenPx, docs.Select(d => d.Content is FolderView { Tab.Path: var p } ? p : "").Where(p => p.Length > 0));
        // Where it lived before floating: AvalonDock remembers it (internal), else the last docked pane we saw active.
        var previous = typeof(LayoutContent).GetProperty("PreviousContainer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        bool Docked(LayoutDocumentPane? p) => p != null && p.Root == Dock.Layout && p.FindParent<LayoutDocumentFloatingWindow>() == null;
        var home = docs.Select(d => previous?.GetValue(d) as LayoutDocumentPane).FirstOrDefault(Docked)
                   ?? (Docked(_lastDockedPane) ? _lastDockedPane : null)
                   ?? Dock.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault(Docked);
        if (home != null) HeaderDocking.DockInto(docs, home);
    }

    // ---------- sidebar ----------

    private double _sidebarWidth = 240;

    private void SidebarBtn_Click(object sender, RoutedEventArgs e) => SetSidebar(SidebarBtn.IsChecked == true);

    /// <summary>Shows/hides the whole sidebar, remembering its width.</summary>
    public void SetSidebar(bool visible)
    {
        if (!visible && SidePanel.Visibility == Visibility.Visible && SidebarColumn.ActualWidth > 0) _sidebarWidth = SidebarColumn.ActualWidth;
        SidePanel.Visibility = SidebarSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = visible ? new GridLength(_sidebarWidth) : new GridLength(0);
        SidebarBtn.IsChecked = visible;
    }

    // ---------- bookmark bar ----------

    /// <summary>Bookmark click → the active tab goes there; Ctrl/middle-click → new tab in the active pane.</summary>
    private void Bookmarks_OpenRequested(string path, bool newTab)
    {
        var doc = ActiveDocument;
        if (!newTab && doc?.Content is FolderView { Tab: { } tab } view)
        {
            tab.Navigate(path);
            doc.IsActive = true;
            view.FocusList();
        }
        else OpenTab(path, doc?.Parent as LayoutDocumentPane);
    }

    // ---------- session save / restore ----------

    /// <summary>Saves splits, tab order, floating windows, each tab's folder + history and the window placement.</summary>
    public void SaveSession()
    {
        var state = new SessionState { ActiveTab = ActiveDocument?.ContentId, Maximized = WindowState == WindowState.Maximized };
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        (state.Left, state.Top, state.Width, state.Height) = (bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        foreach (var doc in Dock.Layout.Descendents().OfType<LayoutDocument>())
            if (doc.ContentId != null && doc.Content is FolderView { Tab: { } tab })
                state.Tabs[doc.ContentId] = new TabState { Path = tab.Path, Back = tab.BackHistory, Forward = tab.ForwardHistory };
        state.SidebarVisible = SidePanel.Visibility == Visibility.Visible;
        state.SidebarWidth = state.SidebarVisible ? SidebarColumn.ActualWidth : _sidebarWidth;
        state.TreePinned = SidePanel.Pinned;
        state.CollapsedSections = SidePanel.CollapsedSections;
        SessionStore.Save(state, w => new XmlLayoutSerializer(Dock).Serialize(w));
        ViewModeStore.Save();
        SortStore.Save();
    }

    private bool TryRestoreSession()
    {
        var state = SessionStore.LoadState();
        if (state == null || state.Tabs.Count == 0) return false;
        var serializer = new XmlLayoutSerializer(Dock);
        serializer.LayoutSerializationCallback += (_, e) =>
        {
            if (e.Model is LayoutDocument doc && doc.ContentId != null && state.Tabs.TryGetValue(doc.ContentId, out var saved))
            {
                var tab = new FolderTab(saved.Path);
                tab.RestoreHistory(saved.Back, saved.Forward);
                e.Content = WireDocument(doc, tab);
            }
            else e.Cancel = true;
        };
        try
        {
            using var reader = new StreamReader(SessionStore.LayoutPath);
            serializer.Deserialize(reader);
        }
        catch { return false; }

        var docs = Dock.Layout.Descendents().OfType<LayoutDocument>().Where(d => d.Content is FolderView).ToList();
        if (docs.Count == 0) return false;
        _nextTabId = docs.Max(d => int.TryParse(d.ContentId, out var id) ? id : 0);
        _lastPane = docs[0].Parent as LayoutDocumentPane;

        // Window placement, only if it is still on a connected screen.
        var saved = new Rect(state.Left, state.Top, state.Width, state.Height);
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (saved.Width >= 400 && saved.Height >= 300 && screen.IntersectsWith(saved))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            (Left, Top, Width, Height) = (saved.Left, saved.Top, saved.Width, saved.Height);
            if (state.Maximized) WindowState = WindowState.Maximized;
        }

        _sidebarWidth = Math.Max(120, state.SidebarWidth);
        SetSidebar(state.SidebarVisible);
        SidePanel.Pinned = state.TreePinned;
        SidePanel.CollapsedSections = state.CollapsedSections;

        (docs.FirstOrDefault(d => d.ContentId == state.ActiveTab) ?? docs[0]).IsActive = true;
        return true;
    }

    // ---------- documents (tabs) ----------

    private LayoutDocument CreateDocument(FolderTab tab)
    {
        // ContentId = stable per-tab id (used by automation and session save/restore).
        var doc = new LayoutDocument { ContentId = (++_nextTabId).ToString() };
        doc.Content = WireDocument(doc, tab);
        return doc;
    }

    /// <summary>Same-named tabs (in any pane or floating window) get "專案A\src" / "專案A\…\src" labels.</summary>
    private void RefreshTabTitles()
    {
        if (Dock.Layout == null) return;
        var docs = Dock.Layout.Descendents().OfType<LayoutDocument>()
            .Where(d => d.Content is FolderView { DataContext: FolderTab }).ToList();
        var tabs = docs.Select(d => (FolderTab)((FolderView)d.Content).DataContext).ToList();
        var labels = TabTitles.Compute(tabs.Select(t => (t.Path, t.Title)).ToList());
        for (int i = 0; i < docs.Count; i++)
            if (docs[i].Title != labels[i]) docs[i].Title = labels[i];
    }

    /// <summary>Creates the tab's view and keeps the tab header in sync with its folder.</summary>
    private FolderView WireDocument(LayoutDocument doc, FolderTab tab)
    {
        var view = new FolderView { DataContext = tab };
        doc.Title = tab.Title;
        doc.ToolTip = PathLabel(tab.Path);
        tab.Navigated += (_, _) =>
        {
            doc.Title = tab.Title;
            doc.ToolTip = PathLabel(tab.Path);
            RefreshTabTitles();
            HistoryStore.Add(tab.Path);
            if (doc == _lastDoc)
            {
                UpdateWindowTitle(tab);
                SidePanel.SyncTo(tab.Path); // the tree follows the focused tab
            }
        };
        view.OpenInNewTabRequested += (v, path) => OpenTab(path, doc.Parent as LayoutDocumentPane, activate: false);
        return view;
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
        if (_lastPane?.FindParent<LayoutDocumentFloatingWindow>() == null) _lastDockedPane = _lastPane;
        FocusFrame.SetActive(_lastPane);
        if (doc.Content is FolderView { Tab: { } tab } view)
        {
            UpdateWindowTitle(tab);
            SidePanel.SyncTo(tab.Path);
            // The very first keyboard focus initializes text input services (~50-100 ms): not before the first frame.
            Dispatcher.BeginInvoke(view.FocusList, _firstFrameShown ? System.Windows.Threading.DispatcherPriority.Input
                                                                    : System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

    private bool _firstFrameShown;
    protected override void OnContentRendered(EventArgs e)
    {
        _firstFrameShown = true;
        base.OnContentRendered(e);
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
