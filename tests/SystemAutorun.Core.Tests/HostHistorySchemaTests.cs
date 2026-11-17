using System.Reflection;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.Core.Tests;

public sealed class HostHistorySchemaTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-12T00:00:00Z");
    private const string Instance = "11111111111111111111111111111111";
    private static readonly string Identity = new('a', 64);

    [Fact]
    public void OldFrameAndBucketJsonKeepNewFieldsUnknown()
    {
        const string oldFrame = """{"schemaVersion":1,"rootId":"fixture","agentInstanceId":"11111111111111111111111111111111","observedAt":"2026-11-12T00:00:00Z","tasks":[]}""";
        var frame = JsonSerializer.Deserialize<MonitoringHistoryFrame>(oldFrame, AgentProtocol.JsonOptions)!;
        Assert.Null(frame.Host); Assert.Equal(1, frame.SchemaVersion);
        var bucket = JsonSerializer.Deserialize<MonitoringHistoryBucket>("""{"from":"2026-11-12T00:00:00Z","to":"2026-11-12T00:00:10Z","healthySeconds":0,"faultSeconds":0,"unknownSeconds":10,"availabilityPercent":null,"coveragePercent":0,"cpuPercent":null,"memoryPercent":null}""", AgentProtocol.JsonOptions)!;
        Assert.Null(bucket.WorkingSetBytes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void WorkingSetByteBucketsDoNotRequireAPhysicalMemoryDenominator(bool missing)
    {
        var frames = new[]
        {
            new MonitoringHistoryFrame(1, "fixture", Instance, At, [new("task", Identity, ObservationHealth.Unknown, WorkingSetBytes: missing ? null : 100)]),
            new MonitoringHistoryFrame(1, "fixture", Instance, At.AddSeconds(10), [new("task", Identity, ObservationHealth.Unknown, WorkingSetBytes: missing ? null : 400)]),
        };
        var result = MonitoringHistoryProjection.Project(frames, new("task", Identity, At, At.AddSeconds(15), 1), At.AddSeconds(15), Instance);
        var bucket = Assert.Single(result.Buckets); Assert.Null(bucket.MemoryPercent);
        Assert.Equal(missing ? null : (double?)200, bucket.WorkingSetBytes);
        Assert.Equal(15, bucket.UnknownSeconds);
    }

    [Fact]
    public async Task HostExtensionPersistsInTheExistingSchemaOneSegment()
    {
        using var fixture = new Fixture();
        var host = new HostHistorySample(12, 500, 1000, 0, 1250, 200, 300);
        await fixture.Store.AppendAsync(fixture.Frame(host));
        var path = Assert.Single(Directory.GetFiles(fixture.Store.DirectoryPath, "history-v1-*.jsonl"));
        var stored = JsonSerializer.Deserialize<MonitoringHistoryFrame>(await File.ReadAllTextAsync(path), AgentProtocol.JsonOptions)!;
        Assert.Equal(1, stored.SchemaVersion); Assert.Equal(host, stored.Host);
        var result = await fixture.Store.QueryAsync(new("task", Identity, At, At.AddSeconds(10), 1), Instance);
        Assert.Equal(100, Assert.Single(result.Buckets).WorkingSetBytes);
    }

    [Theory]
    [InlineData("cpu-nan")] [InlineData("cpu-range")] [InlineData("used-negative")] [InlineData("physical-negative")]
    [InlineData("used-exceeds-total")] [InlineData("read-nan")] [InlineData("write-infinite")]
    [InlineData("receive-negative")] [InlineData("send-nan")]
    public async Task InvalidHostNumbersAreRejectedBeforeAnySegmentIsPublished(string kind)
    {
        using var fixture = new Fixture();
        var host = kind switch
        {
            "cpu-nan" => new HostHistorySample(CpuPercent: double.NaN),
            "cpu-range" => new HostHistorySample(CpuPercent: 101),
            "used-negative" => new HostHistorySample(MemoryUsedBytes: -1),
            "physical-negative" => new HostHistorySample(PhysicalMemoryBytes: -1),
            "used-exceeds-total" => new HostHistorySample(MemoryUsedBytes: 100, PhysicalMemoryBytes: 50),
            "read-nan" => new HostHistorySample(DiskReadBytesPerSecond: double.NaN),
            "write-infinite" => new HostHistorySample(DiskWriteBytesPerSecond: double.PositiveInfinity),
            "receive-negative" => new HostHistorySample(NetworkReceiveBytesPerSecond: -1),
            _ => new HostHistorySample(NetworkSendBytesPerSecond: double.NaN),
        };
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.AppendAsync(fixture.Frame(host)));
        Assert.Empty(Directory.GetFiles(fixture.Store.DirectoryPath, "history-v1-*.jsonl"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ResourceReadSharesTornTailAndMissingAppendBarriers(bool removeSegment)
    {
        using var fixture = new Fixture();
        await fixture.Store.AppendAsync(fixture.Frame(new(12, 500, 1000, 100, 200, 300, 400)));
        var path = Assert.Single(Directory.GetFiles(fixture.Store.DirectoryPath, "history-v1-*.jsonl"));
        if (removeSegment) { File.Delete(path); }
        else { await File.AppendAllTextAsync(path, "{\\\"schemaVersion\\\":"); }
        var result = await fixture.Store.QueryResourcesAsync(new(At, At.AddSeconds(10), Source: ResourceHistorySource.Durable), Instance);
        Assert.True(result.Truncated); Assert.NotNull(result.Warning);
        var point = Assert.Single(result.Points); Assert.Null(point.CpuPercent); Assert.Null(point.MemoryBytes);
        Assert.Null(point.DiskReadBytesPerSecond); Assert.Null(point.NetworkSendBytesPerSecond);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ResourceReadSharesRollbackAndSameTimeEpochInvalidation(bool sameTime)
    {
        using var fixture = new Fixture();
        await fixture.Store.AppendAsync(fixture.Frame(new(CpuPercent: 10)));
        await fixture.Store.AppendAsync(fixture.Frame(new(CpuPercent: 20)) with { ObservedAt = sameTime ? At : At.AddSeconds(-5) });
        var result = await fixture.Store.QueryResourcesAsync(new(At.AddSeconds(-10), At.AddSeconds(10), Source: ResourceHistorySource.Durable), Instance);
        Assert.True(result.Truncated); Assert.Contains(result.Points, point => point.CpuPercent == 20);
        Assert.DoesNotContain(result.Points, point => point.CpuPercent == 10);
    }

    [Fact]
    public async Task MoreThan256FittingTasksDoNotDisableHostOrTaskHistory()
    {
        using var fixture = new Fixture();
        var tasks = Enumerable.Range(0, 300).Select(index => new TaskHistorySample($"task-{index}", Identity,
            ObservationHealth.Healthy, WorkingSetBytes: 100)).ToArray();
        await fixture.Store.AppendAsync(fixture.Frame(new(CpuPercent: 12)) with { Tasks = tasks });
        var host = await fixture.Store.QueryResourcesAsync(new(At, At.AddSeconds(10), Source: ResourceHistorySource.Durable), Instance);
        Assert.Equal(12, Assert.Single(host.Points).CpuPercent);
        var last = await fixture.Store.QueryResourcesAsync(new(At, At.AddSeconds(10), TaskName: "task-299", TaskIdentity: Identity,
            Source: ResourceHistorySource.Durable), Instance);
        Assert.Equal(100, Assert.Single(last.Points).MemoryBytes);
    }

    [Fact]
    public async Task MidFileCorruptionInvalidatesAdjacentHostButPreservesLaterGoodRecords()
    {
        using var fixture = new Fixture();
        await fixture.Store.AppendAsync(fixture.Frame(new(CpuPercent: 10)));
        await fixture.Store.AppendAsync(fixture.Frame(new(CpuPercent: 20)) with { ObservedAt = At.AddSeconds(5) });
        await fixture.Store.AppendAsync(fixture.Frame(new(CpuPercent: 30)) with { ObservedAt = At.AddSeconds(10) });
        var path = Assert.Single(Directory.GetFiles(fixture.Store.DirectoryPath, "history-v1-*.jsonl"));
        var lines = await File.ReadAllLinesAsync(path); lines[1] = "not-json";
        await File.WriteAllLinesAsync(path, lines);
        var result = await fixture.Store.QueryResourcesAsync(new(At, At.AddSeconds(20), Source: ResourceHistorySource.Durable), Instance);
        Assert.True(result.Truncated); Assert.Null(result.Points[0].CpuPercent);
        Assert.Equal(30, result.Points[1].CpuPercent);
    }

    [Fact]
    public async Task NewLongReadsYieldImmediatelyWhileADurableSampleIsQueued()
    {
        using var fixture = new Fixture();
        var gate = (SemaphoreSlim)typeof(MonitoringHistoryStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Store)!;
        await gate.WaitAsync();
        var writer = fixture.Store.AppendAsync(fixture.Frame(new(CpuPercent: 12)));
        Task<ResourceHistoryResult>? reader = null;
        try
        {
            reader = fixture.Store.QueryResourcesAsync(new(At, At.AddSeconds(10), Source: ResourceHistorySource.Durable), Instance);
            Assert.True(reader.IsCompleted, "A new reader must yield to the queued sampler without spending its read budget.");
            await Assert.ThrowsAsync<TimeoutException>(() => reader);
        }
        finally
        {
            gate.Release(); await writer;
            if (reader is not null) { try { await reader; } catch (TimeoutException) { } }
        }
    }

    [Fact]
    public void AdjacentCorruptionInvalidatesHostAndTaskValuesTogether()
    {
        var frames = new List<MonitoringHistoryFrame>
        {
            new(1, "fixture", Instance, At, [new("task", Identity, ObservationHealth.Healthy, 12, 100, 1000)], Host: new(12, 500, 1000, 100, 200, 300, 400)),
        };
        // Focused helper regression; full torn-file IO remains covered by Store tests.
        typeof(MonitoringHistoryStore).GetMethod("InvalidateAdjacentFrame", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [frames, 0]);
        Assert.Null(frames[0].Host);
        var task = Assert.Single(frames[0].Tasks); Assert.Equal(ObservationHealth.Unknown, task.Health);
        Assert.Null(task.CpuPercent); Assert.Null(task.WorkingSetBytes); Assert.Null(task.PhysicalMemoryBytes);
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => At.AddSeconds(20); }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("SystemAutorun-host-history-tests-").FullName;
        internal MonitoringHistoryStore Store { get; }
        private readonly string _rootId;
        internal Fixture()
        {
            var paths = new ProjectPaths(_root); _rootId = AgentProtocol.ForRoot(_root).RootId;
            Store = new(paths, clock: new Clock());
        }
        internal MonitoringHistoryFrame Frame(HostHistorySample? host) => new(1, _rootId, Instance, At,
            [new("task", Identity, ObservationHealth.Healthy, WorkingSetBytes: 100)], Host: host);
        public void Dispose()
        {
            Store.Dispose(); var resolved = Path.GetFullPath(_root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("SystemAutorun-host-history-tests-", StringComparison.Ordinal))
            { throw new InvalidOperationException("Refusing to delete a non-owned host history fixture."); }
            Directory.Delete(resolved, recursive: true);
        }
    }
}
