using System.IO;
using System.Reflection;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.Core.Tests;

public sealed class RemoteHostResourceHistoryTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-16T06:00:00Z");
    private const string Instance = "11111111111111111111111111111111";

    [Fact]
    public void RemoteHostIdentityIgnoresDisplayMetadataButIsolatesEndpointAndCredentials()
    {
        var baseDef = new RemoteHostDefinition
        {
            Id = "arm-1",
            Name = "Oracle ARM",
            Host = "10.18.245.127",
            Port = 22,
            UserName = "ubuntu",
            Platform = RemoteHostPlatform.Linux,
            Authentication = RemoteAuthenticationMode.SshAgent,
            KnownHostsFile = @"C:\keys\known_hosts",
            ConnectTimeoutSeconds = 10,
            CommandTimeoutSeconds = 20,
        };
        var baseId = MonitoringRemoteHostIdentity.For(baseDef);
        Assert.Equal(64, baseId.Length);
        Assert.True(baseId.All(char.IsAsciiHexDigitLower));

        var renamed = baseDef with { Name = "Renamed Server", ConnectTimeoutSeconds = 30, CommandTimeoutSeconds = 60 };
        Assert.Equal(baseId, MonitoringRemoteHostIdentity.For(renamed));

        Assert.NotEqual(baseId, MonitoringRemoteHostIdentity.For(baseDef with { Host = "10.18.245.128" }));
        Assert.NotEqual(baseId, MonitoringRemoteHostIdentity.For(baseDef with { Port = 2222 }));
        Assert.NotEqual(baseId, MonitoringRemoteHostIdentity.For(baseDef with { UserName = "root" }));
        Assert.NotEqual(baseId, MonitoringRemoteHostIdentity.For(baseDef with { Platform = RemoteHostPlatform.Windows }));
        Assert.NotEqual(baseId, MonitoringRemoteHostIdentity.For(baseDef with
        {
            Authentication = RemoteAuthenticationMode.PrivateKey,
            IdentityFile = @"C:\keys\id_ed25519",
        }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad id")]
    [InlineData("bad\tid")]
    public void QueryValidationRejectsInvalidRemoteHostIdsAndMixedTargets(string badId)
    {
        Assert.Throws<ArgumentException>(() => ResourceHistoryProjection.ValidateQuery(
            new ResourceHistoryQuery(At.AddSeconds(-10), At, RemoteHostId: badId), At));
        Assert.Throws<ArgumentException>(() => ResourceHistoryProjection.ValidateQuery(
            new ResourceHistoryQuery(At.AddSeconds(-10), At, 301, "task0", new string('a', 64), RemoteHostId: "arm-1"), At));
        Assert.Throws<ArgumentException>(() => ResourceHistoryProjection.ValidateQuery(
            new ResourceHistoryQuery(At.AddSeconds(-10), At, 301, TaskName: "task0", RemoteHostId: "arm-1"), At));
    }

    [Fact]
    public void ValidatorEnforcesMatchingRemoteHostIdForSchemaOneAndSchemaTwo()
    {
        var ring = new ResourceHistoryBuffer();
        ring.Append(new ResourceHistoryFrame(At, "agent:1", [], new(25, 4L << 30, 8L << 30, 100, 200, 300, 400), At));
        var query = new ResourceHistoryQuery(At.AddSeconds(-10), At, 601, RemoteHostId: "arm-1");
        var precise = ring.QueryPrecise(query, At, 500, Instance);
        Assert.Equal("arm-1", precise.RemoteHostId);
        ResourceHistoryResponseValidator.Validate(precise, query);
        Assert.Throws<InvalidDataException>(() => ResourceHistoryResponseValidator.Validate(precise with { RemoteHostId = "other" }, query));
        Assert.Throws<InvalidDataException>(() => ResourceHistoryResponseValidator.Validate(precise with { RemoteHostId = null }, query));

        var legacy = ring.QueryCompatible(query with { BucketCount = 301 }, At);
        Assert.Equal("arm-1", legacy.RemoteHostId);
        ResourceHistoryResponseValidator.Validate(legacy, query with { BucketCount = 301 });
        Assert.Throws<InvalidDataException>(() => ResourceHistoryResponseValidator.Validate(legacy with { RemoteHostId = "other" }, query with { BucketCount = 301 }));
    }

    [Fact]
    public async Task DurableStorePersistsProjectsIsolatesAndBreaksSessionsForRemoteHosts()
    {
        var root = Directory.CreateTempSubdirectory("SystemAutorun-remote-durable-tests-").FullName;
        var hostIdentity = MonitoringRemoteHostIdentity.For(new RemoteHostDefinition
        {
            Id = "arm-1",
            Name = "arm",
            Host = "10.0.0.2",
            UserName = "ubuntu",
            KnownHostsFile = @"C:\keys\known",
        });
        var newIdentity = new string('b', 64);
        var otherIdentity = new string('c', 64);
        var clock = new FixedClock(At.AddSeconds(40));
        try
        {
            var paths = new ProjectPaths(root);
            var rootId = AgentProtocol.ForRoot(root).RootId;
            using var store = new MonitoringHistoryStore(paths, clock: clock);

            await store.AppendAsync(new MonitoringHistoryFrame(1, rootId, Instance, At, [], RemoteHosts:
            [
                new RemoteHostHistorySample("arm-1", newIdentity, 1, new(99, 100, 1000, 1, 2, 3, 4)),
                new RemoteHostHistorySample("vps-2", otherIdentity, 1, new(55, 200, 2000)),
            ]));
            await store.AppendAsync(new MonitoringHistoryFrame(1, rootId, Instance, At.AddSeconds(10), [], RemoteHosts:
            [
                new RemoteHostHistorySample("arm-1", hostIdentity, 1, new(15, 300, 1000, 10, 20, 30, 40)),
            ]));
            // Absent in frame at +20s -> offline gap. Reconnected at +30s with sessionEpoch 2 -> BreakBefore.
            await store.AppendAsync(new MonitoringHistoryFrame(1, rootId, Instance, At.AddSeconds(20), []));
            await store.AppendAsync(new MonitoringHistoryFrame(1, rootId, Instance, At.AddSeconds(30), [], RemoteHosts:
            [
                new RemoteHostHistorySample("arm-1", hostIdentity, 2, new(45, 500, 1000, 50, 60, 70, 80)),
            ]));

            var query = new ResourceHistoryQuery(At, At.AddSeconds(40), 4, Source: ResourceHistorySource.Durable, RemoteHostId: "arm-1");
            var result = await store.QueryResourcesAsync(query, Instance, CancellationToken.None, hostIdentity);
            ResourceHistoryResponseValidator.Validate(result, query);

            Assert.Equal("arm-1", result.RemoteHostId);
            Assert.Equal(4, result.Points.Count);
            // +0s held old identity -> must not bleed into hostIdentity.
            Assert.Null(result.Points[0].CpuPercent);
            Assert.Equal(15, result.Points[1].CpuPercent);
            Assert.Equal(300, result.Points[1].MemoryBytes);
            Assert.Equal(10, result.Points[1].DiskReadBytesPerSecond);
            Assert.Equal(40, result.Points[1].NetworkSendBytesPerSecond);
            // +20s was offline.
            Assert.Null(result.Points[2].CpuPercent);
            // +30s came back with sessionEpoch 2 -> BreakBefore must be true.
            Assert.Equal(45, result.Points[3].CpuPercent);
            Assert.True(result.Points[3].BreakBefore);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StoreRejectsInvalidRemoteHostSamplesAndAdjacentCorruptionClearsThem()
    {
        var root = Directory.CreateTempSubdirectory("SystemAutorun-remote-durable-bad-").FullName;
        var clock = new FixedClock(At.AddSeconds(20));
        try
        {
            var paths = new ProjectPaths(root);
            var rootId = AgentProtocol.ForRoot(root).RootId;
            using var store = new MonitoringHistoryStore(paths, clock: clock);

            await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(
                new MonitoringHistoryFrame(1, rootId, Instance, At, [], RemoteHosts:
                [
                    new RemoteHostHistorySample("bad id", new string('a', 64), 1, new(10)),
                ])));
            await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(
                new MonitoringHistoryFrame(1, rootId, Instance, At, [], RemoteHosts:
                [
                    new RemoteHostHistorySample("arm-1", new string('a', 64), -1, new(10)),
                ])));
            await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(
                new MonitoringHistoryFrame(1, rootId, Instance, At, [], RemoteHosts:
                [
                    new RemoteHostHistorySample("arm-1", new string('a', 64), 1, new(CpuPercent: 101)),
                ])));
            Assert.Empty(Directory.GetFiles(store.DirectoryPath, "history-v1-*.jsonl"));

            var frames = new List<MonitoringHistoryFrame>
            {
                new(1, rootId, Instance, At, [], Host: new(10), RemoteHosts:
                [
                    new RemoteHostHistorySample("arm-1", new string('a', 64), 1, new(22, 100, 1000)),
                ]),
            };
            typeof(MonitoringHistoryStore).GetMethod("InvalidateAdjacentFrame", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [frames, 0]);
            Assert.Null(frames[0].Host);
            Assert.Null(frames[0].RemoteHosts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
