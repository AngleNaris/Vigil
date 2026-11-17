using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Recovery;

namespace SystemAutorun.Core.Tests;

public sealed class AgentRecoveryReceiptClientTests
{
    private static readonly string Revision = new('a', 64);
    private static RecoveryPendingOperation Pending(string root) => new() { SchemaVersion = 1, RootId = AgentProtocol.ForRoot(root).RootId,
        SubjectKey = CurrentUserIdentity.Key, TaskName = "media", ExpectedRevision = Revision, OperationId = AgentProtocol.NewOperationId(), ReservedAt = DateTimeOffset.UtcNow };
    // Independent vector for the existing Journal canonical JSON (no client helper).
    private static string Fingerprint(string name = "media", string? revision = null, string payload = "null") => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes("{\"command\":\"recovery-resume\",\"name\":\"" + name + "\",\"expectedRevision\":\"" + (revision ?? Revision) + "\",\"payload\":" + payload + "}")));
    private static OperationReceipt Receipt(RecoveryPendingOperation pending) => new(1, pending.OperationId, "recovery-resume", pending.TaskName,
        Fingerprint(), "original-owner", OperationState.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        new AgentResponse("original-request", true, new { opaque = "history, never current pause state" }, OperationId: pending.OperationId));

    [Theory]
    [InlineData("not-found", RecoveryReceiptState.NotFound)] [InlineData("null", RecoveryReceiptState.NotFound)]
    [InlineData("running", RecoveryReceiptState.Running)] [InlineData("unknown", RecoveryReceiptState.Unknown)]
    [InlineData("omitted", RecoveryReceiptState.ResultUnavailable)] [InlineData("complete", RecoveryReceiptState.CompletedHistory)]
    [InlineData("failed", RecoveryReceiptState.CompletedHistory)] [InlineData("root", RecoveryReceiptState.CompletedHistory)]
    [InlineData("historical-version", RecoveryReceiptState.CompletedHistory)]
    public async Task QueryReturnsOnlyBoundHistoricalMetadataAndCannotClearOrSend(string kind, RecoveryReceiptState expected)
    {
        using var root = Root(); var pending = Pending(root.Path); var receipt = Receipt(pending);
        receipt = kind switch
        {
            "running" => receipt with { State = OperationState.Running, CompletedAt = null, Response = null },
            "unknown" => receipt with { State = OperationState.Unknown, CompletedAt = null, Response = null },
            "omitted" => receipt with { ResultOmitted = true, Response = null },
            "failed" => receipt with { Response = receipt.Response! with { Ok = false, ErrorCode = "not_found" } },
            "root" => receipt with { Response = receipt.Response! with { RootId = pending.RootId } },
            "historical-version" => receipt with { Response = receipt.Response! with { ProtocolVersion = 1 } }, _ => receipt,
        };
        await Query(root, pending, request => kind == "not-found"
            ? new(request.Id, false, RootId: pending.RootId, ErrorCode: "operation_not_found")
            : new(request.Id, true, kind == "null" ? null : receipt, RootId: pending.RootId), async read =>
        {
            var observation = await read(); Assert.Equal(expected, observation.State); Assert.Equal(pending.OperationId, observation.OperationId);
            Assert.Equal(new[] { "OperationId", "State" }, typeof(RecoveryReceiptObservation).GetProperties().Select(property => property.Name).Order().ToArray());
        });
    }

    [Theory]
    [InlineData("id")] [InlineData("command")] [InlineData("command-case")] [InlineData("name")] [InlineData("name-case")]
    [InlineData("fingerprint")] [InlineData("fingerprint-case")] [InlineData("revision")] [InlineData("payload")]
    [InlineData("historical-id")] [InlineData("historical-root")] [InlineData("schema")] [InlineData("terminal")]
    public async Task ForeignOrMismatchedReceiptNeverBecomesAnObservation(string defect)
    {
        using var root = Root(); var pending = Pending(root.Path); var receipt = Receipt(pending);
        receipt = defect switch
        {
            "id" => receipt with { OperationId = AgentProtocol.NewOperationId() }, "command" => receipt with { Command = "stop" },
            "command-case" => receipt with { Command = "RECOVERY-RESUME" }, "name" => receipt with { Name = "another" },
            "name-case" => receipt with { Name = "MEDIA" }, "fingerprint" => receipt with { Fingerprint = new string('0', 64) },
            "fingerprint-case" => receipt with { Fingerprint = receipt.Fingerprint.ToUpperInvariant() },
            "revision" => receipt with { Fingerprint = Fingerprint(revision: new string('b', 64)) },
            "payload" => receipt with { Fingerprint = Fingerprint(payload: "{}") },
            "historical-id" => receipt with { Response = receipt.Response! with { OperationId = AgentProtocol.NewOperationId() } },
            "historical-root" => receipt with { Response = receipt.Response! with { RootId = new string('0', 64) } },
            "schema" => receipt with { SchemaVersion = 2 }, _ => receipt with { Response = null, ResultOmitted = false },
        };
        await Query(root, pending, request => new(request.Id, true, receipt, RootId: pending.RootId), async read =>
        { Assert.Equal("protocol_mismatch", (await Assert.ThrowsAsync<AgentCommandException>(read)).Code); });
    }

    [Theory]
    [InlineData("array")] [InlineData("version-type")] [InlineData("root-type")] [InlineData("id-type")] [InlineData("outer-operation")]
    public async Task MalformedLiveEnvelopeIsNormalizedButCannotBecomeUnknownMutation(string defect)
    {
        using var root = Root(); var pending = Pending(root.Path);
        await Query(root, pending, request => new(request.Id, true, Receipt(pending), RootId: pending.RootId), async read =>
        { var error = await Assert.ThrowsAsync<AgentCommandException>(read); Assert.Equal("protocol_mismatch", error.Code); Assert.Null(error.OperationId); }, defect);
    }

    [Theory]
    [InlineData("permission_denied")] [InlineData("operation_store_corrupt")]
    public async Task ServerReadErrorsStayIntactAndRetainNoMutationAuthority(string code)
    {
        using var root = Root(); var pending = Pending(root.Path);
        await Query(root, pending, request => new(request.Id, false, RootId: pending.RootId, ErrorCode: code), async read =>
        { Assert.Equal(code, (await Assert.ThrowsAsync<AgentCommandException>(read)).Code); });
    }

    [Theory]
    [InlineData("id")] [InlineData("revision")] [InlineData("subject")] [InlineData("schema")] [InlineData("name")]
    public async Task InvalidPendingCannotResolveRootOrQueryAnotherId(string defect)
    {
        using var root = new TempDirectory(); var pending = new RecoveryPendingOperation { SchemaVersion = 1, RootId = new string('a', 64),
            SubjectKey = CurrentUserIdentity.Key, TaskName = "media", ExpectedRevision = Revision, OperationId = AgentProtocol.NewOperationId(), ReservedAt = DateTimeOffset.UtcNow };
        pending = defect switch { "id" => pending with { OperationId = "bad" }, "revision" => pending with { ExpectedRevision = "bad" },
            "subject" => pending with { SubjectKey = "foreign_subject" }, "schema" => pending with { SchemaVersion = 2 }, _ => pending with { TaskName = "\n" } };
        Assert.Equal("invalid_argument", (await Assert.ThrowsAsync<AgentCommandException>(() => new AgentRecoveryReceiptClient(new(root.Path)).ReadAsync(pending))).Code);
        Assert.Empty(Directory.GetFileSystemEntries(root.Path));
    }

    [Fact]
    public async Task DifferentRootCannotQueryEvenAValidOriginalId()
    {
        using var root = Root(); var pending = Pending(root.Path) with { RootId = new string('0', 64) };
        Assert.Equal("protocol_mismatch", (await Assert.ThrowsAsync<AgentCommandException>(() => new AgentRecoveryReceiptClient(new(root.Path)).ReadAsync(pending))).Code);
        Assert.Single(Directory.GetFileSystemEntries(root.Path));
    }

    private static TempDirectory Root() { var root = new TempDirectory(); root.Write("autorun.yaml", "version: 1\ntasks: []\n"); return root; }
    private static async Task Query(TempDirectory root, RecoveryPendingOperation pending, Func<AgentRequest, AgentResponse> respond,
        Func<Func<Task<RecoveryReceiptObservation>>, Task> verify, string? defect = null)
    {
        // Real Pipe but synthetic Journal response. Persist the actual hint to prove
        // no query state (including completed success) can remove or replace it.
        var store = new RecoveryPendingStore(new(root.Path), pending.TaskName);
        var owner = await store.ReserveAsync(pending.OperationId, pending.ExpectedRevision); pending = owner.Operation;
        var path = Path.Combine(root.Path, store.FileName); var before = File.ReadAllBytes(path); var calls = 0;
        var endpoint = AgentProtocol.ForRoot(root.Path); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NamedPipeServerStream(endpoint.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serving = Serve();
        try { await verify(() => new AgentRecoveryReceiptClient(new(root.Path)).ReadAsync(pending, deadline.Token)); await serving; Assert.Equal(1, calls); }
        finally { deadline.Cancel(); await serving; }
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.Equal(pending, (await store.ReadAsync()).Operation);
        Assert.Empty(Directory.GetFiles(root.Path, "*.operation.json", SearchOption.AllDirectories)); Assert.Empty(Directory.GetFiles(root.Path, "*.tmp"));
        async Task Serve()
        {
            await server.WaitForConnectionAsync(deadline.Token); using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            var request = JsonSerializer.Deserialize<AgentRequest>((await reader.ReadLineAsync(deadline.Token))!, AgentProtocol.JsonOptions)!; Interlocked.Increment(ref calls);
            Assert.Equal("operation", request.Command); Assert.Equal(pending.OperationId, request.Name); Assert.Null(request.OperationId); Assert.Null(request.Payload); Assert.Null(request.ExpectedRevision);
            var line = JsonSerializer.Serialize(respond(request), AgentProtocol.JsonOptions);
            if (defect == "array") { line = "[]"; }
            else if (defect is not null)
            {
                var node = JsonNode.Parse(line)!.AsObject();
                if (defect == "version-type") { node["protocolVersion"] = "2"; } if (defect == "root-type") { node["rootId"] = 17; } if (defect == "id-type") { node["id"] = 17; }
                if (defect == "outer-operation") { node["operationId"] = AgentProtocol.NewOperationId(); }
                line = node.ToJsonString();
            }
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(line.AsMemory(), deadline.Token);
        }
    }
}
