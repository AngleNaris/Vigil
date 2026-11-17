using System.Diagnostics;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.Core.Tests;

public sealed class MonitoringHistoryStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string Identity = new('a', 64);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task QueryValidationStillPrecedesGateAndCancellationAndMatchesProjection(int invalid)
    {
        using var fixture = new Fixture(); using var store = fixture.Open();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var query = invalid switch
        {
            0 => Query(30) with { TaskName = " " }, 1 => Query(30) with { TaskIdentity = " " },
            2 => Query(31), 3 => Query(30) with { To = Start },
            4 => Query(30) with { From = Start.AddDays(-30) },
            5 => Query(30) with { BucketCount = 0 }, _ => Query(30) with { BucketCount = 1001 },
        };
        var expected = Assert.IsAssignableFrom<ArgumentException>(Record.Exception(() =>
            MonitoringHistoryProjection.Project([], query, fixture.Clock.Now, "current")));
        var gate = (SemaphoreSlim)typeof(MonitoringHistoryStore).GetField("_gate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
        await gate.WaitAsync();
        try
        {
            var actual = Assert.IsAssignableFrom<ArgumentException>(await Record.ExceptionAsync(() => store.QueryAsync(query, "current", cancelled.Token)));
            Assert.Equal(expected.GetType(), actual.GetType()); Assert.Equal(expected.ParamName, actual.ParamName);
            Assert.Equal(expected.Message, actual.Message);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task AQueryAfterSameLengthAndTimestampRewriteStillReadsTheContent()
    {
        using var fixture = new Fixture(); using var store = fixture.Open(); var id = Guid.NewGuid().ToString("N");
        await store.AppendAsync(fixture.Frame(0, id)); await store.AppendAsync(fixture.Frame(10, id));
        Assert.Equal(20, (await store.QueryAsync(Query(30), id)).HealthySeconds);
        var path = Assert.Single(Directory.GetFiles(store.DirectoryPath, "history-v1-*.jsonl"));
        var original = await File.ReadAllTextAsync(path); var timestamp = File.GetLastWriteTimeUtc(path); var length = new FileInfo(path).Length;
        var changed = original.Replace("\"healthy\"", "\"unknown\"", StringComparison.Ordinal);
        Assert.NotEqual(original, changed);
        await File.WriteAllTextAsync(path, changed); File.SetLastWriteTimeUtc(path, timestamp);
        Assert.Equal(length, new FileInfo(path).Length); Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        var result = await store.QueryAsync(Query(30), id);
        Assert.Equal(0, result.HealthySeconds); Assert.Equal(30, result.UnknownSeconds); Assert.Null(result.AvailabilityPercent);
    }

    [Fact]
    public async Task PersistsAcrossRestartWithoutBridgingTheStoppedAgent()
    {
        using var fixture = new Fixture();
        var firstId = Guid.NewGuid().ToString("N");
        var secondId = Guid.NewGuid().ToString("N");
        using (var store = fixture.Open())
        {
            await store.AppendAsync(fixture.Frame(0, firstId));
            await store.AppendAsync(fixture.Frame(10, firstId, ObservationHealth.Fault));
            await store.AppendAsync(fixture.Frame(20, firstId));
            Assert.Throws<IOException>(() => fixture.Open());
        }
        fixture.Clock.Now = Start.AddSeconds(40);
        using (var restarted = fixture.Open())
        {
            await restarted.AppendAsync(fixture.Frame(30, secondId));
            var history = await restarted.QueryAsync(Query(40), secondId);
            Assert.Equal(20, history.HealthySeconds);
            Assert.Equal(10, history.FaultSeconds);
            Assert.Equal(10, history.UnknownSeconds);
            Assert.Equal(75, history.CoveragePercent);
            Assert.False(history.Truncated);
        }
    }

    [Fact]
    public async Task TornTailIsBoundedAndCannotExtendTheAdjacentHealthySample()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid().ToString("N");
        using var store = fixture.Open();
        await store.AppendAsync(fixture.Frame(0, id));
        await store.AppendAsync(fixture.Frame(10, id, ObservationHealth.Fault));
        await store.AppendAsync(fixture.Frame(20, id));
        var file = Assert.Single(Directory.GetFiles(store.DirectoryPath, "history-v1-*.jsonl"));
        await File.AppendAllTextAsync(file, "{\"torn\":");
        var history = await store.QueryAsync(Query(30), id);
        Assert.True(history.Truncated);
        Assert.Equal(10, history.HealthySeconds);
        Assert.Equal(10, history.FaultSeconds);
        Assert.Equal(10, history.UnknownSeconds);
        Assert.Contains("invalid", history.Warning!);
    }

    [Fact]
    public async Task EnforcesCapacityAndDoesNotDeleteUnownedFiles()
    {
        using var fixture = new Fixture();
        fixture.Clock.Now = Start.AddHours(3);
        var id = Guid.NewGuid().ToString("N");
        using var store = fixture.Open(new MonitoringHistoryLimits(2048, 1024, 1024, 2));
        var unrelated = Path.Combine(store.DirectoryPath, "not-a-history-file.txt");
        await File.WriteAllTextAsync(unrelated, "must survive");
        await store.AppendAsync(fixture.Frame(0, id));
        await store.AppendAsync(fixture.Frame(3600, id));
        await store.AppendAsync(fixture.Frame(7200, id));
        var files = Directory.GetFiles(store.DirectoryPath, "history-v1-*.jsonl");
        Assert.Equal(2, files.Length);
        Assert.True(files.Sum(file => new FileInfo(file).Length) <= 2048);
        Assert.Equal("must survive", await File.ReadAllTextAsync(unrelated));
        var history = await store.QueryAsync(Query(10800), id);
        Assert.True(history.Truncated);
        Assert.True(history.UnknownSeconds > 10700);
    }

    [Fact]
    public async Task PrunesThirtyDayOldChunksButLeavesOtherFilesAlone()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid().ToString("N");
        using var store = fixture.Open();
        await store.AppendAsync(fixture.Frame(0, id));
        var oldFile = Assert.Single(Directory.GetFiles(store.DirectoryPath, "history-v1-*.jsonl"));
        fixture.Clock.Now = Start.AddDays(31);
        await store.AppendAsync(fixture.Frame(31 * 86400, id));
        Assert.False(File.Exists(oldFile));
        Assert.Single(Directory.GetFiles(store.DirectoryPath, "history-v1-*.jsonl"));
    }

    [Fact]
    public async Task ClockRollbackWatermarkSurvivesRestartAndOverlappingTimeRemainsUnknown()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid().ToString("N");
        using (var store = fixture.Open())
        {
            await store.AppendAsync(fixture.Frame(0, id));
            await store.AppendAsync(fixture.Frame(10, id, ObservationHealth.Fault));
            fixture.Clock.Now = Start.AddSeconds(5);
            await store.AppendAsync(fixture.Frame(5, id, ObservationHealth.Unknown));
        }
        fixture.Clock.Now = Start.AddSeconds(15);
        using var restarted = fixture.Open();
        var history = await restarted.QueryAsync(Query(15), id);
        Assert.True(history.Truncated);
        Assert.Equal(15, history.UnknownSeconds);
        Assert.Null(history.AvailabilityPercent);
        Assert.Contains("Clock", history.Warning!);
        fixture.Clock.Now = Start.AddSeconds(20);
        await restarted.AppendAsync(fixture.Frame(15, id));
        var resumed = await restarted.QueryAsync(Query(20), id);
        Assert.Equal(5, resumed.HealthySeconds);
        Assert.Equal(15, resumed.UnknownSeconds);
    }

    [Fact]
    public async Task CorrectedFutureClockDoesNotKeepFreshHistoryUnknownOrPermanentlyTruncated()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid().ToString("N");
        fixture.Clock.Now = Start.AddDays(365);
        using var store = fixture.Open();
        await store.AppendAsync(fixture.Frame(365 * 86400, id));
        var futureFile = Assert.Single(Directory.GetFiles(store.DirectoryPath, "history-v1-*.jsonl"));
        fixture.Clock.Now = Start.AddSeconds(5);
        await store.AppendAsync(fixture.Frame(5, id));
        Assert.False(File.Exists(futureFile));
        fixture.Clock.Now = Start.AddSeconds(20);
        await store.AppendAsync(fixture.Frame(15, id));
        var history = await store.QueryAsync(new MonitoringHistoryQuery("task", Identity,
            Start.AddSeconds(10), Start.AddSeconds(20), 2), id);
        Assert.Equal(10, history.HealthySeconds);
        Assert.Equal(100, history.CoveragePercent);
        Assert.False(history.Truncated);
        Assert.Equal(Start.AddSeconds(10), history.RetainedFrom);
    }

    [Fact]
    public async Task WriterSurvivesAReadSizedContentionWindowInsteadOfDroppingItsSample()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var gate = (SemaphoreSlim)typeof(MonitoringHistoryStore).GetField("_gate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
        await gate.WaitAsync();
        Task append;
        try
        {
            append = store.AppendAsync(fixture.Frame(0, Guid.NewGuid().ToString("N")));
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            Assert.False(append.IsCompleted);
        }
        finally { gate.Release(); }
        await append.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Single(Directory.GetFiles(store.DirectoryPath, "history-v1-*.jsonl"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public async Task RejectsValidityOutsideTheOriginalTenSecondBudget(int validitySeconds)
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var frame = fixture.Frame(0, Guid.NewGuid().ToString("N"));
        frame = frame with { Tasks = [frame.Tasks[0] with { ValidUntil = Start.AddSeconds(validitySeconds) }] };
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(frame));
    }

    [Fact]
    public async Task NewerSampleThanTheQueryAsOfTimeIsNotMistakenForClockRollback()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid().ToString("N");
        fixture.Clock.Now = Start.AddSeconds(20);
        using var store = fixture.Open();
        await store.AppendAsync(fixture.Frame(0, id));
        await store.AppendAsync(fixture.Frame(20, id));
        fixture.Clock.OneReadOverride = Start.AddSeconds(10);
        var history = await store.QueryAsync(Query(10), id);
        Assert.Equal(10, history.HealthySeconds);
        Assert.Equal(0, history.UnknownSeconds);
        Assert.False(history.Truncated);
        Assert.Null(history.Warning);
    }

    [Fact]
    public async Task MovedRootStartsAFreshNamespaceWithoutDeletingTheOldArchive()
    {
        using var fixture = new Fixture();
        var before = Directory.CreateDirectory(Path.Combine(fixture.Root, "before")).FullName;
        var after = Path.Combine(fixture.Root, "after");
        var id = Guid.NewGuid().ToString("N");
        string oldRelative;
        using (var store = new MonitoringHistoryStore(new ProjectPaths(before), clock: fixture.Clock))
        {
            await store.AppendAsync(fixture.Frame(0, id) with { RootId = AgentProtocol.ForRoot(before).RootId });
            oldRelative = Path.GetRelativePath(before, store.DirectoryPath);
        }
        Assert.Equal(Path.GetFullPath(fixture.Root), Path.GetDirectoryName(Path.GetFullPath(before)));
        Assert.Equal(Path.GetFullPath(fixture.Root), Path.GetDirectoryName(Path.GetFullPath(after)));
        Directory.Move(before, after);
        using var moved = new MonitoringHistoryStore(new ProjectPaths(after), clock: fixture.Clock);
        Assert.NotEqual(Path.Combine(after, oldRelative), moved.DirectoryPath);
        Assert.Single(Directory.GetFiles(Path.Combine(after, oldRelative), "history-v1-*.jsonl"));
        Assert.Null((await moved.QueryAsync(Query(30), id)).AvailabilityPercent);
        await moved.AppendAsync(fixture.Frame(20, id) with { RootId = AgentProtocol.ForRoot(after).RootId });
        Assert.Equal(10, (await moved.QueryAsync(Query(30), id)).HealthySeconds);
    }

    [Fact]
    public async Task RejectsOversizedSamplesWithoutCreatingADataChunk()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid().ToString("N");
        using var store = fixture.Open(new MonitoringHistoryLimits(2048, 1024, 1024, 2));
        var large = fixture.Frame(0, id) with
        {
            Tasks = Enumerable.Range(0, 8).Select(i => new TaskHistorySample(new string('x', 500) + i,
                Identity, ObservationHealth.Healthy)).ToArray(),
        };
        await Assert.ThrowsAsync<IOException>(() => store.AppendAsync(large));
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "history-v1-*.jsonl"));
    }

    [Fact]
    public async Task ForeignRootRecordIsNotAcceptedAsLocalHistory()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid().ToString("N");
        using var store = fixture.Open();
        await store.AppendAsync(fixture.Frame(0, id));
        var file = Assert.Single(Directory.GetFiles(store.DirectoryPath, "history-v1-*.jsonl"));
        var foreign = fixture.Frame(10, id) with { RootId = new string('b', 64) };
        await File.AppendAllTextAsync(file, JsonSerializer.Serialize(foreign, AgentProtocol.JsonOptions) + "\n");
        var history = await store.QueryAsync(Query(30), id);
        Assert.True(history.Truncated);
        Assert.Null(history.AvailabilityPercent);
        Assert.Equal(0, history.CoveragePercent);
    }

    [Fact]
    public async Task RejectsJunctionStateDirectoryBeforeOpeningAWriter()
    {
        using var fixture = new Fixture();
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside")).FullName;
        var link = Path.Combine(fixture.Root, ".autorun-state");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, outside }) { start.ArgumentList.Add(argument); }
        using var command = Process.Start(start)!;
        try
        {
            await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, command.ExitCode);
            Assert.Throws<UnauthorizedAccessException>(() => fixture.Open());
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally
        {
            if (!command.HasExited) { command.Kill(entireProcessTree: true); await command.WaitForExitAsync(); }
            Assert.Equal(Path.GetFullPath(fixture.Root), Path.GetDirectoryName(Path.GetFullPath(link)));
            if (Directory.Exists(link)) { Directory.Delete(link); }
        }
    }

    private static MonitoringHistoryQuery Query(int seconds) => new("task", Identity, Start, Start.AddSeconds(seconds), 6);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start.AddSeconds(30);
        public DateTimeOffset? OneReadOverride { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            var value = OneReadOverride ?? Now;
            OneReadOverride = null;
            return value;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("SystemAutorun-monitor-history-tests-").FullName;
        public Clock Clock { get; } = new();
        public MonitoringHistoryStore Open(MonitoringHistoryLimits? limits = null) => new(new ProjectPaths(Root), limits, Clock);
        public MonitoringHistoryFrame Frame(int second, string instanceId, ObservationHealth health = ObservationHealth.Healthy) =>
            new(1, AgentProtocol.ForRoot(Root).RootId, instanceId, Start.AddSeconds(second),
                [new TaskHistorySample("task", Identity, health, 0, 100, 1000)]);
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("SystemAutorun-monitor-history-tests-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove a non-owned history fixture.");
            }
            Directory.Delete(resolved, recursive: true);
        }
    }
}
