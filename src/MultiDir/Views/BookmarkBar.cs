using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MultiDir.Services;

namespace MultiDir.Views;

/// <summary>
/// Chrome-style bookmark bar.
/// Click = open in the active tab, Ctrl+click / middle-click = new tab, right-click = 開啟 / 新分頁 / 重新命名 / 移除.
/// Drag a folder onto empty bar space to add it; drag files onto a bookmark to move them there (like any folder);
/// drag a bookmark along the bar to reorder. Bookmarks that don't fit go into the » menu.
/// </summary>
public sealed class BookmarkBar : DockPanel
{
    private const string ReorderFormat = "MultiDir.Bookmark";

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
        Background = Brushes.Transparent; // hit-testable, so the empty area accepts drops
        AllowDrop = true;
        LastChildFill = true;

        _overflow = new Button
        {
            Content = "", // »
            ToolTip = "其他書籤",
            Style = (Style)Application.Current.FindResource("ToolButton"),
            FontSize = 9, Visibility = Visibility.Collapsed,
        };
        _overflow.Click += (_, _) => ShowOverflowMenu();
        SetDock(_overflow, Dock.Right);
        Children.Add(_overflow);

        _empty = new TextBlock
        {
            Text = "把資料夾拖到這裡，或按功能列的 ☆ 加入書籤",
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

        _panel.OverflowChanged += () =>
            _overflow.Visibility = _panel.HiddenChildren.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        BookmarkStore.Items.CollectionChanged += (_, _) => Rebuild();
        Rebuild();

        DragEnter += Bar_DragOver;
        DragOver += Bar_DragOver;
        DragLeave += Bar_DragLeave;
        Drop += Bar_Drop;
    }

    // ---------- buttons ----------

    private void Rebuild()
    {
        _panel.Children.Clear();
        foreach (var b in BookmarkStore.Items) _panel.Children.Add(CreateButton(b));
        _empty.Visibility = BookmarkStore.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Button CreateButton(Bookmark b)
    {
        var text = new TextBlock { Text = b.Name, MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
        if (!b.Exists) { text.Opacity = 0.5; text.FontStyle = FontStyles.Italic; }
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Image { Source = b.Icon, Width = 16, Height = 16 });
        content.Children.Add(text);

        var button = new Button
        {
            Content = content, Tag = b, Height = 24, Margin = new Thickness(1, 0, 1, 0), Padding = new Thickness(6, 0, 6, 0),
            Style = (Style)Application.Current.FindResource("CrumbButton"),
            ToolTip = b.Exists ? b.Path : b.Path + "（找不到這個資料夾）",
            ContextMenu = BuildMenu(b),
        };
        button.Click += (_, _) => Open(b, newTab: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        button.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) { Open(b, newTab: true); e.Handled = true; }
            if (e.ChangedButton == MouseButton.Left) { _downAt = e.GetPosition(this); _downOn = b; }
        };
        button.PreviewMouseMove += (_, e) => StartReorderDrag(button, e);
        return button;
    }

    private ContextMenu BuildMenu(Bookmark b)
    {
        var menu = new ContextMenu { Style = (Style)Application.Current.FindResource("DarkMenu") };
        menu.Items.Add(MenuItem("開啟", () => Open(b, false)));
        menu.Items.Add(MenuItem("在新分頁開啟", () => Open(b, true)));
        menu.Items.Add(MenuItem("重新命名…", () =>
        {
            if (!App.IsAutomation && Window.GetWindow(this) is { } owner && RenameDialog.Prompt(owner, "重新命名書籤", b.Name) is { } name)
                BookmarkStore.Rename(b, name);
        }));
        menu.Items.Add(MenuItem("移除", () => BookmarkStore.Remove(b)));
        return menu;
    }

    private static MenuItem MenuItem(string header, Action action, ImageSource? icon = null)
    {
        var item = new MenuItem { Header = header, Style = (Style)Application.Current.FindResource("DarkMenuItem") };
        if (icon != null) item.Icon = new Image { Source = icon, Width = 16, Height = 16 };
        item.Click += (_, _) => action();
        return item;
    }

    private void ShowOverflowMenu()
    {
        var menu = new ContextMenu { Style = (Style)Application.Current.FindResource("DarkMenu"), PlacementTarget = _overflow };
        foreach (var child in _panel.HiddenChildren)
            if (child is Button { Tag: Bookmark b })
                menu.Items.Add(MenuItem(b.Name, () => Open(b, (Keyboard.Modifiers & ModifierKeys.Control) != 0), b.Icon));
        menu.IsOpen = true;
    }

