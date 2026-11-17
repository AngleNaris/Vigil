using System.IO;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Recovery;
using RealServer = SystemAutorun.IntegrationTests.ConfigurationControlPlaneTests.ServerFixture;

namespace SystemAutorun.IntegrationTests;

public sealed class RecoveryWorkflowIntegrationTests
{
    internal const string Configuration = """
        version: 2
        settings:
          default_startup_delay: 0
        tasks:
          - name: media
            type: windows
            exec: never-execute-r90-workflow.exe
            enabled: true
            expected_running: false
            health:
              type: process
              pattern: never-execute-r90-workflow
              timeout: 1
        """;

    [Fact]
    public async Task ProductionAgentServerFreshAckClearsHintAndAllowsAnotherExplicitConfirmedIntent()
    {
        await using var host = new RealServer(); await File.WriteAllTextAsync(host.Paths.ConfigPath, Configuration);
        var store = new RecoveryControlStore(host.Paths); var initial = await store.ReadAsync(); await store.InitializePausedAsync(new[] { "media" }, initial.Revision);
        var beforeConfig = File.ReadAllBytes(host.Paths.ConfigPath); var w = RecoveryWorkflow.ForTask(host.Paths, "media");
        var preparation = (await w.PrepareAsync()).Preparation!; Assert.True(preparation.Observation.CanResume);
        var first = await w.SubmitAsync(preparation with { Confirmed = true }); Assert.Equal(RecoveryWorkflowState.Completed, first.State);
        Assert.False(first.Observation!.Entry!.Paused); Assert.False(first.IsLocked); Assert.Null(first.ReadErrorCode);
        Assert.Equal(RecoveryPendingState.Missing, (await new RecoveryPendingStore(host.Paths, "media").ReadAsync()).State);
        var receipt = await new AgentOperationClient(host.Client).ReadAsync(first.OperationId!); Assert.Equal(OperationState.Completed, receipt!.State);
        Assert.Equal("recovery-resume", receipt.Command); Assert.Equal("media", receipt.Name); Assert.False(receipt.Response!.Replayed);
        await store.SetPausedAsync("media", true, recordIntent: true);
        var secondPreparation = (await w.PrepareAsync()).Preparation!; Assert.NotEqual(preparation.PreparationId, secondPreparation.PreparationId);
        var second = await w.SubmitAsync(secondPreparation with { Confirmed = true }); Assert.Equal(RecoveryWorkflowState.Completed, second.State);
        Assert.NotEqual(first.OperationId, second.OperationId); Assert.Equal(2, Directory.GetFiles(host.Root, "*.operation.json", SearchOption.AllDirectories).Length);
        Assert.Equal(beforeConfig, File.ReadAllBytes(host.Paths.ConfigPath)); Assert.Empty(host.ObservedErrors);
        Assert.False(File.Exists(Path.Combine(host.Root, new RecoveryPendingStore(host.Paths, "media").FileName)));
    }

    [Theory]
    [InlineData("revision")] [InlineData("disabled")] [InlineData("unpaused")]
    public async Task KnownDriftAtConfirmedPreflightNeverReservesOrSendsAnOperation(string kind)
    {
        await using var host = new RealServer(); await File.WriteAllTextAsync(host.Paths.ConfigPath, Configuration);
        var store = new RecoveryControlStore(host.Paths); var initial = await store.ReadAsync(); await store.InitializePausedAsync(new[] { "media" }, initial.Revision);
        var w = RecoveryWorkflow.ForTask(host.Paths, "media"); var preparation = (await w.PrepareAsync()).Preparation!;
        if (kind == "disabled") { await File.WriteAllTextAsync(host.Paths.ConfigPath, Configuration.Replace("enabled: true", "enabled: false", StringComparison.Ordinal)); }
        else { await store.SetPausedAsync("media", kind != "unpaused", recordIntent: true); }
        var bytes = File.ReadAllBytes(store.FilePath); var result = await w.SubmitAsync(preparation with { Confirmed = true });
        Assert.Equal(RecoveryWorkflowState.Ready, result.State); Assert.Equal("preparation_changed", result.ErrorCode); Assert.Null(result.OperationId); Assert.Null(result.Preparation);
        Assert.Equal(bytes, File.ReadAllBytes(store.FilePath)); Assert.Empty(Directory.GetFiles(host.Root, "*.operation.json", SearchOption.AllDirectories));
        Assert.Equal(RecoveryPendingState.Missing, (await new RecoveryPendingStore(host.Paths, "media").ReadAsync()).State); Assert.Empty(host.ObservedErrors);
    }

