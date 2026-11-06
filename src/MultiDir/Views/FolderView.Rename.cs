using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MultiDir.Models;

namespace MultiDir.Views;

/// <summary>
/// Rename in place, like Explorer: F2 / 右鍵「重新命名」 turns the item's name into a text box right where it is.
/// Enter or clicking elsewhere applies, Esc cancels; the name is pre-selected without its extension.
/// </summary>
public partial class FolderView
{
    private RenameBox? _rename;

    /// <summary>The view with a rename in progress (at most one in the whole app).</summary>
    private static FolderView? _renaming;
    private static bool _renameHooked;

    /// <summary>
    /// A click anywhere else (also empty list space, which never takes keyboard focus, so the box would not
    /// notice) or switching to another app applies the rename, like Explorer.
    /// </summary>
    private static void HookRenameDismissal()
    {
        if (_renameHooked) return;
        _renameHooked = true;
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (_renaming?._rename is not { } r) return;
            for (var d = e.OriginalSource as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d == r.Box) return; // clicking inside the box moves the caret
            _renaming.EndRename(commit: true);
        }), handledEventsToo: true);
        Application.Current.Deactivated += (_, _) => _renaming?.EndRename(commit: true);
    }

    public bool IsRenaming => _rename != null;

    /// <summary>Automation: the text box of the rename in progress.</summary>
    internal TextBox? RenameTextBox => _rename?.Box;

    private void Rename(FileItem item)
    {
        if (_tab == null) return;
        EndRename(commit: true);
        List.ScrollIntoView(item);
        List.UpdateLayout();
        if (List.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem container) return;
        List.SelectedItems.Clear();
        List.SelectedItem = item;
        // The name text of whichever view mode is showing.
        var label = Descendants<TextBlock>(container).FirstOrDefault(t => t.Text == item.Name);
        var layer = Overlay.AdornerLayer;
        if (label == null || layer == null) return;

        _rename = new RenameBox(label, item, wrap: label.TextWrapping == TextWrapping.Wrap,
                                maxWidth: Math.Max(80, List.ActualWidth - label.TranslatePoint(default, List).X - 24));
        if (Descendants<ScrollViewer>(List).FirstOrDefault() is { } sv) _rename.StartOffset = new Point(sv.HorizontalOffset, sv.VerticalOffset);
        var box = _rename.Box;
        layer.Add(_rename);
        if (_renaming != null && _renaming != this) _renaming.EndRename(commit: true);
        _renaming = this;
        HookRenameDismissal();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; EndRename(commit: true); }
            else if (e.Key == Key.Escape) { e.Handled = true; EndRename(commit: false); }
        };
        box.PreviewTextInput += (_, e) =>
        {
            if (e.Text.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) return;
            e.Handled = true;
            _rename?.ShowWarning(RenameDialog.InvalidCharsMessage);
        };
        box.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() => { if (_rename?.Box == box) EndRename(commit: true); });
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.Focus();
            int dot = item.IsDirectory ? -1 : item.Name.LastIndexOf('.');
            box.Select(0, dot > 0 ? dot : item.Name.Length);
        });
    }

    /// <summary>Applies (or drops) the rename in progress, if any.</summary>
    private void EndRename(bool commit)
    {
        if (_rename is not { } r) return;
        _rename = null;
        if (_renaming == this) _renaming = null;
        r.ShowWarning(null);
        Overlay.AdornerLayer?.Remove(r);
        var newName = r.Box.Text.Trim();
        bool keepFocus = r.Box.IsKeyboardFocused;
        if (keepFocus) List.Focus();
        if (!commit || newName.Length == 0 || newName == r.Item.Name) return;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return; // pasted; typing is already blocked

        var owner = App.IsAutomation ? null : Window.GetWindow(this); // a test instance never pops dialogs
        if (RenameDialog.Apply(owner, r.Item.FullPath, newName) == null) return;
        SelectAfterRename(newName);
    }

    /// <summary>The folder watcher brings the renamed item back a moment later: select it again, like Explorer.</summary>
    private void SelectAfterRename(string name)
    {
        var tab = _tab;
        int tries = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            if (_tab != tab || ++tries > 30) { timer.Stop(); return; }
            if (tab?.Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal)) is not { } item) return;
            timer.Stop();
            List.ScrollIntoView(item);
            List.UpdateLayout();
            if (IsKeyboardFocusWithin) (List.ItemContainerGenerator.ContainerFromItem(item) as ListViewItem)?.Focus();
            List.SelectedItems.Clear();
            List.SelectedItem = item;
        };
        timer.Start();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in Descendants<T>(c)) yield return d;
        }
    }

    /// <summary>The text box, laid over the item's name label.</summary>
    private sealed class RenameBox : Adorner
    {
        public TextBox Box { get; }
        public FileItem Item { get; }
        /// <summary>The list's scroll position when the rename began; scrolling away ends it.</summary>
        public Point StartOffset { get; set; }
        private readonly TextBlock _label;
        private readonly bool _wrap;
        private readonly Popup _warning = new()
        {
            Placement = PlacementMode.Bottom, AllowsTransparency = true, StaysOpen = true, Focusable = false,
            PopupAnimation = PopupAnimation.None,
        };
        private DispatcherTimer? _warningTimer;

        public RenameBox(TextBlock label, FileItem item, bool wrap, double maxWidth) : base(label)
        {
            _label = label;
            _wrap = wrap;
            Item = item;
            Box = new TextBox
            {
                Text = item.Name, FontFamily = label.FontFamily, FontSize = label.FontSize,
                Background = (Brush)Application.Current.FindResource("Bg"),
                Foreground = (Brush)Application.Current.FindResource("Text"),
                CaretBrush = (Brush)Application.Current.FindResource("Text"),
                SelectionBrush = (Brush)Application.Current.FindResource("Accent"),
                BorderBrush = (Brush)Application.Current.FindResource("Accent"),
                BorderThickness = new Thickness(1), Padding = new Thickness(1, 0, 1, 0),
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextAlignment = wrap ? TextAlignment.Center : TextAlignment.Left,
                MaxWidth = maxWidth,
            };
            if (wrap) Box.Width = Math.Max(label.ActualWidth, 60);
            else Box.MinWidth = label.ActualWidth + 8;
            AddVisualChild(Box);
            _warning.PlacementTarget = Box;
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => Box;

        protected override Size MeasureOverride(Size constraint)
        {
            Box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Box.DesiredSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            // Text inside the box lines up with the label underneath (border + padding = 2 px each side).
            double x = _wrap ? (_label.ActualWidth - Box.DesiredSize.Width) / 2 : -3;
            Box.Arrange(new Rect(new Point(x, -2), Box.DesiredSize));
            return finalSize;
        }

        /// <summary>Explorer-style note under the box (e.g. an invalid character); null hides it.</summary>
        public void ShowWarning(string? text)
        {
            _warningTimer?.Stop();
            if (text == null) { _warning.IsOpen = false; return; }
            var border = new Border
            {
                Background = (Brush)Application.Current.FindResource("Surface"),
                BorderBrush = (Brush)Application.Current.FindResource("Border"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 4, 0, 0),
                Child = new TextBlock { Text = text, Foreground = (Brush)Application.Current.FindResource("Text"), FontSize = 12 },
            };
            TextElement.SetFontFamily(border, SystemFonts.MessageFontFamily);
            _warning.Child = border;
            _warning.IsOpen = true;
            _warningTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _warningTimer.Tick += (_, _) => { _warningTimer.Stop(); _warning.IsOpen = false; };
            _warningTimer.Start();
        }
    }
}