    /// <summary>Opens a bookmark. Automation uses this through <see cref="OpenAt"/>.</summary>
    private void Open(Bookmark b, bool newTab)
    {
        if (!b.Exists)
        {
            if (App.IsAutomation || Window.GetWindow(this) is not { } owner) return;
            if (MessageBox.Show(owner, $"找不到資料夾：\n{b.Path}\n\n要移除這個書籤嗎？", "書籤", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                BookmarkStore.Remove(b);
            return;
        }
        OpenRequested?.Invoke(b.Path, newTab);
    }

    /// <summary>Automation: open the bookmark at <paramref name="index"/>.</summary>
    public void OpenAt(int index, bool newTab) => Open(BookmarkStore.Items[index], newTab);

    /// <summary>Automation: which bookmarks are visible on the bar and which went into ».</summary>
    public string Describe() =>
        string.Join("\n", _panel.Children.OfType<Button>().Select((btn, i) =>
            $"  {i}: {((Bookmark)btn.Tag).Name} | {((Bookmark)btn.Tag).Path}{(_panel.HiddenChildren.Contains(btn) ? "  (in »)" : "")}"));

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
        HideInsertMark();
    }

    // ---------- drag: add folder / drop files onto a bookmark ----------

    private void Bar_DragOver(object sender, DragEventArgs e)
    {
        var target = ButtonAt(e);
        if (e.Data.GetDataPresent(ReorderFormat))
        {
            ShowInsertMark(InsertIndex(e));
            e.Effects = DragDropEffects.Move;
        }
        else if (target is { Tag: Bookmark b } && b.Exists)
        {
            // Onto a bookmark = into that folder (move/copy like every other drop target).
            HideInsertMark();
            SetDropHot(target);
            ShellDragDrop.Over(e, this, b.Path);
            e.Handled = true;
            return;
        }
        else if (DroppedFolders(e).Count > 0)
        {
            ShowInsertMark(InsertIndex(e));
            e.Effects = DragDropEffects.Link;
        }
        else
        {
            HideInsertMark();
            e.Effects = DragDropEffects.None;
        }
        SetDropHot(null);
        ShellDragDrop.Leave();
        e.Handled = true;
    }

    private void Bar_DragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(this);
        if (p.X >= 0 && p.Y >= 0 && p.X < ActualWidth && p.Y < ActualHeight) return;
        HideInsertMark();
        SetDropHot(null);
        ShellDragDrop.Leave();
    }

    private void Bar_Drop(object sender, DragEventArgs e)
    {
        var target = ButtonAt(e);
        int index = InsertIndex(e);
        HideInsertMark();
        SetDropHot(null);
        if (e.Data.GetData(ReorderFormat) is Bookmark moving)
            BookmarkStore.Move(moving, index);
        else if (target is { Tag: Bookmark b } && b.Exists)
            ShellDragDrop.Drop(e, this, b.Path);
        else
            foreach (var folder in DroppedFolders(e)) BookmarkStore.Add(folder, index++);
        e.Handled = true;
    }

    private static List<string> DroppedFolders(DragEventArgs e)
    {
        try
        {
            return e.Data.GetData(DataFormats.FileDrop) is string[] paths ? paths.Where(Directory.Exists).ToList() : new();
        }
        catch { return new(); }
    }

    private Button? ButtonAt(DragEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is Button btn && btn.Tag is Bookmark) return btn;
        return null;
    }

    /// <summary>Insert position from the cursor: before the first visible button whose middle is right of it.</summary>
    private int InsertIndex(DragEventArgs e)
    {
        var x = e.GetPosition(_panel).X;
        var visible = _panel.Children.OfType<Button>().Where(b => !_panel.HiddenChildren.Contains(b)).ToList();
        for (int i = 0; i < visible.Count; i++)
        {
            var left = visible[i].TranslatePoint(new Point(0, 0), _panel).X;
            if (x < left + visible[i].ActualWidth / 2) return i;
        }
        return visible.Count;
    }

    private void ShowInsertMark(int index)
    {
        var visible = _panel.Children.OfType<Button>().Where(b => !_panel.HiddenChildren.Contains(b)).ToList();
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
