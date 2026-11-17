using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

public sealed class AgentSelfStartupClientTests
{
    [Theory]
    [InlineData("null")] [InlineData("schema")] [InlineData("context")] [InlineData("unknown")]
    [InlineData("malformed-absent")] [InlineData("unconfirmed")]
    public async Task InvalidOrUnconfirmedSubmissionIsRejectedWithoutResolvingRootOrConnecting(string defect)
    {
        using var root = new TempDirectory(); // No autorun.yaml: any transport entry would fail differently.
        var request = Request();
        request = defect switch
        {
            "null" => null!, "schema" => request with { SchemaVersion = 2 },
            "context" => request with { TargetContextId = "invalid" },
            "unknown" => request with { ExpectedRegistration = new(null) },
            "malformed-absent" => request with { ExpectedRegistration = new(false, 1) },
            _ => request with { Confirmed = false },
        };
        var client = new AgentSelfStartupClient(new(root.Path), root.Path);
        var error = await Assert.ThrowsAsync<AgentCommandException>(() => client.SubmitAsync(request, AgentProtocol.NewOperationId()));
        Assert.Equal(defect == "unconfirmed" ? "authorization_required" : "invalid_argument", error.Code);
        Assert.Null(error.OperationId); Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
        if (request is not null && defect == "unconfirmed") { Assert.False(request.Confirmed); }
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("invalid")] [InlineData("019a1234-5678-7abc-8def-0123456789ab")]
    [InlineData("019a123456784abc8def0123456789ab")]
    public async Task SubmissionRequiresCallerReservedUuidV7AndNeverGeneratesOne(string? operationId)
    {
        using var root = new TempDirectory();
        var error = await Assert.ThrowsAsync<AgentCommandException>(() =>
            new AgentSelfStartupClient(new(root.Path), root.Path).SubmitAsync(Request(), operationId!));
        Assert.Equal("invalid_operation_id", error.Code); Assert.Null(error.OperationId);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    [Fact]
    public async Task MismatchedClientRootIsRejectedBeforeDispatch()
    {
        using var transport = new TempDirectory(); using var expected = new TempDirectory();
        transport.Write("autorun.yaml", "version: 1\ntasks: []\n"); expected.Write("autorun.yaml", "version: 1\ntasks: []\n");
        await Assert.ThrowsAsync<ArgumentException>(() => new AgentSelfStartupClient(new(transport.Path), expected.Path)
            .SubmitAsync(Request(), AgentProtocol.NewOperationId(), TimeSpan.FromMilliseconds(20)));
        Assert.Single(Directory.EnumerateFileSystemEntries(transport.Path)); Assert.Single(Directory.EnumerateFileSystemEntries(expected.Path));
    }

    [Theory]
    [InlineData("version")] [InlineData("root")] [InlineData("operation")] [InlineData("id")] [InlineData("id-bound")]
    [InlineData("revision")] [InlineData("contradiction")] [InlineData("null")] [InlineData("array")] [InlineData("size")]
    [InlineData("missing-code")] [InlineData("code-case")] [InlineData("code-bound")]
    public void InvalidAcknowledgementIsUnknownWithOriginalIdNotABusinessFailure(string defect)
    {
        using var root = new TempDirectory(); var id = AgentProtocol.NewOperationId(); var response = Ack(root.Path, id);
        response = defect switch
        {
            "version" => response with { ProtocolVersion = 1 }, "root" => response with { RootId = new string('0', 64) },
            "operation" => response with { OperationId = AgentProtocol.NewOperationId() }, "id" => response with { Id = " " },
            "id-bound" => response with { Id = new string('a', AgentProtocol.MaximumRequestIdCharacters + 1) },
            "revision" => response with { Revision = new string('a', 64) }, "contradiction" => response with { Error = "invalid" },
            "null" => response with { Data = null }, "array" => response with { Data = Array.Empty<int>() },
            "size" => response with { Data = new { value = new string('x', AgentProtocol.MaximumResponseCharacters) } },
            "missing-code" => response with { Ok = false }, "code-case" => response with { Ok = false, ErrorCode = "NOT_EXECUTED" },
            _ => response with { Ok = false, ErrorCode = new string('a', 65) },
        };
        var error = Assert.Throws<AgentOutcomeUnknownException>(() => AgentSelfStartupClient.ReadAcknowledgement(response, id, AgentProtocol.ForRoot(root.Path).RootId));
        Assert.Equal(id, error.OperationId);
        Assert.Equal(defect is "id" or "id-bound" ? null : response.Id, error.RequestId);
    }

    [Fact]
    public void OpaqueDataIsClonedWithoutInterpretingInternalOrFutureFields()
    {
        using var root = new TempDirectory(); var id = AgentProtocol.NewOperationId(); AgentResponse result;
        using (var document = JsonDocument.Parse("{\"protocolCompleted\":false,\"driverDispatched\":true,\"retryAllowed\":true,\"schemaVersion\":987}"))
        {
            result = AgentSelfStartupClient.ReadAcknowledgement(Ack(root.Path, id) with { Data = document.RootElement, Replayed = true }, id, AgentProtocol.ForRoot(root.Path).RootId);
        }
        Assert.True(result.Ok); Assert.True(result.Replayed); Assert.Equal(id, result.OperationId);
        var data = Assert.IsType<JsonElement>(result.Data); Assert.False(data.GetProperty("protocolCompleted").GetBoolean());
        Assert.True(data.GetProperty("retryAllowed").GetBoolean()); Assert.Equal(987, data.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void HistoricalAcknowledgementUsesCapturedRootIdWithoutPostExchangeFilesystemLookup()
    {
        using var root = new TempDirectory(); var id = AgentProtocol.NewOperationId(); var response = Ack(root.Path, id);
        var expectedRootId = response.RootId!; root.Dispose(); Assert.False(Directory.Exists(root.Path));
        var result = AgentSelfStartupClient.ReadAcknowledgement(response, id, expectedRootId);
        Assert.True(result.Ok); Assert.Equal(expectedRootId, result.RootId); Assert.Equal(id, result.OperationId);
    }

    [Theory]
    [InlineData("not_executed")] [InlineData("outcome_unknown")] [InlineData("operation_in_progress")] [InlineData("operation_id_conflict")]
    public void FailedAcknowledgementRetainsCodeAndIdentityButNeverExposesInternalData(string code)
    {
        using var root = new TempDirectory(); var id = AgentProtocol.NewOperationId();
        var response = Ack(root.Path, id) with { Ok = false, ErrorCode = code, Error = "Historical error", Data = new Action(() => throw new InvalidOperationException()), Replayed = true };
        var result = AgentSelfStartupClient.ReadAcknowledgement(response, id, AgentProtocol.ForRoot(root.Path).RootId);
        Assert.False(result.Ok); Assert.Equal(code, result.ErrorCode); Assert.Equal(id, result.OperationId); Assert.True(result.Replayed); Assert.Null(result.Data);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealMissingPipeTimeoutOrCallerCancelBeforeWriteIsNotUnknown(bool callerCancel)
    {
        using var root = new TempDirectory(); root.Write("autorun.yaml", "version: 1\ntasks: []\n");
        using var cancel = new CancellationTokenSource(); if (callerCancel) { cancel.Cancel(); }
        var client = new AgentSelfStartupClient(new(root.Path), root.Path);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SubmitAsync(Request(), AgentProtocol.NewOperationId(), TimeSpan.FromMilliseconds(30), cancel.Token));
        Assert.Single(Directory.EnumerateFileSystemEntries(root.Path));
    }

    [Theory]
    [InlineData("drop")] [InlineData("data")] [InlineData("operation")] [InlineData("root")] [InlineData("contradiction")] [InlineData("code")]
    public async Task ActualWireWriteThenLossOrInvalidAcknowledgementKeepsOriginalIdentity(string defect)
    {
        using var root = new TempDirectory(); root.Write("autorun.yaml", "version: 1\ntasks: []\n");
        var endpoint = AgentProtocol.ForRoot(root.Path); var id = AgentProtocol.NewOperationId(); var received = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NamedPipeServerStream(endpoint.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serving = Serve();
        try
        {
            var failure = await Assert.ThrowsAsync<AgentOutcomeUnknownException>(() => new AgentSelfStartupClient(new(root.Path), root.Path)
                .SubmitAsync(Request(), id.ToUpperInvariant(), TimeSpan.FromSeconds(5)));
            Assert.Equal(id, failure.OperationId); Assert.False(string.IsNullOrWhiteSpace(failure.RequestId));
            await serving; Assert.Equal(1, received);
        }
        finally { deadline.Cancel(); await serving; }
        Assert.Single(Directory.EnumerateFileSystemEntries(root.Path));

        async Task Serve()
        {
            await server.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(deadline.Token); var request = JsonSerializer.Deserialize<AgentRequest>(line!, AgentProtocol.JsonOptions)!;
            Interlocked.Increment(ref received); Assert.Equal(id, request.OperationId); Assert.Equal("startup-self-set", request.Command);
            Assert.Null(request.Name); Assert.Null(request.ExpectedRevision); Assert.Equal(endpoint.RootId, request.RootId);
            Assert.True(request.Payload!.Value.GetProperty("confirmed").GetBoolean());
            if (defect == "drop") { server.Disconnect(); return; }
            var response = Ack(root.Path, id) with { Id = request.Id };
            response = defect switch
            {
                "data" => response with { Data = Array.Empty<int>() }, "operation" => response with { OperationId = AgentProtocol.NewOperationId() },
                "root" => response with { RootId = new string('0', 64) }, "contradiction" => response with { ErrorCode = "not_executed" },
                _ => response with { Ok = false, ErrorCode = "INVALID" },
            };
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, AgentProtocol.JsonOptions).AsMemory(), deadline.Token);
        }
    }

    private static SelfStartupMutationRequest Request() => new()
    { SchemaVersion = 1, Enabled = true, Confirmed = true, TargetContextId = new string('a', 64), ExpectedRegistration = new(false) };
    private static AgentResponse Ack(string root, string id) => new("ack", true, new { opaque = "history" }, RootId: AgentProtocol.ForRoot(root).RootId, OperationId: id);
}
