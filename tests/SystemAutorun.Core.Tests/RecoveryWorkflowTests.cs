using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Recovery;
using Fixture = SystemAutorun.Core.Tests.RecoveryWorkflowPipeFixture;

namespace SystemAutorun.Core.Tests;

public sealed class RecoveryWorkflowTests
{
    [Fact]
    public async Task SharedNameScopedWorkflowPersistsBeforeExactlyOneSendThenReadsCurrentNotAck()
    {
        await using var f = new Fixture(); f.Start(request =>
        {
            if (request.Command != "recovery-resume") { return f.Reply(request); }
            Assert.True(File.Exists(f.PendingPath)); var pending = JsonSerializer.Deserialize<RecoveryPendingOperation>(File.ReadAllBytes(f.PendingPath), AgentProtocol.JsonOptions)!;
            Assert.Equal(request.OperationId, pending.OperationId); Assert.Equal(request.ExpectedRevision, pending.ExpectedRevision); Assert.Equal("media", request.Name);
            var ack = f.Reply(request); // a newer stop after this ACK must be observed
            f.State = new(new string('c', 64), new[] { new RecoveryTaskState("media", true, DateTimeOffset.UtcNow, IntentId: AgentProtocol.NewOperationId()) }, true);
            return ack;
        });
        var w = f.Workflow; Assert.Same(w, RecoveryWorkflow.ForTask(new(Path.Combine(f.Root.Path, ".")), "MEDIA"));
        Assert.NotSame(w, RecoveryWorkflow.ForTask(f.Paths, "another"));
        Assert.Equal(RecoveryWorkflowState.Ready, (await w.OpenAsync()).State); Assert.Empty(f.Requests);
        var prepared = (await w.PrepareAsync()).Preparation!; Assert.False(prepared.Confirmed); Assert.False(File.Exists(f.PendingPath));
        Assert.Equal("authorization_required", (await Assert.ThrowsAsync<AgentCommandException>(() => w.SubmitAsync(prepared))).Code);
        var completed = await w.SubmitAsync(prepared with { Confirmed = true });
        Assert.Equal(RecoveryWorkflowState.Completed, completed.State); Assert.False(completed.IsLocked); Assert.True(completed.CanPrepare);
        Assert.True(completed.Observation!.Entry!.Paused); Assert.Equal(new string('c', 64), completed.Observation.RecoveryRevision); Assert.False(File.Exists(f.PendingPath));
        Assert.Single(f.Requests, request => request.Command == "recovery-resume"); Assert.Equal(7, f.Requests.Count); Assert.False(prepared.Confirmed);
        Assert.Equal(completed, await w.OpenAsync()); await Assert.ThrowsAsync<AgentCommandException>(() => w.SubmitAsync(prepared with { Confirmed = true }));
        Assert.DoesNotContain(typeof(RecoveryWorkflowSnapshot).GetProperties(), property => property.Name is "Data" or "Response" or "Healthy");
        Assert.True((await w.PrepareAsync()).CanConfirm); Assert.NotEqual(prepared.PreparationId, w.Current.Preparation!.PreparationId);
        Assert.True(w.TryDiscardPreparation(w.Current.Preparation!));
    }

