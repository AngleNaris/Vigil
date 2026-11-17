using System.IO;
using System.Security.Cryptography;
using SystemAutorun.Agent;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.IntegrationTests;

public sealed class ResourceHistoryRpcTests
{
    [Fact]
    public async Task ResourceRpcRoundTripsOverTheActualPipeWithoutAStoreProbeJournalWriteOrControl()
    {
        var clock = new Clock(); var source = new Source(clock);
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(monitorFactory: (workspace, instance) =>
            new AgentMonitor(workspace, source, instance, null, "fixture-no-store", clock: clock, interval: TimeSpan.FromSeconds(1)));
        await fixture.Monitor!.SampleOnceAsync();
        var before = Fingerprints(fixture.Root);
        var query = new ResourceHistoryQuery(clock.Now.AddSeconds(-1), clock.Now);
        var result = await fixture.Client.SendAsync<ResourceHistoryResult>("resource-history", payload: query);
        Assert.Equal(ResourceHistorySource.RecentSeconds, result.Source);
        Assert.Equal(500, Assert.Single(result.Points).MemoryBytes); Assert.Equal(1, result.SourceIntervalSeconds);
        Assert.False(AgentProtocol.IsMutation("resource-history")); Assert.Equal(1, source.HealthCalls);
        var preciseQuery = query with { BucketCount = 601 };
        var precise = await new DashboardMonitoringClient(fixture.Client).ReadRecentResourcesAsync(preciseQuery);
        Assert.Equal(1000, precise.SourceIntervalMilliseconds); Assert.Equal(500, Assert.Single(precise.Points).MemoryBytes);
        var snapshot = await fixture.Client.SendAsync<AgentMonitoringSnapshot>("monitoring-snapshot");
        Assert.Equal(1000, snapshot.ResourceSamplingIntervalMilliseconds); Assert.Equal(2, snapshot.RecentResourceSchemaVersion);
        Assert.Equal(snapshot.InstanceId, precise.AgentInstanceId);
        Assert.False(AgentProtocol.IsMutation("recent-resources-v2"));
        Assert.Equal(before, Fingerprints(fixture.Root));
        var unavailable = await Assert.ThrowsAsync<AgentCommandException>(() => fixture.Client.SendAsync<ResourceHistoryResult>(
            "resource-history", payload: query with { Source = ResourceHistorySource.Durable }));
        Assert.Equal("history_unavailable", unavailable.Code);
        await Assert.ThrowsAsync<AgentCommandException>(() => fixture.Client.SendAsync<ResourceHistoryResult>(
            "resource-history", payload: query with { BucketCount = 1001 }));
        Assert.Equal(before, Fingerprints(fixture.Root)); Assert.Equal(1, source.HealthCalls);
    }

    [Fact]
    public async Task HalfSecondAgentExposesPreciseAndLegacyRealViewsOverTheSamePipe()
    {
        var clock = new Clock(); var source = new Source(clock); var start = clock.Now;
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(monitorFactory: (workspace, instance) =>
            new AgentMonitor(workspace, source, instance, null, "fixture-no-store", clock: clock,
                interval: TimeSpan.FromMilliseconds(500), healthInterval: TimeSpan.FromSeconds(10)));
        for (var index = 0; index < 5; index++)
        { clock.Now = start.AddMilliseconds(index * 500); await fixture.Monitor!.SampleOnceAsync(); }
        var query = new ResourceHistoryQuery(start, clock.Now, 601);
        var precise = await new DashboardMonitoringClient(fixture.Client).ReadRecentResourcesAsync(query);
        Assert.Equal(500, precise.SourceIntervalMilliseconds); Assert.Equal(5, precise.Points.Count);
        Assert.Equal(start.AddMilliseconds(500), precise.Points[1].At); Assert.Equal(1, source.HealthCalls);
        var legacyQuery = query with { BucketCount = 301 };
        var legacy = await fixture.Client.SendAsync<ResourceHistoryResult>("resource-history", payload: legacyQuery);
        ResourceHistoryResponseValidator.Validate(legacy, legacyQuery);
        Assert.Equal(1, legacy.SourceIntervalSeconds); Assert.Equal(3, legacy.Points.Count);
        Assert.All(legacy.Points, point => Assert.Contains(precise.Points, actual => actual.At == point.At));
    }

    [Fact]
    public async Task DurableAndRecentRpcKeepTheirRealResolutionAcrossIndependentClients()
    {
        var clock = new Clock(); var source = new Source(clock); var start = clock.Now;
        MonitoringHistoryStore? history = null;
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(monitorFactory: (workspace, instance) =>
        {
            history = new MonitoringHistoryStore(workspace.Paths, clock: clock);
            return new AgentMonitor(workspace, source, instance, history, clock: clock);
        });
        try
        {
            await fixture.Monitor!.SampleOnceAsync(); clock.Now = start.AddSeconds(10);
            await fixture.Monitor.SampleOnceAsync(); clock.Now = start.AddSeconds(20);
            var dashboard = new DashboardMonitoringClient(fixture.Client);
            var durable = await dashboard.ReadResourceHistoryAsync(new(start, clock.Now, Source: ResourceHistorySource.Durable));
            Assert.Equal(10, durable.SourceIntervalSeconds); Assert.Equal(10, durable.BucketSeconds);
            Assert.Equal(2, durable.Points.Count); Assert.All(durable.Points, point => Assert.Equal(500, point.MemoryBytes));
            var anotherClient = new AgentClient(fixture.Root);
            var recent = await new DashboardMonitoringClient(anotherClient).ReadResourceHistoryAsync(new(start, clock.Now));
            Assert.Equal(1, recent.SourceIntervalSeconds); Assert.Equal(2, recent.Points.Count);
            Assert.Equal(start.AddSeconds(10), recent.Points[^1].At); Assert.Equal(2, source.HealthCalls);
        }
        finally
        {
            // All manual samples and pipe reads are awaited; release the owned writer
            // lease before the server fixture removes its root. No sampler loop exists.
            history?.Dispose();
        }
    }

    [Fact]
    public async Task OlderConfigurationOnlyServerExplicitlyReportsMissingSamplerInsteadOfFabricatingHistory()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var now = DateTimeOffset.UtcNow;
        var exception = await Assert.ThrowsAsync<AgentCommandException>(() => fixture.Client.SendAsync<ResourceHistoryResult>(
            "resource-history", payload: new ResourceHistoryQuery(now.AddSeconds(-1), now)));
        Assert.Equal("monitoring_unavailable", exception.Code);
    }

    private static string[] Fingerprints(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":"
            + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Source(Clock clock) : IAgentMonitoringSource
    {
        internal int HealthCalls { get; private set; }
        public Task<IReadOnlyList<TaskSnapshot>> ObserveTasksAsync(AutorunConfiguration configuration, CancellationToken cancellationToken)
        { HealthCalls++; return Task.FromResult<IReadOnlyList<TaskSnapshot>>([]); }
        public Task<IReadOnlyList<ManagedProcessSnapshot>> ObserveProcessesAsync(AutorunConfiguration configuration,
            IReadOnlyList<TaskSnapshot> health, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ManagedProcessSnapshot>>([]);
        public Task<HostStatusSnapshot> ObserveHostAsync(CancellationToken cancellationToken) => Task.FromResult(
            new HostStatusSnapshot("fixture", "fixture", clock.Now, TimeSpan.Zero, 10, 1000, 500, [], []));
    }
}
