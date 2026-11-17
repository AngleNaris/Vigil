using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Cli;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using static SystemAutorun.IntegrationTests.StartupNativeInspectionControlPlaneTests;

namespace SystemAutorun.IntegrationTests;

// Synthetic transport/identities test the CLI validation boundary, not native execution.
public sealed class StartupNativeInspectionCliProtocolTests
{
    [Theory]
    [InlineData("flag")] [InlineData("cas")] [InlineData("replayed")] [InlineData("schema")]
    [InlineData("legacy")] [InlineData("metadata-authority")] [InlineData("slot")] [InlineData("record")]
    [InlineData("revision")] [InlineData("reason")] [InlineData("parent")] [InlineData("computed")]
    public async Task NativeCliRejectsMalformedRepliesBeforePrintingOrAcceptingTheirEvidence(string defect)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); var revision = await Revision(fixture);
        var node = ValidData(revision);
        switch (defect)
        {
            case "flag": node.AsObject().Remove("nativeWritePerformed"); break;
            case "cas": node["slots"]!["cas"] = true; break;
            case "schema": node["schemaVersion"] = 1; break;
            case "legacy": node = node["metadata"]!.DeepClone(); break;
            case "metadata-authority": node["metadata"]!["nativeWriteReady"] = true; break;
            case "slot": node["slots"]!.AsObject().Remove("original"); break;
            case "record": node["recordMetadataDigestAfter"] = new string('b', 64); break;
            case "reason": node["slots"]!["original"]!["state"] = "unknown"; node["slots"]!["original"]!["reason"] = "restorable"; break;
            case "parent": node["slots"]!["original"]!["directoryIdentity"] = null; break;
            case "computed": node["metadata"]!["entry"]!["isManaged"] = true; break;
        }
        var transport = new ReplyTransport(new("synthetic-wire", true, node, Revision: defect == "revision" ? new string('b', 64) : revision,
            Replayed: defect == "replayed"));
        var result = await StartupNativeInspectionCliTests.Run(fixture.Root, Entry, revision, application: new(_ => transport));
        Assert.Equal(3, result.Exit); Assert.Empty(result.Error); Assert.Equal(1, transport.Calls);
        using var json = JsonDocument.Parse(result.Output); var root = json.RootElement;
        Assert.Equal("protocol_mismatch", root.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("data").ValueKind); Assert.Null(root.GetProperty("operationId").GetString());
        Assert.DoesNotContain("current fixed name", result.Output);
    }

    [Theory]
    [InlineData("startup_scan_busy", 1)] [InlineData("startup_native_observation_timeout", 6)] [InlineData("startup_backup_changed", 5)]
    public async Task FailedNativeReplyCannotSmuggleSuccessfulEvidenceIntoCliOutput(string code, int exit)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); var revision = await Revision(fixture);
        var data = ValidData(revision); data["nativeWriteReady"] = true;
        var transport = new ReplyTransport(new("synthetic-failure", false, data, Error: "Rejected observation.", ErrorCode: code, Revision: revision));
        var result = await StartupNativeInspectionCliTests.Run(fixture.Root, Entry, revision, application: new(_ => transport));
        Assert.Equal(exit, result.Exit); Assert.Equal(1, transport.Calls); Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output); var root = json.RootElement;
        Assert.Equal(code, root.GetProperty("error").GetProperty("code").GetString()); Assert.Equal("synthetic-failure", root.GetProperty("requestId").GetString());
        Assert.Equal(revision, root.GetProperty("revision").GetString()); Assert.Equal(JsonValueKind.Null, root.GetProperty("data").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("observedAt").ValueKind); Assert.Null(root.GetProperty("operationId").GetString());
    }

    [Fact]
    public async Task OldAgentWithoutNativeEndpointCannotCauseSilentMetadataFallback()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); var revision = await Revision(fixture);
        var transport = new ReplyTransport(new("synthetic-wire", false, Error: "Unsupported read-only endpoint.", ErrorCode: "unknown_command"));
        var result = await StartupNativeInspectionCliTests.Run(fixture.Root, Entry, revision, application: new(_ => transport));
        Assert.Equal(1, result.Exit); Assert.Equal(1, transport.Calls);
        using var json = JsonDocument.Parse(result.Output); Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown_command", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("data").ValueKind);
    }

    [Theory]
    [InlineData("path")] [InlineData("identity")] [InlineData("nativeWriteReady")] [InlineData("missing-state")] [InlineData("preview")]
    public async Task NativeInputCannotSupplyProbePathsIdentitiesAuthorityOrInvalidPreview(string defect)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); var revision = await Revision(fixture); var connections = 0;
        var node = JsonSerializer.SerializeToNode(StartupInspectionRequest.FromEntry(Entry), AgentProtocol.JsonOptions)!;
        if (defect == "missing-state") { node.AsObject().Remove("isDisabled"); }
        else if (defect == "preview") { node["rebuildInput"] = JsonSerializer.SerializeToNode(new StartupRebuildInput(@"\\offline.invalid\never-open.exe"), AgentProtocol.JsonOptions); }
        else { node[defect] = @"\\offline.invalid\never-probe"; }
        var output = new StringWriter(); var error = new StringWriter();
        var exit = await new CliApplication(_ => { connections++; throw new InvalidOperationException("No connection allowed."); })
            .RunAsync(StartupNativeInspectionCliTests.Arguments(fixture.Root, Entry.Id, revision, true, true), new StringReader(node.ToJsonString()), output, error);
        Assert.Equal(2, exit); Assert.Equal(0, connections); Assert.Empty(error.ToString());
        using var json = JsonDocument.Parse(output.ToString()); Assert.Null(json.RootElement.GetProperty("operationId").GetString());
    }

    private static JsonNode ValidData(string revision)
    {
        var read = Evidence(); var now = DateTimeOffset.UtcNow;
        var metadata = new StartupInspectionSnapshot(1, revision, now, now, Entry, Entry.MetadataDigest, "not_checked", false,
            false, false, false, StartupInspectionPolicy.Describe(Entry));
        return JsonSerializer.SerializeToNode(new StartupNativeInspectionSnapshot(2, revision, now, metadata, read.Evidence,
            read.RecordMetadataDigestAfter, "sequential-independent", "unverified", false, false, false), AgentProtocol.JsonOptions)!;
    }
    private sealed class ReplyTransport(AgentResponse response) : ICliTransport
    {
        internal int Calls;
        public Task<AgentResponse> SendAsync(CliRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls); Assert.Equal("startup-inspect-native", request.Command); Assert.Null(request.OperationId);
            return Task.FromResult(response);
        }
    }
}
