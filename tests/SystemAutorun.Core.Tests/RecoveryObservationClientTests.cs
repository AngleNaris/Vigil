using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Recovery;

namespace SystemAutorun.Core.Tests;

public sealed class RecoveryObservationClientTests
{
    [Fact]
    public async Task ExplicitLiveFieldsProduceCapturedEligibilityNotHealthOrMutation()
    {
        await using var f = new RecoveryWorkflowPipeFixture(); f.Start(f.Reply);
        var observed = await new AgentRecoveryObservationClient(new(f.Root.Path), f.Paths, "MEDIA").ReadAsync();
        Assert.True(observed.CanResume); Assert.True(observed.Initialized); Assert.True(observed.Eligible); Assert.Null(observed.EligibilityReason);
        Assert.Equal("MEDIA", observed.TaskName); Assert.Equal(f.Endpoint.RootId, observed.RootId); Assert.Equal(CurrentUserIdentity.Key, observed.SubjectKey);
        Assert.Equal(RecoveryWorkflowPipeFixture.ConfigRevision, observed.ConfigurationRevision); Assert.Equal(f.State.Revision, observed.RecoveryRevision);
        Assert.Equal("media", observed.Entry!.TaskName); Assert.Equal(new[] { "configuration", "recovery-state" }, f.Requests.Select(request => request.Command));
        Assert.All(f.Requests, request => { Assert.Null(request.OperationId); Assert.Null(request.ExpectedRevision); Assert.Null(request.Payload); });
        Assert.Single(Directory.GetFileSystemEntries(f.Root.Path));
    }

    [Theory]
    [InlineData("disabled", "disabled")] [InlineData("system", "unsupported_scope")] [InlineData("boot", "unsupported_scope")]
    [InlineData("manual", "unsupported_scope")] [InlineData("missing", "not_found")] [InlineData("uninitialized", null)] [InlineData("unpaused", null)]
    public async Task IneligibleOrUninitializedFactsNeverManufactureResume(string kind, string? reason)
    {
        await using var f = new RecoveryWorkflowPipeFixture(); var task = f.Configuration["configuration"]!["tasks"]![0]!;
        if (kind == "disabled") { task["enabled"] = false; } if (kind == "system") { task["launch"]!["runAs"] = "system"; }
        if (kind is "boot" or "manual") { task["launch"]!["trigger"] = kind; }
        if (kind == "missing") { task["name"] = "another"; }
        if (kind == "uninitialized") { f.State = new(f.State.Revision, Array.Empty<RecoveryTaskState>(), false); }
        if (kind == "unpaused") { f.State = f.State with { Tasks = new[] { new RecoveryTaskState("media") } }; }
        f.Start(f.Reply); var result = await new AgentRecoveryObservationClient(new(f.Root.Path), f.Paths, "media").ReadAsync();
        Assert.False(result.CanResume); Assert.Equal(reason, result.EligibilityReason); Assert.DoesNotContain(f.Requests, request => request.OperationId is not null);
    }

    [Theory]
    [InlineData("revision")] [InlineData("revision-envelope")] [InlineData("time")] [InlineData("missing-root")] [InlineData("root")]
    [InlineData("tasks")] [InlineData("name")] [InlineData("duplicate-name")] [InlineData("missing-enabled")] [InlineData("enabled-type")]
    [InlineData("missing-runAs")] [InlineData("runAs-type")] [InlineData("runAs-unknown")] [InlineData("missing-trigger")]
    [InlineData("trigger-type")] [InlineData("launch-type")] [InlineData("duplicate-case")] [InlineData("array")]
    public async Task MissingOrMalformedAuthorityFieldsCannotBecomeDefaultEligibility(string defect)
    {
        await using var f = new RecoveryWorkflowPipeFixture(); var node = f.Configuration; var configuration = node["configuration"]!.AsObject(); var tasks = configuration["tasks"]!.AsArray(); var task = tasks[0]!.AsObject(); var launch = task["launch"]!.AsObject();
        switch (defect)
        {
            case "revision": node["revision"] = "bad"; break; case "time": node["capturedAt"] = DateTimeOffset.MinValue; break;
            case "missing-root": configuration.Remove("rootDirectory"); break; case "root": configuration["rootDirectory"] = "foreign-root"; break;
            case "tasks": configuration["tasks"] = null; break; case "name": task["name"] = "\n"; break;
            case "duplicate-name": tasks.Add(task.DeepClone()); break; case "missing-enabled": task.Remove("enabled"); break;
            case "enabled-type": task["enabled"] = "true"; break; case "missing-runAs": launch.Remove("runAs"); break;
            case "runAs-type": launch["runAs"] = 0; break; case "runAs-unknown": launch["runAs"] = "admin"; break;
            case "missing-trigger": launch.Remove("trigger"); break; case "trigger-type": launch["trigger"] = 1; break;
            case "launch-type": task["launch"] = new JsonArray(); break; case "duplicate-case": task["Enabled"] = true; break;
        }
        f.Start(request => f.Reply(request) with { Data = defect == "array" ? Array.Empty<object>() : f.Configuration,
            Revision = defect == "revision-envelope" ? new string('0', 64) : RecoveryWorkflowPipeFixture.ConfigRevision });
        var error = await Assert.ThrowsAsync<AgentCommandException>(() => new AgentRecoveryObservationClient(new(f.Root.Path), f.Paths, "media").ReadAsync());
        Assert.Equal("protocol_mismatch", error.Code); Assert.Single(f.Requests); Assert.Null(f.Requests.Single().OperationId);
    }