    [Theory]
    [InlineData("revision")] [InlineData("configuration")] [InlineData("name")] [InlineData("root")]
    [InlineData("subject")] [InlineData("preparation")]
    public async Task AlteredOrStaleConfirmationCannotReserveOrSend(string defect)
    {
        await using var f = new Fixture(); f.Start(f.Reply); var w = f.Workflow; var preparation = (await w.PrepareAsync()).Preparation!;
        var observed = preparation.Observation; var changed = preparation with { Confirmed = true };
        changed = defect switch
        {
            "revision" => changed with { Observation = observed with { RecoveryRevision = new string('b', 64) } },
            "configuration" => changed with { Observation = observed with { ConfigurationRevision = new string('b', 64) } },
            "name" => changed with { Observation = observed with { TaskName = "another" } },
            "root" => changed with { Observation = observed with { RootId = new string('b', 64) } },
            "subject" => changed with { Observation = observed with { SubjectKey = "another" } },
            _ => changed with { PreparationId = Guid.NewGuid().ToString("N") }
        };
        Assert.Equal("invalid_argument", (await Assert.ThrowsAsync<AgentCommandException>(() => w.SubmitAsync(changed))).Code);
        Assert.True(w.Current.CanConfirm); Assert.False(File.Exists(f.PendingPath)); Assert.Equal(2, f.Requests.Count);
        Assert.False(w.TryDiscardPreparation(preparation with { })); Assert.False(w.TryDiscardPreparation(preparation with { Confirmed = true }));
        Assert.True(w.TryDiscardPreparation(preparation)); var next = (await w.PrepareAsync()).Preparation!;
        Assert.False(w.TryDiscardPreparation(preparation)); await Assert.ThrowsAsync<AgentCommandException>(() => w.SubmitAsync(preparation with { Confirmed = true }));
        Assert.True(w.TryDiscardPreparation(next));
    }

    [Theory]
    [InlineData("disabled")] [InlineData("system")] [InlineData("uninitialized")] [InlineData("unpaused")]
    public async Task ReadsAreNotImplicitPreparationOrInitialization(string kind)
    {
        await using var f = new Fixture();
        if (kind == "disabled") { f.Configuration["configuration"]!["tasks"]![0]!["enabled"] = false; }
        if (kind == "system") { f.Configuration["configuration"]!["tasks"]![0]!["launch"]!["runAs"] = "system"; }
        if (kind == "uninitialized") { f.State = new(f.State.Revision, Array.Empty<RecoveryTaskState>(), false); }
        if (kind == "unpaused") { f.State = f.State with { Tasks = new[] { new RecoveryTaskState("media") } }; }
        f.Start(f.Reply); var w = f.Workflow; var read = await w.RefreshAsync(); Assert.False(read.CanPrepare); Assert.Null(read.Preparation);
        var prepared = await w.PrepareAsync(); Assert.False(prepared.CanConfirm); Assert.Null(prepared.Preparation);
        Assert.False(File.Exists(f.PendingPath)); Assert.DoesNotContain(f.Requests, request => request.OperationId is not null);
    }

    [Fact]
    public async Task LateUnconfirmedReadCannotRecreatePreparationAfterDismissal()
    {
        await using var f = new Fixture(); var entered = Signal(); var release = Signal();
        f.Start(async request => { if (request.Command == "configuration" && f.Requests.Count == 1) { entered.SetResult(); await release.Task.WaitAsync(f.Token); } return f.Reply(request); });
        var w = f.Workflow; using var owner = new CancellationTokenSource(); var read = w.PrepareAsync(token: owner.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(w.Current.IsBusy); Assert.True(w.DismissUnconfirmed(owner.Token));
            Assert.False(w.Current.CanConfirm); Assert.False(w.Current.IsBusy); Assert.False(File.Exists(f.PendingPath));
        }
        finally { release.TrySetResult(); }
        Assert.False((await read).CanConfirm); Assert.Null(w.Current.Preparation); Assert.False(File.Exists(f.PendingPath));
        var next = (await w.PrepareAsync()).Preparation!; Assert.True(w.TryDiscardPreparation(next));
    }

