using System.IO;
using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Agent;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.IntegrationTests;

public sealed class AgentMonitorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SamplePersistsHealthAndCpuBaselineDoesNotBecomeZero()
    {
        using var fixture = new Fixture();
        await fixture.Monitor.SampleOnceAsync();
        var first = fixture.Monitor.GetSnapshot();
        Assert.True(first.Tasks.Available, first.Tasks.Error);
        Assert.True(first.Processes.Available);
        Assert.Equal(fixture.Source.ProcessStartedAt, Assert.Single(first.Runtime).ProcessStartedAt);
        Assert.Equal(Start, Assert.Single(first.Runtime).ConsecutiveHealthySince);
        fixture.Clock.Now = Start.AddSeconds(10);
        fixture.Source.CpuAvailable = true;
        await fixture.Monitor.SampleOnceAsync();
        fixture.Clock.Now = Start.AddSeconds(20);
        var history = await fixture.QueryAsync(20, 2);
        Assert.Equal(20, history.HealthySeconds);
        Assert.Null(history.Buckets[0].CpuPercent);
        Assert.Equal(0d, history.Buckets[1].CpuPercent);
        Assert.Equal(10d, history.Buckets[1].MemoryPercent);
        Assert.Equal(Start, Assert.Single(fixture.Monitor.GetSnapshot().Runtime).ConsecutiveHealthySince);
    }

    [Fact]
    public async Task UnknownHealthEndsContinuityAndRemainsUnknownInHistory()
    {
        using var fixture = new Fixture();
        await fixture.Monitor.SampleOnceAsync();
        fixture.Clock.Now = Start.AddSeconds(10);
        fixture.Source.Health = HealthResult.Unknown("fixture-unavailable", "No observation.");
        await fixture.Monitor.SampleOnceAsync();
        Assert.Null(Assert.Single(fixture.Monitor.GetSnapshot().Runtime).ConsecutiveHealthySince);
        fixture.Clock.Now = Start.AddSeconds(20);
        fixture.Source.Health = HealthResult.Ok("fixture", "Observed.");
        await fixture.Monitor.SampleOnceAsync();
        Assert.Equal(Start.AddSeconds(20), Assert.Single(fixture.Monitor.GetSnapshot().Runtime).ConsecutiveHealthySince);
        fixture.Clock.Now = Start.AddSeconds(30);
        var history = await fixture.QueryAsync(30);
        Assert.Equal(20, history.HealthySeconds);
        Assert.Equal(10, history.UnknownSeconds);
        Assert.Equal(0, history.FaultSeconds);
    }

    [Fact]
    public async Task ResourceFailureDoesNotBorrowCachedResourcesOrEraseHealthyObservations()
    {
        using var fixture = new Fixture();
        await fixture.Monitor.SampleOnceAsync();
        fixture.Clock.Now = Start.AddSeconds(10);
        fixture.Source.FailProcesses = true;
        await fixture.Monitor.SampleOnceAsync();
        var unavailable = fixture.Monitor.GetSnapshot();
        Assert.True(unavailable.Tasks.Available);
        Assert.False(unavailable.Processes.Available);
        Assert.NotNull(unavailable.Processes.Data); // diagnostic cache only
        Assert.Null(Assert.Single(unavailable.Runtime).ProcessStartedAt);
        Assert.Equal(Start, Assert.Single(unavailable.Runtime).ConsecutiveHealthySince);
        fixture.Clock.Now = Start.AddSeconds(20);
        var history = await fixture.QueryAsync(20, 2);
        Assert.Null(history.Buckets[1].CpuPercent);
        Assert.Null(history.Buckets[1].MemoryPercent);
        fixture.Source.FailProcesses = false;
        fixture.Source.ProcessStartedAt = Start.AddSeconds(19); // same PID, new process
        await fixture.Monitor.SampleOnceAsync();
        Assert.Equal(Start.AddSeconds(20), Assert.Single(fixture.Monitor.GetSnapshot().Runtime).ConsecutiveHealthySince);
    }

    [Fact]
    public async Task HostResourcesUseTheExistingTenSecondWriterAndNeverPersistFailedCachedData()
    {
        using var fixture = new Fixture(); fixture.Source.HostRatesAvailable = true;
        await fixture.Monitor.SampleOnceAsync();
        fixture.Clock.Now = Start.AddSeconds(1); await fixture.Monitor.SampleOnceAsync();
        var first = Assert.Single(fixture.ReadFrames());
        Assert.Equal(1, first.SchemaVersion);
        Assert.Equal(new HostHistorySample(0, 500, 1000, 100, 200, 300, 400), first.Host);
        Assert.Equal(Start, fixture.Monitor.GetSnapshot().Persistence.LastWrittenAt);
        fixture.Clock.Now = Start.AddSeconds(10); fixture.Source.FailHost = true;
        await fixture.Monitor.SampleOnceAsync();
        var frames = fixture.ReadFrames(); Assert.Equal(2, frames.Count);
        Assert.Null(frames[1].Host); Assert.True(fixture.Monitor.GetSnapshot().Tasks.Available);
        Assert.Equal(100, Assert.Single(frames[1].Tasks).WorkingSetBytes);
    }

    [Fact]
    public async Task HostFailureDoesNotUseOldMemoryDenominatorOrPoisonHealth()
    {
        using var fixture = new Fixture();
        await fixture.Monitor.SampleOnceAsync();
        fixture.Clock.Now = Start.AddSeconds(10);
        fixture.Source.FailHost = true;
        await fixture.Monitor.SampleOnceAsync();
        Assert.True(fixture.Monitor.GetSnapshot().Tasks.Available);
        Assert.False(fixture.Monitor.GetSnapshot().Host.Available);
        fixture.Clock.Now = Start.AddSeconds(20);
        var history = await fixture.QueryAsync(20, 2);
        Assert.Null(history.Buckets[1].MemoryPercent);
        Assert.Equal(20, history.HealthySeconds);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task WrongWorkloadIdentityOrTypeCannotSupplyMetricsOrProcessUptime(bool wrongIdentity, bool wrongType)
    {
        using var fixture = new Fixture();
        fixture.Source.WrongIdentity = wrongIdentity;
        fixture.Source.ReportedTypeOverride = wrongType ? "windows_service" : null;
        fixture.Source.CpuAvailable = true;
        await fixture.Monitor.SampleOnceAsync();
        Assert.Null(Assert.Single(fixture.Monitor.GetSnapshot().Runtime).ProcessStartedAt);
        fixture.Clock.Now = Start.AddSeconds(10);
        var history = await fixture.QueryAsync(10);
        Assert.Null(Assert.Single(history.Buckets).CpuPercent);
        Assert.Null(Assert.Single(history.Buckets).MemoryPercent);
    }

    [Fact]
    public async Task SharedWindowsServiceHostStartIsNotServiceUptime()
    {
        using var fixture = new Fixture(service: true);
        await fixture.Monitor.SampleOnceAsync();
        var runtime = Assert.Single(fixture.Monitor.GetSnapshot().Runtime);
        Assert.Null(runtime.ProcessStartedAt);
        Assert.NotNull(runtime.ProcessIdentity); // host identity is only a replacement witness
        Assert.Equal(Start, runtime.ConsecutiveHealthySince);
        fixture.Clock.Now = Start.AddSeconds(10);
        fixture.Source.ProcessStartedAt = Start.AddSeconds(9);
        await fixture.Monitor.SampleOnceAsync();
        var replacedHost = Assert.Single(fixture.Monitor.GetSnapshot().Runtime);
        Assert.Null(replacedHost.ProcessStartedAt);
        Assert.Equal(Start.AddSeconds(10), replacedHost.ConsecutiveHealthySince);
    }

    [Fact]
    public async Task StaleAndRegressedClockSnapshotsAreUnavailableWithoutFilesystemReads()
    {
        using var fixture = new Fixture();
        await fixture.Monitor.SampleOnceAsync();
        fixture.Clock.Now = Start.AddSeconds(31);
        var stale = fixture.Monitor.GetSnapshot();
        Assert.False(stale.Tasks.Available);
        Assert.False(stale.Processes.Available);
        Assert.False(stale.Host.Available);
        Assert.Empty(stale.Runtime);
        fixture.Clock.Now = Start.AddSeconds(-6);
        Assert.False(fixture.Monitor.GetSnapshot().Tasks.Available);
    }

    [Fact]
    public async Task ConfigurationInvalidationWinsOverAnInFlightOldSample()
    {
        using var fixture = new Fixture();
        fixture.Source.BlockHealth = true;
        var sampling = fixture.Monitor.SampleOnceAsync();
        try
        {
            await fixture.Source.HealthEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.Monitor.InvalidateConfiguration();
        }
        finally
        {
            fixture.Source.HealthRelease.TrySetResult();
            await sampling.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.False(fixture.Monitor.GetSnapshot().Tasks.Available);
        Assert.Equal("configuration-changed", fixture.Monitor.GetSnapshot().Tasks.Error);
        fixture.Source.BlockHealth = false;
        fixture.Clock.Now = Start.AddSeconds(10);
        await fixture.Monitor.SampleOnceAsync();
        Assert.True(fixture.Monitor.GetSnapshot().Tasks.Available);
    }

    [Fact]
    public async Task BrokenConfigurationLeavesHostReadableAndDoesNotPublishOldHealthAsCurrent()
    {
        using var fixture = new Fixture();
        await fixture.Monitor.SampleOnceAsync();
        await File.WriteAllTextAsync(fixture.Paths.ConfigPath, "version: 1\ntasks: [broken\n");
        fixture.Clock.Now = Start.AddSeconds(10);
        await fixture.Monitor.SampleOnceAsync();
        var snapshot = fixture.Monitor.GetSnapshot();
        Assert.False(snapshot.Tasks.Available);
        Assert.False(snapshot.Processes.Available);
        Assert.True(snapshot.Host.Available);
        Assert.Empty(snapshot.Runtime);
    }

    [Fact]
    public async Task ObservationTimeoutIsUnknownWhileIndependentHostRemainsAvailable()
    {
        using var fixture = new Fixture();
        fixture.Source.BlockHealth = true;
        using var monitor = new AgentMonitor(fixture.Workspace, fixture.Source, Guid.NewGuid().ToString("N"), null,
            clock: fixture.Clock, cycleTimeout: TimeSpan.FromMilliseconds(100));
        await monitor.SampleOnceAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var snapshot = monitor.GetSnapshot();
        Assert.False(snapshot.Tasks.Available);
        Assert.Equal("observation-timeout", snapshot.Tasks.Error);
        Assert.False(snapshot.Processes.Available);
        Assert.True(snapshot.Host.Available);
        Assert.Empty(snapshot.Runtime);
    }

    [Fact]
    public async Task PersistenceWriteFailureDoesNotPoisonLiveObservation()
    {
        using var fixture = new Fixture();
        fixture.History.Dispose();
        await fixture.Monitor.SampleOnceAsync();
        var snapshot = fixture.Monitor.GetSnapshot();
        Assert.True(snapshot.Tasks.Available);
        Assert.True(snapshot.Processes.Available);
        Assert.True(snapshot.Host.Available);
        Assert.False(snapshot.Persistence.Available);
        Assert.Null(snapshot.Persistence.LastWrittenAt);
        Assert.NotNull(Assert.Single(snapshot.Runtime).ConsecutiveHealthySince);
    }

    [Fact]
    public async Task SlowPublicationDoesNotExtendEarlierHealthBeyondItsSamplingBudget()
    {
        using var fixture = new Fixture();
        fixture.Source.AdvanceDuringProcesses = TimeSpan.FromSeconds(5);
        await fixture.Monitor.SampleOnceAsync();
        fixture.Clock.Now = Start.AddSeconds(20);
        var history = await fixture.QueryAsync(20);
        Assert.Equal(5, history.HealthySeconds); // publication at 5; validity ends at cycle start + 10
        Assert.Equal(15, history.UnknownSeconds);
    }

    [Fact]
    public async Task DurableCheckpointDoesNotStampOldResourcesAsANewObservation()
    {
        using var fixture = new Fixture();
        fixture.Source.SourceTimestampOffset = TimeSpan.FromSeconds(-4); fixture.Source.CpuAvailable = true;
        await fixture.Monitor.SampleOnceAsync();
        var frame = Assert.Single(fixture.ReadFrames()); var task = Assert.Single(frame.Tasks);
        Assert.Equal(ObservationHealth.Healthy, task.Health);
        Assert.Null(task.CpuPercent); Assert.Null(task.WorkingSetBytes); Assert.Null(task.PhysicalMemoryBytes);
        Assert.Null(task.ResourceIdentity); Assert.Null(frame.Host);
    }

    [Theory]
    [InlineData(-31)]
    [InlineData(1)]
    public async Task SourceTimestampsCannotBeLaunderedBySuccessfulCapture(int offset)
    {
        using var fixture = new Fixture();
        fixture.Source.SourceTimestampOffset = TimeSpan.FromSeconds(offset);
        await fixture.Monitor.SampleOnceAsync();
        var snapshot = fixture.Monitor.GetSnapshot();
        Assert.True(snapshot.Tasks.Available);
        Assert.False(snapshot.Host.Available);
        Assert.False(snapshot.Processes.Available);
        Assert.Null(Assert.Single(snapshot.Runtime).ProcessStartedAt);
    }

    [Fact]
    public async Task ResourceTicksRefreshEverySecondWithoutProbingPersistingOrAdvancingContinuity()
    {
        using var fixture = new Fixture();
        var published = 0;
        fixture.Monitor.SamplePublished += _ => published++;
        await fixture.Monitor.SampleOnceAsync();
        Assert.Equal(1, fixture.Source.HealthCalls);
        var first = fixture.Monitor.GetSnapshot();
        var firstWrite = first.Persistence.LastWrittenAt;
        fixture.Clock.Now = Start.AddSeconds(1);
        fixture.Source.CpuAvailable = true;
        await fixture.Monitor.SampleOnceAsync();
        var tick = fixture.Monitor.GetSnapshot();
        Assert.Equal(1, fixture.Source.HealthCalls);
        Assert.Equal(1, published);
        Assert.Equal(first.Tasks.ObservedAt, tick.Tasks.ObservedAt);
        Assert.True(tick.Tasks.Available);
        Assert.Equal(Start.AddSeconds(1), tick.Host.ObservedAt);
        Assert.True(Assert.Single(tick.Processes.Data!).Processes.Single().CpuUsageAvailable);
        Assert.Equal(firstWrite, tick.Persistence.LastWrittenAt);
        Assert.Equal(Start, Assert.Single(tick.Runtime).ConsecutiveHealthySince);
        fixture.Clock.Now = Start.AddSeconds(9);
        await fixture.Monitor.SampleOnceAsync();
        Assert.Equal(1, fixture.Source.HealthCalls);
        fixture.Clock.Now = Start.AddSeconds(10);
        await fixture.Monitor.SampleOnceAsync();
        Assert.Equal(2, fixture.Source.HealthCalls);
        Assert.Equal(2, published);
        Assert.Equal(Start.AddSeconds(10), fixture.Monitor.GetSnapshot().Persistence.LastWrittenAt);
    }

    [Fact]
    public async Task RecentResourcesContainNeverSelectedTasksAndHostAndUseCurrentProcessIdentity()
    {
        using var fixture = new Fixture(secondTask: true); fixture.Source.CpuAvailable = true;
        await fixture.Monitor.SampleOnceAsync();
        var runtimeIdentity = fixture.Monitor.GetSnapshot().Runtime.First().ProcessIdentity;
        fixture.Clock.Now = Start.AddSeconds(1); fixture.Source.ProcessStartedAt = Start;
        await fixture.Monitor.SampleOnceAsync();
        var second = (await fixture.Workspace.ReadAsync()).Configuration.Tasks.Single(task => task.Name == "second");
        var result = await fixture.Monitor.QueryResourcesAsync(new(Start.AddSeconds(-1), fixture.Clock.Now,
            TaskName: second.Name, TaskIdentity: MonitoringTaskIdentity.For(second)), default);
        Assert.Equal(2, result.Points.Count); Assert.All(result.Points, point => Assert.Equal(100, point.MemoryBytes));
        Assert.NotEqual(result.Points[0].ContinuityId, result.Points[1].ContinuityId);
        Assert.Equal(runtimeIdentity, fixture.Monitor.GetSnapshot().Runtime.First().ProcessIdentity);
        var host = await fixture.Monitor.QueryResourcesAsync(new(Start.AddSeconds(-1), fixture.Clock.Now), default);
        Assert.Equal(2, host.Points.Count); Assert.All(host.Points, point => Assert.Equal(500, point.MemoryBytes));
        Assert.Single(fixture.ReadFrames()); Assert.Equal(1, fixture.Source.HealthCalls);
    }

    [Fact]
    public async Task RecentResourcesDoNotDependOnPersistentHistoryAvailabilityOrRetainFailedHostData()
    {
        using var fixture = new Fixture();
        using var monitor = new AgentMonitor(fixture.Workspace, fixture.Source, Guid.NewGuid().ToString("N"), null,
            "fixture-history-disabled", clock: fixture.Clock);
        await monitor.SampleOnceAsync();
        fixture.Clock.Now = Start.AddSeconds(1); fixture.Source.FailHost = true;
        await monitor.SampleOnceAsync();
        var result = await monitor.QueryResourcesAsync(new(Start.AddSeconds(-1), fixture.Clock.Now), default);
        Assert.Equal(2, result.Points.Count); Assert.Equal(500, result.Points[0].MemoryBytes);
        Assert.Null(result.Points[1].MemoryBytes); Assert.False(monitor.GetSnapshot().Persistence.Available);
        await Assert.ThrowsAsync<AgentCommandException>(() => monitor.QueryResourcesAsync(
            new(Start.AddSeconds(-1), fixture.Clock.Now, Source: ResourceHistorySource.Durable), default));
    }

    [Fact]
    public async Task ConfigurationBoundaryRetainsPointsAndCutsOnlyTaskContinuity()
    {
        using var fixture = new Fixture(); await fixture.Monitor.SampleOnceAsync();
        fixture.Monitor.InvalidateConfiguration(); fixture.Clock.Now = Start.AddSeconds(1);
        await fixture.Monitor.SampleOnceAsync();
        var task = (await fixture.Workspace.ReadAsync()).Configuration.Tasks.Single();
        var result = await fixture.Monitor.QueryResourcesAsync(new(Start.AddSeconds(-1), fixture.Clock.Now,
            TaskName: task.Name, TaskIdentity: MonitoringTaskIdentity.For(task)), default);
        Assert.Equal(2, result.Points.Count); Assert.NotEqual(result.Points[0].ContinuityId, result.Points[1].ContinuityId);
        Assert.All(result.Points, point => Assert.Equal(100, point.MemoryBytes));
        var host = await fixture.Monitor.QueryResourcesAsync(new(Start.AddSeconds(-1), fixture.Clock.Now), default);
        Assert.Equal(host.Points[0].ContinuityId, host.Points[1].ContinuityId);
    }

    [Fact]
    public async Task ResourceTimerJitterDoesNotAccumulateIntoAnElevenSecondHealthCadence()
    {
        using var fixture = new Fixture();
        await fixture.Monitor.SampleOnceAsync();
        foreach (var seconds in new[] { 10.05, 20.01, 30.04, 40.005 })
        { fixture.Clock.Now = Start.AddSeconds(seconds); await fixture.Monitor.SampleOnceAsync(); }
        Assert.Equal(5, fixture.Source.HealthCalls);
        Assert.Equal(5, fixture.ReadFrames().Count);
    }

    [Fact]
    public async Task MissedHealthSlotsAreNotReplayedAndConfigurationResetsTheCadence()
    {
        using var fixture = new Fixture(); await fixture.Monitor.SampleOnceAsync();
        fixture.Clock.Now = Start.AddSeconds(35); await fixture.Monitor.SampleOnceAsync();
        await fixture.Monitor.SampleOnceAsync(); Assert.Equal(2, fixture.Source.HealthCalls);
        fixture.Clock.Now = Start.AddSeconds(36); fixture.Monitor.InvalidateConfiguration(); await fixture.Monitor.SampleOnceAsync();
        fixture.Clock.Now = Start.AddSeconds(45.99); await fixture.Monitor.SampleOnceAsync(); Assert.Equal(3, fixture.Source.HealthCalls);
        fixture.Clock.Now = Start.AddSeconds(46); await fixture.Monitor.SampleOnceAsync(); Assert.Equal(4, fixture.Source.HealthCalls);
        fixture.Clock.Now = Start.AddSeconds(45); await fixture.Monitor.SampleOnceAsync(); Assert.Equal(5, fixture.Source.HealthCalls);
    }

    [Fact]
    public async Task ConfigurationInvalidationForcesAHealthCycleOnTheNextResourceTick()
    {
        using var fixture = new Fixture();
        await fixture.Monitor.SampleOnceAsync();
        fixture.Monitor.InvalidateConfiguration();
        fixture.Clock.Now = Start.AddSeconds(1);
        await fixture.Monitor.SampleOnceAsync();
        Assert.Equal(2, fixture.Source.HealthCalls);
        Assert.True(fixture.Monitor.GetSnapshot().Tasks.Available);
    }

    [Fact]
    public async Task ResourceTickFailureDropsProcessUptimeButKeepsHealth()
    {
        using var fixture = new Fixture();
        await fixture.Monitor.SampleOnceAsync();
        fixture.Clock.Now = Start.AddSeconds(1);
        fixture.Source.FailProcesses = true;
        await fixture.Monitor.SampleOnceAsync();
        var snapshot = fixture.Monitor.GetSnapshot();
        Assert.True(snapshot.Tasks.Available);
        Assert.False(snapshot.Processes.Available);
        var runtime = Assert.Single(snapshot.Runtime);
        Assert.Null(runtime.ProcessStartedAt);
        Assert.Equal(Start, runtime.ConsecutiveHealthySince);
    }

    [Fact]
    public async Task LoopSamplesWithoutAClientAndCancellationDrainsIt()
    {
        using var fixture = new Fixture();
        fixture.Clock.Now = DateTimeOffset.UtcNow;
        using var monitor = new AgentMonitor(fixture.Workspace, fixture.Source, Guid.NewGuid().ToString("N"), null,
            "fixture-history-disabled", interval: TimeSpan.FromMilliseconds(30), healthInterval: TimeSpan.FromMilliseconds(300));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var loop = monitor.RunAsync(cancellation.Token);
        try { await fixture.Source.ThirdSample.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { cancellation.Cancel(); await loop.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.True(fixture.Source.HealthCalls >= 3);
        Assert.True(monitor.GetSnapshot().Tasks.Available);
        Assert.False(monitor.GetSnapshot().Persistence.Available);
        Assert.Equal("fixture-history-disabled", monitor.GetSnapshot().Persistence.Error);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("SystemAutorun-agent-monitor-tests-").FullName;
        public ProjectPaths Paths { get; }
        public ConfigurationWorkspace Workspace { get; }
        public Clock Clock { get; } = new();
        public Source Source { get; }
        public MonitoringHistoryStore History { get; }
        public AgentMonitor Monitor { get; }
        public Fixture(bool service = false, bool secondTask = false)
        {
            try
            {
                File.WriteAllText(Path.Combine(Root, "autorun.yaml"), "version: 1\nsettings:\n  default_startup_delay: 0\ntasks:\n  - name: fixture\n    type: "
                    + (service ? "windows_service\n    service: fixture-never-control" : "windows\n    exec: fixture-never-execute.exe")
                    + "\n    expected_running: true\n    health:\n"
                    + (service ? "      type: windows_service\n      service: fixture-never-control\n"
                        : "      type: process\n      pattern: fixture-never-execute\n"));
                if (secondTask) { File.AppendAllText(Path.Combine(Root, "autorun.yaml"),
                    "  - name: second\n    type: windows\n    exec: fixture-never-execute-second.exe\n    expected_running: true\n    health:\n      type: process\n      pattern: fixture-never-execute-second\n"); }
                Paths = ProjectPaths.Resolve(Root);
                Workspace = new ConfigurationWorkspace(Paths);
                Source = new Source(Clock);
                History = new MonitoringHistoryStore(Paths, clock: Clock);
                Monitor = new AgentMonitor(Workspace, Source, Guid.NewGuid().ToString("N"), History, clock: Clock);
            }
            catch
            {
                Monitor?.Dispose(); History?.Dispose(); CleanupRoot();
                throw;
            }
        }
        public async Task<MonitoringHistoryResult> QueryAsync(int seconds, int buckets = 1)
        {
            var task = (await Workspace.ReadAsync()).Configuration.Tasks.Single();
            return await Monitor.QueryHistoryAsync(new(task.Name, MonitoringTaskIdentity.For(task), Start, Start.AddSeconds(seconds), buckets), default);
        }
        public IReadOnlyList<MonitoringHistoryFrame> ReadFrames() => Directory.GetFiles(History.DirectoryPath, "history-v1-*.jsonl")
            .SelectMany(File.ReadLines).Select(line => JsonSerializer.Deserialize<MonitoringHistoryFrame>(line, AgentProtocol.JsonOptions)!)
            .OrderBy(frame => frame.ObservedAt).ToArray();
        public void Dispose()
        {
            Monitor.Dispose(); History.Dispose(); CleanupRoot();
        }
        private void CleanupRoot()
        {
            var resolved = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(resolved).StartsWith("SystemAutorun-agent-monitor-tests-", StringComparison.Ordinal))
            { throw new InvalidOperationException("Non-owned fixture."); }
            Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Source(Clock clock) : IAgentMonitoringSource
    {
        public HealthResult Health { get; set; } = HealthResult.Ok("fixture", "Observed.");
        public bool CpuAvailable { get; set; }
        public bool FailProcesses { get; set; }
        public bool FailHost { get; set; }
        public bool HostRatesAvailable { get; set; }
        public bool WrongIdentity { get; set; }
        public string? ReportedTypeOverride { get; set; }
        public bool BlockHealth { get; set; }
        public TimeSpan AdvanceDuringProcesses { get; set; }
        public TimeSpan SourceTimestampOffset { get; set; }
        public DateTimeOffset ProcessStartedAt { get; set; } = Start.AddHours(-1);
        public int HealthCalls;
        public TaskCompletionSource ThirdSample { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HealthEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HealthRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<TaskSnapshot>> ObserveTasksAsync(AutorunConfiguration configuration, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref HealthCalls) >= 3) { ThirdSample.TrySetResult(); }
            if (BlockHealth)
            {
                HealthEntered.TrySetResult();
                await HealthRelease.Task.WaitAsync(cancellationToken);
            }
            return configuration.Tasks.Select(task => TaskSnapshot.From(task, Health)).ToArray();
        }
        public Task<IReadOnlyList<ManagedProcessSnapshot>> ObserveProcessesAsync(AutorunConfiguration configuration,
            IReadOnlyList<TaskSnapshot> observations, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailProcesses) { throw new IOException("Fixture process source unavailable."); }
            clock.Now += AdvanceDuringProcesses;
            IReadOnlyList<ManagedProcessSnapshot> result = configuration.Tasks.Select(task => new ManagedProcessSnapshot(
                task.Name, task.Description, ReportedTypeOverride ?? task.Type.ToKey(), task.ExpectedRunning, task.Enabled, Health.Healthy,
                "fixture", Health.Reason, Health.Detail, clock.Now + SourceTimestampOffset,
                [new ManagedProcessInstanceSnapshot(123, "fixture", null, ProcessStartedAt, 0, 100, 100, 1, CpuAvailable)],
                Health.Observed, WrongIdentity ? new string('f', 64) : MonitoringTaskIdentity.For(task))).ToArray();
            return Task.FromResult(result);
        }
        public Task<HostStatusSnapshot> ObserveHostAsync(CancellationToken cancellationToken)
        {
            if (FailHost) { throw new IOException("Fixture host source unavailable."); }
            return Task.FromResult(new HostStatusSnapshot("fixture", "fixture", clock.Now + SourceTimestampOffset, TimeSpan.FromHours(1), 0, 1000, 500, [], [],
                DiskReadBytesPerSecond: HostRatesAvailable ? 100 : null, DiskWriteBytesPerSecond: HostRatesAvailable ? 200 : null,
                NetworkReceiveBytesPerSecond: HostRatesAvailable ? 300 : null, NetworkSendBytesPerSecond: HostRatesAvailable ? 400 : null));
        }
    }
}