    [Fact]
    public async Task VersionTwoAndOpaqueUnrelatedTaskFieldsDoNotBecomeEligibilityDependencies()
    {
        await using var f = new RecoveryWorkflowPipeFixture(); f.Configuration["configuration"]!["version"] = 2;
        var selected = f.Configuration["configuration"]!["tasks"]![0]!.AsObject(); selected.Remove("type");
        f.Configuration["configuration"]!["tasks"]!.AsArray().Add(new JsonObject { ["name"] = "unrelated", ["futureShape"] = true });
        f.Start(f.Reply); Assert.True((await new AgentRecoveryObservationClient(new(f.Root.Path), f.Paths, "media").ReadAsync()).CanResume);
    }

    [Theory]
    [InlineData("permission_denied")] [InlineData("configuration_error")]
    public async Task ActualBusinessReadErrorsRemainReadErrors(string code)
    {
        await using var f = new RecoveryWorkflowPipeFixture(); f.Start(request => new(request.Id, false, RootId: f.Endpoint.RootId, ErrorCode: code));
        Assert.Equal(code, (await Assert.ThrowsAsync<AgentCommandException>(() => new AgentRecoveryObservationClient(new(f.Root.Path), f.Paths, "media").ReadAsync())).Code);
        Assert.Single(f.Requests);
    }
}

// Synthetic business data over a real CurrentUserOnly Pipe. Handler assertions are
// observed by DisposeAsync; none run inside a Journal that could swallow them.
internal sealed class RecoveryWorkflowPipeFixture : IAsyncDisposable
{
    internal const string ConfigRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal TempDirectory Root { get; } = new();
    internal ProjectPaths Paths { get; }
    internal AgentEndpoint Endpoint { get; }
    internal JsonObject Configuration { get; }
    internal RecoveryControlSnapshot State { get; set; }
    internal ConcurrentQueue<AgentRequest> Requests { get; } = new();
    internal CancellationToken Token => _stop.Token;
    internal RecoveryWorkflow Workflow => RecoveryWorkflow.ForTask(Paths, "media");
    internal string PendingPath => Path.Combine(Paths.RootDirectory, new RecoveryPendingStore(Paths, "media").FileName);
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
    private Task? _server;
    internal RecoveryWorkflowPipeFixture()
    {
        Root.Write("autorun.yaml", "version: 1\ntasks: []\n"); Paths = ProjectPaths.Resolve(Root.Path); Endpoint = AgentProtocol.ForRoot(Paths.RootDirectory);
        Configuration = JsonSerializer.SerializeToNode(new { revision = ConfigRevision, capturedAt = DateTimeOffset.UtcNow,
            configuration = new { version = 1, rootDirectory = Paths.RootDirectory, tasks = new[] { new { name = "media", type = "windows", enabled = true, launch = new { runAs = "current_user", trigger = "logon" } } } } })!.AsObject();
        State = new(ConfigRevision, new[] { new RecoveryTaskState("media", true, DateTimeOffset.UtcNow, IntentId: AgentProtocol.NewOperationId()) }, true);
    }
    internal AgentResponse Reply(AgentRequest request) => request.Command switch
    {
        "configuration" => new(request.Id, true, Configuration, RootId: Endpoint.RootId, Revision: ConfigRevision),
        "recovery-state" => new(request.Id, true, State, RootId: Endpoint.RootId, Revision: State.Revision),
        "recovery-resume" => Resume(request),
        _ => new(request.Id, false, RootId: Endpoint.RootId, ErrorCode: "operation_not_found")
    };
    private AgentResponse Resume(AgentRequest request)
    {
        State = new(new string('b', 64), new[] { new RecoveryTaskState("media", IntentId: AgentProtocol.NewOperationId()) }, true);
        return new(request.Id, true, State, RootId: Endpoint.RootId, Revision: State.Revision, OperationId: request.OperationId);
    }
    internal void Start(Func<AgentRequest, AgentResponse?> handler) => Start(request => Task.FromResult(handler(request)));
    internal void Start(Func<AgentRequest, Task<AgentResponse?>> handler)
    {
        _server = Task.Run(async () =>
        {
            while (true)
            {
                await using var pipe = new NamedPipeServerStream(Endpoint.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token); using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(_stop.Token); if (line is null) { continue; }
                var request = JsonSerializer.Deserialize<AgentRequest>(line, AgentProtocol.JsonOptions)!; Requests.Enqueue(request); var response = await handler(request);
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
