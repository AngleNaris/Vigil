using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Cli;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using Host = SystemAutorun.IntegrationTests.SelfStartupAgentTests.Host;

namespace SystemAutorun.IntegrationTests;

public sealed class SelfStartupCliTests
{
    [Theory]
    [InlineData("empty", true, 1)] [InlineData("empty", false, 0)] [InlineData("missing", true, 2)] [InlineData("missing", false, 0)]
    [InlineData("same", true, 0)] [InlineData("same", false, 1)]
    public async Task IndependentConsolePreparesReviewsFileConfirmsAndQueriesOriginalJournalWithoutLaunchingTargets(string state, bool enabled, int calls)
    {
        await using var host = new Host(); if (state == "missing") { host.Owned.Native.DeleteRun(); }
        if (state == "same") { host.Owned.Native.Set(1, host.Owned.TargetBytes); }
        var storage = host.Storage(); var preview = await Console(host, ["startup", "self", "prepare", enabled ? "enable" : "disable"]);
        Assert.Equal(0, preview.Exit); Assert.Empty(preview.Error); AssertStorage(storage, host.Storage());
        var input = preview.Json.GetProperty("data"); Assert.False(input.GetProperty("confirmed").GetBoolean()); Assert.Null(preview.Json.GetProperty("operationId").GetString());
        var file = Path.Combine(host.Owned.Package.Root, "reviewed-self.json"); File.WriteAllText(file, input.GetRawText()); var bytes = File.ReadAllBytes(file);
        var id = AgentProtocol.NewOperationId(); string[] command = ["startup", "self", "set", "--file", file, "--yes", "--operation-id", id];
        var result = await Console(host, command); Assert.Equal(0, result.Exit); Assert.Equal(id, result.Json.GetProperty("operationId").GetString()); Assert.Contains(id, result.Error);
        Assert.True(result.Json.GetProperty("data").GetProperty("protocolCompleted").GetBoolean()); Assert.Equal(calls, result.Json.GetProperty("data").GetProperty("calls").GetArrayLength());
        Assert.Equal(bytes, File.ReadAllBytes(file)); var native = host.Owned.Native.Read(); var opens = host.Opens; storage = host.Storage();
        var replay = await Console(host, command); Assert.Equal(0, replay.Exit); Assert.True(replay.Json.GetProperty("replayed").GetBoolean());
        Assert.Equal(opens, host.Opens); Assert.Equal(native, host.Owned.Native.Read());
        var pendingPath = Path.Combine(host.Paths.RootDirectory, SelfStartupPendingStore.FileName); var pendingBytes = File.ReadAllBytes(pendingPath);
        Assert.Equal(id, (await new SelfStartupPendingStore(host.Paths).ReadAsync()).Operation!.OperationId); Assert.Contains("pending retained", replay.Error);
        var replayStorage = host.Storage(); Assert.True(replayStorage.Remove(pendingPath)); AssertStorage(storage, replayStorage);
        var receipt = await Console(host, ["operation", "show", id]); Assert.Equal(0, receipt.Exit); Assert.Null(receipt.Json.GetProperty("operationId").GetString());
        Assert.Equal(pendingBytes, File.ReadAllBytes(pendingPath));
        Assert.Equal(id, receipt.Json.GetProperty("data").GetProperty("operationId").GetString()); Assert.Equal("completed", receipt.Json.GetProperty("data").GetProperty("state").GetString());
        Assert.Single(Directory.EnumerateFiles(host.Paths.RootDirectory, "*.operation.json", SearchOption.AllDirectories)); await host.Owned.AssertReleased();
    }

