using System.IO;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Recovery;
using Host = SystemAutorun.IntegrationTests.ConfigurationControlPlaneTests.ServerFixture;

namespace SystemAutorun.IntegrationTests;

public sealed class RecoveryPendingIntegrationTests
{
    private const string Configuration = """
        version: 1
        settings:
          default_startup_delay: 0
        tasks:
          - name: media
            type: windows
            exec: never-execute-recovery-pending-fixture.exe
            enabled: true
            expected_running: false
            health:
              type: process
              pattern: never-execute-recovery-pending-fixture
              timeout: 1
        """;

    [Fact]
    public async Task RealResumeAndJournalQueryBindOriginalIntentButHistoricalCompletionCannotClearHint()
    {
        await using var host = new Host(); await File.WriteAllTextAsync(host.Paths.ConfigPath, Configuration);
        var control = new RecoveryControlStore(host.Paths); var initial = await control.ReadAsync(); await control.InitializeEmptyAsync(initial.Revision);
        await control.SetPausedAsync("media", true, recordIntent: true);
        var client = new AgentRecoveryClient(host.Client); var observed = await client.ReadAsync(); var store = new RecoveryPendingStore(host.Paths, "media");
        var owner = await store.ReserveAsync(AgentProtocol.NewOperationId(), observed.Revision); var pending = owner.Operation;
        var path = Path.Combine(host.Root, store.FileName); var bytes = File.ReadAllBytes(path); var config = File.ReadAllBytes(host.Paths.ConfigPath);
        var ack = await client.SubmitResumeAsync(pending.TaskName, pending.ExpectedRevision, pending.OperationId, true);
        Assert.True(ack.Ok); Assert.False(ack.Replayed);
        var reader = new AgentRecoveryReceiptClient(host.Client);
        Assert.Equal(RecoveryReceiptState.CompletedHistory, (await reader.ReadAsync(pending)).State);
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Equal(pending, (await new RecoveryPendingStore(host.Paths, "MEDIA").ReadAsync()).Operation);
        // A newer Stop intent cannot be undone/inferred from historical successful Data.
        await control.SetPausedAsync("media", true, recordIntent: true); var stopped = File.ReadAllBytes(control.FilePath);
        Assert.Equal(RecoveryReceiptState.CompletedHistory, (await reader.ReadAsync(pending)).State);
        Assert.True(Assert.Single((await client.ReadAsync()).Tasks).Paused); Assert.Equal(stopped, File.ReadAllBytes(control.FilePath));
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.True((await store.ReadAsync()).BlocksSubmission);
        Assert.Equal(config, File.ReadAllBytes(host.Paths.ConfigPath)); Assert.Single(Directory.GetFiles(host.Root, "*.operation.json", SearchOption.AllDirectories)); Assert.Empty(host.ObservedErrors);
        // This test deliberately retains the hint; no query acquires/reconstructs owner capability.
    }

    [Fact]
    public async Task ReservedButNeverSentOriginalIdStaysBlockedAfterRealNotFoundQuery()
    {
        await using var host = new Host(); var store = new RecoveryPendingStore(host.Paths, "media");
        var owner = await store.ReserveAsync(AgentProtocol.NewOperationId(), new string('a', 64)); var pending = owner.Operation;
        var path = Path.Combine(host.Root, store.FileName); var bytes = File.ReadAllBytes(path);
        var observed = await new AgentRecoveryReceiptClient(host.Client).ReadAsync(pending);
        Assert.Equal(RecoveryReceiptState.NotFound, observed.State); Assert.Equal(pending.OperationId, observed.OperationId);
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Equal("pending_exists", (await Assert.ThrowsAsync<RecoveryPendingStoreException>(() =>
            new RecoveryPendingStore(host.Paths, "MEDIA").ReserveAsync(AgentProtocol.NewOperationId(), pending.ExpectedRevision))).Code);
        Assert.Empty(Directory.GetFiles(host.Root, "*.operation.json", SearchOption.AllDirectories));
        var error = Assert.Single(host.ObservedErrors); Assert.Equal("operation", error.Command); Assert.Null(error.OperationId);
        Assert.Equal("operation_not_found", Assert.IsType<AgentCommandException>(error.Error).Code);
    }

    [Fact]
    public async Task RealRecoveryConflictUnknownReceiptRemainsBoundToOriginalRevisionWithoutUnlock()
    {
        await using var host = new Host(); await File.WriteAllTextAsync(host.Paths.ConfigPath, Configuration);
        var control = new RecoveryControlStore(host.Paths); var initial = await control.ReadAsync(); await control.InitializeEmptyAsync(initial.Revision);
        await control.SetPausedAsync("media", true, recordIntent: true);
        var client = new AgentRecoveryClient(host.Client); var stale = await client.ReadAsync(); var store = new RecoveryPendingStore(host.Paths, "media");
        var owner = await store.ReserveAsync(AgentProtocol.NewOperationId(), stale.Revision); var pending = owner.Operation;
        await control.SetPausedAsync("media", true, recordIntent: true); var stateBytes = File.ReadAllBytes(control.FilePath);
        var path = Path.Combine(host.Root, store.FileName); var hintBytes = File.ReadAllBytes(path);
        var response = await client.SubmitResumeAsync(pending.TaskName, pending.ExpectedRevision, pending.OperationId, true);
        Assert.False(response.Ok); Assert.Equal("outcome_unknown", response.ErrorCode);
        var observed = await new AgentRecoveryReceiptClient(host.Client).ReadAsync(pending);
        Assert.Equal(RecoveryReceiptState.Unknown, observed.State); Assert.Equal(pending.OperationId, observed.OperationId);
        Assert.Equal(stateBytes, File.ReadAllBytes(control.FilePath)); Assert.Equal(hintBytes, File.ReadAllBytes(path));
        Assert.True((await store.ReadAsync()).BlocksSubmission); Assert.Single(Directory.GetFiles(host.Root, "*.operation.json", SearchOption.AllDirectories));
    }
}
