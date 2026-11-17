using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

public sealed class SelfStartupWorkflowTests
{
    [Fact]
    public async Task SameRootSharesCurrentStateAndOnlyExplicitMatchingConfirmationReservesBeforeSingleWireSend()
    {
        await using var fixture = new Fixture();
        fixture.Start(request =>
        {
            if (request.Command == "startup-self-prepare") { Assert.False(File.Exists(fixture.PendingPath)); return fixture.Prepared(request); }
            Assert.Equal("startup-self-set", request.Command); Assert.True(request.Payload!.Value.GetProperty("confirmed").GetBoolean());
            var pending = JsonSerializer.Deserialize<SelfStartupPendingOperation>(File.ReadAllBytes(fixture.PendingPath), AgentProtocol.JsonOptions)!;
            Assert.Equal(request.OperationId, pending.OperationId); Assert.Equal(fixture.Endpoint.RootId, pending.RootId);
            return fixture.Ack(request) with { Data = new { protocolCompleted = false, currentRegistered = true, permissionToRetry = true } };
        });
        var workflow = fixture.Workflow; Assert.Same(workflow, SelfStartupWorkflow.ForRoot(new(Path.Combine(fixture.Root.Path, "."))));
        Assert.True((await workflow.OpenAsync()).CanPrepare); var prepared = await workflow.PrepareAsync(true);
        Assert.True(prepared.CanConfirm); Assert.False(prepared.Preparation!.Confirmed);
        Assert.False(File.Exists(fixture.PendingPath)); Assert.False(File.Exists(Path.Combine(fixture.Root.Path, RootWriteLease.FileName)));
        var refusal = await Assert.ThrowsAsync<AgentCommandException>(() => workflow.SubmitAsync(prepared.Preparation));
        Assert.Equal("authorization_required", refusal.Code); Assert.False(File.Exists(fixture.PendingPath));
        var result = await workflow.SubmitAsync(prepared.Preparation with { Confirmed = true });
        Assert.Equal(SelfStartupWorkflowState.Completed, result.State); Assert.True(result.CanPrepare); Assert.False(result.IsLocked);
        Assert.NotNull(result.OperationId); Assert.False(File.Exists(fixture.PendingPath)); Assert.False(prepared.Preparation.Confirmed);
        Assert.Equal(result, await workflow.OpenAsync()); Assert.Equal(2, fixture.Requests.Count);
        Assert.Single(fixture.Requests, request => request.Command == "startup-self-set");
        Assert.DoesNotContain(typeof(SelfStartupWorkflowSnapshot).GetProperties(), property => property.Name is "Data" or "Response" or "Registered");
    }

    [Theory]
    [InlineData("enabled")] [InlineData("context")] [InlineData("before")] [InlineData("schema")]
    public async Task AlteredConfirmationCannotUseCurrentPreparationOrCreateAnyPending(string change)
    {
        await using var fixture = new Fixture(); fixture.Start(fixture.Prepared); var workflow = fixture.Workflow;
        var prepared = (await workflow.PrepareAsync(true)).Preparation!;
        var confirmed = prepared with { Confirmed = true };
        confirmed = change switch { "enabled" => confirmed with { Enabled = false }, "context" => confirmed with { TargetContextId = new string('b', 64) },
            "before" => confirmed with { ExpectedRegistration = SelfStartupNativeState.FromNativeBytes(1, [0, 0]) }, _ => confirmed with { SchemaVersion = 2 } };
        await Assert.ThrowsAsync<AgentCommandException>(() => workflow.SubmitAsync(confirmed));
        Assert.True(workflow.Current.CanConfirm); Assert.False(File.Exists(fixture.PendingPath)); Assert.Single(fixture.Requests);
        Assert.True(workflow.DiscardPreparation().CanPrepare);
    }