    [Fact]
    public async Task CloseLosingConfirmRaceCannotCancelOrUnlockConfirmedSend()
    {
        await using var f = new Fixture(); var entered = Signal(); var release = Signal();
        f.Start(async request => { if (request.Command == "recovery-resume") { entered.SetResult(); await release.Task.WaitAsync(f.Token); } return f.Reply(request); });
        var w = f.Workflow; var prepared = (await w.PrepareAsync()).Preparation!; var submit = w.SubmitAsync(prepared with { Confirmed = true });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(w.DismissUnconfirmed(default)); Assert.False(w.TryDiscardPreparation(prepared));
            Assert.True(w.Current.IsBusy); Assert.True(File.Exists(f.PendingPath)); Assert.Equal(w.Current, await w.OpenAsync());
            await Assert.ThrowsAsync<AgentCommandException>(() => w.SubmitAsync(prepared with { Confirmed = true }));
            await Assert.ThrowsAsync<AgentCommandException>(() => w.RefreshAsync());
        }
        finally { release.TrySetResult(); }
        Assert.Equal(RecoveryWorkflowState.Completed, (await submit).State); Assert.False(File.Exists(f.PendingPath));
        Assert.Single(f.Requests, request => request.Command == "recovery-resume");
    }

    [Fact]
    public async Task ConcretePreWriteConnectTimeoutReleasesExactReservationAndAllowsNewPreparation()
    {
        await using var f = new Fixture(); f.Start(f.Reply); var w = f.Workflow; var preparation = (await w.PrepareAsync()).Preparation!;
        // Reuse only R89's private filesystem seam, not an injected transport.
        // Stop after durable publication (and confirmed preflight) but before send;
        // wait for the actual listener to exit, not merely a cancellation request.
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var constructor = typeof(RecoveryPendingStore).GetConstructor(flags, null, new[] { typeof(ProjectPaths), typeof(string), typeof(Action<string>) }, null)!;
        var stopped = false; var store = (RecoveryPendingStore)constructor.Invoke(new object[] { f.Paths, "media", (Action<string>)(stage => { if (stage == "published") { f.StopAsync().GetAwaiter().GetResult(); stopped = true; } }) });
        typeof(RecoveryWorkflow).GetField("_pending", flags)!.SetValue(w, store);
        var result = await w.SubmitAsync(preparation with { Confirmed = true }, TimeSpan.FromMilliseconds(60)); Assert.True(stopped);
        Assert.Equal(RecoveryWorkflowState.NotSent, result.State); Assert.Equal("timeout", result.ErrorCode); Assert.False(result.IsLocked);
        Assert.False(result.CanQuery); Assert.NotNull(result.OperationId); Assert.False(File.Exists(f.PendingPath));
        Assert.Equal(RecoveryPendingState.Missing, (await new RecoveryPendingStore(f.Paths, "media").ReadAsync()).State);
        var reserve = await new RecoveryPendingStore(f.Paths, "media").ReserveAsync(AgentProtocol.NewOperationId(), f.State.Revision);
        await reserve.ReleaseAfterKnownOutcomeAsync(); Assert.DoesNotContain(f.Requests, request => request.Command == "recovery-resume");
        var readFailed = await w.RefreshAsync(TimeSpan.FromMilliseconds(60)); Assert.Equal(RecoveryWorkflowState.NotSent, readFailed.State);
        Assert.Equal(result.OperationId, readFailed.OperationId); Assert.Equal("timeout", readFailed.ReadErrorCode); Assert.False(readFailed.IsLocked);
    }

    [Theory]
    [InlineData("dropped")] [InlineData("replayed")] [InlineData("false")]
    public async Task UnknownsKeepOriginalIdAcrossQueriesCurrentReadsAndLostHint(string kind)
    {
        await using var f = new Fixture(); f.Start(request => request.Command != "recovery-resume" ? f.Reply(request) : kind switch
        { "dropped" => null, "replayed" => f.Reply(request) with { Replayed = true }, _ => new(request.Id, false, RootId: f.Endpoint.RootId, OperationId: request.OperationId, ErrorCode: "invalid_argument") });
        var w = f.Workflow; var preparation = (await w.PrepareAsync()).Preparation!; var unknown = await w.SubmitAsync(preparation with { Confirmed = true });
        Assert.Equal(RecoveryWorkflowState.OutcomeUnknown, unknown.State); Assert.True(unknown.CanQuery); var bytes = File.ReadAllBytes(f.PendingPath);
        var query = await w.QueryAsync(); Assert.Equal(RecoveryReceiptState.NotFound, query.Receipt!.State); Assert.True(query.IsLocked);
        var read = await w.RefreshAsync(); Assert.True(read.IsLocked); Assert.NotNull(read.Observation); Assert.Null(read.Preparation); Assert.Equal(bytes, File.ReadAllBytes(f.PendingPath));
        Assert.Equal(unknown.OperationId, read.OperationId); await Assert.ThrowsAsync<AgentCommandException>(() => w.PrepareAsync());
        File.Delete(f.PendingPath); var changed = await w.RefreshAsync(); Assert.Equal(RecoveryWorkflowState.StorageBlocked, changed.State);
        Assert.Equal(unknown.OperationId, changed.OperationId); Assert.True(changed.CanQuery); Assert.True((await w.QueryAsync()).IsLocked);
        Assert.Single(f.Requests, request => request.Command == "recovery-resume");
    }

    [Fact]
    public async Task PeerReservationAndCorruptExistingHintCannotBeOverwrittenByConfirmation()
    {
        await using var f = new Fixture(); f.Start(f.Reply); var w = f.Workflow; var prepared = (await w.PrepareAsync()).Preparation!;
        var peer = await new RecoveryPendingStore(f.Paths, "media").ReserveAsync(AgentProtocol.NewOperationId(), f.State.Revision);
        var result = await w.SubmitAsync(prepared with { Confirmed = true }); Assert.True(result.IsLocked); Assert.Equal(peer.Operation.OperationId, result.OperationId);
        Assert.DoesNotContain(f.Requests, request => request.Command == "recovery-resume");
        File.WriteAllText(f.PendingPath, "bad hint"); var changed = await w.RefreshAsync(); Assert.Equal(RecoveryWorkflowState.StorageBlocked, changed.State);
        Assert.Equal(peer.Operation.OperationId, changed.OperationId); Assert.Equal("bad hint", File.ReadAllText(f.PendingPath));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task FinalizationFailureStaysLockedAndReadbackFailureCannotEraseKnownAck(bool clearFailure)
    {
        await using var f = new Fixture(); f.Start(request =>
        {
            if (request.Command == "recovery-resume") { if (clearFailure) { File.SetAttributes(f.PendingPath, FileAttributes.ReadOnly); } return f.Reply(request); }
            if (!clearFailure && request.Command == "configuration" && f.Requests.Any(row => row.Command == "recovery-resume"))
            { return new(request.Id, false, RootId: f.Endpoint.RootId, ErrorCode: "configuration_error"); }
            return f.Reply(request);
        });
        try
        {
            var w = f.Workflow; var prepared = (await w.PrepareAsync()).Preparation!; var result = await w.SubmitAsync(prepared with { Confirmed = true });
            Assert.Equal(clearFailure ? RecoveryWorkflowState.StorageBlocked : RecoveryWorkflowState.Completed, result.State);
            Assert.Equal(clearFailure, result.IsLocked); Assert.Equal(clearFailure, File.Exists(f.PendingPath));
            Assert.Equal(clearFailure ? "pending_clear_unverified" : null, result.ErrorCode); Assert.Equal(clearFailure ? null : "configuration_error", result.ReadErrorCode);
            Assert.Null(result.Observation); Assert.Single(f.Requests, request => request.Command == "recovery-resume");
        }
        finally { if (File.Exists(f.PendingPath)) { File.SetAttributes(f.PendingPath, FileAttributes.Normal); } }
    }

    [Fact]
    public async Task ChangedIntentBeforeConfirmedPreflightCreatesNoPendingOrMutation()
    {
        await using var f = new Fixture(); f.Start(f.Reply); var w = f.Workflow; var preparation = (await w.PrepareAsync()).Preparation!;
        f.State = f.State with { Revision = new string('b', 64) };
        var result = await w.SubmitAsync(preparation with { Confirmed = true });
        Assert.Equal(RecoveryWorkflowState.Ready, result.State); Assert.Equal("preparation_changed", result.ErrorCode); Assert.Null(result.OperationId);
        Assert.False(File.Exists(f.PendingPath)); Assert.DoesNotContain(f.Requests, request => request.Command == "recovery-resume"); Assert.True(result.CanPrepare);
    }

    [Fact]
    public async Task DifferentViewCannotDismissSomebodyElsesActiveUnconfirmedRead()
    {
        await using var f = new Fixture(); var entered = Signal(); var release = Signal(); using var owner = new CancellationTokenSource(); using var stranger = new CancellationTokenSource();
        f.Start(async request => { if (request.Command == "configuration") { entered.TrySetResult(); await release.Task.WaitAsync(f.Token); } return f.Reply(request); });
        var w = f.Workflow; var read = w.PrepareAsync(token: owner.Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(w.DismissUnconfirmed(stranger.Token)); Assert.True(w.Current.Reading); }
        finally { release.TrySetResult(); }
        var prepared = (await read).Preparation!; Assert.True(w.TryDiscardPreparation(prepared));
    }

    [Fact]
    public async Task CancellingLockedRefreshResetsReadingAndKeepsOriginalQueryable()
    {
        await using var f = new Fixture(); var entered = Signal(); var release = Signal(); var block = false;
        f.Start(async request =>
        {
            if (block && request.Command == "configuration") { entered.SetResult(); await release.Task.WaitAsync(f.Token); return null; }
            return request.Command == "recovery-resume" ? null : f.Reply(request);
        });
        var w = f.Workflow; var preparation = (await w.PrepareAsync()).Preparation!; var unknown = await w.SubmitAsync(preparation with { Confirmed = true });
        using var caller = new CancellationTokenSource(); block = true; var refresh = w.RefreshAsync(token: caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel(); var result = await refresh;
            Assert.True(result.IsLocked); Assert.False(result.Reading); Assert.False(result.IsBusy); Assert.True(result.CanQuery);
            Assert.Equal("cancelled", result.ReadErrorCode); Assert.Equal(unknown.OperationId, result.OperationId);
        }
        finally { block = false; release.TrySetResult(); }
        Assert.True((await w.QueryAsync()).IsLocked);
    }

    [Fact]
    public async Task KnownCompletionSurvivesItsViewsDismissedReadWithoutDiscardingAnotherView()
    {
        await using var f = new Fixture(); var entered = Signal(); var release = Signal(); var block = false;
        f.Start(async request => { if (block && request.Command == "configuration") { entered.SetResult(); await release.Task.WaitAsync(f.Token); } return f.Reply(request); });
        var w = f.Workflow; var preparation = (await w.PrepareAsync()).Preparation!; var completed = await w.SubmitAsync(preparation with { Confirmed = true });
        using var owner = new CancellationTokenSource(); using var stranger = new CancellationTokenSource(); block = true; var read = w.RefreshAsync(token: owner.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(w.DismissUnconfirmed(stranger.Token)); Assert.True(w.Current.Reading);
            Assert.True(w.DismissUnconfirmed(owner.Token)); Assert.Equal(RecoveryWorkflowState.Completed, w.Current.State);
            Assert.Equal(completed.OperationId, w.Current.OperationId); Assert.False(w.Current.IsBusy); Assert.False(File.Exists(f.PendingPath));
        }
        finally { block = false; release.TrySetResult(); }
        Assert.Equal(RecoveryWorkflowState.Completed, (await read).State); Assert.Equal(completed.OperationId, w.Current.OperationId);
    }

    [Fact]
    public async Task UnrelatedConfigurationRevisionChangeIsNotRecoveryOrTargetCas()
    {
        await using var f = new Fixture(); f.Start(request => f.Reply(request) with { Revision = request.Command == "configuration" ? f.Configuration["revision"]!.GetValue<string>() : f.State.Revision });
        var w = f.Workflow; var prepared = (await w.PrepareAsync()).Preparation!;
        f.Configuration["revision"] = new string('d', 64); f.Configuration["configuration"]!["tasks"]![0]!["description"] = "edited target/description, still same name admission";
        var result = await w.SubmitAsync(prepared with { Confirmed = true }); Assert.Equal(RecoveryWorkflowState.Completed, result.State);
        var sent = Assert.Single(f.Requests, request => request.Command == "recovery-resume"); Assert.Equal(prepared.Observation.RecoveryRevision, sent.ExpectedRevision);
        Assert.Equal(new string('d', 64), result.Observation!.ConfigurationRevision); Assert.False(File.Exists(f.PendingPath));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
