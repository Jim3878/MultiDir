using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvalonDock.Layout;
using MultiDir.ViewModels;
using MultiDir.Views;

namespace MultiDir.Services;

/// <summary>
/// Test automation channel (started with <c>--automation</c>). Lets a tester drive MultiDir over a named pipe
/// without touching the user's real mouse or keyboard:
/// <list type="bullet">
/// <item>screenshots are rendered in-process (window may be off-screen / behind other windows),</item>
/// <item>clicks / wheel / typing are posted as window messages to MultiDir's own HWNDs ("virtual cursor"),</item>
/// <item>layout and file-drop operations are invoked directly through the same code paths the UI uses.</item>
/// </list>
/// Press-and-drag gestures cannot be virtualised (OLE drag and window-move loops read the physical cursor).
/// Protocol: one command line in, response lines out, terminated by a line "&lt;&lt;END&gt;&gt;".
/// </summary>
public sealed class AutomationServer
{
    public const string PipeName = "MultiDir.Automation";
    private readonly MainWindow _window;

    private AutomationServer(MainWindow window) => _window = window;

    public static void Start(MainWindow window)
    {
        var server = new AutomationServer(window);
        // A restored session's floating windows get clamped onto the user's screen; move them off it.
        server.KeepFloatingOffScreen();
        window.Dispatcher.BeginInvoke(server.KeepFloatingOffScreen, System.Windows.Threading.DispatcherPriority.Loaded);
        var thread = new Thread(server.Loop) { IsBackground = true, Name = "Automation pipe" };
        thread.Start();
    }

