using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SystemAutorun.Cli;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

public sealed class SelfStartupCliPendingTests
{
    [Theory]
    [InlineData("valid", false)] [InlineData("valid", true)]
    [InlineData("corrupt", false)] [InlineData("corrupt", true)]
    [InlineData("directory", false)] [InlineData("directory", true)]
    public async Task ExistingOrUnreadableHintBlocksPreparationAndSubmissionBeforeConnecting(string state, bool prepare)
    {
        await using var fixture = new Fixture(); var original = AgentProtocol.NewOperationId();
        if (state == "valid") { _ = await new SelfStartupPendingStore(fixture.Paths).ReserveAsync(original, true); }
        else if (state == "corrupt") { File.WriteAllText(fixture.Pending, "{broken"); }
        else { Directory.CreateDirectory(fixture.Pending); }
        var bytes = File.Exists(fixture.Pending) ? File.ReadAllBytes(fixture.Pending) : null; var connections = 0;
        var result = await Run(new(_ => { connections++; return new Fake(request => Task.FromResult(fixture.Ack(request.OperationId!))); }), fixture,
            prepare ? ["startup", "self", "prepare", "enable"] : Set(AgentProtocol.NewOperationId()));
        Assert.Equal(state == "valid" ? 6 : 3, result.Exit); Assert.Equal(0, connections); Assert.Empty(fixture.Requests);
        Assert.Equal(state == "valid" ? "self_busy" : "pending_storage_unavailable", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(state == "valid" ? original : null, result.Json.GetProperty("operationId").GetString());
        Assert.DoesNotContain("reserved operationId:", result.Error); Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("data").ValueKind);
        if (bytes is not null) { Assert.Equal(bytes, File.ReadAllBytes(fixture.Pending)); } else { Assert.True(Directory.Exists(fixture.Pending)); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PublicationAndDiagnosticPrecedeFactoryAndOnlyOneExchangePreservesTheCallerId(bool explicitId)
    {
        await using var fixture = new Fixture(); var id = AgentProtocol.NewOperationId(); using var output = new StringWriter(); using var error = new StringWriter();
        var transport = new Fake(request => Task.FromResult(fixture.Ack(request.OperationId!))); string? reservedId = null;
        var app = new CliApplication(_ =>
        {
            var record = JsonSerializer.Deserialize<SelfStartupPendingOperation>(File.ReadAllText(fixture.Pending), AgentProtocol.JsonOptions)!;
            Assert.Equal(fixture.Endpoint.RootId, record.RootId); Assert.True(record.Enabled); reservedId = record.OperationId;
            Assert.Contains("reserved operationId: " + reservedId, error.ToString()); return transport;
        });
        var code = await app.RunAsync([.. Set(explicitId ? id : null), "--root", fixture.Paths.RootDirectory, "--json"], new StringReader(Input()), output, error);
        Assert.Equal(0, code); Assert.Equal(reservedId, Assert.Single(transport.Requests).OperationId);
        Assert.Equal(reservedId, Parse(output.ToString()).GetProperty("operationId").GetString()); if (explicitId) { Assert.Equal(id, reservedId); }
        Assert.False(File.Exists(fixture.Pending)); Assert.Empty(Directory.EnumerateFiles(fixture.Paths.RootDirectory, "*.tmp"));
    }

    [Theory]
    [InlineData("io")] [InlineData("timeout")] [InlineData("cancel")] [InlineData("permission")] [InlineData("argument")] [InlineData("foreign_unknown")]
    public async Task ArbitraryInjectedTransportExceptionsAreNotProofOfNoDispatch(string defect)
    {
        await using var fixture = new Fixture(); var id = AgentProtocol.NewOperationId(); byte[]? published = null;
        var transport = new Fake(_ =>
        {
            published = File.ReadAllBytes(fixture.Pending);
            throw defect switch { "io" => new IOException("opaque transport"), "timeout" => new TimeoutException("opaque transport"),
                "cancel" => new OperationCanceledException("opaque transport"), "permission" => new UnauthorizedAccessException("opaque transport"),
                "foreign_unknown" => new AgentOutcomeUnknownException(AgentProtocol.NewOperationId(), new IOException("foreign transport identity")),
                _ => new ArgumentException("opaque transport") };
        });
        var result = await Run(new(_ => transport), fixture, Set(id)); Assert.Equal(6, result.Exit);
        Assert.Equal("outcome_unknown", result.Json.GetProperty("error").GetProperty("code").GetString()); Assert.Equal(id, result.Json.GetProperty("operationId").GetString());
        Assert.Single(transport.Requests); Assert.NotNull(published); Assert.Equal(published, File.ReadAllBytes(fixture.Pending));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CallerCancellationCannotInterruptKnownOutcomeConditionalClearance(bool beforeSend)
    {
        await using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource(); var id = AgentProtocol.NewOperationId();
        var transport = new Fake(request => { cancellation.Cancel(); return Task.FromResult(fixture.Ack(request.OperationId!)); });
        var app = new CliApplication(_ =>
        {
            if (beforeSend) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            return transport;
        });
        var result = await Run(app, fixture, Set(id), cancellation.Token); Assert.Equal(beforeSend ? 6 : 0, result.Exit);
        Assert.Equal(beforeSend ? null : id, result.Json.GetProperty("operationId").GetString()); Assert.Equal(beforeSend ? 0 : 1, transport.Requests.Count);
        Assert.False(File.Exists(fixture.Pending));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DiagnosticFailureBeforeOrAfterExchangeNeverLosesItsSubmissionBoundary(bool afterExchange)
    {
        await using var fixture = new Fixture(); var id = AgentProtocol.NewOperationId(); using var output = new StringWriter();
        using var diagnostic = new BrokenDiagnostic(afterExchange ? 2 : 1); var transport = new Fake(request => Task.FromResult(fixture.Ack(request.OperationId!) with { Replayed = true }));
        var connected = false; var app = new CliApplication(_ => { connected = true; return transport; });
        var exit = await app.RunAsync([.. Set(id), "--root", fixture.Paths.RootDirectory, "--json"], new StringReader(Input()), output, diagnostic);
        var result = Parse(output.ToString()); Assert.Equal(afterExchange ? 6 : 3, exit); Assert.Equal(afterExchange, connected);
        Assert.Equal(afterExchange ? "outcome_unknown" : "connection_failed", result.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(afterExchange ? id : null, result.GetProperty("operationId").GetString()); Assert.Equal(afterExchange, File.Exists(fixture.Pending));
        Assert.Equal(afterExchange ? 1 : 0, transport.Requests.Count);
    }

    [Theory]
    [InlineData("success", false)] [InlineData("not_executed", false)] [InlineData("operation_id_conflict", true)]
    [InlineData("replay_success", true)] [InlineData("replay_not_executed", true)]
    public async Task OnlyFreshDefinitiveAcknowledgementsClearTheFreshCapability(string outcome, bool retained)
    {
        await using var fixture = new Fixture(); var id = AgentProtocol.NewOperationId(); byte[]? published = null;
        var transport = new Fake(request =>
        {
            published = File.ReadAllBytes(fixture.Pending); var success = outcome is "success" or "replay_success";
            return Task.FromResult(fixture.Ack(request.OperationId!) with { Ok = success, Data = success ? new { opaque = "historical" } : null,
                Error = success ? null : "refused", ErrorCode = success ? null : outcome == "operation_id_conflict" ? outcome : "not_executed", Replayed = outcome.StartsWith("replay", StringComparison.Ordinal) });
        });
        var result = await Run(new(_ => transport), fixture, Set(id)); Assert.Equal(outcome is "success" or "replay_success" ? 0 : 1, result.Exit);
        Assert.Equal(id, result.Json.GetProperty("operationId").GetString()); Assert.Single(transport.Requests); Assert.Equal(retained, File.Exists(fixture.Pending));
        if (retained) { Assert.Equal(published, File.ReadAllBytes(fixture.Pending)); Assert.Contains("pending retained", result.Error); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ChangedHintCannotBeClearedByAnEarlierSuccessfulRequest(bool directory)
    {
        await using var fixture = new Fixture(); var id = AgentProtocol.NewOperationId(); var replacement = AgentProtocol.NewOperationId(); byte[]? changed = null;
        var transport = new Fake(_ =>
        {
            if (directory) { File.Delete(fixture.Pending); Directory.CreateDirectory(fixture.Pending); }
            else
            {
                var record = JsonSerializer.Deserialize<SelfStartupPendingOperation>(File.ReadAllText(fixture.Pending), AgentProtocol.JsonOptions)!;
                File.WriteAllText(fixture.Pending, JsonSerializer.Serialize(record with { OperationId = replacement }, AgentProtocol.JsonOptions)); changed = File.ReadAllBytes(fixture.Pending);
            }
            return Task.FromResult(fixture.Ack(id));
        });
        var result = await Run(new(_ => transport), fixture, Set(id)); Assert.Equal(6, result.Exit);
        Assert.Equal("pending_clear_unverified", result.Json.GetProperty("error").GetProperty("code").GetString()); Assert.Equal(id, result.Json.GetProperty("operationId").GetString());
        Assert.Equal("ack", result.Json.GetProperty("requestId").GetString()); Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("data").ValueKind); Assert.Single(transport.Requests);
        if (directory) { Assert.True(Directory.Exists(fixture.Pending)); } else { Assert.Equal(changed, File.ReadAllBytes(fixture.Pending)); }
    }

    [Fact]
    public async Task LocalLeaseContentionRespectsTheSelectedShortBudgetWithoutSending()
    {
        await using var fixture = new Fixture(); await using var held = await RootWriteLease.AcquireAsync(fixture.Paths);
        var connected = false; var elapsed = Stopwatch.StartNew();
        var result = await Run(new(_ => { connected = true; throw new IOException("must not connect"); }), fixture, [.. Set(), "--timeout", "0.1"]);
        Assert.Equal(3, result.Exit); Assert.Equal("pending_storage_unavailable", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.False(connected); Assert.Null(result.Json.GetProperty("operationId").GetString()); Assert.False(File.Exists(fixture.Pending)); Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConsoleActualDisconnectPersistsOriginalIdAndAlreadyOpenGuiWorkflowCannotPrepareOrUnlockByQuery()
    {
        await using var fixture = new Fixture(); var workflow = SelfStartupWorkflow.ForRoot(fixture.Paths); Assert.True((await workflow.OpenAsync()).CanPrepare);
        fixture.Handler = request => Task.FromResult<AgentResponse?>(request.Command == "startup-self-set" ? null : new(request.Id, true, null, RootId: fixture.Endpoint.RootId));
        var id = AgentProtocol.NewOperationId(); var result = await Console(fixture, Set(id)); Assert.Equal(6, result.Exit);
        Assert.Equal("outcome_unknown", result.Json.GetProperty("error").GetProperty("code").GetString()); Assert.Equal(id, result.Json.GetProperty("operationId").GetString());
        var bytes = File.ReadAllBytes(fixture.Pending); var prepared = await workflow.PrepareAsync(false);
        Assert.Equal(SelfStartupWorkflowState.OutcomeUnknown, prepared.State); Assert.Equal(id, prepared.OperationId); Assert.False(prepared.CanConfirm);
        Assert.Equal(SelfStartupReceiptState.NotFound, (await workflow.QueryAsync()).Observation!.State); Assert.False(workflow.Current.CanPrepare);
        var query = await Console(fixture, ["operation", "show", id]); Assert.Equal(0, query.Exit);
        var repeat = await Console(fixture, Set(id)); Assert.Equal(6, repeat.Exit); Assert.Equal("self_busy", repeat.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Pending)); Assert.Equal(new[] { "startup-self-set", "operation", "operation" }, fixture.Requests.Select(value => value.Command));
    }

    [Fact]
    public async Task GuiOriginatedPendingBlocksIndependentConsoleWithoutNativeOrJournalActivity()
    {
        await using var fixture = new Fixture(); var workflow = SelfStartupWorkflow.ForRoot(fixture.Paths);
        var prepared = await workflow.PrepareAsync(true); Assert.True(prepared.CanConfirm);
        fixture.Handler = request => Task.FromResult<AgentResponse?>(request.Command == "startup-self-set" ? null : fixture.Ack(request.OperationId!) with { Id = request.Id });
        var unknown = await workflow.SubmitAsync(prepared.Preparation! with { Confirmed = true }); Assert.Equal(SelfStartupWorkflowState.OutcomeUnknown, unknown.State);
        var bytes = File.ReadAllBytes(fixture.Pending); var count = fixture.Requests.Count;
        var blocked = await Console(fixture, Set(AgentProtocol.NewOperationId())); Assert.Equal(6, blocked.Exit);
        Assert.Equal(unknown.OperationId, blocked.Json.GetProperty("operationId").GetString()); Assert.Equal("self_busy", blocked.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(count, fixture.Requests.Count); Assert.Equal(bytes, File.ReadAllBytes(fixture.Pending));
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.RootDirectory, "*.operation.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TwoIndependentConsolesCannotExchangeWhileTheFirstOriginalRequestIsUnresolved()
    {
        await using var fixture = new Fixture(); var received = new TaskCompletionSource<AgentRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler = async request => { received.TrySetResult(request); await release.Task.WaitAsync(fixture.Token); return fixture.Ack(request.OperationId!) with { Id = request.Id }; };
        var first = Console(fixture, Set(AgentProtocol.NewOperationId())); var second = Console(fixture, Set(AgentProtocol.NewOperationId()));
        try
        {
            var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(10)); var pending = File.ReadAllBytes(fixture.Pending);
            var loser = await (await Task.WhenAny(first, second).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(6, loser.Exit); Assert.Equal("self_busy", loser.Json.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(request.OperationId, loser.Json.GetProperty("operationId").GetString()); Assert.Equal(pending, File.ReadAllBytes(fixture.Pending)); Assert.Single(fixture.Requests);
        }
        finally { release.TrySetResult(); await Task.WhenAll(first, second); }
        var completed = await Task.WhenAll(first, second);
        Assert.Equal(new[] { 0, 6 }, completed.Select(value => value.Exit).Order()); Assert.False(File.Exists(fixture.Pending)); Assert.Single(fixture.Requests);
    }

    private static string[] Set(string? id = null) => ["startup", "self", "set", "--file", "-", "--yes", .. id is null ? Array.Empty<string>() : new[] { "--operation-id", id }];
    private static string Input() => JsonSerializer.Serialize(new SelfStartupMutationRequest { SchemaVersion = 1, Enabled = true, Confirmed = false,
        TargetContextId = new string('a', 64), ExpectedRegistration = new(false) }, AgentProtocol.JsonOptions);
    private static async Task<Result> Run(CliApplication app, Fixture fixture, string[] args, CancellationToken token = default)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await app.RunAsync([.. args, "--root", fixture.Paths.RootDirectory, "--json"], new StringReader(Input()), output, error, token);
        return new(exit, Parse(output.ToString()), error.ToString());
    }
    private static async Task<Result> Console(Fixture fixture, string[] args)
    {
        var path = typeof(SelfStartupCliPendingTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(value => value.Key == "CliExecutablePath").Value!;
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args.Concat(["--root", fixture.Paths.RootDirectory, "--json"])) { info.ArgumentList.Add(argument); }
        using var process = Process.Start(info)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(Input()); process.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, Parse(await stdout.WaitAsync(deadline.Token)), await stderr.WaitAsync(deadline.Token));
        }
        finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
    }
    private static JsonElement Parse(string text) { using var value = JsonDocument.Parse(text); return value.RootElement.Clone(); }
    private sealed record Result(int Exit, JsonElement Json, string Error);
    private sealed class BrokenDiagnostic(int failOn) : StringWriter
    {
        private int _writes;
        public override Task WriteLineAsync(string? value) => ++_writes == failOn ? Task.FromException(new IOException("diagnostic unavailable")) : base.WriteLineAsync(value);
    }
    private sealed class Fake(Func<CliRequest, Task<AgentResponse>> send) : ICliTransport
    {
        internal List<CliRequest> Requests { get; } = [];
        public Task<AgentResponse> SendAsync(CliRequest request, CancellationToken token) { Requests.Add(request); return send(request); }
    }
    // Real CurrentUserOnly Pipe; deliberately no native adapter, dispatcher, Journal,
    // runtime host, public target override or workload executor in this fixture.
    private sealed class Fixture : IAsyncDisposable
    {
        internal ProjectPaths Paths { get; }
        internal AgentEndpoint Endpoint { get; }
        internal string Pending => Path.Combine(Paths.RootDirectory, SelfStartupPendingStore.FileName);
        internal ConcurrentQueue<AgentRequest> Requests { get; } = new();
        internal Func<AgentRequest, Task<AgentResponse?>> Handler { get; set; }
        private readonly CancellationTokenSource _stop = new(); private readonly Task _server;
        internal CancellationToken Token => _stop.Token;
        internal Fixture()
        {
            var root = Path.Combine(Path.GetTempPath(), "SystemAutorun-cli-pending-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Paths = new(root); File.WriteAllText(Paths.ConfigPath, "version: 1\ntasks: []\n"); Endpoint = AgentProtocol.ForRoot(root);
            Handler = request => Task.FromResult<AgentResponse?>(new(request.Id, true,
                request.Command == "startup-self-prepare" ? JsonSerializer.Deserialize<SelfStartupMutationRequest>(Input(), AgentProtocol.JsonOptions) : new { opaque = "historical" }, RootId: Endpoint.RootId, OperationId: request.OperationId));
            _server = Serve();
        }
        internal AgentResponse Ack(string id) => new("ack", true, new { opaque = "historical" }, RootId: Endpoint.RootId, OperationId: id);
        private async Task Serve()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await using var pipe = new NamedPipeServerStream(Endpoint.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_stop.Token); using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                    var request = JsonSerializer.Deserialize<AgentRequest>((await reader.ReadLineAsync(_stop.Token))!, AgentProtocol.JsonOptions)!; Requests.Enqueue(request);
                    var response = await Handler(request);
                    if (response is not null)
                    {
                        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(response, AgentProtocol.JsonOptions));
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) when (_stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); try { await _server; }
            finally
            {
                _stop.Dispose(); await using (await RootWriteLease.AcquireAsync(Paths, default, TimeSpan.FromSeconds(3))) { }
                var root = new DirectoryInfo(Paths.RootDirectory);
                if (root.FullName != Paths.RootDirectory || root.Attributes.HasFlag(FileAttributes.ReparsePoint)) { throw new IOException("Unexpected fixture root."); }
                Directory.Delete(root.FullName, recursive: true);
            }
        }
    }
}
