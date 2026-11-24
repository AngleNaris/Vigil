using System.Globalization;
using SystemAutorun.App.Controls;

namespace SystemAutorun.IntegrationTests;

public sealed class DashboardPlotHoverModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 0, TimeSpan.Zero);

    [Fact]
    public void HoverFormattingUsesOneDecimalWithoutChangingStoredValues()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal("0.1%", DashboardPlot.HoverMetricLabel(.12955706293367208, false, false));
            Assert.Equal("0.0%", DashboardPlot.HoverMetricLabel(.04, false, false));
            Assert.Equal("1.5 KB/s", DashboardPlot.HoverMetricLabel(1536, false, true));
            Assert.Equal("1.0 MB", DashboardPlot.HoverMetricLabel(1048576, true, false));
            Assert.Equal("未采样", DashboardPlot.HoverMetricLabel(null, false, false));
            Assert.Equal("未采样", DashboardPlot.HoverMetricLabel(double.NaN, false, false));
            Assert.Equal("未采样", DashboardPlot.HoverMetricLabel(-1, true, false));
            Assert.Contains("E+", DashboardPlot.HoverMetricLabel(double.MaxValue, true, false));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void LiveHoverUsesOneActualTimestampAndNeverBorrowsOtherSeriesFromAnotherTime()
    {
        PlotPoint[] primary = [new(Start, 10), new(Start.AddSeconds(1), 20)];
        PlotPoint[] secondary = [new(Start, 1), new(Start.AddSeconds(2), 3)];
        var hit = Assert.IsType<DashboardPlot.HoverSample>(DashboardPlot.ResolveHover(primary, secondary, Start.AddSeconds(1.1)));
        Assert.Equal(Start.AddSeconds(1), hit.At); Assert.Equal(20, hit.Primary); Assert.Null(hit.Secondary);
        Assert.Null(DashboardPlot.ResolveHover(primary, secondary, Start.AddSeconds(10)));
        var missing = Assert.IsType<DashboardPlot.HoverSample>(DashboardPlot.ResolveHover(
            [new(Start, null)], [new(Start, 3)], Start));
        Assert.Null(missing.Primary); Assert.Equal(3, missing.Secondary);
    }

    [Fact]
    public void LiveHoverDoesNotBorrowAcrossBreaksOrUnknownValues()
    {
        PlotPoint[] broken = [new(Start, 10), new(Start.AddSeconds(1), 20, BreakBefore: true)];
        PlotPoint[] continuous = [new(Start, 1), new(Start.AddSeconds(1), 2)];
        Assert.Null(DashboardPlot.ResolveHover(broken, [], Start.AddMilliseconds(400)));
        var paired = Assert.IsType<DashboardPlot.HoverSample>(DashboardPlot.ResolveHover(broken, continuous, Start.AddMilliseconds(400)));
        Assert.Null(paired.Primary); Assert.Equal(1, paired.Secondary);
        var exact = Assert.IsType<DashboardPlot.HoverSample>(DashboardPlot.ResolveHover(broken, [], Start.AddSeconds(1)));
        Assert.Equal(20, exact.Primary);
        PlotPoint[] missing = [new(Start, 10), new(Start.AddSeconds(1), null), new(Start.AddSeconds(2), 30)];
        Assert.Null(DashboardPlot.ResolveHover(missing, [], Start.AddMilliseconds(1400)));
    }

    [Fact]
    public void LiveHoverResolvesBackgroundTenSecondRemoteSamplesWhileStillRejectingBreaksAndOutOfRange()
    {
        PlotPoint[] remote =
        [
            new(Start, 12),
            new(Start.AddSeconds(10), 18),
            new(Start.AddSeconds(22), 24), // 12s with ±20% jitter, still <= 15s
            new(Start.AddSeconds(32), 30, BreakBefore: true),
            new(Start.AddSeconds(50), 36), // 18s gap > 15s
        ];

        var mid1 = Assert.IsType<DashboardPlot.HoverSample>(DashboardPlot.ResolveHover(remote, [], Start.AddSeconds(4)));
        Assert.Equal(Start, mid1.At);
        Assert.Equal(12, mid1.Primary);

        var mid2 = Assert.IsType<DashboardPlot.HoverSample>(DashboardPlot.ResolveHover(remote, [], Start.AddSeconds(17)));
        Assert.Equal(Start.AddSeconds(22), mid2.At);
        Assert.Equal(24, mid2.Primary);

        // Explicit reconnect break and >15s gap remain un-sampled.
        Assert.Null(DashboardPlot.ResolveHover(remote, [], Start.AddSeconds(27)));
        Assert.Null(DashboardPlot.ResolveHover(remote, [], Start.AddSeconds(41)));
        // Past the final point by >3s still returns null.
        Assert.Null(DashboardPlot.ResolveHover(remote, [], Start.AddSeconds(60)));
    }

    [Fact]
    public void HistoryHoverUsesTheContainingBucketAndKeepsMissingDirectionUnknown()
    {
        var middle = Start.AddMinutes(1); var end = Start.AddMinutes(2);
        PlotPoint[] primary = [new(Start.AddSeconds(30), 10, Start, middle), new(middle.AddSeconds(30), 20, middle, end)];
        PlotPoint[] secondary = [new(Start.AddSeconds(30), 1, Start, middle), new(middle.AddSeconds(30), null, middle, end)];
        var beforeBoundary = Assert.IsType<DashboardPlot.HoverSample>(DashboardPlot.ResolveHover(primary, secondary, middle.AddTicks(-1)));
        Assert.Equal(10, beforeBoundary.Primary); Assert.Equal(1, beforeBoundary.Secondary);
        var boundary = Assert.IsType<DashboardPlot.HoverSample>(DashboardPlot.ResolveHover(primary, secondary, middle));
        Assert.Equal(middle, boundary.From); Assert.Equal(end, boundary.To);
        Assert.Equal(20, boundary.Primary); Assert.Null(boundary.Secondary);
        Assert.Null(DashboardPlot.ResolveHover(primary, secondary, end));
        Assert.Null(DashboardPlot.ResolveHover(primary, secondary, Start.AddSeconds(-1)));
    }

    [Fact]
    public void HealthHoverReportsActualCategoryAndUnknownGaps()
    {
        HealthInterval[] intervals = [new(Start, Start.AddSeconds(5), "healthy"),
            new(Start.AddSeconds(2), Start.AddSeconds(4), "failed"), new(Start.AddSeconds(3), Start.AddSeconds(5), "unknown")];
        Assert.Equal("healthy", DashboardPlot.ResolveHealthHover(intervals, Start.AddSeconds(1)).State);
        Assert.Equal("failed", DashboardPlot.ResolveHealthHover(intervals, Start.AddSeconds(3)).State);
        Assert.Equal("healthy", DashboardPlot.ResolveHealthHover(intervals, Start.AddSeconds(4)).State);
        var gap = DashboardPlot.ResolveHealthHover(intervals, Start.AddSeconds(6));
        Assert.Equal("unknown", gap.State); Assert.Null(gap.From); Assert.Null(gap.To);
    }
}
