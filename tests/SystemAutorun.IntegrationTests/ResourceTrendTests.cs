using SystemAutorun.App.Controls;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

public sealed class ResourceTrendTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static ManagedProcessSnapshot Snapshot(DateTimeOffset at, double cpu = .04, long bytes = 80L << 20,
        bool available = true, int pid = 123, DateTimeOffset? started = null) => new("fixture", "", "windows", true, true, true, "OK", "", "", at,
        [new(pid, "fixture", null, started ?? Epoch.AddDays(-1), cpu, bytes, bytes, 2, available)], TaskIdentity: "fixture");

    [Fact]
    public void RepeatedOldAndFutureSnapshotsCannotInventSecondSamples()
    {
        var buffer = new ResourceTrendBuffer();
        var first = buffer.Observe("a", Snapshot(Epoch), Epoch, false)!;
        for (var second = 1; second < 20; second++) { buffer.Observe("a", Snapshot(Epoch), Epoch.AddSeconds(second), false); }
        Assert.Single(first.Cpu);
        buffer.Observe("a", Snapshot(Epoch.AddSeconds(-1)), Epoch.AddSeconds(20), false);
        buffer.Observe("a", Snapshot(Epoch.AddMinutes(1)), Epoch.AddSeconds(20), false);
        Assert.Single(first.Cpu);
        Assert.Equal(.04, first.Cpu[0].Value); Assert.Equal(80L << 20, first.Memory[0].Value);
    }

    [Fact]
    public void ActualSecondSamplesAreRetainedWithBoundedWindowAndLruSeries()
    {
        var buffer = new ResourceTrendBuffer();
        ResourceTrendBuffer.Series? series = null;
        for (var second = 0; second < 900; second++)
        { var at = Epoch.AddSeconds(second); series = buffer.Observe("a", Snapshot(at), at, false); }
        Assert.Equal(ResourceTrendBuffer.MaximumSamples, series!.Cpu.Count);
        Assert.Equal(Epoch.AddSeconds(599), series.Cpu[0].At);
        for (var index = 0; index < 20; index++) { buffer.Observe($"other-{index}", Snapshot(Epoch), Epoch, false); }
        Assert.Equal(ResourceTrendBuffer.MaximumSeries, buffer.SeriesCount);
        buffer.Clear(); Assert.Equal(0, buffer.SeriesCount);
    }

    [Fact]
    public void RestartAndExplicitInterruptionBreakBothResourceTrends()
    {
        var buffer = new ResourceTrendBuffer();
        buffer.Observe("a", Snapshot(Epoch), Epoch, false);
        var series = buffer.Observe("a", Snapshot(Epoch.AddSeconds(1), pid: 124), Epoch.AddSeconds(1), false)!;
        Assert.True(series.Cpu[^1].BreakBefore); Assert.True(series.Memory[^1].BreakBefore);
        series = buffer.Observe("a", Snapshot(Epoch.AddSeconds(2), pid: 124), Epoch.AddSeconds(2), true)!;
        Assert.True(series.Cpu[^1].BreakBefore);
        series = buffer.Observe("a", Snapshot(Epoch.AddSeconds(3), pid: 124), Epoch.AddSeconds(3), false)!;
        Assert.False(series.Cpu[^1].BreakBefore);
    }

    [Fact]
    public void UnknownCpuDoesNotTurnIntoZeroOrEraseKnownBytes()
    {
        var series = new ResourceTrendBuffer().Observe("a", Snapshot(Epoch, available: false), Epoch, false)!;
        Assert.Null(series.Cpu[0].Value); Assert.Equal(80L << 20, series.Memory[0].Value);
        var invalid = new ResourceTrendBuffer().Observe("a", Snapshot(Epoch, cpu: double.NaN, bytes: -1), Epoch, false)!;
        Assert.Null(invalid.Cpu[0].Value); Assert.Null(invalid.Memory[0].Value);
    }

    [Fact]
    public void MissingObservationsRemainGapsRatherThanInterpolatedZeros()
    {
        var buffer = new ResourceTrendBuffer();
        buffer.Observe("a", Snapshot(Epoch), Epoch, false);
        buffer.Observe("a", null, Epoch.AddSeconds(6), false);
        var series = buffer.Observe("a", Snapshot(Epoch.AddSeconds(10)), Epoch.AddSeconds(10), false)!;
        Assert.Equal(2, series.Cpu.Count);
        Assert.False(DashboardPlot.CanConnect(series.Cpu[0], series.Cpu[1], ResourceTrendBuffer.MaximumGap));
    }

    [Fact]
    public void ExplicitUnknownObservationBreaksEvenAShortGapAndDuplicateCannotConsumeIt()
    {
        var buffer = new ResourceTrendBuffer();
        buffer.Observe("a", Snapshot(Epoch), Epoch, false);
        buffer.Observe("a", null, Epoch.AddSeconds(1), false);
        buffer.Observe("a", Snapshot(Epoch), Epoch.AddSeconds(1), false);
        var series = buffer.Observe("a", Snapshot(Epoch.AddSeconds(2)), Epoch.AddSeconds(2), false)!;
        Assert.Equal(2, series.Cpu.Count);
        Assert.True(series.Cpu[^1].BreakBefore);
        Assert.False(DashboardPlot.CanConnect(series.Cpu[0], series.Cpu[1], ResourceTrendBuffer.MaximumGap));
    }

    [Fact]
    public void UnverifiableStartIdentityNeverBridgesPidReuse()
    {
        var buffer = new ResourceTrendBuffer();
        var unknown = Snapshot(Epoch);
        unknown = unknown with { Processes = [unknown.Processes[0] with { StartedAt = null }] };
        buffer.Observe("a", unknown, Epoch, false);
        var series = buffer.Observe("a", unknown with { CapturedAt = Epoch.AddSeconds(1) }, Epoch.AddSeconds(1), false)!;
        Assert.All(series.Cpu, point => Assert.True(point.BreakBefore));
        Assert.All(series.Memory, point => Assert.Equal(80L << 20, point.Value));
    }

    [Fact]
    public void ConstantSamplesStayConstantAndAgentOrTaskKeysNeverBorrowAnotherSeries()
    {
        var buffer = new ResourceTrendBuffer();
        for (var i = 0; i < 8; i++) { buffer.Observe("agent-a:task-a", Snapshot(Epoch.AddSeconds(i), 0), Epoch.AddSeconds(i), false); }
        var first = buffer.Observe("agent-a:task-a", Snapshot(Epoch.AddSeconds(7), 0), Epoch.AddSeconds(7), false)!;
        Assert.Equal(8, first.Cpu.Count); Assert.All(first.Cpu, point => Assert.Equal(0, point.Value));
        foreach (var key in new[] { "agent-b:task-a", "agent-a:task-b" })
        {
            Assert.Null(buffer.Observe(key, null, Epoch.AddSeconds(7), false));
            var other = buffer.Observe(key, Snapshot(Epoch.AddSeconds(7), 9), Epoch.AddSeconds(7), false)!;
            Assert.Single(other.Cpu); Assert.NotSame(first, other);
        }
    }

    [Fact]
    public void HistoricalBucketMeansConnectOnlyAcrossAdjacentKnownBuckets()
    {
        var first = new PlotPoint(Epoch.AddMinutes(6), 1, Epoch, Epoch.AddMinutes(12));
        var next = new PlotPoint(Epoch.AddMinutes(18), 2, Epoch.AddMinutes(12), Epoch.AddMinutes(24));
        Assert.True(DashboardPlot.CanConnect(first, next, ResourceTrendBuffer.MaximumGap));
        Assert.False(DashboardPlot.CanConnect(first, next with { From = Epoch.AddMinutes(13) }, ResourceTrendBuffer.MaximumGap));
        Assert.False(DashboardPlot.CanConnect(first, next with { Value = null }, ResourceTrendBuffer.MaximumGap));
        Assert.False(DashboardPlot.CanConnect(first, next with { BreakBefore = true }, ResourceTrendBuffer.MaximumGap));
    }

    [Fact]
    public void CpuHasRealZeroAndUsefulMinimumRangeWhileMemoryNeverMagnifiesTinyNoise()
    {
        var cpu = TrendScale.Fit([.02, .04, .03], bytes: false);
        Assert.Equal(0, cpu.Minimum); Assert.Equal(.1, cpu.Maximum);
        var idle = TrendScale.Fit([0, 0, 0], bytes: false);
        Assert.Equal(cpu, idle);
        var memory = TrendScale.Fit([300L << 20, (300L << 20) + 4096], bytes: true);
        Assert.True(memory.Maximum - memory.Minimum >= 30L << 20);
        Assert.True(memory.Minimum <= 300L << 20); Assert.True(memory.Maximum > 300L << 20);
    }

    [Fact]
    public void RatesUseSharedZeroBaselineAndFiniteBoundsWithoutMemoryZoom()
    {
        var scale = TrendScale.FitRate([1, 4096, 900_000]);
        Assert.Equal(0, scale.Minimum); Assert.True(scale.Maximum >= 900_000);
        Assert.Equal(1, TrendScale.FitRate([0, 0]).Maximum);
        Assert.True(double.IsFinite(TrendScale.FitRate([double.MaxValue]).Maximum));
        var state = new StableTrendScale();
        Assert.Equal(0, state.Update([8192, 8193], true, Epoch, rate: true).Minimum);
    }

    [Theory]
    [InlineData(512d, false, "512 B")]
    [InlineData(1024d, true, "1 KB/s")]
    [InlineData(1073741824d, false, "1 GB")]
    public void ByteAndRateAxesKeepTheirUnits(double value, bool rate, string expected) =>
        Assert.Equal(expected, DashboardPlot.MetricLabel(value, value, bytes: true, rate));

    [Fact]
    public void ScaleExpandsImmediatelyButContractsOnlyAfterHoldAndCanResetForNewSource()
    {
        var state = new StableTrendScale();
        var high = state.Update([18], false, Epoch);
        Assert.Equal(high, state.Update([.01], false, Epoch.AddSeconds(5)));
        Assert.True(state.Update([90], false, Epoch.AddSeconds(10)).Maximum >= 90);
        Assert.True(state.Update([.01], false, Epoch.AddSeconds(42)).Maximum < high.Maximum);
        state.Reset(); Assert.Equal(.1, state.Update([0], false, Epoch.AddSeconds(43)).Maximum);
    }
}
