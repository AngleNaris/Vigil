using System.IO;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.Core.Tests;

public sealed class ResourceHistoryTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-12T00:00:00Z");
    private static readonly string Identity = new('a', 64);
    private static ResourceHistoryFrame Frame(int second, int tasks = 1) => new(At.AddSeconds(second), "agent",
        Enumerable.Range(0, tasks).Select(index => new ResourceTaskSample($"task{index}", Identity, At.AddSeconds(second), "process", 10, 100)).ToArray(),
        new(20, 500, 1000, 100, 200, 300, 400), At.AddSeconds(second));
    private static ResourceHistoryQuery Query(DateTimeOffset from, DateTimeOffset to, bool task = false,
        ResourceHistorySource source = ResourceHistorySource.RecentSeconds, int count = 301) =>
        new(from, to, count, task ? "task0" : null, task ? Identity : null, source);

    [Fact]
    public void RingRetainsFiveMinutesOfActualPointsAndNeverNeedsASelectedTask()
    {
        var ring = new ResourceHistoryBuffer();
        for (var second = 0; second <= 360; second++) { ring.Append(Frame(second, 2)); }
        var now = At.AddSeconds(360);
        var query = Query(now.AddMinutes(-5), now, task: true) with { TaskName = "task1" };
        var result = ring.Query(query, now);
        Assert.Equal(301, result.Points.Count); Assert.Equal(now.AddMinutes(-5), result.RetainedFrom);
        Assert.Equal(now, result.LatestRecordAt); Assert.False(result.Truncated);
        Assert.All(result.Points, point => Assert.Equal(100, point.MemoryBytes));
        Assert.Equal(1, result.SourceIntervalSeconds);
    }

    [Fact]
    public void HalfSecondRecentHistoryRetainsRealFiveMinutesAndLegacyOneSecondView()
    {
        var ring = new ResourceHistoryBuffer();
        for (var index = 0; index <= 600; index++) { ring.Append(HalfFrame(index)); }
        var now = At.AddMinutes(5); var query = Query(At, now, task: true, count: 601);
        var result = ring.QueryPrecise(query, now, 500, Guid.Empty.ToString("N"));
        ResourceHistoryResponseValidator.Validate(result, query);
        Assert.Equal(601, result.Points.Count); Assert.Equal(At, result.Points[0].At); Assert.Equal(now, result.Points[^1].At);
        Assert.Equal(500, result.SourceIntervalMilliseconds); Assert.Equal(.5, result.BucketSeconds); Assert.False(result.Truncated);
        var legacyQuery = query with { BucketCount = 301 };
        var legacy = ring.QueryCompatible(legacyQuery, now);
        ResourceHistoryResponseValidator.Validate(legacy, legacyQuery);
        Assert.Equal(301, legacy.Points.Count); Assert.Equal(1, legacy.SourceIntervalSeconds); Assert.False(legacy.Truncated);
        Assert.All(legacy.Points, point => Assert.Contains(result.Points, actual => actual.At == point.At && actual.CpuPercent == point.CpuPercent));
    }

    [Fact]
    public void LegacyProjectionAndPreciseSchemaKeepUnknownAndMissingTickBarriers()
    {
        var ring = new ResourceHistoryBuffer(); ring.Append(HalfFrame(0)); ring.Append(HalfFrame(1));
        var missing = HalfFrame(2) with { Tasks = [], Host = null, HostCapturedAt = null };
        ring.Append(missing); ring.Append(HalfFrame(3)); ring.Append(HalfFrame(5)); // Half-second tick 4 was not captured.
        var now = At.AddMilliseconds(2500); var query = Query(At, now, count: 601);
        var precise = ring.QueryPrecise(query, now, 500, Guid.Empty.ToString("N")); ResourceHistoryResponseValidator.Validate(precise, query);
        Assert.Equal(0, precise.Points[2].SampleCount); Assert.True(precise.Points[^1].BreakBefore);
        var legacyQuery = query with { BucketCount = 301 }; var legacy = ring.QueryCompatible(legacyQuery, now);
        ResourceHistoryResponseValidator.Validate(legacy, legacyQuery);
        Assert.True(legacy.Points[1].BreakBefore); Assert.True(legacy.Points[^1].BreakBefore);
        Assert.Throws<InvalidDataException>(() => ResourceHistoryResponseValidator.Validate(precise with { SourceIntervalMilliseconds = 1 }, query));
        var joinedGap = precise.Points.ToArray(); joinedGap[^1] = joinedGap[^1] with { BreakBefore = false };
        Assert.Throws<InvalidDataException>(() => ResourceHistoryResponseValidator.Validate(precise with { Points = joinedGap }, query));
        Assert.Throws<InvalidDataException>(() => ResourceHistoryResponseValidator.Validate(precise with { Points = [precise.Points[0] with { SampleCount = 0 }] }, query));
    }

    private static ResourceHistoryFrame HalfFrame(int index)
    {
        var at = At.AddMilliseconds(index * 500);
        return new(at, "agent", [new("task0", Identity, at, "process", 10, 100)],
            new(20, 500, 1000, 100, 200, 300, 400), at);
    }

    [Fact]
    public void MissingRowsAndUnknownHostRemainTimeBarriersRatherThanBorrowingPreviousData()
    {
        var ring = new ResourceHistoryBuffer(); ring.Append(Frame(0));
        ring.Append(new(At.AddSeconds(1), "agent", [])); ring.Append(Frame(2));
        foreach (var task in new[] { false, true })
        {
            var result = ring.Query(Query(At, At.AddSeconds(2), task), At.AddSeconds(2));
            Assert.Equal(3, result.Points.Count); Assert.Null(result.Points[1].CpuPercent);
            Assert.Null(result.Points[1].MemoryBytes); Assert.True(result.Points[1].BreakBefore);
            Assert.Equal(0, result.Points[1].SampleCount);
        }
    }

    [Fact]
    public void PayloadPressureShortensRetentionExplicitlyAndNeverSilentlyTakesTasks()
    {
        var ring = new ResourceHistoryBuffer(maximumRows: 3);
        ring.Append(Frame(0, 2)); ring.Append(Frame(1, 2));
        var result = ring.Query(Query(At, At.AddSeconds(1), true), At.AddSeconds(1));
        Assert.True(result.Truncated); Assert.Single(result.Points); Assert.NotNull(result.Warning);
        var rejected = new ResourceHistoryBuffer(maximumRows: 1); rejected.Append(Frame(0, 2));
        var unavailable = rejected.Query(Query(At.AddSeconds(-1), At, true), At);
        Assert.True(unavailable.Truncated); Assert.Null(Assert.Single(unavailable.Points).MemoryBytes);
        Assert.Null(unavailable.RetainedFrom);
        var bytes = new ResourceHistoryBuffer(maximumBytes: 512); bytes.Append(Frame(0, 2));
        Assert.True(bytes.Query(Query(At.AddSeconds(-1), At), At).Truncated);
    }

    [Fact]
    public void ClockRollbackStartsANewResourceEpochImmediatelyAndRestartDoesNotRehydrateSeconds()
    {
        var ring = new ResourceHistoryBuffer(); ring.Append(Frame(10));
        var before = ring.Query(Query(At, At.AddSeconds(10)), At.AddSeconds(10));
        ring.Append(Frame(5));
        var after = ring.Query(Query(At, At.AddSeconds(5)), At.AddSeconds(5));
        Assert.Single(after.Points); Assert.True(after.Truncated);
        Assert.NotEqual(before.Points[0].ContinuityId, after.Points[0].ContinuityId);
        Assert.Empty(new ResourceHistoryBuffer().Query(Query(At, At.AddSeconds(5)), At.AddSeconds(5)).Points);
    }

    [Fact]
    public void DuplicateCapturedTimestampsDoNotBecomeFreshSecondPoints()
    {
        var ring = new ResourceHistoryBuffer(); var first = Frame(0);
        ring.Append(first); ring.Append(first with { At = At.AddSeconds(1) });
        var result = ring.Query(Query(At, At.AddSeconds(1)), At.AddSeconds(1));
        Assert.Single(result.Points); Assert.Equal(At, result.LatestRecordAt);
    }

    [Fact]
    public void DurableOldHostValuesStayUnknownWhileLegacyWorkingSetBytesRemainQueryable()
    {
        var frames = new[] { new MonitoringHistoryFrame(1, "root", "agent:1", At,
            [new("task0", Identity, ObservationHealth.Healthy, WorkingSetBytes: 100)]) };
        var host = ResourceHistoryProjection.ProjectDurable(frames,
            Query(At, At.AddSeconds(20), source: ResourceHistorySource.Durable, count: 1000), At.AddSeconds(20));
        Assert.Equal(2, host.Points.Count); Assert.Equal(10, host.SourceIntervalSeconds);
        Assert.All(host.Points, point => { Assert.Null(point.MemoryBytes); Assert.Null(point.DiskReadBytesPerSecond); });
        Assert.Null(host.RetainedFrom);
        var task = ResourceHistoryProjection.ProjectDurable(frames,
            Query(At, At.AddSeconds(20), task: true, source: ResourceHistorySource.Durable), At.AddSeconds(20));
        Assert.Equal(100, task.Points[0].MemoryBytes); Assert.Null(task.Points[0].ContinuityId);
        Assert.Null(task.Points[1].MemoryBytes); Assert.Equal(10, task.BucketSeconds);
    }

    [Fact]
    public void DurableAggregationUsesKnownSamplesWithoutInventingHealthValidityOrContinuity()
    {
        var frames = new[]
        {
            new MonitoringHistoryFrame(1, "root", "agent:1", At, [], Host: new(10, 100, 1000, 10)),
            new MonitoringHistoryFrame(1, "root", "agent:2", At.AddSeconds(10), [], Host: new(30, 300, 1000, 30)),
        };
        var result = ResourceHistoryProjection.ProjectDurable(frames,
            Query(At, At.AddSeconds(20), source: ResourceHistorySource.Durable, count: 1), At.AddSeconds(20));
        var point = Assert.Single(result.Points); Assert.Equal(20, point.CpuPercent); Assert.Equal(200, point.MemoryBytes);
        Assert.Equal(2, point.SampleCount); Assert.True(point.BreakBefore); Assert.Null(point.ContinuityId);
    }

    [Fact]
    public void DurableCrossBucketGapsCannotAssertContinuityEvenWhenEachBucketHasOnePoint()
    {
        var frames = new[]
        {
            new MonitoringHistoryFrame(1, "root", "agent:1", At, [], Host: new(CpuPercent: 10)),
            new MonitoringHistoryFrame(1, "root", "agent:1", At.AddSeconds(90), [], Host: new(CpuPercent: 30)),
        };
        var result = ResourceHistoryProjection.ProjectDurable(frames,
            Query(At, At.AddSeconds(120), source: ResourceHistorySource.Durable, count: 2), At.AddSeconds(120));
        Assert.Equal(2, result.Points.Count); Assert.True(result.Points[1].BreakBefore);
        Assert.Null(result.Points[1].ContinuityId); Assert.Equal(30, result.Points[1].CpuPercent);
    }

    [Fact]
    public void PartiallyMissingMetricsDoNotBecomeAContinuousBucketMean()
    {
        var frames = new[]
        {
            new MonitoringHistoryFrame(1, "root", "agent:1", At, [], Host: new(10, 100)),
            new MonitoringHistoryFrame(1, "root", "agent:1", At.AddSeconds(10), [], Host: new(MemoryUsedBytes: 100)),
            new MonitoringHistoryFrame(1, "root", "agent:1", At.AddSeconds(20), [], Host: new(30, 100)),
        };
        var query = Query(At, At.AddSeconds(30), source: ResourceHistorySource.Durable, count: 1);
        var result = ResourceHistoryProjection.ProjectDurable(frames, query, At.AddSeconds(30));
        var point = Assert.Single(result.Points); Assert.Equal(20, point.CpuPercent);
        Assert.True(point.BreakBefore); Assert.Null(point.ContinuityId);
        ResourceHistoryResponseValidator.Validate(result, query);
    }

    [Fact]
    public void FractionalTickUnknownBucketCannotCollideWithLastRealCapture()
    {
        var query = Query(At, At.AddSeconds(31), source: ResourceHistorySource.Durable, count: 3);
        var captured = At.AddTicks(TimeSpan.FromSeconds(31).Ticks / 3);
        var result = ResourceHistoryProjection.ProjectDurable([new(1, "root", "agent", captured, [], Host: new(10))], query, query.To);
        Assert.Equal(captured, result.Points[0].At); Assert.True(result.Points[1].At > captured);
        Assert.Equal(captured, result.LatestRecordAt); Assert.Equal(0, result.Points[1].SampleCount);
        ResourceHistoryResponseValidator.Validate(result, query);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DurableResponseCannotOmitABucketOrShiftItsValueIntoAnotherBucket(bool shifted)
    {
        var query = Query(At, At.AddSeconds(30), source: ResourceHistorySource.Durable, count: 3);
        var result = ResourceHistoryProjection.ProjectDurable([], query, query.To);
        result = result with { Points = shifted ? [result.Points[0] with { At = At.AddSeconds(11) }, result.Points[1], result.Points[2]] : result.Points.Take(2).ToArray() };
        Assert.Throws<InvalidDataException>(() => ResourceHistoryResponseValidator.Validate(result, query));
    }

    [Theory]
    [InlineData("schema")] [InlineData("source")] [InlineData("target")] [InlineData("window")]
    [InlineData("cadence")] [InlineData("bucket")] [InlineData("count")] [InlineData("future")]
    [InlineData("order")] [InlineData("cpu")] [InlineData("memory")] [InlineData("network")]
    [InlineData("identity")] [InlineData("metadata")]
    public void ClientRejectsMismatchedOutOfBoundsAndMalformedResourceResponses(string kind)
    {
        var ring = new ResourceHistoryBuffer(); ring.Append(Frame(0));
        var query = Query(At.AddSeconds(-1), At); var result = ring.Query(query, At);
        ResourceHistoryResponseValidator.Validate(result, query);
        var point = result.Points[0];
        result = kind switch
        {
            "schema" => result with { SchemaVersion = 2 },
            "source" => result with { Source = ResourceHistorySource.Durable },
            "target" => result with { TaskName = "wrong-target" },
            "window" => result with { From = query.From.AddTicks(1) },
            "cadence" => result with { SourceIntervalSeconds = 10 },
            "bucket" => result with { BucketSeconds = double.NaN },
            "count" => result with { Points = Enumerable.Repeat(point, 1001).ToArray() },
            "future" => result with { Points = [point with { At = query.To.AddTicks(1) }] },
            "order" => result with { Points = [point, point] },
            "cpu" => result with { Points = [point with { CpuPercent = 101 }] },
            "memory" => result with { Points = [point with { MemoryBytes = -1 }] },
            "network" => result with { Points = [point with { NetworkSendBytesPerSecond = double.PositiveInfinity }] },
            "identity" => result with { Points = [point with { ContinuityId = new string('x', 257) }] },
            _ => result with { LatestRecordAt = query.To.AddTicks(1) },
        };
        Assert.Throws<InvalidDataException>(() => ResourceHistoryResponseValidator.Validate(result, query));
    }

    [Theory]
    [InlineData("future")] [InlineData("window")] [InlineData("buckets")] [InlineData("partial-host")]
    [InlineData("identity")] [InlineData("name")] [InlineData("source")]
    public void InvalidResourceQueriesAreRejected(string kind)
    {
        var query = Query(At, At.AddSeconds(10));
        query = kind switch
        {
            "future" => query with { To = At.AddSeconds(11) },
            "window" => query with { From = At.AddMinutes(-6) },
            "buckets" => query with { BucketCount = 1001 },
            "partial-host" => query with { TaskIdentity = Identity },
            "identity" => query with { TaskName = "task0", TaskIdentity = "wrong" },
            "name" => query with { TaskName = new string('x', 513), TaskIdentity = Identity },
            _ => query with { Source = (ResourceHistorySource)99 },
        };
        Assert.Throws<ArgumentException>(() => ResourceHistoryProjection.ValidateQuery(query, At.AddSeconds(10)));
    }

    [Fact]
    public void ProcessContinuityUsesTheCurrentPidAndStartSetRatherThanNamesOrHealthRuntime()
    {
        var process = new ManagedProcessSnapshot("task", "", "windows", false, true, true, "", "", "", At,
            [new(123, "name", null, At.AddSeconds(-10), 0, 100, 100, 1)]);
        var identity = ResourceProcessIdentity.For(process, At); Assert.NotNull(identity);
        Assert.NotEqual(identity, ResourceProcessIdentity.For(process with { Processes = [process.Processes[0] with { StartedAt = At.AddSeconds(-1) }] }, At));
        Assert.Null(ResourceProcessIdentity.For(process with { Processes = [process.Processes[0] with { StartedAt = null }] }, At));
        Assert.Null(ResourceProcessIdentity.For(process with { Type = "wsl" }, At));
    }
}
