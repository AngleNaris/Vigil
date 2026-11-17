using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Agent;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.IntegrationTests;

// Control-plane schedules/results below are simulated. Real native/ledger evidence lives
// in StartupNativeInspectionDataTests; a completed fake Task is not OS execution evidence.
public sealed class StartupNativeInspectionControlPlaneTests
{
    internal const string BackupId = "11111111222233334444555555555555";
    internal static StartupEntry Entry => new("backup:" + BackupId, "Startup", "owned", null, @"C:\never-run.exe", null, null,
        @"C:\owned-source\owned.lnk", null, true, BackupId, DateTimeOffset.UnixEpoch, false, false, null);
    internal static StartupManagementSnapshot Inventory(StartupEntry? entry = null) => new([entry ?? Entry], 0, 0, 1, false) { ReadableSources = 1 };
    internal static StartupSlotEvidenceRead Evidence()
    {
        var now = DateTimeOffset.UtcNow; var parent = new StartupNativeFileIdentity(new string('1', 16), new string('2', 32)); var digest = new string('a', 64);
        return new(new(1, BackupId, "Startup", "owned", digest,
            new("missing", null, 2, @"C:\owned-backups\startup", "insensitive", parent, null, null, null, now),
            new("missing", null, 2, @"C:\owned-source", "insensitive", parent with { FileId = new string('3', 32) }, null, null, null, now),
            false, false, false), digest);
    }

