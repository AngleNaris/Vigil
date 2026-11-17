using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Core.Tests;

public sealed class AgentOperationClientTests
{
    private static readonly string Id = AgentProtocol.NewOperationId();
    private static OperationReceipt Receipt(OperationState state = OperationState.Completed, bool omitted = false) =>
        new(OperationReceipt.CurrentSchemaVersion, Id, "startup-adopt", "original-entry", new string('a', 64), "older-agent-instance",
            state, DateTimeOffset.UtcNow, state == OperationState.Completed ? DateTimeOffset.UtcNow : null,
            state == OperationState.Completed && !omitted ? new("original-mutation-request", true, new { opaque = "never applied" }, OperationId: Id) : null, omitted);
    private static AgentResponse Reply(object? data) => new("different-query-request", true, data, RootId: "current-root");
    private static JsonObject Wire(OperationReceipt? receipt = null) => JsonSerializer.SerializeToNode(receipt ?? Receipt(), AgentProtocol.JsonOptions)!.AsObject();
    private static AgentCommandException Reject(JsonNode node) => Assert.Throws<AgentCommandException>(() =>
        AgentOperationClient.ReadReceipt(Reply(JsonSerializer.SerializeToElement(node)), Id));

    [Theory]
    [InlineData(OperationState.Running, false)]
    [InlineData(OperationState.Unknown, false)]
    [InlineData(OperationState.Completed, false)]
    [InlineData(OperationState.Completed, true)]
    public void LegalJournalShapesAreHistoryNotQueryAcknowledgements(OperationState state, bool omitted)
    {
        var receipt = Receipt(state, omitted);
        var result = AgentOperationClient.ReadReceipt(Reply(receipt), Id.ToUpperInvariant());
        Assert.Equal(JsonSerializer.Serialize(receipt, AgentProtocol.JsonOptions), JsonSerializer.Serialize(result, AgentProtocol.JsonOptions));
        Assert.Equal("older-agent-instance", result!.OwnerInstanceId);
        Assert.Null(result.Response?.RootId);
        if (result.Response is not null) { Assert.Equal("original-mutation-request", result.Response.Id); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(AgentProtocol.Version)]
    public void HistoricalProtocolIsNotTheCurrentQueryProtocol(int version)
    {
        var receipt = Receipt(); receipt = receipt with { Response = receipt.Response! with { ProtocolVersion = version } };
        var observed = AgentOperationClient.ReadReceipt(Reply(receipt), Id)!;
        Assert.Equal(version, observed.Response!.ProtocolVersion);
        Assert.Equal("original-mutation-request", observed.Response.Id);
    }

    [Fact]
    public void CompletedFailureAndCompletedUnknownAreNotBusinessSuccess()
    {
        var response = new AgentResponse("original", false, Error: "rejected", ErrorCode: "invalid_argument", OperationId: Id);
        var failed = Receipt() with { Response = response };
        Assert.False(AgentOperationClient.ReadReceipt(Reply(failed), Id)!.Response!.Ok);
        var unknown = failed with { State = OperationState.Unknown, Response = response with { ErrorCode = "outcome_unknown" }, ResultOmitted = true };
        Assert.Equal(OperationState.Unknown, AgentOperationClient.ReadReceipt(Reply(unknown), Id)!.State);
    }

    [Fact]
    public void NullIsUnavailableWhileUndefinedIsMalformed()
    {
        Assert.Null(AgentOperationClient.ReadReceipt(Reply(JsonSerializer.SerializeToElement<object?>(null)), Id));
        Assert.Equal("protocol_mismatch", Assert.Throws<AgentCommandException>(() =>
            AgentOperationClient.ReadReceipt(Reply(default(JsonElement)), Id)).Code);
    }

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("operationId", "\"another-id\"")]
    [InlineData("state", "3")]
    [InlineData("state", "\"succeeded\"")]
    [InlineData("command", "null")]
    [InlineData("fingerprint", "\"not-a-hash\"")]
    [InlineData("ownerInstanceId", "\" \"")]
    [InlineData("acceptedAt", "\"0001-01-01T00:00:00+00:00\"")]
    [InlineData("completedAt", "null")]
    [InlineData("response", "null")]
    [InlineData("resultOmitted", "\"false\"")]
    public void InvalidIdentityAndTerminalSkeletonFailClosed(string field, string json)
    {
        var data = Wire(); data[field] = JsonNode.Parse(json);
        Assert.Equal("protocol_mismatch", Reject(data).Code);
    }

    [Theory]
    [InlineData("schemaVersion")] [InlineData("operationId")] [InlineData("command")]
    [InlineData("name")] [InlineData("fingerprint")] [InlineData("ownerInstanceId")]
    [InlineData("state")] [InlineData("acceptedAt")] [InlineData("completedAt")]
    [InlineData("response")] [InlineData("resultOmitted")]
    public void MissingFieldsCannotDefaultIntoAReceipt(string field)
    {
        var data = Wire(); data.Remove(field); Assert.Equal("protocol_mismatch", Reject(data).Code);
    }

    [Theory]
    [InlineData("ok", "null")] [InlineData("protocolVersion", "999")] [InlineData("protocolVersion", "0")]
    [InlineData("operationId", "\"other-operation\"")]
    public void HistoricalResponseIdentityAndStatusAreChecked(string field, string json)
    {
        var data = Wire(); data["response"]![field] = JsonNode.Parse(json);
        Assert.Equal("protocol_mismatch", Reject(data).Code);
    }

    [Fact]
    public void DuplicateCaseInsensitiveFieldsAndRunningTerminalDataAreRejected()
    {
        var raw = JsonSerializer.Serialize(Receipt(), AgentProtocol.JsonOptions);
        var duplicated = JsonSerializer.Deserialize<JsonElement>(raw[..^1] + ",\"OperationId\":\"" + Id + "\"}");
        Assert.Equal("protocol_mismatch", Assert.Throws<AgentCommandException>(() => AgentOperationClient.ReadReceipt(Reply(duplicated), Id)).Code);
        Assert.Equal("protocol_mismatch", Reject(Wire(Receipt() with { State = OperationState.Running })).Code);
    }

    [Fact]
    public void ExplicitNotFoundIsUnavailableNotPermissionToReplay()
    {
        var response = new AgentResponse("query-missing", false, new { fake = "not trusted" }, Error: "not retained", ErrorCode: "operation_not_found");
        Assert.Null(AgentOperationClient.ReadReceipt(response, Id));
    }

    [Fact]
    public void QueryFailurePreservesQueryMetadataAndNeverParsesCarriedEvidence()
    {
        var response = new AgentResponse("query-error", false, new { fake = "evidence" }, Error: "unreadable", ErrorCode: "operation_store_corrupt");
        var error = Assert.Throws<AgentCommandException>(() => AgentOperationClient.ReadReceipt(response, Id));
        Assert.Equal("operation_store_corrupt", error.Code); Assert.Equal("query-error", error.RequestId);
    }
}
