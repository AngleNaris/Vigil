using Size = System.Windows.Size;

namespace SystemAutorun.App.Controls;

/// <summary>A caption and right-aligned readout share a line when they fit; otherwise the readout wraps below.</summary>
public sealed class MetricHeadingPanel : System.Windows.Controls.Panel
{
    private const double InlineGap = 8;
    private const double StackedGap = 4;
    private bool _stacked;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count != 2) { return new Size(); }
        var caption = InternalChildren[0]; var value = InternalChildren[1];
        var constraint = new Size(availableSize.Width, double.PositiveInfinity);
        caption.Measure(constraint); value.Measure(constraint);
        var inlineWidth = caption.DesiredSize.Width + InlineGap + value.DesiredSize.Width;
        _stacked = inlineWidth > availableSize.Width;
        return _stacked
            ? new Size(Math.Max(caption.DesiredSize.Width, value.DesiredSize.Width),
                caption.DesiredSize.Height + StackedGap + value.DesiredSize.Height)
            : new Size(inlineWidth, Math.Max(caption.DesiredSize.Height, value.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 2) { return finalSize; }
        var caption = InternalChildren[0]; var value = InternalChildren[1];
        var captionWidth = Math.Min(caption.DesiredSize.Width, finalSize.Width);
        var valueWidth = Math.Min(value.DesiredSize.Width, finalSize.Width);
        var captionY = _stacked ? 0 : (finalSize.Height - caption.DesiredSize.Height) / 2;
        var valueY = _stacked ? caption.DesiredSize.Height + StackedGap : (finalSize.Height - value.DesiredSize.Height) / 2;
        caption.Arrange(new Rect(0, captionY, captionWidth, caption.DesiredSize.Height));
        value.Arrange(new Rect(finalSize.Width - valueWidth, valueY, valueWidth, value.DesiredSize.Height));
        return finalSize;
    }
}
