using System.Text;
using System.Text.Json;

namespace SystemAutorun.Core.Ipc;

// A journal observation, never a business ACK or permission to replay a mutation.
public sealed class AgentOperationClient(AgentClient client)
{
    public async Task<OperationReceipt?> ReadAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var id = OperationJournal.NormalizeId(operationId);
        try
        {
            var response = await client.SendResponseAsync("operation", name: id,
                cancellationToken: cancellationToken, requireData: true).ConfigureAwait(false);
            return ReadReceipt(response, id);
        }
        catch (JsonException)
        { throw new AgentCommandException("protocol_mismatch", "Agent returned a malformed operation query envelope."); }
    }

    public static OperationReceipt? ReadReceipt(AgentResponse response, string operationId)
    {
        var id = OperationJournal.NormalizeId(operationId);
        // The current RPC represents an unavailable receipt as this explicit error;
        // it is not evidence that the mutation was never accepted or can be replayed.
        if (!response.Ok && response.ErrorCode == "operation_not_found") { return null; }
        if (!response.Ok)
        { throw new AgentCommandException(response.ErrorCode ?? "command_failed", response.Error ?? "Operation query failed.", response.Revision, response.OperationId, response.Id); }
        try
        {
            var data = response.Data is JsonElement element ? element : JsonSerializer.SerializeToElement(response.Data, AgentProtocol.JsonOptions);
            if (data.ValueKind == JsonValueKind.Null) { return null; }
            RequireObject(data);
            if (Encoding.UTF8.GetByteCount(data.GetRawText()) > new OperationJournalLimits().MaximumReceiptBytes
                || data.GetProperty("schemaVersion").GetInt32() != OperationReceipt.CurrentSchemaVersion
                || data.GetProperty("operationId").GetString() != id
                || data.GetProperty("state").GetString() is not ("running" or "completed" or "unknown"))
            { throw new JsonException("Invalid journal identity, schema, state or bound."); }
            // Require presence; constructor defaults must not turn malformed input into an observation.
            _ = data.GetProperty("command"); _ = data.GetProperty("name"); _ = data.GetProperty("fingerprint");
            _ = data.GetProperty("ownerInstanceId"); _ = data.GetProperty("acceptedAt");
            _ = data.GetProperty("completedAt"); _ = data.GetProperty("response");
            _ = data.GetProperty("resultOmitted").GetBoolean();
            var receipt = data.Deserialize<OperationReceipt>(AgentProtocol.JsonOptions) ?? throw new JsonException("Missing receipt.");
            if (string.IsNullOrWhiteSpace(receipt.Command) || receipt.Command.Length > AgentProtocol.MaximumCommandCharacters
                || receipt.Name?.Length > 256 || !Hash(receipt.Fingerprint) || string.IsNullOrWhiteSpace(receipt.OwnerInstanceId)
                || receipt.OwnerInstanceId.Length > 128 || receipt.AcceptedAt == default
                || receipt.State == OperationState.Completed && (receipt.CompletedAt is null || receipt.Response is null && !receipt.ResultOmitted)
                || receipt.State == OperationState.Running && (receipt.CompletedAt is not null || receipt.Response is not null || receipt.ResultOmitted))
            { throw new JsonException("Invalid journal header or terminal skeleton."); }
            if (receipt.Response is { } historical)
            {
                var wire = data.GetProperty("response"); RequireObject(wire);
                _ = wire.GetProperty("ok").GetBoolean();
                // Retained history may predate the live wire protocol; only the outer
                // query must match its current version. Future/invalid history stays closed.
                var historicalVersion = wire.GetProperty("protocolVersion").GetInt32();
                if (historicalVersion < 1 || historicalVersion > AgentProtocol.Version
                    || wire.GetProperty("operationId").GetString() != id
                    || string.IsNullOrWhiteSpace(historical.Id) || historical.Id.Length > AgentProtocol.MaximumRequestIdCharacters)
                { throw new JsonException("Mismatched historical response."); }
                // Its request/root IDs belong to the ORIGINAL exchange, not this query.
                // Its Data is opaque history and is never applied to current business state.
            }
            return receipt;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new AgentCommandException("protocol_mismatch", "Agent returned an invalid or mismatched operation observation.", response.Revision, requestId: response.Id); }
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.EnumerateObject().Count())
        { throw new JsonException("Missing object or duplicate journal fields."); }
    }
}
