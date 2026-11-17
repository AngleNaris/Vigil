using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.Core.Tests;

public sealed class MonitoringHistoryPercentBoundaryTests
{
    [Fact]
    public void FractionalSecondObservationKeepsAggregateAndBucketAvailabilityWithinPercentDomain()
    {
        var from = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddTicks(13);
        var seconds = (to - from).TotalSeconds;
        // Deterministic reproduction of the real parity failure: an otherwise valid
        // all-healthy percentage rounds above 100 and the GUI correctly rejects it.
        Assert.True(seconds * 100d / seconds > 100d);
        var query = new MonitoringHistoryQuery("fixture", "identity", from, to, 1);
        var result = MonitoringHistoryProjection.Project(
            [new(1, "root", "current", from, [new("fixture", "identity", ObservationHealth.Healthy)])],
            query, to, "current");
        Assert.Equal(seconds, result.HealthySeconds); Assert.Equal(0, result.FaultSeconds); Assert.Equal(0, result.UnknownSeconds);
        Assert.Equal(100d, result.AvailabilityPercent); Assert.Equal(100d, result.CoveragePercent);
        var bucket = Assert.Single(result.Buckets);
        Assert.Equal(seconds, bucket.HealthySeconds); Assert.Equal(0, bucket.FaultSeconds); Assert.Equal(0, bucket.UnknownSeconds);
        Assert.Equal(100d, bucket.AvailabilityPercent); Assert.Equal(100d, bucket.CoveragePercent);
    }
}
