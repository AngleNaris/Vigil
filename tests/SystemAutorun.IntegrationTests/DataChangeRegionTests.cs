using SystemAutorun.App.Controls;
using SystemAutorun.App.Services;

namespace SystemAutorun.IntegrationTests;

public sealed class DataChangeRegionTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch.AddDays(100);
    private static DateTimeOffset At(int seconds) => Start.AddSeconds(seconds);
    [Fact]
    public void ConstantAppendsPansAndFirstValidLoadAreNotChanges()
    {
        PlotPoint[] old = [new(At(0), 10), new(At(1), 10)];
        Assert.Equal([false, false], DataChangeRegions.Trend(old, [new(At(1), 10), new(At(2), 10)], At(1), At(2)));
        Assert.Equal([false], DataChangeRegions.Trend([], [new(At(0), 10)], null, At(0)));
        Assert.Equal([false], DataChangeRegions.Trend([new(At(0), null)], [new(At(0), 10)], At(0), At(0)));
        Assert.Equal([false, false], DataChangeRegions.Trend(old, old, At(1), At(1)));
    }
    [Fact]
    public void ChangesAreLocalToAnActualValueOrNewDifferentReading()
    {
        PlotPoint[] old = [new(At(0), 10), new(At(1), 10), new(At(2), null)];
        Assert.Equal([false, true, false, true], DataChangeRegions.Trend(old,
            [new(At(0), 10), new(At(1), 12), new(At(2), null), new(At(3), 11, BreakBefore: true)], At(2), At(3)));
        Assert.Equal([false], DataChangeRegions.Trend(old, [new(At(3), double.NaN)], At(2), At(3)));
    }
    [Fact]
    public void MovingDurableBucketBoundsDoNotInventFreshObservations()
    {
        PlotPoint[] old = [new(At(5), 20, At(0), At(10)), new(At(15), 30, At(10), At(20))];
        PlotPoint[] pan = [new(At(6), 21, At(1), At(11)), new(At(16), 31, At(11), At(21))];
        Assert.Equal([false, false], DataChangeRegions.Trend(old, pan, At(19), At(19)));
        Assert.Equal([false, true], DataChangeRegions.Trend(old, pan, At(19), At(20)));
    }
    [Fact]
    public void HealthKeepsStateAndCellSemanticsAcrossExtensionUnknownsAndOverlaps()
    {
        HealthInterval[] old = [new(At(0), At(60), "healthy")];
        Assert.Empty(DataChangeRegions.Health(old, [new(At(0), At(70), "healthy")], At(60), At(0), At(70)));
        Assert.Equal([new HealthInterval(At(20), At(60), "unknown")],
            DataChangeRegions.Health(old, [new(At(0), At(20), "healthy"), new(At(60), At(70), "healthy")], At(60), At(0), At(70)));
        Assert.Equal([new HealthInterval(At(20), At(30), "healthy")],
            DataChangeRegions.Health([new(At(0), At(60), "healthy"), new(At(20), At(30), "failed")],
                [new(At(0), At(60), "healthy"), new(At(20), At(30), "unknown")], At(60), At(0), At(60)));
    }
    [Fact]
    public void HealthSweepMatchesIndependentPerSecondOracleWithOverlaps()
    {
        var random = new Random(121);
        for (var run = 0; run < 100; run++)
        {
            var old = Rows(); var current = Rows(); var changed = DataChangeRegions.Health(old, current, At(60), At(10), At(70));
            for (var second = 10; second < 70; second++)
            {
                var oldState = State(old, Math.Min(second, 59)); var currentState = State(current, second);
                var span = changed.SingleOrDefault(row => row.From <= At(second) && row.To > At(second));
                Assert.Equal(oldState != currentState, span is not null);
                if (span is not null) { Assert.Equal(currentState, span.State); }
            }
        }
        HealthInterval[] Rows() => Enumerable.Range(0, 30).Select(_ =>
        { var from = random.Next(0, 70); return new HealthInterval(At(from), At(random.Next(from + 1, 71)), random.Next(3) switch { 0 => "healthy", 1 => "failed", _ => "unknown" }); }).ToArray();
        static string State(HealthInterval[] rows, int second) => rows.LastOrDefault(row => row.From <= At(second) && row.To > At(second)
            && row.State is "healthy" or "failed")?.State ?? "unknown";
    }
}