    [Theory]
    [InlineData("--yes")] [InlineData("--file -")] [InlineData("--operation-id invalid")] [InlineData("--expected-revision abc")]
    [InlineData("--scope user")] [InlineData("--native")] [InlineData("--count 1")] [InlineData("--interval 1")]
    public async Task PreparationIsStrictlyReadOnlyAndRejectsExtraOptionsBeforeConnection(string options)
    {
        var connections = 0; var app = new CliApplication(_ => { connections++; throw new IOException("No connection."); });
        var result = await Run(app, "unused", ["startup", "self", "prepare", "enable", .. options.Split(' ')]);
        Assert.Equal(2, result.Exit); Assert.Equal(0, connections); Assert.Empty(result.Error); Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("data").ValueKind);
    }

    [Theory]
    [InlineData("ENABLE")] [InlineData("toggle")] [InlineData("other.exe")]
    public void PreparationNameIsAnExactBoundedIntentNotATarget(string name) =>
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(["startup", "self", "prepare", name]));

    [Theory]
    [InlineData("duplicate")] [InlineData("nested-duplicate")] [InlineData("case")] [InlineData("extra")]
    [InlineData("missing-enabled")] [InlineData("missing-confirmed")] [InlineData("schema")] [InlineData("context")]
    [InlineData("unknown-state")] [InlineData("boolean")] [InlineData("null")] [InlineData("envelope")]
    public async Task DedicatedStrictReaderRejectsAmbiguousOrArbitraryInputsBeforeConnecting(string defect)
    {
        await using var host = new Host(); var source = JsonSerializer.Serialize(host.Owned.Request() with { Confirmed = false }, AgentProtocol.JsonOptions);
        var fields = JsonNode.Parse(source)!.AsObject();
        switch (defect)
        {
            case "duplicate": source = source.Replace("\"enabled\":true", "\"enabled\":true,\"enabled\":false"); break;
            case "nested-duplicate": source = source.Replace("\"registered\":false", "\"registered\":false,\"registered\":false"); break;
            case "case": fields["Enabled"] = fields["enabled"]!.DeepClone(); fields.Remove("enabled"); break;
            case "extra": fields["path"] = @"\\offline.invalid\forbidden.exe"; break;
            case "missing-enabled": fields.Remove("enabled"); break;
            case "missing-confirmed": fields.Remove("confirmed"); break;
            case "schema": fields["schemaVersion"] = 2; break;
            case "context": fields["targetContextId"] = "not-a-context"; break;
            case "unknown-state": fields["expectedRegistration"]!["registered"] = null; break;
            case "boolean": fields["enabled"] = "false"; break;
            case "null": source = "null"; break;
            case "envelope": source = JsonSerializer.Serialize(new { ok = true, data = fields }); break;
        }
        if (defect is not ("duplicate" or "nested-duplicate" or "null" or "envelope")) { source = fields.ToJsonString(); }
        var connections = 0; var before = host.Storage();
        var result = await Run(new(_ => { connections++; throw new IOException("No connection."); }), host.Paths.RootDirectory,
            ["startup", "self", "set", "--file", "-", "--yes"], source);
        Assert.Equal(2, result.Exit); Assert.Equal(0, connections); Assert.Equal(0, host.Opens); AssertStorage(before, host.Storage());
        Assert.Null(result.Json.GetProperty("operationId").GetString()); Assert.Empty(result.Error);
    }

    [Fact]
    public async Task FileCannotSupplyInvocationConfirmationAndSetNeverPreparesOrStartsDaemon()
    {
        await using var host = new Host(); var connections = 0; var starts = 0;
        var app = new CliApplication(_ => { connections++; throw new IOException("No connect."); }, (_, _) => { starts++; throw new IOException("No daemon."); });
        var result = await Run(app, host.Paths.RootDirectory, ["startup", "self", "set", "--file", "-"], JsonSerializer.Serialize(host.Owned.Request(), AgentProtocol.JsonOptions));
        Assert.Equal(4, result.Exit); Assert.Equal("authorization_required", result.Json.GetProperty("error").GetProperty("code").GetString()); Assert.Equal(0, connections); Assert.Equal(0, starts);
    }

    [Theory]
    [InlineData("root")] [InlineData("version")] [InlineData("operation")] [InlineData("replay")] [InlineData("revision")]
    [InlineData("confirmed")] [InlineData("enabled")] [InlineData("schema")] [InlineData("missing")] [InlineData("case")]
    [InlineData("duplicate")] [InlineData("extra")] [InlineData("null")] [InlineData("context")] [InlineData("contradiction")]
    public async Task PreparationReaderRejectsMalformedMismatchedOrApparentlyConfirmedResponses(string defect)
    {
        await using var host = new Host(); var response = Preparation(host); var fields = JsonNode.Parse(JsonSerializer.Serialize(response.Data, AgentProtocol.JsonOptions))!.AsObject();
        response = defect switch
        {
            "root" => response with { RootId = new string('0', 64) }, "version" => response with { ProtocolVersion = 1 },
            "operation" => response with { OperationId = AgentProtocol.NewOperationId() }, "replay" => response with { Replayed = true },
            "revision" => response with { Revision = new string('a', 64) }, "contradiction" => response with { ErrorCode = "not_executed" }, _ => response
        };
        if (defect == "confirmed") { fields["confirmed"] = true; } if (defect == "enabled") { fields["enabled"] = false; }
        if (defect == "schema") { fields["schemaVersion"] = 2; } if (defect == "missing") { fields.Remove("confirmed"); }
        if (defect == "context") { fields["targetContextId"] = "invalid"; } if (defect == "extra") { fields["verified"] = true; }
        if (defect == "case") { fields["Confirmed"] = false; fields.Remove("confirmed"); }
        var data = fields.ToJsonString(); if (defect == "duplicate") { data = data.Replace("\"confirmed\":false", "\"confirmed\":false,\"confirmed\":true"); }
        using var body = JsonDocument.Parse(defect == "null" ? "null" : data);
        response = response with { Data = body.RootElement.Clone() };
        var transport = new Fake(_ => Task.FromResult(response)); var result = await Run(new(_ => transport), host.Paths.RootDirectory, ["startup", "self", "prepare", "enable"]);
        Assert.Equal(3, result.Exit); Assert.Equal("protocol_mismatch", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("data").ValueKind); Assert.Single(transport.Calls); Assert.Null(transport.Calls[0].OperationId); Assert.Null(transport.Calls[0].Payload);
    }

    [Fact]
    public async Task PreparationFailureAndCallerCancelCarryNoDataOrMutationIdentity()
    {
        await using var host = new Host(); var failed = Preparation(host) with { Ok = false, ErrorCode = "permission_denied", Error = "failure", Data = new { confirmed = true, secret = "do-not-show" } };
        var result = await Run(new(_ => new Fake(_ => Task.FromResult(failed))), host.Paths.RootDirectory, ["startup", "self", "prepare", "enable"]);
        Assert.Equal(4, result.Exit); Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("data").ValueKind); Assert.DoesNotContain("do-not-show", result.Text);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var cancelled = await Run(new(_ => new Fake(_ => throw new OperationCanceledException(cancel.Token))), host.Paths.RootDirectory,
            ["startup", "self", "prepare", "enable"], token: cancel.Token);
        Assert.Equal(6, cancelled.Exit); Assert.Equal("cancelled", cancelled.Json.GetProperty("error").GetProperty("code").GetString()); Assert.Null(cancelled.Json.GetProperty("operationId").GetString());
    }

    [Theory]
    [InlineData("root")] [InlineData("version")] [InlineData("operation")] [InlineData("revision")] [InlineData("null")]
    [InlineData("array")] [InlineData("contradiction")] [InlineData("post-io")] [InlineData("post-timeout")] [InlineData("post-cancel")]
    public async Task SentMutationMismatchOrTransportLossRemainsUnknownWithOriginalIdentityAndNoRetry(string defect)
    {
        await using var host = new Host(); var id = AgentProtocol.NewOperationId(); using var output = new StringWriter(); using var error = new StringWriter();
        var transport = new Fake(request =>
        {
            Assert.Contains(id, error.ToString()); Assert.Equal(id, request.OperationId); Assert.True(Assert.IsType<SelfStartupMutationRequest>(request.Payload).Confirmed);
            var response = new AgentResponse("ack", true, new { opaque = "historical" }, RootId: AgentProtocol.ForRoot(host.Paths.RootDirectory).RootId, OperationId: id);
            return defect switch
            {
                "root" => Task.FromResult(response with { RootId = "wrong" }), "version" => Task.FromResult(response with { ProtocolVersion = 1 }),
                "operation" => Task.FromResult(response with { OperationId = AgentProtocol.NewOperationId() }), "revision" => Task.FromResult(response with { Revision = new string('a', 64) }),
                "null" => Task.FromResult(response with { Data = null }), "array" => Task.FromResult(response with { Data = Array.Empty<int>() }),
                "contradiction" => Task.FromResult(response with { ErrorCode = "failure" }),
                "post-io" => throw new AgentOutcomeUnknownException(id, new IOException("lost after write")),
                "post-timeout" => throw new AgentOutcomeUnknownException(id, new TimeoutException("late after write")),
                _ => throw new AgentOutcomeUnknownException(id, new OperationCanceledException("cancel after write"))
            };
        });
        var exit = await new CliApplication(_ => transport).RunAsync(["startup", "self", "set", "--file", "-", "--yes", "--operation-id", id, "--root", host.Paths.RootDirectory, "--json"],
            new StringReader(JsonSerializer.Serialize(host.Owned.Request() with { Confirmed = false }, AgentProtocol.JsonOptions)), output, error);
        var result = Parse(output.ToString()); Assert.Equal(6, exit); Assert.Equal("outcome_unknown", result.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(id, result.GetProperty("operationId").GetString()); Assert.Equal(JsonValueKind.Null, result.GetProperty("data").ValueKind); Assert.Single(transport.Calls); Assert.Equal(0, host.Opens);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task StdinConfirmationUsesOneGeneratedOrExplicitIdAndOpaqueHistoricalAcknowledgement(bool explicitId)
    {
        await using var host = new Host(); var id = AgentProtocol.NewOperationId(); var starts = 0; var transport = new Fake(request => Task.FromResult(new AgentResponse("ack", true,
            new { opaque = "historical" }, RootId: AgentProtocol.ForRoot(host.Paths.RootDirectory).RootId, OperationId: request.OperationId)));
        var args = new List<string> { "startup", "self", "set", "--file", "-", "--yes" }; if (explicitId) { args.AddRange(["--operation-id", id]); }
        var result = await Run(new(_ => transport, (_, _) => { starts++; throw new IOException("No daemon."); }), host.Paths.RootDirectory, args.ToArray(),
            JsonSerializer.Serialize(host.Owned.Request() with { Confirmed = false }, AgentProtocol.JsonOptions), json: false);
        Assert.Equal(0, result.Exit); Assert.Equal(0, starts); var request = Assert.Single(transport.Calls); Assert.Equal("startup-self-set", request.Command); Assert.Null(request.Name); Assert.Null(request.Revision);
        Assert.Equal(request.OperationId, OperationJournal.NormalizeId(request.OperationId)); if (explicitId) { Assert.Equal(id, request.OperationId); }
        Assert.Contains(request.OperationId!, result.Error); Assert.Contains("opaque driver evidence", result.Text); Assert.Contains("NOT proof of Windows", result.Text); Assert.Equal(0, host.Opens);
    }

    [Theory]
    [InlineData("io", 3, "connection_failed")] [InlineData("timeout", 6, "timeout")] [InlineData("cancel", 6, "timeout")]
    [InlineData("permission", 4, "permission_denied")] [InlineData("argument", 2, "invalid_argument")]
    public async Task FactoryFailureBeforeSendIsNotAnUnknownReceiptAndClearsItsExactHint(string defect, int exit, string code)
    {
        await using var host = new Host(); var connections = 0;
        var app = new CliApplication(_ =>
        {
            connections++; Assert.True(File.Exists(Path.Combine(host.Paths.RootDirectory, SelfStartupPendingStore.FileName)));
            throw defect switch { "io" => new IOException("factory refused before SendAsync"), "timeout" => new TimeoutException("factory deadline before SendAsync"),
                "permission" => new UnauthorizedAccessException("factory access before SendAsync"), "argument" => new ArgumentException("factory rejected before SendAsync"),
                _ => new OperationCanceledException("factory before SendAsync") };
        });
        var result = await Run(app, host.Paths.RootDirectory, ["startup", "self", "set", "--file", "-", "--yes"],
            JsonSerializer.Serialize(host.Owned.Request() with { Confirmed = false }, AgentProtocol.JsonOptions));
        Assert.Equal(exit, result.Exit); Assert.Equal(code, result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Null(result.Json.GetProperty("operationId").GetString()); Assert.Contains("reserved operationId", result.Error);
        Assert.DoesNotContain("query operation show", result.Error); Assert.Equal(1, connections); Assert.Equal(0, host.Opens);
        Assert.False(File.Exists(Path.Combine(host.Paths.RootDirectory, SelfStartupPendingStore.FileName))); Assert.Empty(Directory.EnumerateFiles(host.Paths.RootDirectory, "*.tmp"));
    }

    [Fact]
    public async Task RealPipeConnectionDeadlineBeforeAnyServerExistsIsNotAnUnknownMutation()
    {
        using var owned = new SelfStartupPlanningSessionTests.Fixture(); var paths = SelfStartupJournalTests.Paths(owned);
        File.WriteAllText(paths.ConfigPath, "version: 1\ntasks: []\n");
        // Leave room for cold local preparation/reservation; this is the real pipe-connect deadline case.
        var result = await Run(new(), paths.RootDirectory, ["startup", "self", "set", "--file", "-", "--yes", "--timeout", "1"],
            JsonSerializer.Serialize(owned.Request() with { Confirmed = false }, AgentProtocol.JsonOptions));
        Assert.Equal(6, result.Exit); Assert.Equal("timeout", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Null(result.Json.GetProperty("operationId").GetString()); Assert.Contains("reserved operationId", result.Error);
        Assert.DoesNotContain("query operation show", result.Error); Assert.Empty(Directory.EnumerateFiles(paths.RootDirectory, "*.operation.json", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(paths.RootDirectory, SelfStartupPendingStore.FileName))); Assert.Empty(Directory.EnumerateFiles(paths.RootDirectory, "*.tmp"));
    }

    [Fact]
    public async Task FailedMutationAcknowledgementWithholdsInternalDriverData()
    {
        await using var host = new Host(); var transport = new Fake(request => Task.FromResult(new AgentResponse("ack", false,
            new { secret = "not-public-evidence", protocolCompleted = true }, Error: "refused", RootId: AgentProtocol.ForRoot(host.Paths.RootDirectory).RootId,
            ErrorCode: "not_executed", OperationId: request.OperationId)));
        var result = await Run(new(_ => transport), host.Paths.RootDirectory, ["startup", "self", "set", "--file", "-", "--yes"],
            JsonSerializer.Serialize(host.Owned.Request() with { Confirmed = false }, AgentProtocol.JsonOptions));
        Assert.Equal(1, result.Exit); Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("data").ValueKind);
        Assert.Equal(transport.Calls[0].OperationId, result.Json.GetProperty("operationId").GetString()); Assert.DoesNotContain("not-public-evidence", result.Text);
    }

    [Fact]
    public async Task SharedTypedPreparationAndHumanOutputDoNotConfirmAndHelpPreservesBoundaries()
    {
        await using var host = new Host(); var prepared = await new AgentSelfStartupClient(host.Client, host.Paths.RootDirectory).PrepareAsync(true);
        Assert.False(prepared.Confirmed); Assert.True(prepared.Enabled);
        var result = await Run(new(_ => new Fake(_ => Task.FromResult(Preparation(host)))), host.Paths.RootDirectory, ["startup", "self", "prepare", "enable"], json: false);
        Assert.Equal(0, result.Exit); Assert.Contains("NOT confirmation/ownership/CAS", result.Text); Assert.Contains("JSON.data", result.Text);
        Assert.Contains("stderr BEFORE exchange", CliApplication.HelpText); Assert.Contains("never substitute a new ID", CliApplication.HelpText); Assert.Contains("Boot writes stay closed", CliApplication.HelpText);
    }

    private static AgentResponse Preparation(Host host) => new("prepare", true, host.Owned.Request() with { Confirmed = false }, RootId: AgentProtocol.ForRoot(host.Paths.RootDirectory).RootId);
    private static void AssertStorage(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    { Assert.Equal(before.Keys.Order(), after.Keys.Order()); foreach (var row in before) { Assert.Equal(row.Value, after[row.Key]); } }
    private static async Task<Result> Run(CliApplication app, string root, string[] args, string stdin = "", bool json = true, CancellationToken token = default)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await app.RunAsync([.. args, "--root", root, .. json ? new[] { "--json" } : []], new StringReader(stdin), output, error, token);
        var text = output.ToString(); return new(exit, json ? Parse(text) : default, error.ToString(), text);
    }
    private static async Task<Result> Console(Host host, string[] args)
    {
        var path = typeof(SelfStartupCliTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(value => value.Key == "CliExecutablePath").Value!;
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args.Concat(["--root", host.Paths.RootDirectory, "--json"])) { info.ArgumentList.Add(argument); }
        using var process = Process.Start(info)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await process.WaitForExitAsync(deadline.Token);
            var text = await stdout.WaitAsync(deadline.Token); return new(process.ExitCode, Parse(text), await stderr.WaitAsync(deadline.Token), text);
        }
        finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
    }
    private static JsonElement Parse(string text) { using var json = JsonDocument.Parse(text); return json.RootElement.Clone(); }
    private sealed record Result(int Exit, JsonElement Json, string Error, string Text);
    private sealed class Fake(Func<CliRequest, Task<AgentResponse>> send) : ICliTransport
    {
        internal List<CliRequest> Calls { get; } = [];
        public Task<AgentResponse> SendAsync(CliRequest request, CancellationToken token) { Calls.Add(request); return send(request); }
    }
}
