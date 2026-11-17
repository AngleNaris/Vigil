using System.IO;
using System.Text.Json;
using SystemAutorun.Cli;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Recovery;
using RealServer = SystemAutorun.IntegrationTests.ConfigurationControlPlaneTests.ServerFixture;

namespace SystemAutorun.IntegrationTests;

public sealed partial class RecoveryCliPendingTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RecoveryPublicationAndDiagnosticPrecedeFactoryAndPreserveTheExactRequest(bool explicitId)
    {
        using var fixture = new Fixture(); using var output = new StringWriter(); using var error = new StringWriter();
        var id = AgentProtocol.NewOperationId(); string? reserved = null; var calls = 0;
        var app = new CliApplication(root =>
        {
            var record = JsonSerializer.Deserialize<RecoveryPendingOperation>(File.ReadAllText(fixture.PendingPath), AgentProtocol.JsonOptions)!;
            reserved = record.OperationId; Assert.Equal(Revision, record.ExpectedRevision); Assert.Equal("media", record.TaskName);
            Assert.Contains("reserved operationId: " + reserved, error.ToString());
            return new AsyncFake(request => { calls++; Assert.Equal(reserved, request.OperationId); return Task.FromResult(RecoveryAck(root, request)); });
        });
        var code = await app.RunAsync([.. Resume("media", Revision, explicitId ? id : null), "--root", fixture.Root, "--json"], new StringReader(""), output, error);
        Assert.Equal(0, code); Assert.Equal(1, calls); Assert.NotNull(reserved); if (explicitId) { Assert.Equal(id, reserved); }
        var json = Parse(output.ToString()); Assert.Equal(reserved, json.GetProperty("operationId").GetString());
        Assert.Equal("preserved", json.GetProperty("data").GetProperty("futureField").GetString()); Assert.False(File.Exists(fixture.PendingPath));
    }

    [Theory]
    [InlineData("io")] [InlineData("timeout")] [InlineData("cancel")] [InlineData("permission")]
    [InlineData("argument")] [InlineData("foreign-unknown")] [InlineData("command")]
    public async Task ArbitraryInjectedSendFailureRetainsOnlyOurReservedIdAndBlocksReopen(string kind)
    {
        using var fixture = new Fixture(); var id = AgentProtocol.NewOperationId(); var calls = 0;
        var result = await Run(new(_ => new AsyncFake(_ => { calls++; return Task.FromException<AgentResponse>(Failure(kind)); })), fixture.Root, Resume("media", Revision, id));
        Assert.Equal(6, result.Exit); Assert.Equal("outcome_unknown", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(id, result.Json.GetProperty("operationId").GetString()); Assert.Equal(1, calls);
        var bytes = File.ReadAllBytes(fixture.PendingPath); var pending = await fixture.Pending.ReadAsync(); Assert.Equal(id, pending.Operation!.OperationId);
        Assert.Equal(Revision, pending.Operation.ExpectedRevision); var workflow = RecoveryWorkflow.ForTask(fixture.Paths, "MEDIA");
        Assert.Equal(RecoveryWorkflowState.OutcomeUnknown, (await workflow.OpenAsync()).State); Assert.Equal(id, workflow.Current.OperationId);
        var blocked = await Run(new(_ => throw new InvalidOperationException("Must not reconnect.")), fixture.Root, Resume("media", Revision));
        Assert.Equal(6, blocked.Exit); Assert.Equal(id, blocked.Json.GetProperty("operationId").GetString()); Assert.Equal(bytes, File.ReadAllBytes(fixture.PendingPath));
    }

    [Theory]
    [InlineData("replay")] [InlineData("error")] [InlineData("not-executed")]
    [InlineData("wrong-root")] [InlineData("wrong-id")] [InlineData("wrong-name")] [InlineData("malformed")]
    public async Task ReplayErrorsAndMalformedAcknowledgementsNeverClearRecoveryPending(string kind)
    {
        using var fixture = new Fixture(); var id = AgentProtocol.NewOperationId(); var calls = 0;
        var result = await Run(new(root => new AsyncFake(request =>
        {
            calls++; var ack = RecoveryAck(root, request);
            return Task.FromResult(kind switch
            {
                "replay" => ack with { Replayed = true },
                "error" or "not-executed" => ack with { Ok = false, Data = null, Error = "fixture refusal", ErrorCode = kind == "error" ? "revision_conflict" : "not_executed" },
                "wrong-root" => ack with { RootId = new string('b', 64) },
                "wrong-id" => ack with { OperationId = AgentProtocol.NewOperationId() },
                "wrong-name" => RecoveryAck(root, request with { Name = "different" }),
                _ => ack with { Data = new { revision = Revision } }
            });
        })), fixture.Root, Resume("media", Revision, id));
        Assert.Equal(kind == "replay" ? 0 : kind == "error" ? 5 : kind == "not-executed" ? 1 : 6, result.Exit);
        Assert.Equal(id, result.Json.GetProperty("operationId").GetString()); Assert.Equal(id, (await fixture.Pending.ReadAsync()).Operation!.OperationId); Assert.Equal(1, calls);
        Assert.Contains(kind is "replay" or "error" or "not-executed" ? "pending retained:" : "reserved operationId:", result.Error);
    }

    [Theory]
    [InlineData("io")] [InlineData("permission")] [InlineData("cancel")] [InlineData("foreign-unknown")]
    public async Task FactoryFailureIsKnownBeforeDispatchAndCannotForgeARecoveryId(string kind)
    {
        using var fixture = new Fixture(); var result = await Run(new(_ => throw Failure(kind)), fixture.Root, Resume("media", Revision));
        Assert.NotEqual(0, result.Exit); Assert.Null(result.Json.GetProperty("operationId").GetString()); Assert.False(File.Exists(fixture.PendingPath));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DiagnosticFailureBeforeDispatchClearsButFailureAfterReplayRetains(bool afterReplay)
    {
        using var fixture = new Fixture(); using var output = new StringWriter(); using var diagnostic = new FailingRecoveryDiagnostic(afterReplay ? 2 : 1);
        var id = AgentProtocol.NewOperationId(); var factories = 0; var sends = 0;
        var app = new CliApplication(root => { factories++; return new AsyncFake(request => { sends++; return Task.FromResult(RecoveryAck(root, request) with { Replayed = true }); }); });
        var exit = await app.RunAsync([.. Resume("media", Revision, id), "--root", fixture.Root, "--json"], new StringReader(""), output, diagnostic);
        var json = Parse(output.ToString()); Assert.Equal(afterReplay ? 6 : 3, exit); Assert.Equal(afterReplay ? id : null, json.GetProperty("operationId").GetString());
        Assert.Equal(afterReplay ? 1 : 0, factories); Assert.Equal(afterReplay ? 1 : 0, sends); Assert.Equal(afterReplay, File.Exists(fixture.PendingPath));
    }

    [Fact]
    public async Task FreshAckWithChangedPendingBytesReportsUnverifiedClearWithoutDeletingTheReplacement()
    {
        using var fixture = new Fixture(); var id = AgentProtocol.NewOperationId(); var replacement = AgentProtocol.NewOperationId(); byte[]? changed = null;
        var result = await Run(new(root => new AsyncFake(request =>
        {
            var record = JsonSerializer.Deserialize<RecoveryPendingOperation>(File.ReadAllText(fixture.PendingPath), AgentProtocol.JsonOptions)!;
            changed = JsonSerializer.SerializeToUtf8Bytes(record with { OperationId = replacement }, AgentProtocol.JsonOptions); File.WriteAllBytes(fixture.PendingPath, changed);
            return Task.FromResult(RecoveryAck(root, request));
        })), fixture.Root, Resume("media", Revision, id));
        Assert.Equal(6, result.Exit); Assert.Equal("pending_clear_unverified", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(id, result.Json.GetProperty("operationId").GetString()); Assert.Equal(changed, File.ReadAllBytes(fixture.PendingPath));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedOrCancelledReservationHasNoFactoryCallOrInventedRecoveryId(bool cancel)
    {
        using var fixture = new Fixture(); await using var held = await RootWriteLease.AcquireAsync(fixture.Paths);
        using var stop = new CancellationTokenSource(); if (cancel) { stop.CancelAfter(TimeSpan.FromMilliseconds(100)); } var factories = 0;
        var result = await Run(new(_ => { factories++; throw new InvalidOperationException("No reservation capability."); }), fixture.Root, Resume("media", Revision), stop.Token);
        Assert.Equal(cancel ? 6 : 3, result.Exit); Assert.Null(result.Json.GetProperty("operationId").GetString());
        Assert.Equal(0, factories); Assert.False(File.Exists(fixture.PendingPath)); Assert.DoesNotContain("reserved operationId:", result.Error);
    }

    [Fact]
    public async Task ConcreteOfflinePipeFailureReleasesOurPreSendReservation()
    {
        using var fixture = new Fixture(); var result = await Console(fixture.Root, Resume("media", Revision));
        Assert.Equal(6, result.Exit); Assert.Null(result.Json.GetProperty("operationId").GetString()); Assert.False(File.Exists(fixture.PendingPath));
        Assert.Contains("reserved operationId:", result.Error);
    }

    [Fact]
    public async Task ProductionAgentServerConsoleFreshAckClearsAndAllowsANewExplicitIntent()
    {
        await using var host = new RealServer(); await File.WriteAllTextAsync(host.Paths.ConfigPath, RecoveryWorkflowIntegrationTests.Configuration);
        var store = new RecoveryControlStore(host.Paths); var initial = await store.ReadAsync(); var before = await store.InitializePausedAsync(new[] { "media" }, initial.Revision);
        var config = File.ReadAllBytes(host.Paths.ConfigPath); var firstId = AgentProtocol.NewOperationId();
        var first = await Console(host.Root, Resume("media", before.Revision, firstId)); Assert.Equal(0, first.Exit);
        var pending = new RecoveryPendingStore(host.Paths, "media"); Assert.Equal(RecoveryPendingState.Missing, (await pending.ReadAsync()).State);
        Assert.Equal(OperationState.Completed, (await new AgentOperationClient(host.Client).ReadAsync(firstId))!.State); Assert.False((await store.ReadAsync()).Tasks.Single().Paused);
        var next = await store.SetPausedAsync("media", true, recordIntent: true); var secondId = AgentProtocol.NewOperationId();
        Assert.Equal(0, (await Console(host.Root, Resume("media", next.Revision, secondId))).Exit);
        Assert.Equal(2, Directory.GetFiles(host.Root, "*.operation.json", SearchOption.AllDirectories).Length);
        Assert.Equal(RecoveryPendingState.Missing, (await pending.ReadAsync()).State); Assert.Equal(config, File.ReadAllBytes(host.Paths.ConfigPath)); Assert.Empty(host.ObservedErrors);
    }

    [Fact]
    public async Task ConsoleDroppedAckReopensGuiWithTheExactOriginalIdAndNeverResubmits()
    {
        await using var host = new RecoveryWorkflowDispatchFixture(); await host.InitializeAsync(); host.DropResumeAck = true;
        var revision = (await host.Store.ReadAsync()).Revision; var id = AgentProtocol.NewOperationId();
        var first = await Console(host.Root, Resume("media", revision, id)); Assert.Equal(6, first.Exit);
        var pending = new RecoveryPendingStore(host.Paths, "media"); Assert.Equal(id, (await pending.ReadAsync()).Operation!.OperationId);
        var bytes = File.ReadAllBytes(Path.Combine(host.Root, pending.FileName)); var workflow = host.Workflow;
        Assert.Equal(RecoveryWorkflowState.OutcomeUnknown, (await workflow.OpenAsync()).State); Assert.Equal(id, workflow.Current.OperationId);
        Assert.Equal(RecoveryReceiptState.CompletedHistory, (await workflow.QueryAsync()).Receipt!.State); Assert.True(workflow.Current.IsLocked);
        var mutations = host.Requests.Count(request => request.Command == "recovery-resume");
        var blocked = await Console(host.Root, Resume("media", revision)); Assert.Equal(6, blocked.Exit); Assert.Equal(id, blocked.Json.GetProperty("operationId").GetString());
        Assert.Equal(mutations, host.Requests.Count(request => request.Command == "recovery-resume")); Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(host.Root, pending.FileName)));
        Assert.Equal(0, host.WorkloadCalls); Assert.Empty(host.Errors);
    }

    [Fact]
    public async Task TwoIndependentConsolesOnlyDispatchTheWinnerOfThePendingReservation()
    {
        await using var host = new RecoveryWorkflowDispatchFixture(); await host.InitializeAsync(); var revision = (await host.Store.ReadAsync()).Revision;
        var received = new TaskCompletionSource<AgentRequest>(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AfterDispatch = async (request, _) => { if (request.Command == "recovery-resume") { received.TrySetResult(request); await release.Task.WaitAsync(host.Token); } };
        var first = Console(host.Root, Resume("media", revision, AgentProtocol.NewOperationId()), 10); var second = Console(host.Root, Resume("media", revision, AgentProtocol.NewOperationId()), 10);
        try
        {
            var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var loser = await (await Task.WhenAny(first, second).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(6, loser.Exit); Assert.Equal("recovery_busy", loser.Json.GetProperty("error").GetProperty("code").GetString()); Assert.Equal(request.OperationId, loser.Json.GetProperty("operationId").GetString());
            Assert.Single(host.Requests, row => row.Command == "recovery-resume");
        }
        finally { release.TrySetResult(); await Task.WhenAll(first, second); }
        Assert.Equal(new[] { 0, 6 }, (await Task.WhenAll(first, second)).Select(value => value.Exit).Order());
        Assert.Equal(RecoveryPendingState.Missing, (await new RecoveryPendingStore(host.Paths, "media").ReadAsync()).State); Assert.Equal(0, host.WorkloadCalls); Assert.Empty(host.Errors);
    }

    private static AgentResponse RecoveryAck(string root, CliRequest request) => new("ack", true,
        new { revision = Revision, initialized = true, tasks = new[] { new RecoveryTaskState(request.Name!, IntentId: AgentProtocol.NewOperationId()) }, futureField = "preserved" },
        Revision: Revision, RootId: AgentProtocol.ForRoot(root).RootId, OperationId: request.OperationId);
    private static Exception Failure(string kind) => kind switch
    {
        "io" => new IOException("fixture IO"), "timeout" => new TimeoutException("fixture timeout"), "cancel" => new OperationCanceledException("fixture cancel"),
        "permission" => new UnauthorizedAccessException("fixture permission"), "argument" => new ArgumentException("fixture argument"),
        "foreign-unknown" => new AgentOutcomeUnknownException(AgentProtocol.NewOperationId(), new IOException("foreign identity"), "bounded-request"),
        _ => new AgentCommandException("not_executed", "injected command exception is not pre-send proof", operationId: AgentProtocol.NewOperationId())
    };
    private sealed class AsyncFake(Func<CliRequest, Task<AgentResponse>> send) : ICliTransport
    { public Task<AgentResponse> SendAsync(CliRequest request, CancellationToken token) => send(request); }
    private sealed class FailingRecoveryDiagnostic(int failOn) : StringWriter
    {
        private int _writes;
        public override Task WriteLineAsync(string? value) => ++_writes == failOn ? Task.FromException(new IOException("fixture diagnostic")) : base.WriteLineAsync(value);
    }
}