    [Fact]
    public async Task PreCancelledConfirmationCreatesNoIdAndCanStillBeDiscarded()
    {
        await using var fixture = new Fixture(); fixture.Start(fixture.Prepared); var workflow = fixture.Workflow;
        var prepared = (await workflow.PrepareAsync(true)).Preparation!; using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workflow.SubmitAsync(prepared with { Confirmed = true }, token: stop.Token));
        Assert.True(workflow.Current.CanConfirm); Assert.Null(workflow.Current.OperationId); Assert.False(File.Exists(fixture.PendingPath));
        Assert.True(workflow.DiscardPreparation().CanPrepare); Assert.Single(fixture.Requests);
    }

    [Theory]
    [InlineData("not_executed", false, false, SelfStartupWorkflowState.Refused)]
    [InlineData("outcome_unknown", false, false, SelfStartupWorkflowState.OutcomeUnknown)]
    [InlineData("invalid_argument", false, false, SelfStartupWorkflowState.OutcomeUnknown)]
    [InlineData("operation_in_progress", false, true, SelfStartupWorkflowState.OutcomeUnknown)]
    [InlineData("operation_result_unavailable", false, true, SelfStartupWorkflowState.OutcomeUnknown)]
    [InlineData("future_error", false, false, SelfStartupWorkflowState.OutcomeUnknown)]
    [InlineData(null, true, true, SelfStartupWorkflowState.OutcomeUnknown)]
    [InlineData("not_executed", false, true, SelfStartupWorkflowState.OutcomeUnknown)]
    public async Task OnlyFreshSuccessOrExplicitNotExecutedCanReleaseNeverReplayOrGenericError(string? code, bool ok, bool replayed, SelfStartupWorkflowState expected)
    {
        await using var fixture = new Fixture(); fixture.Start(request => request.Command == "startup-self-prepare" ? fixture.Prepared(request)
            : fixture.Ack(request) with { Ok = ok, ErrorCode = code, Error = ok ? null : "owned refusal", Replayed = replayed });
        var workflow = fixture.Workflow; var prepared = (await workflow.PrepareAsync(true)).Preparation!;
        var result = await workflow.SubmitAsync(prepared with { Confirmed = true }); Assert.Equal(expected, result.State);
        Assert.Equal(expected == SelfStartupWorkflowState.OutcomeUnknown, File.Exists(fixture.PendingPath));
        Assert.Equal(result, await workflow.OpenAsync()); Assert.Equal(2, fixture.Requests.Count);
        if (result.IsLocked) { Assert.True(result.CanQuery); await Assert.ThrowsAsync<AgentCommandException>(() => workflow.PrepareAsync(false)); }
        else { Assert.True(result.CanPrepare); }
    }

    [Theory]
    [InlineData("drop")] [InlineData("root")] [InlineData("operation")] [InlineData("data")]
    public async Task ActualWriteThenLostOrInvalidAckRetainsOriginalIdAndPending(string fault)
    {
        await using var fixture = new Fixture(); fixture.Start(request =>
        {
            if (request.Command == "startup-self-prepare") { return fixture.Prepared(request); }
            Assert.True(File.Exists(fixture.PendingPath)); if (fault == "drop") { return null; }
            return fault switch { "root" => fixture.Ack(request) with { RootId = new string('0', 64) },
                "operation" => fixture.Ack(request) with { OperationId = AgentProtocol.NewOperationId() }, _ => fixture.Ack(request) with { Data = Array.Empty<int>() } };
        });
        var workflow = fixture.Workflow; var prepared = (await workflow.PrepareAsync(true)).Preparation!;
        var result = await workflow.SubmitAsync(prepared with { Confirmed = true });
        Assert.Equal(SelfStartupWorkflowState.OutcomeUnknown, result.State); Assert.True(result.IsLocked);
        var sent = Assert.Single(fixture.Requests, request => request.Command == "startup-self-set"); Assert.Equal(sent.OperationId, result.OperationId);
        Assert.Equal(result.OperationId, (await new SelfStartupPendingStore(new(fixture.Root.Path)).ReadAsync()).Operation!.OperationId);
        Assert.Equal(result, await workflow.OpenAsync()); await Assert.ThrowsAsync<AgentCommandException>(() => workflow.SubmitAsync(prepared with { Confirmed = true }));
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public async Task DisconnectedBeforeAttemptedWriteIsNotSentAndClearsOnlyItsFreshReservation()
    {
        await using var fixture = new Fixture(); fixture.Start(fixture.Prepared); var workflow = fixture.Workflow;
        var prepared = (await workflow.PrepareAsync(true)).Preparation!; await fixture.StopAsync();
        var result = await workflow.SubmitAsync(prepared with { Confirmed = true }, TimeSpan.FromMilliseconds(40));
        Assert.Equal(SelfStartupWorkflowState.NotSent, result.State); Assert.Equal("timeout", result.ErrorCode); Assert.True(result.CanPrepare); Assert.NotNull(result.OperationId);
        Assert.False(File.Exists(fixture.PendingPath)); Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task BusyReopenOrDoubleConfirmNeverSubmitsAgainAndDetachedCallerDoesNotLoseWorkflow()
    {
        await using var fixture = new Fixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Start(async request =>
        {
            if (request.Command == "startup-self-prepare") { return fixture.Prepared(request); }
            entered.TrySetResult(); await release.Task; return fixture.Ack(request);
        });
        var workflow = fixture.Workflow; var prepared = (await workflow.PrepareAsync(true)).Preparation!;
        var inFlight = workflow.SubmitAsync(prepared with { Confirmed = true });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var reopened = await fixture.Workflow.OpenAsync();
            Assert.Equal(SelfStartupWorkflowState.Submitting, reopened.State); Assert.NotNull(reopened.OperationId); Assert.True(reopened.IsBusy);
            await Assert.ThrowsAsync<AgentCommandException>(() => workflow.SubmitAsync(prepared with { Confirmed = true }));
            await Assert.ThrowsAsync<AgentCommandException>(() => workflow.PrepareAsync(false));
            Assert.Throws<AgentCommandException>(() => workflow.DiscardPreparation()); Assert.Single(fixture.Requests, request => request.Command == "startup-self-set");
        }
        finally { release.TrySetResult(); await inFlight; }
        Assert.Equal(SelfStartupWorkflowState.Completed, workflow.Current.State); Assert.False(File.Exists(fixture.PendingPath));
    }

    [Theory]
    [InlineData("missing", SelfStartupReceiptState.NotFound)] [InlineData("running", SelfStartupReceiptState.Running)]
    [InlineData("unknown", SelfStartupReceiptState.Unknown)] [InlineData("completed", SelfStartupReceiptState.CompletedWithResponse)]
    [InlineData("omitted", SelfStartupReceiptState.ResultUnavailable)] [InlineData("command", SelfStartupReceiptState.QueryFailed)]
    [InlineData("name", SelfStartupReceiptState.QueryFailed)] [InlineData("failure", SelfStartupReceiptState.QueryFailed)]
    public async Task RecoveredOriginalIdQueriesNeverUnlockDeletePrepareOrApplyHistoricalData(string reply, SelfStartupReceiptState expected)
    {
        await using var fixture = new Fixture(); var owner = await new SelfStartupPendingStore(new(fixture.Root.Path)).ReserveAsync(AgentProtocol.NewOperationId(), true);
        var bytes = File.ReadAllBytes(fixture.PendingPath); var id = owner.Operation.OperationId;
        fixture.Start(request =>
        {
            Assert.Equal("operation", request.Command); Assert.Equal(id, request.Name); Assert.Null(request.OperationId);
            if (reply is "missing" or "failure") { return fixture.Ack(request) with { Ok = false, ErrorCode = reply == "missing" ? "operation_not_found" : "permission_denied", Data = new { confirmed = true } }; }
            var state = reply == "running" ? OperationState.Running : reply == "unknown" ? OperationState.Unknown : OperationState.Completed;
            var historical = fixture.Ack(request) with { Id = "original-request", OperationId = id, Data = new { permissionToRetry = true, registered = true } };
            var receipt = new OperationReceipt(1, id, reply == "command" ? "startup-disable" : "startup-self-set", reply == "name" ? "other" : null,
                new string('a', 64), "original-owner", state, DateTimeOffset.UtcNow,
                state == OperationState.Running ? null : DateTimeOffset.UtcNow, state == OperationState.Running || reply == "omitted" ? null : historical, reply == "omitted");
            return fixture.Ack(request) with { Data = receipt };
        });
        var workflow = fixture.Workflow; var opened = await workflow.OpenAsync(); Assert.True(opened.IsLocked); Assert.Equal(id, opened.OperationId);
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var queried = await workflow.QueryAsync(); Assert.Equal(expected, queried.Observation!.State); Assert.True(queried.IsLocked);
            Assert.True(queried.CanQuery); Assert.False(queried.CanPrepare); Assert.Equal(id, queried.OperationId); Assert.Equal(bytes, File.ReadAllBytes(fixture.PendingPath));
            Assert.Equal(queried, await workflow.OpenAsync());
        }
        await Assert.ThrowsAsync<AgentCommandException>(() => workflow.PrepareAsync(false)); Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public async Task InvalidPendingHasNoQueryKeyAndFixingDiskCannotUnlockTheLiveCoordinator()
    {
        await using var fixture = new Fixture(); File.WriteAllText(fixture.PendingPath, "invalid"); var workflow = fixture.Workflow;
        var opened = await workflow.OpenAsync(); Assert.Equal(SelfStartupWorkflowState.StorageBlocked, opened.State); Assert.Null(opened.OperationId); Assert.False(opened.CanQuery);
        await Assert.ThrowsAsync<AgentCommandException>(() => workflow.QueryAsync()); await Assert.ThrowsAsync<AgentCommandException>(() => workflow.PrepareAsync(true));
        File.Delete(fixture.PendingPath); Assert.Equal(opened, await workflow.OpenAsync()); Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task AnotherProcessReservationBetweenConfirmationAndSubmitIsPreservedWithoutAnySend()
    {
        await using var fixture = new Fixture(); fixture.Start(fixture.Prepared); var workflow = fixture.Workflow;
        var prepared = (await workflow.PrepareAsync(true)).Preparation!;
        var other = await new SelfStartupPendingStore(new(fixture.Root.Path)).ReserveAsync(AgentProtocol.NewOperationId(), false); var bytes = File.ReadAllBytes(fixture.PendingPath);
        var blocked = await workflow.SubmitAsync(prepared with { Confirmed = true });
        Assert.True(blocked.IsLocked); Assert.Equal(other.Operation.OperationId, blocked.OperationId); Assert.Equal(bytes, File.ReadAllBytes(fixture.PendingPath)); Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task FreshKnownAckWithFailedCleanupBlocksOnlySelfAndCannotBeUnlockedByReopen()
    {
        await using var fixture = new Fixture(); fixture.Start(request =>
        {
            if (request.Command == "startup-self-prepare") { return fixture.Prepared(request); }
            File.SetAttributes(fixture.PendingPath, File.GetAttributes(fixture.PendingPath) | FileAttributes.ReadOnly);
            return fixture.Ack(request);
        });
        try
        {
            var workflow = fixture.Workflow; var prepared = (await workflow.PrepareAsync(true)).Preparation!;
            var blocked = await workflow.SubmitAsync(prepared with { Confirmed = true });
            Assert.Equal(SelfStartupWorkflowState.StorageBlocked, blocked.State); Assert.Equal("pending_clear_unverified", blocked.ErrorCode);
            Assert.True(blocked.CanQuery); Assert.False(blocked.CanPrepare); Assert.True(File.Exists(fixture.PendingPath));
            Assert.Equal(blocked, await workflow.OpenAsync()); Assert.Equal(2, fixture.Requests.Count);
            await Assert.ThrowsAsync<AgentCommandException>(() => workflow.PrepareAsync(false));
        }
        finally { if (File.Exists(fixture.PendingPath)) { File.SetAttributes(fixture.PendingPath, FileAttributes.Normal); } }
    }

    [Theory]
    [InlineData("flushed", SelfStartupWorkflowState.NotSent)]
    [InlineData("published", SelfStartupWorkflowState.OutcomeUnknown)]
    public async Task ReservationCancellationDistinguishesUnpublishedNoSendFromPublishedRecovery(string stage, SelfStartupWorkflowState expected)
    {
        await using var fixture = new Fixture(); fixture.Start(fixture.Prepared); var workflow = fixture.Workflow;
        var prepared = (await workflow.PrepareAsync(true)).Preparation!; using var stop = new CancellationTokenSource(); var checkpoints = 0;
        // Reuse only R77's private filesystem fault seam. The transport remains
        // the actual sealed client; no production injection/cleanup API is added.
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var constructor = typeof(SelfStartupPendingStore).GetConstructor(flags, null, [typeof(ProjectPaths), typeof(Action<string>)], null)!;
        var store = (SelfStartupPendingStore)constructor.Invoke([new ProjectPaths(fixture.Root.Path), (Action<string>)(checkpoint =>
        { if (checkpoint == stage) { checkpoints++; stop.Cancel(); } })]);
        typeof(SelfStartupWorkflow).GetField("_pending", flags)!.SetValue(workflow, store);
        var result = await workflow.SubmitAsync(prepared with { Confirmed = true }, token: stop.Token);
        Assert.Equal(1, checkpoints); Assert.Equal(expected, result.State); Assert.NotNull(result.OperationId); Assert.Single(fixture.Requests);
        Assert.Equal(stage == "published", File.Exists(fixture.PendingPath));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root.Path, SelfStartupPendingStore.FileName + ".*.tmp"));
        if (stage == "published")
        { Assert.True(result.IsLocked); Assert.True(result.CanQuery); Assert.Equal(result.OperationId, (await store.ReadAsync()).Operation!.OperationId); }
        else { Assert.True(result.CanPrepare); Assert.Equal("cancelled", result.ErrorCode); Assert.False(result.CanQuery); }
        Assert.Equal(result, await workflow.OpenAsync());
    }

    [Fact]
    public async Task ClosingViewCanAtomicallyDiscardOnlyTheExactUnconfirmedInstance()
    {
        await using var fixture = new Fixture(); fixture.Start(fixture.Prepared); var workflow = fixture.Workflow;
        var prepared = (await workflow.PrepareAsync(true)).Preparation!;
        Assert.False(workflow.TryDiscardPreparation(prepared with { }));
        Assert.False(workflow.TryDiscardPreparation(prepared with { Confirmed = true }));
        Assert.True(workflow.Current.CanConfirm); Assert.True(workflow.TryDiscardPreparation(prepared));
        Assert.False(workflow.TryDiscardPreparation(prepared)); Assert.True(workflow.Current.CanPrepare);
        Assert.Single(fixture.Requests); Assert.False(File.Exists(fixture.PendingPath));
    }

    [Fact]
    public async Task ClosingViewLosingDiscardRaceNeverInterruptsConfirmedSubmission()
    {
        await using var fixture = new Fixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Start(async request => { if (request.Command == "startup-self-set") { entered.TrySetResult(); await release.Task; } return request.Command == "startup-self-prepare" ? fixture.Prepared(request) : fixture.Ack(request); });
        var workflow = fixture.Workflow; var prepared = (await workflow.PrepareAsync(true)).Preparation!;
        var submit = workflow.SubmitAsync(prepared with { Confirmed = true });
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(workflow.TryDiscardPreparation(prepared)); Assert.True(File.Exists(fixture.PendingPath)); }
        finally { release.TrySetResult(); }
        Assert.Equal(SelfStartupWorkflowState.Completed, (await submit).State); Assert.False(File.Exists(fixture.PendingPath));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal TempDirectory Root { get; } = new();
        internal AgentEndpoint Endpoint { get; }
        internal string PendingPath => Path.Combine(Root.Path, SelfStartupPendingStore.FileName);
        internal ConcurrentQueue<AgentRequest> Requests { get; } = new();
        internal SelfStartupWorkflow Workflow => SelfStartupWorkflow.ForRoot(new(Root.Path));
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
        private Task? _server;
        internal Fixture() { Root.Write("autorun.yaml", "version: 1\ntasks: []\n"); Endpoint = AgentProtocol.ForRoot(Root.Path); }
        internal AgentResponse Prepared(AgentRequest request) => Ack(request) with { Data = new SelfStartupMutationRequest
        { SchemaVersion = 1, Enabled = request.Name == "enable", Confirmed = false, TargetContextId = new string('a', 64), ExpectedRegistration = new(false) } };
        internal AgentResponse Ack(AgentRequest request) => new(request.Id, true, new { opaque = "history" }, RootId: Endpoint.RootId, OperationId: request.OperationId);
        internal void Start(Func<AgentRequest, AgentResponse?> handler) => Start(request => Task.FromResult(handler(request)));
        internal void Start(Func<AgentRequest, Task<AgentResponse?>> handler)
        {
            _server = Task.Run(async () =>
            {
                while (true)
                {
                    await using var pipe = new NamedPipeServerStream(Endpoint.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                    var line = await reader.ReadLineAsync(_stop.Token); if (line is null) { continue; }
                    var request = JsonSerializer.Deserialize<AgentRequest>(line, AgentProtocol.JsonOptions)!; Requests.Enqueue(request);
                    var response = await handler(request);
                    if (response is not null)
                    {
                        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(response, AgentProtocol.JsonOptions).AsMemory(), _stop.Token);
                    }
                }
            });
        }
        internal async Task StopAsync()
        { _stop.Cancel(); if (_server is not null) { try { await _server; } catch (OperationCanceledException) when (_stop.IsCancellationRequested) { } } }
        public async ValueTask DisposeAsync() { try { await StopAsync(); } finally { _stop.Dispose(); Root.Dispose(); } }
    }
}
