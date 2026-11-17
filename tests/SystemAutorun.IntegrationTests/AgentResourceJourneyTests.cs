using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SystemAutorun.Agent;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.IntegrationTests;

public sealed class AgentResourceJourneyTests
{
    [Fact]
    public async Task ExplicitFiveMinuteRealResourcesUseAgentTimerPipeAndDurableStoreWithoutWorkloadControl()
    {
        var output = Environment.GetEnvironmentVariable("SA_AGENT_RESOURCE_EVIDENCE");
        if (string.IsNullOrEmpty(output)) { return; } // Explicit long measurement; normal regressions do not wait five minutes.
        Assert.True(Directory.Exists(output));
        using var source = new OwnProcessSource(); MonitoringHistoryStore? store = null;
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(monitorFactory: (workspace, instance) =>
        {
            store = new MonitoringHistoryStore(workspace.Paths);
            return new AgentMonitor(workspace, source, instance, store);
        });
        using var stopping = new CancellationTokenSource(); Task? sampler = null;
        try
        {
            var configuration = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
            foreach (var name in new[] { "own-process-a", "own-process-b" })
            {
                var mutation = await fixture.Client.SendAsync<WorkspaceMutation<ConfigurationMutationResult>>("task-add",
                    payload: new AddTaskRequest(name, AutorunTaskType.Windows, "Readonly own test-host PID alias; no process starts",
                        Executable: Environment.ProcessPath, WorkingDirectory: AppContext.BaseDirectory, StartupDelay: 0,
                        ExpectedRunning: false, StartOnAppLaunch: false, AutoRecover: false), expectedRevision: configuration.Revision);
                configuration = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
                Assert.Equal(mutation.Revision, configuration.Revision);
            }
            Assert.Equal(2, configuration.Configuration.Tasks.Count);
            Assert.All(configuration.Configuration.Tasks, task => { Assert.False(task.StartOnAppLaunch); Assert.False(task.AutoRecover); });
            var a = configuration.Configuration.Tasks[0]; var b = configuration.Configuration.Tasks[1];
            using var process = Process.GetCurrentProcess();
            var initialCpu = process.TotalProcessorTime; var initialBytes = GC.GetTotalAllocatedBytes();
            var initialCollections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            var began = DateTimeOffset.UtcNow; var elapsed = Stopwatch.StartNew();
            sampler = fixture.Monitor!.RunAsync(stopping.Token);
            await File.WriteAllTextAsync(Path.Combine(output, "ready.json"), JsonSerializer.Serialize(new
            { pid = Environment.ProcessId, began, root = fixture.Root, durationSeconds = 310, mode = "AgentMonitor real PeriodicTimer, actual owned pipe/store, own PID only; synthetic passive health, no GUI or production IPC" }));
            await Task.Delay(TimeSpan.FromSeconds(15));
            var firstClient = new DashboardMonitoringClient(fixture.Client);
            var firstA = await firstClient.ReadResourceHistoryAsync(Query(a));
            Assert.True(firstA.Points.Count >= 10); Assert.DoesNotContain(firstA.Points, point => point.MemoryBytes is <= 0);
            // B has never been queried; no consumer reads for the rest of this five-minute period.
            await Task.Delay(TimeSpan.FromSeconds(310) - elapsed.Elapsed);
            stopping.Cancel(); await sampler.WaitAsync(TimeSpan.FromSeconds(15)); sampler = null;
            Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(305));
            process.Refresh();
            var samplerMetrics = new { seconds = elapsed.Elapsed.TotalSeconds,
                cpuMs = (process.TotalProcessorTime - initialCpu).TotalMilliseconds,
                allocatedBytes = GC.GetTotalAllocatedBytes() - initialBytes,
                collections = Enumerable.Range(0, 3).Select(index => GC.CollectionCount(index) - initialCollections[index]).ToArray(),
                workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64,
                scope = "Whole owned testhost including actual AgentMonitor, pipe server, Windows reads and recording; not a production Agent process or recovery worker" };
            var now = DateTimeOffset.UtcNow;
            var reopenedClient = new DashboardMonitoringClient(new AgentClient(fixture.Root));
            var recentA = await reopenedClient.ReadResourceHistoryAsync(Query(a));
            var neverSelectedB = await reopenedClient.ReadResourceHistoryAsync(Query(b));
            var host = await reopenedClient.ReadResourceHistoryAsync(new(now.AddMinutes(-5), now));
            foreach (var result in new[] { recentA, neverSelectedB, host })
            { Assert.Equal(1, result.SourceIntervalSeconds); Assert.InRange(result.Points.Count, 280, 301); Assert.True(result.RetainedFrom >= now.AddMinutes(-5).AddSeconds(-2)); }
            Assert.Equal(recentA.Points.Select(point => (point.CpuPercent, point.MemoryBytes)), neverSelectedB.Points.Select(point => (point.CpuPercent, point.MemoryBytes)));
            Assert.All(recentA.Points, point => Assert.True(point.MemoryBytes is > 0));
            Assert.True(host.Points.Count(point => point.DiskReadBytesPerSecond is >= 0 && point.DiskWriteBytesPerSecond is >= 0) >= 270);
            var durable = await reopenedClient.ReadResourceHistoryAsync(Query(b) with { Source = ResourceHistorySource.Durable, BucketCount = 30 });
            Assert.Equal(10, durable.SourceIntervalSeconds); Assert.InRange(durable.Points.Sum(point => point.SampleCount), 25, 32);
            var snapshot = await fixture.Client.SendAsync<AgentMonitoringSnapshot>("monitoring-snapshot");
            Assert.True(snapshot.Persistence.Available, snapshot.Persistence.Error);
            Assert.InRange(source.HealthCalls, 31, 32); Assert.True(source.ProcessCaptures.Count >= 300);
            Assert.All(source.ProcessCaptures, capture =>
            {
                Assert.Equal(2, capture.Rows.Count); Assert.All(capture.Rows, row => Assert.Equal(Environment.ProcessId, Assert.Single(row.Processes).ProcessId));
                Assert.Equal(capture.Rows[0].CpuUsageAvailable, capture.Rows[1].CpuUsageAvailable);
                Assert.Equal(capture.Rows[0].CpuUsagePercent, capture.Rows[1].CpuUsagePercent); Assert.Equal(capture.Rows[0].WorkingSetBytes, capture.Rows[1].WorkingSetBytes);
            });
            Assert.All(source.ProcessCaptures[0].Rows, row => Assert.False(row.CpuUsageAvailable));
            Assert.Null(source.HostCaptures[0].Value.DiskReadBytesPerSecond); Assert.Null(source.HostCaptures[0].Value.NetworkReceiveBytesPerSecond);
            var gaps = source.ProcessCaptures.Zip(source.ProcessCaptures.Skip(1), (left, right) => (right.At - left.At).TotalMilliseconds).ToArray();
            Assert.InRange(gaps.Max(), 0, 3000);
            var queries = new List<object>();
            foreach (var days in new[] { 1, 7, 30 }) for (var repeat = 0; repeat < 5; repeat++)
            {
                var timer = Stopwatch.StartNew();
                var result = await reopenedClient.ReadResourceHistoryAsync(new(now.AddDays(-days), now, 1000, b.Name, MonitoringTaskIdentity.For(b), ResourceHistorySource.Durable));
                queries.Add(new { days, repeat, elapsedMs = timer.Elapsed.TotalMilliseconds, points = result.Points.Count, known = result.Points.Count(point => point.SampleCount > 0), result.BucketSeconds });
                Assert.InRange(result.Points.Count, 1, 1000); Assert.True(result.Points.Count(point => point.SampleCount > 0) < 5);
            }
            await File.WriteAllLinesAsync(Path.Combine(output, "durable-frames.ndjson"), Directory.GetFiles(store!.DirectoryPath, "history-v1-*.jsonl").Order().SelectMany(File.ReadLines));
            await File.WriteAllLinesAsync(Path.Combine(output, "process-captures.ndjson"), source.ProcessCaptures.Select(capture => JsonSerializer.Serialize(capture)));
            await File.WriteAllLinesAsync(Path.Combine(output, "host-captures.ndjson"), source.HostCaptures.Select(capture => JsonSerializer.Serialize(capture)));
            await File.WriteAllTextAsync(Path.Combine(output, "rpc-results.json"), JsonSerializer.Serialize(new { firstA, recentA, neverSelectedB, host, durable }, new JsonSerializerOptions { WriteIndented = true }));
            // Reopen the actual durable writer after draining the loop. This proves file survival,
            // not an OS Agent-process restart or a native GUI close/reopen journey.
            var beforeReopen = durable.Points.Sum(point => point.SampleCount);
            store!.Dispose(); store = null;
            using var reopenedStore = new MonitoringHistoryStore(fixture.Paths);
            var afterReopen = await reopenedStore.QueryResourcesAsync(Query(b) with { Source = ResourceHistorySource.Durable, BucketCount = 30 }, "reopened-writer", CancellationToken.None);
            Assert.Equal(beforeReopen, afterReopen.Points.Sum(point => point.SampleCount));
            await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
            {
                scope = "Real Agent timer + owned named-pipe RPC + durable files + Windows counters, two aliases of own test-host PID; synthetic passive health, no native GUI/production Agent lifecycle",
                elapsedSeconds = elapsed.Elapsed.TotalSeconds, processCaptures = source.ProcessCaptures.Count, hostCaptures = source.HostCaptures.Count,
                source.HealthCalls, source.HealthAttempts, samplerMetrics, gapMs = new { minimum = gaps.Min(), mean = gaps.Average(), maximum = gaps.Max() },
                processReadMs = source.ProcessCaptures.Select(capture => capture.ElapsedMs), hostReadMs = source.HostCaptures.Select(capture => capture.ElapsedMs),
                recentPoints = recentA.Points.Count, durableSamples = beforeReopen, afterWriterReopenSamples = afterReopen.Points.Sum(point => point.SampleCount),
                sparseWindowQueries = queries, matureVolumeMeasured = false, processLifecycleCalls = 0,
            }, new JsonSerializerOptions { WriteIndented = true }));

            ResourceHistoryQuery Query(AutorunTask task)
            { var at = DateTimeOffset.UtcNow; return new(at.AddMinutes(-5), at, TaskName: task.Name, TaskIdentity: MonitoringTaskIdentity.For(task)); }
        }
        finally
        {
            stopping.Cancel();
            if (sampler is not null) { await sampler.WaitAsync(TimeSpan.FromSeconds(15)); }
            store?.Dispose(); // Drain sampling and release writer before ServerFixture removes its owned root.
        }
    }

    private sealed record ProcessCapture(DateTimeOffset At, double ElapsedMs, IReadOnlyList<ManagedProcessSnapshot> Rows);
    private sealed record HostCapture(DateTimeOffset At, double ElapsedMs, HostHistorySample Value);
    private sealed class OwnProcessSource : IAgentMonitoringSource, IDisposable
    {
        private readonly ManagedProcessReader _processes = new(); private readonly HostStatusReader _host = new();
        internal readonly List<ProcessCapture> ProcessCaptures = []; internal readonly List<HostCapture> HostCaptures = [];
        internal int HealthCalls; internal readonly List<DateTimeOffset> HealthAttempts = [];
        public Task<IReadOnlyList<TaskSnapshot>> ObserveTasksAsync(AutorunConfiguration configuration, CancellationToken cancellationToken)
        { Interlocked.Increment(ref HealthCalls); HealthAttempts.Add(DateTimeOffset.UtcNow); return Task.FromResult<IReadOnlyList<TaskSnapshot>>(configuration.Tasks.Where(task => task.Enabled).Select(task => TaskSnapshot.From(task, HealthResult.Ok("fixture-only", "Synthetic passive health; resource values are real"))).ToArray()); }
        public async Task<IReadOnlyList<ManagedProcessSnapshot>> ObserveProcessesAsync(AutorunConfiguration configuration, IReadOnlyList<TaskSnapshot> health, CancellationToken cancellationToken)
        {
            var timer = Stopwatch.StartNew();
            var rows = await _processes.ReadAsync(configuration.Tasks.Where(task => task.Enabled).ToArray(), new LiveResourceSamplingTests.OwnPidOnly(), new LiveResourceSamplingTests.PassiveFixtureHealth(), cancellationToken);
            ProcessCaptures.Add(new(DateTimeOffset.UtcNow, timer.Elapsed.TotalMilliseconds, rows)); return rows;
        }
        public async Task<HostStatusSnapshot> ObserveHostAsync(CancellationToken cancellationToken)
        {
            var timer = Stopwatch.StartNew(); var value = await _host.ReadAsync(cancellationToken);
            HostCaptures.Add(new(value.CapturedAt, timer.Elapsed.TotalMilliseconds, new(value.CpuUsagePercent, Math.Max(0, value.TotalMemoryBytes - value.AvailableMemoryBytes), value.TotalMemoryBytes,
                value.DiskReadBytesPerSecond, value.DiskWriteBytesPerSecond, value.NetworkReceiveBytesPerSecond, value.NetworkSendBytesPerSecond)));
            return value;
        }
        public void Dispose() => _host.Dispose();
    }
}
