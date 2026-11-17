using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.Core.Tests;

public sealed class MonitoringHistoryProjectionTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CountsOnlyObservedTimeIncludingIntentionalFaultsAndKeepsMissingTimeUnknown()
    {
        var result = Project([
            Frame(0, ObservationHealth.Healthy, cpu: 10, memory: 100),
            Frame(10, ObservationHealth.Fault, cpu: 20, memory: 200),
            Frame(20, ObservationHealth.Unknown),
            Frame(40, ObservationHealth.Healthy, cpu: 40, memory: 400),
        ], 60);
        Assert.Equal(20, result.HealthySeconds);
        Assert.Equal(10, result.FaultSeconds);
        Assert.Equal(30, result.UnknownSeconds);
        Assert.Equal(200d / 3, result.AvailabilityPercent!.Value, 7);
        Assert.Equal(50, result.CoveragePercent);
        var bucket = Assert.Single(result.Buckets);
        Assert.Equal(70d / 3, bucket.CpuPercent!.Value, 7);
        Assert.Equal(70d / 3, bucket.MemoryPercent!.Value, 7);
    }

    [Fact]
    public void NoSamplesNeverMeansOneHundredPercentAvailable()
    {
        var result = Project([], 3600);
        Assert.Null(result.AvailabilityPercent);
        Assert.Null(result.RetainedFrom);
        Assert.Equal(0, result.CoveragePercent);
        Assert.Equal(3600, result.UnknownSeconds);
        Assert.Empty(result.Intervals);
        Assert.Null(Assert.Single(result.Buckets).CpuPercent);
    }

    [Fact]
    public void RestartsDoNotExtendTheOldInstanceAcrossAnUnmonitoredBoundary()
    {
        var result = Project([
            Frame(0, ObservationHealth.Healthy, instance: "old"),
            Frame(10, ObservationHealth.Healthy, instance: "old"),
            Frame(20, ObservationHealth.Healthy), Frame(30, ObservationHealth.Fault),
        ], 40);
        Assert.Equal(20, result.HealthySeconds);
        Assert.Equal(10, result.FaultSeconds);
        Assert.Equal(10, result.UnknownSeconds);
        Assert.Equal(75, result.CoveragePercent);
        var inactive = MonitoringHistoryProjection.Project([Frame(0, ObservationHealth.Healthy, instance: "old")],
            Query(20), Start.AddSeconds(20), "current");
        Assert.Null(inactive.AvailabilityPercent);
    }

    [Fact]
    public void LongGapsFutureDataAndDifferentWorkloadIdentitiesAreNotFilledIn()
    {
        var wrong = Frame(20, ObservationHealth.Healthy) with
        {
            Tasks = [new TaskHistorySample("task", "replacement-target", ObservationHealth.Healthy)],
        };
        var result = Project([Frame(0, ObservationHealth.Healthy), wrong,
            Frame(60, ObservationHealth.Healthy), Frame(200, ObservationHealth.Fault)], 100);
        Assert.Equal(20, result.HealthySeconds);
        Assert.Equal(80, result.UnknownSeconds);
        Assert.Equal(20, result.CoveragePercent);
    }

    [Fact]
    public void RemovingTrackingEndsThePriorObservationRatherThanFillingItAsHealthy()
    {
        var missing = Frame(5, ObservationHealth.Healthy) with { Tasks = [] };
        var result = Project([Frame(0, ObservationHealth.Healthy), missing], 20);
        Assert.Equal(5, result.HealthySeconds);
        Assert.Equal(15, result.UnknownSeconds);
        Assert.Equal(25, result.CoveragePercent);
    }

    [Fact]
    public void IntervalLimitDoesNotChangeFullWindowStatistics()
    {
        var frames = Enumerable.Range(0, 9000).Select(i => Frame(i,
            i % 2 == 0 ? ObservationHealth.Healthy : ObservationHealth.Fault));
        var result = Project(frames, 9000);
        Assert.True(result.Truncated);
        Assert.Equal(MonitoringHistoryProjection.MaximumIntervals, result.Intervals.Count);
        Assert.Equal(4500, result.HealthySeconds);
        Assert.Equal(4500, result.FaultSeconds);
        Assert.Equal(100, result.CoveragePercent);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(30, 10)]
    public void ExplicitValidityCanShortenButNeverExtendTheSamplingInterval(int validitySeconds, int expectedKnown)
    {
        var frame = Frame(0, ObservationHealth.Healthy, cpu: 10, memory: 100);
        frame = frame with { Tasks = [frame.Tasks[0] with { ValidUntil = Start.AddSeconds(validitySeconds) }] };
        var result = Project([frame], 20);
        Assert.Equal(expectedKnown, result.HealthySeconds);
        Assert.Equal(20 - expectedKnown, result.UnknownSeconds);
        if (expectedKnown == 0) { Assert.Null(Assert.Single(result.Buckets).CpuPercent); }
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(120, 4)]
    [InlineData(1000, 4)]
    [InlineData(1, 10)]
    [InlineData(120, 10)]
    [InlineData(1000, 10)]
    public void ASpanCrossingManyBucketsIsClippedIntoEveryIntersectingBucket(int buckets, int validSeconds)
    {
        var frame = Frame(0, ObservationHealth.Healthy, cpu: 12.5, memory: 250);
        frame = frame with { Tasks = [frame.Tasks[0] with { ValidUntil = Start.AddSeconds(validSeconds) }] };
        var result = MonitoringHistoryProjection.Project([frame], Query(10) with { BucketCount = buckets }, Start.AddSeconds(10), "current");
        Assert.Equal(buckets, result.Buckets.Count);
        Assert.Equal(validSeconds, result.HealthySeconds);
        Assert.Equal(10 - validSeconds, result.UnknownSeconds);
        Assert.Equal(validSeconds, result.Buckets.Sum(bucket => bucket.HealthySeconds), 7);
        Assert.Equal(10 - validSeconds, result.Buckets.Sum(bucket => bucket.UnknownSeconds), 7);
        var until = Start.AddSeconds(validSeconds);
        foreach (var bucket in result.Buckets)
        {
            var expected = Math.Max(0, ((bucket.To < until ? bucket.To : until) - bucket.From).TotalSeconds);
            Assert.Equal(expected, bucket.HealthySeconds, 7);
            if (expected > 0) { Assert.Equal(12.5, bucket.CpuPercent!.Value, 7); Assert.Equal(25, bucket.MemoryPercent!.Value, 7); }
            else { Assert.Null(bucket.CpuPercent); Assert.Null(bucket.MemoryPercent); Assert.Null(bucket.AvailabilityPercent); }
        }
    }

    [Fact]
    public void RejectsFutureOrUnboundedWindowsAndZeroBuckets()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MonitoringHistoryProjection.Project([], Query(60), Start, "current"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Project([], 31 * 86400));
        Assert.Throws<ArgumentOutOfRangeException>(() => MonitoringHistoryProjection.Project([], Query(60) with { BucketCount = 0 }, Start.AddSeconds(60), "current"));
    }

    private static MonitoringHistoryResult Project(IEnumerable<MonitoringHistoryFrame> frames, int seconds) =>
        MonitoringHistoryProjection.Project(frames, Query(seconds), Start.AddSeconds(seconds), "current");
    private static MonitoringHistoryQuery Query(int seconds) => new("task", "target", Start, Start.AddSeconds(seconds), 1);
    private static MonitoringHistoryFrame Frame(int second, ObservationHealth health, string instance = "current", double? cpu = null, long? memory = null) =>
        new(1, "root", instance, Start.AddSeconds(second), [new TaskHistorySample("task", "target", health, cpu, memory, 1000)]);
}
