using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SystemAutorun.App.Services;

/// <summary>Bounded text batches: a few frozen clips, no character measurement, masks or per-frame drawing.</summary>
internal static class GlyphReveal
{
    internal static (FrameworkElement Target, AnimationTimeline Animation)? Create(FrameworkElement owner, double delay)
    {
        // Long native editors fade as one surface; never rasterize their potentially enormous text view.
        if (owner is not TextBlock text || text.Clip is not null || text.RenderSize.Width <= 0 || text.RenderSize.Height <= 0)
        { return null; }
        var width = text.RenderSize.Width;
        var inkWidth = Math.Clamp(text.DesiredSize.Width - text.Margin.Left - text.Margin.Right, 1, width);
        var chunks = Math.Clamp((text.Text?.Length ?? 0) / 8, 1, 4);
        var left = text.TextAlignment == TextAlignment.Right ? width - inkWidth
            : text.TextAlignment == TextAlignment.Center ? (width - inkWidth) / 2 : 0;
        var animation = new ObjectAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(delay + chunks * 45), FillBehavior = FillBehavior.Stop,
        };
        Add(0, 0);
        Add(delay, 0);
        for (var step = 1; step <= chunks; step++) { Add(delay + step * 45, (double)step / chunks); }
        return (text, animation);

        void Add(double milliseconds, double fraction)
        {
            var clip = new RectangleGeometry(new Rect(left - 2, -2, (inkWidth + 4) * fraction, text.RenderSize.Height + 4));
            clip.Freeze();
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(clip, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds))));
        }
    }
}
