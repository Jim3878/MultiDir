using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace MultiDir.Views;

/// <summary>
/// The "移動到 Documents" label next to the cursor while dragging over MultiDir, so you know before releasing
/// whether it will move or copy (same drive = move, other drive = copy; Shift/Ctrl force) and where it lands.
/// Placed below-right of the cursor so it is never itself the thing under the mouse.
/// </summary>
public static class DragHint
{
    private static Popup? _popup;
    private static TextBlock? _glyph, _title, _detail;

    /// <summary>Hint for a shell drop: <paramref name="effect"/> as returned by the target folder.</summary>
    public static void ShowDrop(Point screenPx, DragDropEffects effect, string folder, bool rightButton)
    {
        var name = FolderName(folder);
        if (rightButton) { Show(screenPx, "\uE712", $"放到「{name}」", "放開後選擇：複製、移動、建立捷徑…"); return; }
        if (effect.HasFlag(DragDropEffects.Move)) Show(screenPx, "\uE8DE", $"移動到「{name}」", folder);
        else if (effect.HasFlag(DragDropEffects.Copy)) Show(screenPx, "\uE8C8", $"複製到「{name}」", folder);
        else if (effect.HasFlag(DragDropEffects.Link)) Show(screenPx, "\uE71B", $"在「{name}」建立捷徑", folder);
        else Show(screenPx, "\uE711", $"無法放到「{name}」", folder);
    }

    /// <summary>Any other drop meaning (e.g. 加入書籤).</summary>
    /// <param name="leftOfCursor">Place it to the left of the cursor (when something follows the cursor to the
    /// lower right, e.g. a dragged tab's floating window, which would otherwise cover the hint).</param>
    public static void Show(Point screenPx, string glyph, string title, string? detail = null, bool leftOfCursor = false)
    {
        Ensure();
        _glyph!.Text = glyph;
        _title!.Text = title;
        _detail!.Text = detail ?? "";
        _detail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;

        // Popup offsets are in DIPs; the cursor comes in physical pixels.
        double scale = Application.Current?.MainWindow is { } w ? VisualTreeHelper.GetDpi(w).DpiScaleX : 1;
        if (leftOfCursor)
        {
            _popup!.Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _popup.HorizontalOffset = screenPx.X / scale - _popup.Child.DesiredSize.Width - 14;
            _popup.VerticalOffset = screenPx.Y / scale - _popup.Child.DesiredSize.Height / 2;
        }
        else
        {
            _popup!.HorizontalOffset = screenPx.X / scale + 16;
            _popup.VerticalOffset = screenPx.Y / scale + 22;
        }
        if (!_popup.IsOpen) _popup.IsOpen = true;
    }

    public static void Hide()
    {
        if (_popup != null) _popup.IsOpen = false;
    }

    private static string FolderName(string folder)
    {
        var trimmed = folder.TrimEnd('\\');
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed; // drive root → "C:"
    }

    private static void Ensure()
    {
        if (_popup != null) return;
        var text = (Brush)Application.Current.FindResource("Text");
        var dim = (Brush)Application.Current.FindResource("TextDim");
        _glyph = new TextBlock { FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 14, Foreground = (Brush)Application.Current.FindResource("Accent"),
                                 VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        _title = new TextBlock { Foreground = text, FontSize = 12 };
        _detail = new TextBlock { Foreground = dim, FontSize = 11, MaxWidth = 420, TextTrimming = TextTrimming.CharacterEllipsis };
        var lines = new StackPanel();
        lines.Children.Add(_title);
        lines.Children.Add(_detail);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_glyph);
        row.Children.Add(lines);
        var border = new Border
        {
            Child = row, Background = (Brush)Application.Current.FindResource("Surface"),
            BorderBrush = (Brush)Application.Current.FindResource("Border"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 5, 10, 5), IsHitTestVisible = false,
        };
        TextElement.SetFontFamily(border, SystemFonts.MessageFontFamily);
        _popup = new Popup
        {
            Child = border, Placement = PlacementMode.AbsolutePoint, AllowsTransparency = true, StaysOpen = true,
            Focusable = false, IsHitTestVisible = false, PopupAnimation = PopupAnimation.None,
        };
    }
}
