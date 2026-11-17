using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Cli;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    private static StartupOperationInspectionSnapshot DiagnosticSeed(string id)
    {
        var now = DateTimeOffset.UtcNow;
        return new(1, id, now, now, true, "observed", "history_journal_snapshot_observed", "completed", "completed",
            new("observed", "history_operation_observed", "associated_candidate", "history_operation_candidate_associated",
                Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "original_matches", "history_original_matches"),
            false, false, false, false, false, false);
    }

    [Theory]
    [InlineData("schema")] [InlineData("id")] [InlineData("old")] [InlineData("future")]
    [InlineData("diagnosticOnly")] [InlineData("nativeWriteReady")] [InlineData("executionRequested")]
    [InlineData("transactionFinalized")] [InlineData("mayFinalize")] [InlineData("receiptUpdated")] [InlineData("replayRequested")]
    [InlineData("missing")] [InlineData("extra")] [InlineData("cased")] [InlineData("duplicate")]
    [InlineData("nested-extra")] [InlineData("nested-duplicate")] [InlineData("upgrade-running")]
    [InlineData("missing-evidence")] [InlineData("unverified-ids")] [InlineData("oversize")]
    [InlineData("mutation-envelope")] [InlineData("replayed-envelope")] [InlineData("protocol")]
    public void OperationDiagnosticWireRejectsMalformedOrAuthorizingReplies(string mode)
    {
        var id = AgentProtocol.NewOperationId(); var json = JsonSerializer.SerializeToNode(DiagnosticSeed(id), AgentProtocol.JsonOptions)!.AsObject();
        if (mode == "schema") { json["schemaVersion"] = 2; }
        else if (mode == "id") { json["queriedOperationId"] = AgentProtocol.NewOperationId(); }
        else if (mode is "old" or "future") { var time = DateTimeOffset.UtcNow.AddMinutes(mode == "old" ? -2 : 2); json["completedAt"] = JsonValue.Create(time); json["observedAt"] = JsonValue.Create(time); }
        else if (mode == "diagnosticOnly") { json[mode] = false; }
        else if (mode is "nativeWriteReady" or "executionRequested" or "transactionFinalized" or "mayFinalize" or "receiptUpdated" or "replayRequested") { json[mode] = true; }
        else if (mode == "missing") { json.Remove("replayRequested"); }
        else if (mode == "extra") { json["rawReceipt"] = "not-allowed"; }
        else if (mode == "cased") { json["SchemaVersion"] = json["schemaVersion"]!.DeepClone(); json.Remove("schemaVersion"); }
        else if (mode == "nested-extra") { json["evidence"]!["rawBackupPath"] = "not-allowed"; }
        else if (mode == "upgrade-running") { json["persistedReceiptState"] = "running"; }
        else if (mode == "missing-evidence") { json["evidence"] = null; }
        else if (mode == "unverified-ids") { json["evidence"]!["state"] = "unverified"; }
        else if (mode == "oversize") { json["journalReason"] = new string('a', StartupOperationInspectionPolicy.MaximumBytes); }
        var text = json.ToJsonString();
        if (mode == "duplicate") { text = text.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal); }
        if (mode == "nested-duplicate") { text = text.Replace("\"nativeState\":\"original_matches\"", "\"nativeState\":\"original_matches\",\"nativeState\":\"original_matches\"", StringComparison.Ordinal); }
        using var document = JsonDocument.Parse(text);
        var response = new AgentResponse("diagnostic-wire", true, document.RootElement.Clone(),
            ProtocolVersion: mode == "protocol" ? 1 : AgentProtocol.Version,
            OperationId: mode == "mutation-envelope" ? id : null, Replayed: mode == "replayed-envelope");
        var error = Assert.Throws<AgentCommandException>(() => AgentStartupClient.ReadOperationInspection(response, id));
        Assert.Equal("protocol_mismatch", error.Code);
    }

    [Theory]
    [InlineData("completed", 0)] [InlineData("running", 6)] [InlineData("missing", 1)] [InlineData("none", 1)] [InlineData("busy", 6)]
    public async Task OperationDiagnosticCliPreservesTypedPartialDataAndReadOnlyIdentity(string mode, int expected)
    {
        using var owned = new Fixture(); var id = AgentProtocol.NewOperationId(); var value = DiagnosticSeed(id);
        if (mode == "running") { value = value with { PersistedReceiptState = "running", ObservedReceiptState = "unknown" }; }
        if (mode is "missing" or "busy") { value = value with { JournalState = mode, JournalReason = "history_journal_query_" + mode, PersistedReceiptState = "unobserved", ObservedReceiptState = "unobserved", Evidence = null }; }
        if (mode == "none") { value = value with { Evidence = new("none", "history_intent_absent", "unobserved", "history_intent_absent", null, null, "unobserved", "history_native_unobserved") }; }
        var transport = new OperationDiagnosticTransport((request, _) =>
        {
            Assert.Equal("startup-operation-inspect", request.Command); Assert.Equal(id, request.Name);
            Assert.Null(request.Payload); Assert.Null(request.Revision); Assert.Null(request.OperationId);
            return Task.FromResult(new AgentResponse("cli-diagnostic", true, value, RootId: AgentProtocol.ForRoot(owned.Root).RootId));
        });
        var output = new StringWriter(); var error = new StringWriter(); var starts = 0;
        var app = new CliApplication(_ => transport, (_, _) => { starts++; throw new InvalidOperationException("No implicit daemon start."); });
        var exit = await app.RunAsync(["startup", "operation", "inspect", id, "--root", owned.Root, "--json"], new StringReader(""), output, error);
        Assert.Equal(expected, exit); Assert.Empty(error.ToString()); Assert.Equal(0, starts); Assert.Equal(1, transport.Calls);
        using var document = JsonDocument.Parse(output.ToString()); var root = document.RootElement;
        Assert.Equal(expected == 0, root.GetProperty("ok").GetBoolean()); Assert.Equal(JsonValueKind.Null, root.GetProperty("operationId").ValueKind);
        Assert.False(root.GetProperty("replayed").GetBoolean()); Assert.True(root.GetProperty("data").GetProperty("diagnosticOnly").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, root.GetProperty("observedAt").ValueKind);
    }

    [Theory]
    [InlineData("--yes", null)] [InlineData("--file", "-")] [InlineData("--native", null)]
    [InlineData("--operation-id", "unused")] [InlineData("--expected-revision", "unused")] [InlineData("--scope", "user")]
    public async Task OperationDiagnosticCliRejectsMutationAndUnrelatedOptionsBeforeConnecting(string option, string? value)
    {
        var arguments = new List<string> { "startup", "operation", "inspect", AgentProtocol.NewOperationId(), "--json", option };
        if (value is not null) { arguments.Add(value); }
        var app = new CliApplication(_ => throw new InvalidOperationException("Unexpected connection."));
        var output = new StringWriter(); var exit = await app.RunAsync(arguments.ToArray(), new StringReader(""), output, new StringWriter());
        Assert.Equal(2, exit); using var document = JsonDocument.Parse(output.ToString()); Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("data").ValueKind);
    }

    [Theory]
    [InlineData("cancelled", 6)] [InlineData("unknown_command", 1)] [InlineData("permission_denied", 4)] [InlineData("protocol_mismatch", 3)]
    public async Task OperationDiagnosticCliErrorsDiscardUntrustedDataWithoutFallback(string code, int expected)
    {
        using var owned = new Fixture(); var id = AgentProtocol.NewOperationId();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new OperationDiagnosticTransport(async (_, token) =>
        {
            entered.TrySetResult(); if (code == "cancelled") { await Task.Delay(Timeout.Infinite, token); }
            return new("diagnostic-error", false, DiagnosticSeed(id) with { NativeWriteReady = true }, Error: "Controlled rejection.", ErrorCode: code);
        });
        using var cancellation = new CancellationTokenSource();
        var output = new StringWriter(); var app = new CliApplication(_ => transport, (_, _) => throw new InvalidOperationException("Must not start."));
        var run = app.RunAsync(["startup", "operation", "inspect", id, "--root", owned.Root, "--json"], new StringReader(""), output, new StringWriter(), cancellation.Token);
        if (code == "cancelled") { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); }
        var exit = await run;
        Assert.Equal(expected, exit); Assert.Equal(1, transport.Calls);
        using var document = JsonDocument.Parse(output.ToString()); Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("data").ValueKind);
    }

    private sealed class OperationDiagnosticTransport(Func<CliRequest, CancellationToken, Task<AgentResponse>> send) : ICliTransport
    {
        internal int Calls;
        public Task<AgentResponse> SendAsync(CliRequest request, CancellationToken token) { Calls++; return send(request, token); }
    }
}
