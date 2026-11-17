using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SystemAutorun.App.Controls;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class DashboardHealthRenderingTests
{
    [Theory]
    [InlineData(false, 47)] [InlineData(true, 47)]
    [InlineData(false, 183)] [InlineData(true, 183)]
    [InlineData(false, 920)] [InlineData(true, 920)]
    public Task HealthPixelsPreserveExactGapsCellSpacesAndOverlapOrder(bool light, int width) => GuiConfigurationControlTests.RunStaAsync(() =>
    {
        var plot = Plot(light, width);
        var start = plot.EndAt.AddDays(-1);
        DateTimeOffset At(double fraction) => start.AddDays(fraction);
        plot.Intervals = [new(At(-.1), At(.2), "healthy"), new(At(.25), At(.45), "failed"),
            new(At(.30), At(.35), "healthy"), new(At(.31), At(.34), "unknown"),
            new(At(.6), At(.6001), "failed"), new(At(.7), At(1.1), "healthy")];
        var actual = Render(plot, context => typeof(DashboardPlot).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plot, [context]));
        var expected = Render(plot, context => ReferenceBody(plot, context));
        // Compare the health body, not the unchanged date labels/axis. This is pixel semantics, not desktop/FPS evidence.
        for (var index = 0; index < width * 36 * 4; index++)
        {
            if (expected[index] != actual[index])
            { Assert.Fail($"Health pixel byte {index}: expected {expected[index]}, actual {actual[index]}"); }
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task DenseHealthIntervalsAreEnumeratedOnceNotOncePerCell() => GuiConfigurationControlTests.RunStaAsync(() =>
    {
        var plot = Plot(false, 920);
        var data = new CountedIntervals(Enumerable.Range(0, 8192).Select(index =>
            new HealthInterval(plot.EndAt.AddDays(-1).AddSeconds(index * 10.5), plot.EndAt.AddDays(-1).AddSeconds(index * 10.5 + 10), "healthy")).ToArray());
        plot.Intervals = data;
        _ = Render(plot, context => typeof(DashboardPlot).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plot, [context]));
        Assert.Equal(1, data.Enumerations);
        return Task.CompletedTask;
    });

    [Fact]
    public Task RightEdgeShortFaultWithinInterCellMarginStillRendersFullRedBlock() => GuiConfigurationControlTests.RunStaAsync(() =>
    {
        var plot = Plot(false, 920);
        var start = plot.EndAt.AddDays(-1);
        plot.Intervals =
        [
            new(start, plot.EndAt.AddSeconds(-5), "healthy"),
            new(plot.EndAt.AddSeconds(-5), plot.EndAt, "failed"),
        ];
        var pixels = Render(plot, context => typeof(DashboardPlot).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plot, [context]));
        var count = Math.Max(1, (int)(plot.ActualWidth / 9));
        var stride = plot.ActualWidth / count;
        var lastCellCenterX = (int)((count - 1) * stride + Math.Max(1, stride - 3) * 0.5);
        var idx = (18 * (int)plot.ActualWidth + lastCellCenterX) * 4;
        var b = pixels[idx];
        var g = pixels[idx + 1];
        var r = pixels[idx + 2];
        Assert.True(r > 200 && g < 50 && b < 100, $"Expected crimson fault block at rightmost cell x={lastCellCenterX}, got RGB({r},{g},{b}).");
        return Task.CompletedTask;
    });

    private static DashboardPlot Plot(bool light, int width)
    {
        var plot = new DashboardPlot { Mode = "Health", Width = width, Height = 66, WindowHours = 24,
            EndAt = new DateTimeOffset(2026, 11, 12, 4, 0, 0, TimeSpan.Zero) };
        plot.Resources["Semantic.Brush.Selection"] = new SolidColorBrush(light ? Color.FromRgb(235, 235, 238) : Color.FromRgb(19, 19, 21));
        plot.Resources["Semantic.Brush.BorderStrong"] = new SolidColorBrush(light ? Color.FromRgb(190, 190, 196) : Color.FromRgb(48, 48, 54));
        plot.Resources["Semantic.Brush.StatusFailed"] = new SolidColorBrush(Color.FromRgb(225, 29, 72));
        plot.Resources["Semantic.Brush.HistoryHealthy"] = new SolidColorBrush(light ? Color.FromRgb(130, 130, 140) : Color.FromRgb(70, 70, 78));
        plot.Measure(new Size(width, 66)); plot.Arrange(new Rect(0, 0, width, 66));
        return plot;
    }

    private static byte[] Render(DashboardPlot plot, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) { draw(context); }
        var image = new RenderTargetBitmap((int)plot.ActualWidth, 66, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        var bytes = new byte[(int)plot.ActualWidth * 66 * 4]; image.CopyPixels(bytes, (int)plot.ActualWidth * 4, 0);
        return bytes;
    }

    // Contiguous cell bucketing semantic oracle: cell index owns [index, index+1], unknown clears back to hatch,
    // and any uncovered fault inside a cell expands across the full cell width as a visible fault block.
    private static void ReferenceBody(DashboardPlot plot, DrawingContext context)
    {
        var count = Math.Max(1, (int)(plot.ActualWidth / 9)); var stride = plot.ActualWidth / count;
        var cellWidth = Math.Max(1, stride - 3);
        var unknown = (Brush)plot.Resources["Semantic.Brush.Selection"];
        var failed = (Brush)plot.Resources["Semantic.Brush.StatusFailed"];
        var healthy = (Brush)plot.Resources["Semantic.Brush.HistoryHealthy"];
        var hatch = new Pen((Brush)plot.Resources["Semantic.Brush.BorderStrong"], 1);
        var from = plot.EndAt.AddDays(-1);
        var seconds = TimeSpan.FromDays(1).TotalSeconds;
        var cells = new List<DashboardPlot.HealthCellSpan>?[count];
        foreach (var interval in plot.Intervals!)
        {
            var u0 = Math.Max(0, (interval.From - from).TotalSeconds / seconds * count);
            var u1 = Math.Min(count, (interval.To - from).TotalSeconds / seconds * count);
            if (u1 <= u0) { continue; }
            var first = Math.Clamp((int)u0, 0, count - 1);
            var last = Math.Clamp((int)Math.Ceiling(u1) - 1, 0, count - 1);
            for (var index = first; index <= last; index++)
            {
                var left = Math.Max(0, u0 - index);
                var right = Math.Min(1, u1 - index);
                if (right > left) { (cells[index] ??= []).Add(new(left, right, interval.State)); }
            }
        }
        for (var index = 0; index < count; index++)
        {
            var rect = new Rect(index * stride, 0, cellWidth, 36);
            context.DrawRectangle(unknown, null, rect); context.PushClip(new RectangleGeometry(rect));
            for (double y = -8; y < 44; y += 8) { context.DrawLine(hatch, new Point(rect.Left, y + 6), new Point(rect.Right, y)); }
            if (cells[index] is { } spans)
            {
                var resolved = DashboardPlot.ResolveHealthCellSpans(spans);
                if (resolved.Any(span => span.State == "failed" && span.Right - span.Left > 1e-9))
                {
                    context.DrawRectangle(failed, null, rect);
                }
                else
                {
                    foreach (var span in resolved)
                    {
                        var spanWidth = (span.Right - span.Left) * cellWidth;
                        if (spanWidth > 0) { context.DrawRectangle(healthy, null, new Rect(rect.Left + span.Left * cellWidth, 0, spanWidth, 36)); }
                    }
                }
            }
            context.Pop();
        }
    }

    private sealed class CountedIntervals(HealthInterval[] rows) : IReadOnlyList<HealthInterval>
    {
        internal int Enumerations { get; private set; }
        public int Count => rows.Length;
        public HealthInterval this[int index] => rows[index];
        public IEnumerator<HealthInterval> GetEnumerator() { Enumerations++; return ((IEnumerable<HealthInterval>)rows).GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