    [Theory]
    [InlineData(32768, RecoveryReceiptState.CompletedHistory)] [InlineData(1, RecoveryReceiptState.ResultUnavailable)]
    public async Task RealJournalLostAckAndOmittedHistoryRemainReadOnlyWithOriginalHint(int limit, RecoveryReceiptState state)
    {
        await using var host = new RecoveryWorkflowDispatchFixture(limit) { DropResumeAck = true }; await host.InitializeAsync();
        var w = host.Workflow; var preparation = (await w.PrepareAsync()).Preparation!;
        host.BeforeDispatch = async request =>
        {
            if (request.Command != "recovery-resume") { return; }
            var pending = await new RecoveryPendingStore(host.Paths, "media").ReadAsync(); Assert.Equal(request.OperationId, pending.Operation!.OperationId);
            Assert.Equal(request.ExpectedRevision, pending.Operation.ExpectedRevision);
            // Publication's root lease is actually gone before transport/dispatch.
            await using var lease = await RootWriteLease.AcquireAsync(host.Paths, host.Token);
        };
        var unknown = await w.SubmitAsync(preparation with { Confirmed = true }); Assert.True(unknown.IsLocked); Assert.Equal("outcome_unknown", unknown.ErrorCode);
        var path = Path.Combine(host.Root, new RecoveryPendingStore(host.Paths, "media").FileName); var bytes = File.ReadAllBytes(path);
        var receipt = await host.Journal.GetAsync(unknown.OperationId!); Assert.Equal(OperationState.Completed, receipt!.State); Assert.Equal(limit == 1, receipt.ResultOmitted);
        var queried = await w.QueryAsync(); Assert.Equal(state, queried.Receipt!.State); Assert.Equal(unknown.OperationId, queried.OperationId); Assert.True(queried.IsLocked);
        Assert.Null(queried.Observation); Assert.Null(queried.Preparation); Assert.Equal(bytes, File.ReadAllBytes(path));
        var current = await w.RefreshAsync(); Assert.True(current.IsLocked); Assert.False(current.Observation!.Entry!.Paused);
        await host.Store.SetPausedAsync("media", true, recordIntent: true); var stopped = File.ReadAllBytes(host.Store.FilePath);
        var history = await w.QueryAsync(); Assert.Equal(state, history.Receipt!.State); Assert.True(history.IsLocked); Assert.False(history.Observation!.Entry!.Paused);
        Assert.Equal(stopped, File.ReadAllBytes(host.Store.FilePath)); Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.True((await w.RefreshAsync()).Observation!.Entry!.Paused); Assert.True(w.Current.IsLocked);
        Assert.Equal(0, host.WorkloadCalls); Assert.Empty(host.Errors); Assert.Single(host.Requests, request => request.Command == "recovery-resume");
        await Assert.ThrowsAsync<AgentCommandException>(() => w.PrepareAsync());
    }

    [Fact]
    public async Task RaceAfterPreflightRemainsBackendCasUnknownWithoutRetryOrHiddenExecutorError()
    {
        await using var host = new RecoveryWorkflowDispatchFixture(); await host.InitializeAsync(); var w = host.Workflow;
        var preparation = (await w.PrepareAsync()).Preparation!; byte[]? stopped = null; byte[]? hint = null;
        host.BeforeDispatch = async request =>
        {
            if (request.Command != "recovery-resume") { return; }
            var path = Path.Combine(host.Root, new RecoveryPendingStore(host.Paths, "media").FileName); hint = File.ReadAllBytes(path);
            await host.Store.SetPausedAsync("media", true, recordIntent: true); stopped = File.ReadAllBytes(host.Store.FilePath);
        };
        var unknown = await w.SubmitAsync(preparation with { Confirmed = true }); Assert.Equal(RecoveryWorkflowState.OutcomeUnknown, unknown.State);
        Assert.Equal("outcome_unknown", unknown.ErrorCode); Assert.Equal(preparation.Observation.RecoveryRevision, unknown.Pending!.ExpectedRevision);
        var error = Assert.Single(host.Errors); Assert.Equal("recovery-resume", error.Command); Assert.Equal("recovery_revision_conflict", Assert.IsType<AgentCommandException>(error.Error).Code);
        var history = await w.QueryAsync(); Assert.Equal(RecoveryReceiptState.Unknown, history.Receipt!.State); Assert.True(history.IsLocked);
        Assert.Equal(stopped, File.ReadAllBytes(host.Store.FilePath)); Assert.Equal(hint, File.ReadAllBytes(Path.Combine(host.Root, new RecoveryPendingStore(host.Paths, "media").FileName)));
        Assert.Equal(0, host.WorkloadCalls); Assert.Single(host.Requests, request => request.Command == "recovery-resume");
    }

    [Fact]
    public async Task ClosingCancelsOnlyViewAfterRealJournalCompletesBeforeAckDelivery()
    {
        await using var host = new RecoveryWorkflowDispatchFixture(); await host.InitializeAsync(); var w = host.Workflow;
        using var view = new CancellationTokenSource(); var prepared = (await w.PrepareAsync(token: view.Token)).Preparation!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AfterDispatch = async (request, response) =>
        { if (request.Command == "recovery-resume") { Assert.True(response.Ok); entered.SetResult(); await release.Task.WaitAsync(host.Token); } };
        var submit = w.SubmitAsync(prepared with { Confirmed = true });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); view.Cancel(); Assert.False(w.DismissUnconfirmed(view.Token)); Assert.False(w.TryDiscardPreparation(prepared));
            Assert.Equal(OperationState.Completed, (await host.Journal.GetAsync(w.Current.OperationId!))!.State);
            Assert.True((await new RecoveryPendingStore(host.Paths, "media").ReadAsync()).BlocksSubmission); Assert.True(w.Current.IsBusy);
            Assert.Same(w, RecoveryWorkflow.ForTask(host.Paths, "MEDIA"));
        }
        finally { release.TrySetResult(); }
        var completed = await submit; Assert.Equal(RecoveryWorkflowState.Completed, completed.State); Assert.False(completed.IsLocked);
        Assert.Equal(RecoveryPendingState.Missing, (await new RecoveryPendingStore(host.Paths, "media").ReadAsync()).State);
        Assert.Equal(0, host.WorkloadCalls); Assert.Empty(host.Errors); Assert.Single(host.Requests, request => request.Command == "recovery-resume");
    }
}
