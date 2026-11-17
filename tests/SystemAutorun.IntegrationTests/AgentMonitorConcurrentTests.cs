using System.IO;
using SystemAutorun.Agent;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.IntegrationTests;

public sealed class AgentMonitorConcurrentTests
{
    [Fact]
    public async Task SlowHealthDoesNotBlockHalfSecondRealResourcesAndShutdownWaitsForTheRealProbe()
    {
        var source = new ControlledSource(blockHealth: true);
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(monitorFactory: (workspace, instance) =>
            new AgentMonitor(workspace, source, instance, null, "isolated-no-store", interval: TimeSpan.FromMilliseconds(500),
                healthInterval: TimeSpan.FromSeconds(10), cycleTimeout: TimeSpan.FromSeconds(4)));
        using var stop = new CancellationTokenSource();
        var run = Task.Run(() => fixture.Monitor!.RunAsync(stop.Token));
        try
        {
            await source.HealthEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await source.ThirdHost.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, source.HealthCalls); Assert.True(source.HostCalls >= 3); Assert.True(source.ProcessCalls >= 1);
            RecentResourceHistoryResult recent;
            var waitUntil = DateTimeOffset.UtcNow.AddSeconds(2);
            do
            {
                var now = DateTimeOffset.UtcNow;
                recent = fixture.Monitor!.QueryRecentResources(new(now.AddSeconds(-5), now, 601), default);
                if (recent.Points.Count >= 3) { break; }
                await Task.Delay(20);
            } while (DateTimeOffset.UtcNow < waitUntil);
            Assert.Equal(500, recent.SourceIntervalMilliseconds); Assert.True(recent.Points.Count >= 3);
            Assert.Equal(recent.Points.Count, recent.Points.Select(point => point.At).Distinct().Count());
            stop.Cancel(); await Task.Delay(80);
            Assert.False(run.IsCompleted); // The fixture deliberately ignores cancellation until released.
        }
        finally
        {
            stop.Cancel(); source.HealthRelease.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(8));
        }
    }

    [Fact]
    public async Task InvalidationRevokesAQueuedHealthCycleEvenIfTheRevisionIsUnchanged()
    {
        var source = new ControlledSource(blockHealth: false);
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(monitorFactory: (workspace, instance) =>
            new AgentMonitor(workspace, source, instance, null, "isolated-no-store", interval: TimeSpan.FromMilliseconds(500),
                healthInterval: TimeSpan.FromSeconds(10)));
        var published = new TaskCompletionSource<AgentMonitoringSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Monitor!.SamplePublished += cycle => published.TrySetResult(cycle);
        using var stop = new CancellationTokenSource();
        var run = Task.Run(() => fixture.Monitor.RunAsync(stop.Token));
        try
        {
            var cycle = await published.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(fixture.Monitor.RecoveryEvidenceCurrent(cycle));
            fixture.Monitor.InvalidateConfiguration();
            Assert.False(fixture.Monitor.RecoveryEvidenceCurrent(cycle));
        }
        finally { stop.Cancel(); source.HealthRelease.TrySetResult(); await run.WaitAsync(TimeSpan.FromSeconds(8)); }
    }

    [Fact]
    public async Task RecoveryEvidenceCannotRegainItsLeaseWhenWallTimeMovesBack()
    {
        var clock = new DualClock(); var source = new ControlledSource(blockHealth: false) { BlockHostAfter = 2 };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(monitorFactory: (workspace, instance) =>
            new AgentMonitor(workspace, source, instance, null, "isolated-no-store", clock: clock,
                interval: TimeSpan.FromMilliseconds(50), healthInterval: TimeSpan.FromSeconds(10)));
        var published = new TaskCompletionSource<AgentMonitoringSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Monitor!.SamplePublished += cycle => published.TrySetResult(cycle);
        using var stop = new CancellationTokenSource(); var run = Task.Run(() => fixture.Monitor.RunAsync(stop.Token));
        try
        {
            var cycle = await published.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await source.HostBlocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(fixture.Monitor.RecoveryEvidenceCurrent(cycle));
            clock.ElapsedTicks = TimeSpan.FromSeconds(11).Ticks; clock.Now = clock.Start.AddSeconds(5);
            Assert.False(fixture.Monitor.RecoveryEvidenceCurrent(cycle)); // Wall time says five seconds, monotonic time says eleven.
        }
        finally
        {
            stop.Cancel(); source.HealthRelease.TrySetResult(); source.HostRelease.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(8));
        }
    }

    [Fact]
    public async Task InvalidationStopsOldProcessSamplingWhileAHistoryWriteIsBlocked()
    {
        var source = new ControlledSource(blockHealth: false);
        var appendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appendRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MonitoringHistoryStore? history = null;
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(monitorFactory: (workspace, instance) =>
        {
            history = new MonitoringHistoryStore(workspace.Paths);
            return new AgentMonitor(workspace, source, instance, history, interval: TimeSpan.FromMilliseconds(100),
                healthInterval: TimeSpan.FromSeconds(10), appendHistory: async (frame, token) =>
                { appendEntered.TrySetResult(); await appendRelease.Task; await history.AppendAsync(frame, token); });
        });
        using var stop = new CancellationTokenSource(); var run = Task.Run(() => fixture.Monitor!.RunAsync(stop.Token));
        try
        {
            await appendEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await source.ProcessEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.Monitor!.InvalidateConfiguration();
            await Task.Delay(150); // Permit a capture already inside the source to settle.
            var processes = source.ProcessCalls; var hosts = source.HostCalls;
            await Task.Delay(350);
            Assert.Equal(processes, source.ProcessCalls); Assert.True(source.HostCalls > hosts);
            Assert.False(fixture.Monitor.GetSnapshot().Processes.Available);
        }
        finally
        {
            stop.Cancel(); appendRelease.TrySetResult(); source.HealthRelease.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(8)); history?.Dispose();
        }
    }

    [Fact]
    public async Task ABlockedHistoryWriteCannotRefreshOldHealthThroughNewProcessTimestamps()
    {
        var source = new ControlledSource(blockHealth: false);
        var appendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appendRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MonitoringHistoryStore? history = null;
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(monitorFactory: (workspace, instance) =>
        {
            history = new MonitoringHistoryStore(workspace.Paths);
            return new AgentMonitor(workspace, source, instance, history, interval: TimeSpan.FromMilliseconds(50),
                healthInterval: TimeSpan.FromMilliseconds(300), appendHistory: async (frame, token) =>
                { appendEntered.TrySetResult(); await appendRelease.Task; await history.AppendAsync(frame, token); });
        });
        await File.WriteAllTextAsync(fixture.Paths.ConfigPath, "version: 1\nsettings:\n  default_startup_delay: 0\ntasks:\n  - name: fixture\n    type: windows\n    exec: fixture-never-execute.exe\n    expected_running: false\n    health:\n      type: process\n      pattern: fixture-never-execute\n");
        using var stop = new CancellationTokenSource(); var run = Task.Run(() => fixture.Monitor!.RunAsync(stop.Token));
        try
        {
            await appendEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await source.FreshHealthSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await source.ExpiredHealthSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, source.HealthCalls); Assert.True(source.ProcessCalls >= 2);
        }
        finally
        {
            stop.Cancel(); appendRelease.TrySetResult(); source.HealthRelease.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(8)); history?.Dispose();
        }
    }

    [Fact]
    public async Task InvalidationDuringAWriteCannotClearTheNewHistoryBoundary()
    {
        var source = new ControlledSource(blockHealth: false);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundaryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundaryRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MonitoringHistoryStore? history = null; var writes = 0;
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(monitorFactory: (workspace, instance) =>
        {
            history = new MonitoringHistoryStore(workspace.Paths);
            return new AgentMonitor(workspace, source, instance, history, interval: TimeSpan.FromMilliseconds(500),
                healthInterval: TimeSpan.FromSeconds(10), appendHistory: async (frame, token) =>
                {
                    var index = Interlocked.Increment(ref writes);
                    if (index == 1) { firstEntered.TrySetResult(); await firstRelease.Task; }
                    if (index == 2) { boundaryEntered.TrySetResult(); await boundaryRelease.Task; }
                    await history.AppendAsync(frame, token);
                });
        });
        using var stop = new CancellationTokenSource(); var run = Task.Run(() => fixture.Monitor!.RunAsync(stop.Token));
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.Monitor!.InvalidateConfiguration(); firstRelease.TrySetResult();
            await boundaryEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.False(fixture.Monitor.GetSnapshot().Persistence.Available);
            var now = DateTimeOffset.UtcNow;
            var query = new MonitoringHistoryQuery("fixture", new string('a', 64), now.AddMinutes(-1), now);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Monitor.QueryHistoryAsync(query, timeout.Token));
            boundaryRelease.TrySetResult();
            var result = await fixture.Monitor.QueryHistoryAsync(query, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, result.HealthySeconds);
        }
        finally
        {
            stop.Cancel(); firstRelease.TrySetResult(); boundaryRelease.TrySetResult(); source.HealthRelease.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(8)); history?.Dispose();
        }
    }

    [Fact]
    public async Task SlowHistoryWriteDoesNotBlockResourcesOrServeAnUncommittedHealthyTail()
    {
        var source = new ControlledSource(blockHealth: false);
        var appendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appendRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MonitoringHistoryStore? history = null;
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(monitorFactory: (workspace, instance) =>
        {
            history = new MonitoringHistoryStore(workspace.Paths);
            return new AgentMonitor(workspace, source, instance, history, interval: TimeSpan.FromMilliseconds(500),
                healthInterval: TimeSpan.FromSeconds(10), cycleTimeout: TimeSpan.FromSeconds(4), appendHistory: async (frame, token) =>
                {
                    appendEntered.TrySetResult(); await appendRelease.Task; // The underlying write deliberately ignores cancellation until released.
                    await history.AppendAsync(frame, token);
                });
        });
        fixture.Monitor!.SamplePublished += _ => source.HealthPublished.TrySetResult();
        using var stop = new CancellationTokenSource();
        var run = Task.Run(() => fixture.Monitor!.RunAsync(stop.Token));
        try
        {
            await appendEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await source.ThirdHost.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var now = DateTimeOffset.UtcNow;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var query = new MonitoringHistoryQuery("fixture", new string('a', 64), now.AddMinutes(-1), now);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Monitor!.QueryHistoryAsync(query, timeout.Token));
            appendRelease.TrySetResult();
            await source.HealthPublished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var result = await fixture.Monitor!.QueryHistoryAsync(query, CancellationToken.None);
            Assert.Equal(0, result.HealthySeconds); // The fixture has no real task rows; absence is unknown.
        }
        finally
        {
            stop.Cancel(); source.HealthRelease.TrySetResult(); appendRelease.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(8)); history?.Dispose();
        }
    }

    private sealed class DualClock : TimeProvider
    {
        public DateTimeOffset Start { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset Now { get; set; }
        public long ElapsedTicks { get; set; }
        public DualClock() { Now = Start; }
        public override DateTimeOffset GetUtcNow() => Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ElapsedTicks;
    }

    private sealed class ControlledSource(bool blockHealth) : IAgentMonitoringSource
    {
        private int _healthCalls, _hostCalls, _processCalls;
        public int BlockHostAfter { get; init; }
        public TaskCompletionSource HostBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HostRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int HealthCalls => Volatile.Read(ref _healthCalls);
        public int HostCalls => Volatile.Read(ref _hostCalls);
        public int ProcessCalls => Volatile.Read(ref _processCalls);
        public TaskCompletionSource HealthEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HealthRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThirdHost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ProcessEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FreshHealthSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExpiredHealthSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HealthPublished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<TaskSnapshot>> ObserveTasksAsync(AutorunConfiguration configuration, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _healthCalls); HealthEntered.TrySetResult();
            if (blockHealth) { await HealthRelease.Task; }
            return configuration.Tasks.Select(task => TaskSnapshot.From(task, HealthResult.Ok("fixture", "controlled observation"))).ToArray();
        }
        public Task<IReadOnlyList<ManagedProcessSnapshot>> ObserveProcessesAsync(AutorunConfiguration configuration,
            IReadOnlyList<TaskSnapshot> observations, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _processCalls); ProcessEntered.TrySetResult();
            if (observations.Count > 0) { FreshHealthSeen.TrySetResult(); }
            else if (FreshHealthSeen.Task.IsCompleted) { ExpiredHealthSeen.TrySetResult(); }
            return Task.FromResult<IReadOnlyList<ManagedProcessSnapshot>>([]);
        }
        public async Task<HostStatusSnapshot> ObserveHostAsync(CancellationToken cancellationToken)
        {
            var calls = Interlocked.Increment(ref _hostCalls);
            if (calls >= 3) { ThirdHost.TrySetResult(); }
            if (BlockHostAfter > 0 && calls >= BlockHostAfter) { HostBlocked.TrySetResult(); await HostRelease.Task; }
            return new HostStatusSnapshot("fixture", "fixture", DateTimeOffset.UtcNow, TimeSpan.Zero, 10, 1000, 500, [], []);
        }
    }
}