    private void Loop()
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte);
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe, new UTF8Encoding(false));
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    string response;
                    try
                    {
                        response = _window.Dispatcher.Invoke(() => Execute(line));
                    }
                    catch (Exception ex) { response = "ERROR " + (ex.InnerException ?? ex).Message; }
                    writer.WriteLine(response);
                    writer.WriteLine("<<END>>");
                }
            }
            // Client went away mid-reply (or anything else): never let the pipe thread die, accept the next client.
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            catch (Exception ex) { SingleInstance.Trace("automation pipe: " + ex.Message); }
        }
    }

    // ---------- commands (run on the UI thread) ----------

    private string Execute(string line)
    {
        var args = Tokenize(line);
        if (args.Count == 0) return "";
        var cmd = args[0].ToLowerInvariant();
        args.RemoveAt(0);
        return cmd switch
        {
            "help" => Help,
            "layout" => Layout(),
            "shot" => Shot(args),
            "click" => Click(args, double_: false),
            "dblclick" => Click(args, double_: true),
            "wheel" => Wheel(args),
            "type" => TypeText(args),
            "key" => Key(args),
            "tab" => TabCommand(args),
            "drop" => Drop(args),
            "wait" => Wait(args),
            "zone" => Zone(args),
            "plus" => Plus(),
            "save" => Save(),
            "menu" => Menu(args),
            "bookmark" => BookmarkCommand(args),
            "sidebar" => SidebarCommand(args),
            _ => "ERROR unknown command; try: help",
        };
    }

    private const string Help = """
        layout                                 panes / tabs / floating windows (with indexes)
        shot <file.png> [win]                  render a window in-process (win: 0 = main, 1.. = floating)
        click <x> <y> [left|right|middle] [win]  post a click at window pixel coords (as in the shot)
        dblclick <x> <y> [win]                 post a double click
        wheel <x> <y> <delta> [ctrl] [win]     post a wheel notch (120 = up)
        type <text>                            post characters to the focused element
        key <enter|esc|back|tab|f2|f5|up|down|left|right|delete> [win]
        tab float <id>                         float tab <id> (ids from layout)
        tab dock <id> <pane>                   move tab <id> into pane #pane
        tab close <id> | tab activate <id>
        tab new <pane> <path>                  open a folder as a new tab in pane #pane
        tab nav <id> <path> | tab mode <id> extralarge|large|list|small | tab filter <id> <text>
        tab sort <id> name|modified|type|size | tab items <id> [n]   (sort like a header click; list first n items)
        drop <folder> <path>... [--right] [--shift|--ctrl]   shell drop through the real drop code path
        menu                                   list the context menu the last right-click built (never shown on screen)
        menu pick <verb or text>               run an item of that menu (avoid ones that open dialogs or touch the clipboard)
        bookmark list | add <path> | open <index> [new] | remove <index> | move <index> <newIndex>
        bookmark folder <name> | into <index> <folderIndex>   (indexes = top level of the bar)
        sidebar [show|hide|pin|unpin|collapse <key>|expand <key>]   state of the sidebar (keys: bookmarks, tree, history)
        save                                   save the session now (as on close); restart with: mdctl start keep
        zone <x> <y>                           which pane header (re-dock drop zone) is at main-window pixel x,y
        wait <ms>
        """;

    private string Layout()
    {
        var sb = new StringBuilder();
        var root = _window.Dock.Layout;
        var panes = root.Descendents().OfType<LayoutDocumentPane>().ToList();
        var docs = Docs();
        sb.AppendLine($"active: tab {(root.ActiveContent as LayoutDocument)?.ContentId ?? "-"}");
        for (int p = 0; p < panes.Count; p++)
        {
            var floating = panes[p].FindParent<LayoutDocumentFloatingWindow>() != null;
            sb.AppendLine($"pane {p}{(floating ? " (floating)" : "")}:");
            foreach (var d in panes[p].Children.OfType<LayoutDocument>())
            {
                var tab = (d.Content as FolderView)?.Tab;
                sb.AppendLine($"  tab {d.ContentId}{(d.IsSelected ? " *" : "")} {d.Title} | {tab?.Path} | {tab?.ViewMode} | {tab?.Status}");
            }
        }
        var wins = Windows();
        for (int i = 0; i < wins.Count; i++)
            sb.AppendLine($"win {i}: {(i == 0 ? "main" : "floating")} {(int)wins[i].ActualWidth}x{(int)wins[i].ActualHeight} @ {(int)wins[i].Left},{(int)wins[i].Top}");
        return sb.ToString().TrimEnd();
    }

    private string Shot(List<string> a)
    {
        if (a.Count < 1) return "ERROR shot <file.png> [win]";
        var win = Win(a, 1);
        var dpi = VisualTreeHelper.GetDpi(win);
        var visual = (Visual)win.Content;
        // Floating windows host their content in a nested HwndSource (an HwndHost), which renders as blank.
        var winHwnd = new WindowInteropHelper(win).Handle;
        if (win is AvalonDock.Controls.LayoutFloatingWindowControl && PresentationSource.CurrentSources.OfType<HwndSource>()
                .FirstOrDefault(s => s.Handle != winHwnd && IsChildOf(s.Handle, winHwnd) && s.RootVisual is FrameworkElement) is { } nested)
            visual = nested.RootVisual;
        var size = ((FrameworkElement)visual).RenderSize;
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi.DpiScaleX), (int)Math.Ceiling(size.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // Background first: the window brush is not part of the content's own rendering.
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(win.Background ?? Brushes.Black, null, new Rect(size));
        bmp.Render(bg);
        bmp.Render(visual);
        // Adorners (e.g. the "+" new-tab buttons) live in the window's adorner layer, outside Content.
        if (System.Windows.Documents.AdornerLayer.GetAdornerLayer(visual) is { } layer) bmp.Render(layer);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(a[0])) enc.Save(fs);
        return $"OK {bmp.PixelWidth}x{bmp.PixelHeight} -> {a[0]} (coords for click = these pixels, client area)";
    }

    private static bool IsChildOf(IntPtr child, IntPtr parent)
    {
        for (var h = GetParent(child); h != IntPtr.Zero; h = GetParent(h)) if (h == parent) return true;
        return false;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);

    // Input is synthesised as WPF routed events on the hit element (Preview→bubble, down→up), i.e. the same
    // route a real click takes. Window messages can't be used: WPF drops mouse messages when the physical
    // cursor is not over the window, which is exactly the situation for an off-screen test instance.

    private string Click(List<string> a, bool double_)
    {
        if (a.Count < 2) return "ERROR click <x> <y> [left|right|middle] [win]";
        int x = int.Parse(a[0]), y = int.Parse(a[1]);
        bool hasButton = a.Count > 2 && !int.TryParse(a[2], out _);
        var button = (hasButton ? a[2].ToLowerInvariant() : "left") switch
        {
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => MouseButton.Left,
        };
        var win = Win(a, hasButton ? 3 : 2);
        var target = HitTest(win, x, y);
        if (target == null) return "ERROR nothing at that point";

        // Buttons only press when the PHYSICAL button is down (ButtonState reads the real mouse), so a
        // left click on a button is delivered as the button's own click (Click event + Command), like a real one.
        if (button == MouseButton.Left && !double_ && FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(target) is { IsEnabled: true } btn)
        {
            // GridViewColumnHeader.OnClick ignores the click when the PHYSICAL mouse is outside it: raise Click directly.
            if (btn is System.Windows.Controls.GridViewColumnHeader)
                btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, btn));
            else typeof(System.Windows.Controls.Primitives.ButtonBase)
                .GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(btn, null);
            Pump();
            return $"OK clicked {Describe(btn)}";
        }

        PressRelease(target, button, 1);
        if (double_) PressRelease(target, button, 2);
        Pump();
        return $"OK hit {Describe(target)}";
    }

    private static void PressRelease(UIElement target, MouseButton button, int clickCount)
    {
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button);
        SetClickCount(down, clickCount);
        RaisePair(target, down, Mouse.PreviewMouseDownEvent, Mouse.MouseDownEvent);
        var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button);
        SetClickCount(up, clickCount);
        RaisePair(target, up, Mouse.PreviewMouseUpEvent, Mouse.MouseUpEvent);
        if (Mouse.Captured != null) Mouse.Capture(null);
    }

    private static void RaisePair(UIElement target, RoutedEventArgs args, RoutedEvent preview, RoutedEvent bubble)
    {
        args.RoutedEvent = preview;
        target.RaiseEvent(args);
        if (args.Handled) return;
        args.RoutedEvent = bubble;
        target.RaiseEvent(args);
    }

    private static void SetClickCount(MouseButtonEventArgs e, int count) =>
        typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.GetSetMethod(nonPublic: true)!.Invoke(e, [count]);

    private string Wheel(List<string> a)
    {
        if (a.Count < 3) return "ERROR wheel <x> <y> <delta> [ctrl] [win]";
        int x = int.Parse(a[0]), y = int.Parse(a[1]), delta = int.Parse(a[2]);
        bool ctrl = a.Skip(3).Any(s => s.Equals("ctrl", StringComparison.OrdinalIgnoreCase));
        var win = Win(a.Where(s => !s.Equals("ctrl", StringComparison.OrdinalIgnoreCase)).ToList(), 3);
        // WPF reads Ctrl from the real keyboard state, so a virtual Ctrl+wheel is applied through the view.
        if (ctrl)
        {
            var view = ViewAt(win, x, y);
            if (view?.Tab == null) return "ERROR no folder view at that point";
            ViewMode[] order = [ViewMode.Small, ViewMode.List, ViewMode.Large, ViewMode.ExtraLarge];
            int i = Array.IndexOf(order, view.Tab.ViewMode) + (delta > 0 ? 1 : -1);
            view.Tab.ViewMode = order[Math.Clamp(i, 0, order.Length - 1)];
            return $"OK view mode = {view.Tab.ViewMode} (ctrl+wheel applied through the view)";
        }
        var target = HitTest(win, x, y);
        if (target == null) return "ERROR nothing at that point";
        RaisePair(target, new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta),
            Mouse.PreviewMouseWheelEvent, Mouse.MouseWheelEvent);
        Pump();
        return $"OK hit {Describe(target)}";
    }

    private string TypeText(List<string> a)
    {
        var text = string.Join(" ", a);
        var target = FocusTarget();
        if (target == null) return "ERROR nothing focused";
        TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, target, text));
        Pump();
        return $"OK typed {text.Length} chars into {Describe(target)}";
    }

    private string Key(List<string> a)
    {
        if (a.Count < 1) return "ERROR key <name>";
        var key = a[0].ToLowerInvariant() switch
        {
            "enter" => System.Windows.Input.Key.Enter, "esc" => System.Windows.Input.Key.Escape,
            "back" => System.Windows.Input.Key.Back, "tab" => System.Windows.Input.Key.Tab,
            "f2" => System.Windows.Input.Key.F2, "f5" => System.Windows.Input.Key.F5,
            "up" => System.Windows.Input.Key.Up, "down" => System.Windows.Input.Key.Down,
            "left" => System.Windows.Input.Key.Left, "right" => System.Windows.Input.Key.Right,
            "delete" => System.Windows.Input.Key.Delete,
            _ => throw new ArgumentException("unknown key " + a[0]),
        };
        var target = FocusTarget();
        if (target == null) return "ERROR nothing focused";
        var source = PresentationSource.FromVisual((Visual)target) ?? PresentationSource.FromVisual(_window)!;
        RaisePair(target, new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key),
            Keyboard.PreviewKeyDownEvent, Keyboard.KeyDownEvent);
        RaisePair(target, new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key),
            Keyboard.PreviewKeyUpEvent, Keyboard.KeyUpEvent);
        Pump();
        return $"OK {a[0]} -> {Describe(target)}";
    }

    /// <summary>The element that would receive keys: keyboard focus, else the logical focus of a MultiDir window.</summary>
    private UIElement? FocusTarget()
    {
        if (Keyboard.FocusedElement is UIElement k) return k;
        foreach (var w in Windows())
            if (FocusManager.GetFocusedElement(w) is UIElement f) return f;
        return null;
    }

    private static UIElement? HitTest(Window win, int x, int y)
    {
        var dpi = VisualTreeHelper.GetDpi(win);
        var content = (UIElement)win.Content;
        var pt = win.TranslatePoint(new Point(x / dpi.DpiScaleX, y / dpi.DpiScaleY), content);
        // Adorners (the "+" new-tab buttons) sit above the content, like a real click would see them.
        if (System.Windows.Documents.AdornerLayer.GetAdornerLayer(content) is { } layer
            && layer.InputHitTest(content.TranslatePoint(pt, layer)) is DependencyObject hit && FindUi(hit) is { } ui && ui is not System.Windows.Documents.AdornerLayer) return ui;
        return content.InputHitTest(pt) as UIElement ?? (content.InputHitTest(pt) as DependencyObject is { } d ? FindUi(d) : null);
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var c = d; c != null; c = VisualTreeHelper.GetParent(c)) if (c is T t) return t;
        return null;
    }

    private static UIElement? FindUi(DependencyObject d)
    {
        for (var c = d; c != null; c = VisualTreeHelper.GetParent(c)) if (c is UIElement u) return u;
        return null;
    }

    private static string Describe(object o)
    {
        var name = o.GetType().Name;
        if (o is FrameworkElement { DataContext: Models.FileItem fi }) name += $" [{fi.Name}]";
        else if (o is System.Windows.Controls.TextBlock tb) name += $" \"{tb.Text}\"";
        else if (o is System.Windows.Controls.ContentControl { Content: string c }) name += $" \"{c}\"";
        return name;
    }

    private string TabCommand(List<string> a)
    {
        if (a.Count < 2) return "ERROR see help";
        var sub = a[0].ToLowerInvariant();
        var docs = Docs();
        var panes = _window.Dock.Layout.Descendents().OfType<LayoutDocumentPane>().ToList();
        // Tabs are addressed by their stable id (ContentId), which never changes when tabs move or float.
        LayoutDocument Doc(int id) => docs.FirstOrDefault(d => d.ContentId == id.ToString()) ?? throw new ArgumentException($"no tab {id}");
        FolderTab Tab(int i) => (Doc(i).Content as FolderView)?.Tab ?? throw new ArgumentException("not a folder tab");

        switch (sub)
        {
            case "float":
                Doc(int.Parse(a[1])).Float();
                Pump();
                KeepFloatingOffScreen();
                break;
            case "close": Doc(int.Parse(a[1])).Close(); break;
            case "activate": Doc(int.Parse(a[1])).IsActive = true; break;
            case "dock":
            {
                var doc = Doc(int.Parse(a[1]));
                var pane = panes[int.Parse(a[2])];
                (doc.Parent as ILayoutContainer)?.RemoveChild(doc);
                pane.Children.Add(doc);
                doc.IsActive = true;
                _window.Dock.Layout.CollectGarbage(); // drop panes / floating windows left empty, like a real drag
                break;
            }
            case "new":
                _window.OpenTab(string.Join(" ", a.Skip(2)), panes[int.Parse(a[1])]);
                break;
            case "nav": Tab(int.Parse(a[1])).Navigate(string.Join(" ", a.Skip(2))); break;
            case "mode": Tab(int.Parse(a[1])).ViewMode = Enum.Parse<ViewMode>(a[2], ignoreCase: true); break;
            case "sort": Tab(int.Parse(a[1])).SortBy(Enum.Parse<SortKey>(a[2], ignoreCase: true)); break;
            case "items":
                var t = Tab(int.Parse(a[1]));
                return string.Join(Environment.NewLine, t.Items.Take(a.Count > 2 ? int.Parse(a[2]) : 10)
                    .Select(i => $"  {i.Name} | {i.ModifiedText} | {i.TypeName} | {i.SizeText}")) + Environment.NewLine + $"  (sort {t.SortKey} {(t.SortDescending ? "desc" : "asc")})";
            case "filter": Tab(int.Parse(a[1])).Filter = string.Join(" ", a.Skip(2)); break;
            default: return "ERROR unknown tab command";
        }
        Pump();
        return "OK\n" + Layout();
    }

    private static string Drop(List<string> a)
    {
        bool right = a.Remove("--right");
        int mods = (a.Remove("--shift") ? ShellDragDrop.MK_SHIFT : 0) | (a.Remove("--ctrl") ? ShellDragDrop.MK_CONTROL : 0);
        if (a.Count < 2) return "ERROR drop <folder> <path>... [--right] [--shift|--ctrl]";
        int effect = ShellDragDrop.TestDrop(a.Skip(1).ToList(), a[0], right, mods);
        // A shell "optimized move" reports DROPEFFECT_NONE by OLE convention: the target already moved the files.
        string name = effect switch { 0 => "none (a move also reports none: check the folders)", 1 => "copy", 2 => "move", 4 => "link", _ => effect.ToString() };
        return $"OK effect={name}";
    }

    private string Zone(List<string> a)
    {
        if (a.Count < 2) return "ERROR zone <x> <y>";
        var dpi = VisualTreeHelper.GetDpi(_window);
        var content = (UIElement)_window.Content;
        var screen = content.PointToScreen(new Point(int.Parse(a[0]) / dpi.DpiScaleX, int.Parse(a[1]) / dpi.DpiScaleY));
        var zone = _window.HeaderDocking.ZoneAt(screen);
        if (zone == null) return "OK no zone";
        var panes = _window.Dock.Layout.Descendents().OfType<LayoutDocumentPane>().ToList();
        var b = zone.Value.Bounds;
        return $"OK pane {panes.IndexOf(zone.Value.Pane)} header {b.X:0},{b.Y:0} {b.Width:0}x{b.Height:0} (DIP)";
    }

    /// <summary>Where each pane's "+" new-tab button is (main-window DIP), or why it is missing.</summary>
    private string Plus()
    {
        var sb = new StringBuilder();
        // Floating windows host their content in a nested HwndSource, so walk every presentation source.
        foreach (var src in PresentationSource.CurrentSources.OfType<PresentationSource>().Where(s => s.RootVisual != null))
            foreach (var pc in Find<AvalonDock.Controls.LayoutDocumentPaneControl>(src.RootVisual))
            {
                var w = (UIElement)src.RootVisual;
                var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(pc);
                var ads = layer?.GetAdorners(pc);
                var btn = ads?.SelectMany(ad => Find<System.Windows.Controls.Button>(ad)).FirstOrDefault();
                sb.AppendLine(layer == null ? "pane: no adorner layer"
                    : btn == null ? $"pane: adorners={ads?.Length ?? 0}, no button"
                    : $"pane{(pc.Model?.FindParent<LayoutDocumentFloatingWindow>() != null ? " (floating)" : "")}: + at {btn.TranslatePoint(new Point(0, 0), w):0} visible={btn.IsVisible} size={btn.ActualWidth:0}x{btn.ActualHeight:0}");
            }
        return sb.ToString().TrimEnd();
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in Find<T>(c)) yield return d;
        }
    }

    private static string Menu(List<string> a)
    {
        if (FolderView.LastAutomationMenu is not { } last) return "ERROR no menu yet (right-click first: click <x> <y> right)";
        var items = last.Menu.Items();
        if (a.Count >= 2 && a[0] == "pick")
        {
            var key = string.Join(" ", a.Skip(1));
            var item = items.FirstOrDefault(i => string.Equals(i.Verb, key, StringComparison.OrdinalIgnoreCase))
                    ?? items.FirstOrDefault(i => i.Id > 0 && i.Text.Contains(key, StringComparison.OrdinalIgnoreCase));
            if (item == null) return $"ERROR no item '{key}'";
            last.View.ExecuteMenuCommand(last.Menu, item.Id, last.Paths);
            Pump();
            return $"OK ran '{item.Text}' (verb {item.Verb ?? "-"})";
        }
        var sb = new StringBuilder($"menu for {(last.Paths.Count == 0 ? "folder background" : string.Join(", ", last.Paths))}:{Environment.NewLine}");
        foreach (var i in items) sb.AppendLine($"{new string(' ', 2 + i.Depth * 2)}{i.Text}{(i.Verb != null ? $"  [{i.Verb}]" : "")}");
        return sb.ToString().TrimEnd();
    }

    private string BookmarkCommand(List<string> a)
    {
        if (a.Count == 0 || a[0] == "list") { _window.UpdateLayout(); return "bookmarks:" + Environment.NewLine + _window.Bookmarks.Describe(); }
        switch (a[0])
        {
            case "add": BookmarkStore.Add(string.Join(" ", a.Skip(1))); break;
            case "open": _window.Bookmarks.OpenAt(int.Parse(a[1]), a.Count > 2 && a[2] == "new"); break;
            case "remove": BookmarkStore.Remove(BookmarkStore.Items[int.Parse(a[1])]); break;
            case "move": BookmarkStore.Move(BookmarkStore.Items[int.Parse(a[1])], int.Parse(a[2])); break;
            case "folder": BookmarkStore.AddFolder(string.Join(" ", a.Skip(1))); break;
            case "into": BookmarkStore.Move(BookmarkStore.Items[int.Parse(a[1])], -1, BookmarkStore.Items[int.Parse(a[2])]); break;
            default: return "ERROR bookmark list|add|open|remove|move|folder|into";
        }
        Pump();
        _window.UpdateLayout();
        return "OK" + Environment.NewLine + _window.Bookmarks.Describe();
    }

    private string SidebarCommand(List<string> a)
    {
        var side = _window.SidePanel;
        switch (a.FirstOrDefault())
        {
            case "show": _window.SetSidebar(true); break;
            case "hide": _window.SetSidebar(false); break;
            case "pin": side.Pinned = true; break;
            case "unpin": side.Pinned = false; break;
            case "collapse": side.CollapsedSections = side.CollapsedSections.Append(a[1]).ToList(); break;
            case "expand": side.CollapsedSections = side.CollapsedSections.Where(k => k != a[1]).ToList(); break;
        }
        var until = DateTime.Now.AddMilliseconds(600); // let the tree finish loading/expanding
        while (DateTime.Now < until) { Pump(); Thread.Sleep(20); }
        return $"visible={side.IsVisible} " + side.Describe();
    }

    private string Save() { _window.SaveSession(); return $"OK saved to {AppPaths.DataDir}"; }

    private static string Wait(List<string> a)
    {
        var until = DateTime.Now.AddMilliseconds(a.Count > 0 ? int.Parse(a[0]) : 300);
        while (DateTime.Now < until) { Pump(); Thread.Sleep(20); }
        return "OK";
    }

    // ---------- helpers ----------

    private List<LayoutDocument> Docs() => _window.Dock.Layout.Descendents().OfType<LayoutDocument>().ToList();

    /// <summary>Floating windows open at the cursor or (0,0); keep them off-screen next to the main window.</summary>
    private void KeepFloatingOffScreen()
    {
        int i = 0;
        foreach (var w in Windows().Skip(1))
        {
            w.Left = _window.Left + 40 * ++i;
            w.Top = _window.Top + 40 * i;
        }
    }

    /// <summary>Main window first, then AvalonDock floating windows.</summary>
    private List<Window> Windows()
    {
        var list = new List<Window> { _window };
        list.AddRange(Application.Current.Windows.OfType<Window>().Where(w => w != _window && w.IsVisible));
        return list;
    }

    private Window Win(List<string> a, int index)
    {
        var wins = Windows();
        if (a.Count > index && int.TryParse(a[index], out var w))
            return w >= 0 && w < wins.Count ? wins[w] : throw new ArgumentException($"no window {w}");
        return _window;
    }

    private static FolderView? ViewAt(Window win, int x, int y)
    {
        for (DependencyObject? d = HitTest(win, x, y); d != null; d = VisualTreeHelper.GetParent(d))
            if (d is FolderView v) return v;
        return null;
    }

    private static IntPtr Hwnd(Window w) => new WindowInteropHelper(w).Handle;

    /// <summary>Lets posted messages and the resulting layout/render work run before replying.</summary>
    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle, () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void Send(IntPtr hwnd, int msg, int wParam, IntPtr lParam) => PostMessage(hwnd, msg, (IntPtr)wParam, lParam);
    private static void Send(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam) => PostMessage(hwnd, msg, wParam, lParam);

    private static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

    private static List<string> Tokenize(string line)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (ch == ' ' && !quoted) { if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); } continue; }
            sb.Append(ch);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    private const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203,
        WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_RBUTTONDBLCLK = 0x206,
        WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MBUTTONDBLCLK = 0x209, WM_MOUSEWHEEL = 0x20A,
        WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_CHAR = 0x102;
    private const int MK_LBUTTON = 1, MK_RBUTTON = 2, MK_MBUTTON = 0x10;

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