    [Fact]
    public async Task SimulatedRealPipeKeepsOldMetadataSchemaAndNativeReadsExplicitIndependentAndUntrusted()
    {
        var metadata = new MetadataReader(); var slots = new SlotReader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        var client = new AgentStartupClient(fixture.Client); var revision = await Revision(fixture);
        var files = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order().ToArray(); var yaml = await File.ReadAllBytesAsync(fixture.Paths.ConfigPath);
        var old = await client.InspectAsync(Entry.Id, StartupInspectionRequest.FromEntry(Entry), revision);
        Assert.Equal(1, old.SchemaVersion); Assert.Equal("not_checked", old.OriginalBackupState); Assert.Equal(0, slots.Calls);
        for (var pass = 0; pass < 2; pass++)
        {
            var result = await client.InspectNativeAsync(Entry.Id, StartupInspectionRequest.FromEntry(Entry), revision);
            Assert.Equal(2, result.SchemaVersion); Assert.Equal(Entry, result.Metadata.Entry); Assert.Equal("sequential-independent", result.ValidationScope);
            Assert.Equal("unverified", result.HistoricalIdentity); Assert.Equal("missing", result.Slots.Archive.State); Assert.Equal("missing", result.Slots.Original.State);
            Assert.False(result.NativeWriteReady); Assert.False(result.NativeWritePerformed); Assert.False(result.ExecutionRequested);
            Assert.All(result.Metadata.Actions.Skip(1), action => Assert.NotEqual("ready", action.State));
        }
        Assert.Equal(2, slots.Calls); Assert.Equal(5, metadata.Calls); Assert.Equal(yaml, await File.ReadAllBytesAsync(fixture.Paths.ConfigPath));
        Assert.Equal(files, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    [Theory]
    [InlineData("active")] [InlineData("registry")] [InlineData("revision")]
    public async Task SimulatedInvalidNativeSelectionCannotReachEitherWorker(string defect)
    {
        var metadata = new MetadataReader(); var slots = new SlotReader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        var request = StartupInspectionRequest.FromEntry(defect == "active" ? Entry with { IsDisabled = false } : defect == "registry" ? Entry with { Source = "HKCU" } : Entry);
        if (defect == "revision")
        { await Assert.ThrowsAsync<ArgumentException>(() => fixture.Client.SendResponseAsync("startup-inspect-native", Entry.Id, request, expectedRevision: "bad")); }
        else
        {
            var reply = await fixture.Client.SendResponseAsync("startup-inspect-native", Entry.Id, request, expectedRevision: await Revision(fixture));
            Assert.False(reply.Ok); Assert.Equal("invalid_argument", reply.ErrorCode); Assert.Null(reply.Data);
        }
        Assert.Equal(0, metadata.Calls); Assert.Equal(0, slots.Calls);
    }

    [Theory]
    [InlineData("path")] [InlineData("identity")] [InlineData("nativeWriteReady")] [InlineData("missing-state")] [InlineData("mutation-id")]
    public async Task SimulatedNativeRpcRejectsInjectedPathsIdentityAuthorityAndMutationIdsBeforeScanning(string defect)
    {
        var metadata = new MetadataReader(); var slots = new SlotReader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        var request = JsonSerializer.SerializeToNode(StartupInspectionRequest.FromEntry(Entry), AgentProtocol.JsonOptions)!;
        if (defect == "missing-state") { request.AsObject().Remove("isDisabled"); }
        else if (defect != "mutation-id") { request[defect] = @"\\offline.invalid\never-open"; }
        var revision = await Revision(fixture);
        if (defect == "mutation-id")
        { await Assert.ThrowsAsync<ArgumentException>(() => fixture.Client.SendResponseAsync("startup-inspect-native", Entry.Id, request,
            expectedRevision: revision, operationId: AgentProtocol.NewOperationId())); }
        else
        {
            var reply = await fixture.Client.SendResponseAsync("startup-inspect-native", Entry.Id, request, expectedRevision: revision);
            Assert.Equal("invalid_argument", reply.ErrorCode); Assert.Null(reply.Data);
        }
        Assert.Equal(0, metadata.Calls); Assert.Equal(0, slots.Calls);
    }

    [Theory]
    [InlineData("name")] [InlineData("target")] [InlineData("source")] [InlineData("state")] [InlineData("missing")]
    public async Task SimulatedPostNativeSelectionDriftCannotPublishEarlierEvidence(string defect)
    {
        var metadata = new MetadataReader { Factory = call => call == 1 ? Inventory() : defect == "missing" ? new([], 0, 0, 0, false) { ReadableSources = 1 }
            : Inventory(defect switch { "name" => Entry with { Name = "changed" }, "target" => Entry with { Target = @"C:\changed.exe" },
                "source" => Entry with { Source = "Common Startup" }, _ => Entry with { IsDisabled = false } }) };
        var slots = new SlotReader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        var reply = await fixture.Client.SendResponseAsync("startup-inspect-native", Entry.Id, StartupInspectionRequest.FromEntry(Entry), expectedRevision: await Revision(fixture));
        Assert.False(reply.Ok); Assert.Null(reply.Data); Assert.Equal("startup_entry_changed", reply.ErrorCode); Assert.Equal(1, slots.Calls);
    }

    [Fact]
    public async Task SimulatedPostRecordDigestMismatchIsFailureNotPartialOrMissingSuccess()
    {
        var slots = new SlotReader { Factory = () => Task.FromResult(Evidence() with { RecordMetadataDigestAfter = new string('b', 64) }) };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: new MetadataReader(), startupSlotReader: slots);
        var reply = await fixture.Client.SendResponseAsync("startup-inspect-native", Entry.Id, StartupInspectionRequest.FromEntry(Entry), expectedRevision: await Revision(fixture));
        Assert.Equal("startup_backup_changed", reply.ErrorCode); Assert.Null(reply.Data); Assert.Null(reply.OperationId);
    }

    [Fact]
    public async Task SimulatedConfigurationChangeDuringNativeReadCannotPublishAnEarlierScope()
    {
        var slots = new SlotReader(); await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: new MetadataReader(), startupSlotReader: slots);
        var revision = await Revision(fixture);
        slots.Factory = async () => { _ = await fixture.Client.SendResponseAsync("settings-patch", payload: new { autoStartMissingOnLaunch = true }, expectedRevision: revision); return Evidence(); };
        var reply = await fixture.Client.SendResponseAsync("startup-inspect-native", Entry.Id, StartupInspectionRequest.FromEntry(Entry), expectedRevision: revision);
        Assert.Equal("revision_conflict", reply.ErrorCode); Assert.Null(reply.Data); Assert.NotEqual(revision, reply.Revision);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task SimulatedIncompleteBeforeOrAfterMetadataCannotPublishEvidence(int stage)
    {
        var metadata = new MetadataReader { Factory = call => Inventory() with { Truncated = call == stage } }; var slots = new SlotReader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        var reply = await fixture.Client.SendResponseAsync("startup-inspect-native", Entry.Id, StartupInspectionRequest.FromEntry(Entry), expectedRevision: await Revision(fixture));
        Assert.Equal("startup_inventory_incomplete", reply.ErrorCode); Assert.Null(reply.Data); Assert.Equal(stage - 1, slots.Calls);
    }

    [Fact]
    public async Task SimulatedNativeTimeoutRetainsAdmissionRejectsFanoutAndNeverCachesLateSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StartupSlotEvidenceRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new MetadataReader(); var slots = new SlotReader { Factory = () => { entered.TrySetResult(); return release.Task; } };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        var revision = await Revision(fixture); var client = new AgentStartupClient(fixture.Client);
        var pending = fixture.Client.SendResponseAsync("startup-inspect-native", Entry.Id, StartupInspectionRequest.FromEntry(Entry), expectedRevision: revision);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var fanout = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => fixture.Client.SendResponseAsync("startup-inspect-native", Entry.Id,
                StartupInspectionRequest.FromEntry(Entry), expectedRevision: revision)));
            Assert.All(fanout, reply => { Assert.Equal("startup_scan_busy", reply.ErrorCode); Assert.Null(reply.Data); });
            Assert.Equal(5, fanout.Select(reply => reply.Id).Distinct().Count());
            var expired = await pending.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal("startup_native_observation_timeout", expired.ErrorCode); Assert.Null(expired.Data);
            Assert.Equal("startup_scan_busy", (await client.ReadAsync(new())).Reason); Assert.Equal(1, slots.Calls); Assert.Equal(1, metadata.Calls);
            Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2))); Assert.True((await fixture.Client.SendResponseAsync("configuration")).Ok);
        }
        finally { release.TrySetResult(Evidence()); await pending; }
        await WaitForReadGate(client);
        slots.Factory = () => Task.FromResult(Evidence());
        Assert.Equal("unverified", (await client.InspectNativeAsync(Entry.Id, StartupInspectionRequest.FromEntry(Entry), revision)).HistoricalIdentity);
        Assert.Equal(2, slots.Calls);
    }

    [Theory]
    [InlineData("before")] [InlineData("native")] [InlineData("after")]
    public async Task SimulatedCallerCancellationRetainsTheWholeWorkflowUntilActualStageCompletion(string phase)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new MetadataReader(); var slots = new SlotReader();
        metadata.Before = call => phase == "before" && call == 1 || phase == "after" && call == 2 ? Pause() : Task.CompletedTask;
        if (phase == "native") { slots.Factory = async () => { await Pause(); return Evidence(); }; }
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var service = new AgentStartupService(new ConfigurationWorkspace(fixture.Paths), metadata, slots);
        var revision = await Revision(fixture); using var cancellation = new CancellationTokenSource();
        var pending = service.InspectNativeAsync(Entry.Id, StartupInspectionRequest.FromEntry(Entry), revision, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal("startup_scan_busy", (await service.ReadAsync(new(), default)).Reason);
            Assert.Equal("startup_scan_busy", (await Assert.ThrowsAsync<AgentCommandException>(() => service.InspectNativeAsync(Entry.Id,
                StartupInspectionRequest.FromEntry(Entry), revision, default))).Code);
            Assert.Equal(phase == "after" ? 2 : 1, metadata.Calls); Assert.Equal(phase == "before" ? 0 : 1, slots.Calls);
        }
        finally { release.TrySetResult(); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while ((await service.ReadAsync(new(), deadline.Token)).Reason == "startup_scan_busy") { await Task.Delay(10, deadline.Token); }
        async Task Pause() { entered.TrySetResult(); await release.Task; }
    }

    [Theory]
    [InlineData("io", "startup_native_observation_io_error")]
    [InlineData("permission", "startup_preflight_permission_denied")]
    [InlineData("json", "startup_backup_invalid")]
    public async Task SimulatedNativeReaderFaultNeverBecomesPartialOrCachedEvidence(string fault, string code)
    {
        var slots = new SlotReader { Factory = () => fault switch
        {
            "io" => throw new IOException("owned simulated failure"),
            "permission" => throw new UnauthorizedAccessException("owned simulated failure"),
            _ => throw new JsonException("owned simulated failure"),
        } };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: new MetadataReader(), startupSlotReader: slots);
        var reply = await fixture.Client.SendResponseAsync("startup-inspect-native", Entry.Id, StartupInspectionRequest.FromEntry(Entry), expectedRevision: await Revision(fixture));
        Assert.Equal(code, reply.ErrorCode); Assert.Null(reply.Data); Assert.Null(reply.OperationId); Assert.Equal(1, slots.Calls);
    }

    internal static async Task<string> Revision(ConfigurationControlPlaneTests.ServerFixture fixture) =>
        (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Revision;
    private static async Task WaitForReadGate(AgentStartupClient client)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while ((await client.ReadAsync(new(), deadline.Token)).Reason == "startup_scan_busy") { await Task.Delay(10, deadline.Token); }
    }
    internal sealed class MetadataReader : IStartupSnapshotReader
    {
        internal int Calls; internal Func<int, StartupManagementSnapshot> Factory = _ => Inventory(); internal Func<int, Task>? Before;
        public async Task<StartupManagementSnapshot> ReadAsync(AutorunConfiguration configuration, StartupListQuery query, CancellationToken cancellationToken = default)
        { var call = Interlocked.Increment(ref Calls); if (Before is not null) { await Before(call); } return Factory(call); }
    }
    internal sealed class SlotReader : IStartupSlotEvidenceReader
    {
        internal int Calls; internal Func<Task<StartupSlotEvidenceRead>> Factory = () => Task.FromResult(Evidence());
        public Task<StartupSlotEvidenceRead> ReadAsync(string backupId, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); Assert.Equal(BackupId, backupId); return Factory(); }
    }
}
