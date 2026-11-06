using System.Windows;
using System.Windows.Controls;

namespace MultiDir.Views;

/// <summary>
/// Breadcrumb layout like Explorer's: when the path doesn't fit, the leading segments are hidden (they go into the
/// ‹‹ button) so the current folder — the end of the path — always stays visible.
/// </summary>
public sealed class CrumbPanel : Panel
{
    public static readonly RoutedEvent OverflowChangedEvent =
        EventManager.RegisterRoutedEvent("OverflowChanged", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(CrumbPanel));

    /// <summary>How many leading segments are hidden right now.</summary>
    public int HiddenCount { get; private set; }

    protected override Size MeasureOverride(Size available)
    {
        double w = 0, h = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(double.PositiveInfinity, available.Height));
            w += c.DesiredSize.Width;
            h = Math.Max(h, c.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(available.Width) ? w : Math.Min(w, available.Width), h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        int n = InternalChildren.Count;
        double total = 0;
        foreach (UIElement c in InternalChildren) total += c.DesiredSize.Width;

        // Drop segments from the start until the rest fits (always keep the last one).
        int hidden = 0;
        while (total > final.Width && hidden < n - 1)
            total -= InternalChildren[hidden++].DesiredSize.Width;

        double x = 0;
        for (int i = 0; i < n; i++)
        {
            var c = InternalChildren[i];
            if (i < hidden) { c.Arrange(new Rect(0, 0, 0, 0)); continue; }
            double w = Math.Min(c.DesiredSize.Width, Math.Max(0, final.Width - x)); // a single too-long name is clipped
            c.Arrange(new Rect(x, (final.Height - c.DesiredSize.Height) / 2, w, c.DesiredSize.Height));
            x += w;
        }

        if (hidden != HiddenCount)
        {
            HiddenCount = hidden;
            Dispatcher.BeginInvoke(() => RaiseEvent(new RoutedEventArgs(OverflowChangedEvent, this)));
        }
        return final;
    }
}
