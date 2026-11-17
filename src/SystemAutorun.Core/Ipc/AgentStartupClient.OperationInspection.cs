using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Ipc;

public sealed partial class AgentStartupClient
{
    public async Task<StartupOperationInspectionSnapshot> InspectOperationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var id = OperationJournal.NormalizeId(operationId);
        return ReadOperationInspection(await client.SendResponseAsync("startup-operation-inspect", name: id,
            cancellationToken: cancellationToken, requireData: true).ConfigureAwait(false), id);
    }

    public static StartupOperationInspectionSnapshot ReadOperationInspection(AgentResponse response, string operationId)
    {
        var id = OperationJournal.NormalizeId(operationId);
        if (!response.Ok)
        { throw new AgentCommandException(response.ErrorCode ?? "command_failed", "Operation inspection failed; inspect its error code. No continuation or native action is authorized.", requestId: response.Id); }
        try
        {
            if (response.ProtocolVersion != AgentProtocol.Version || response.OperationId is not null || response.Replayed || response.Revision is not null
                || response.Error is not null || response.ErrorCode is not null)
            { throw new JsonException("Unexpected diagnostic envelope or mutation identity."); }
            var data = response.Data is JsonElement element ? element : JsonSerializer.SerializeToElement(response.Data, AgentProtocol.JsonOptions);
            if (Encoding.UTF8.GetByteCount(data.GetRawText()) > StartupOperationInspectionPolicy.MaximumBytes)
            { throw new JsonException("Oversized operation inspection."); }
            RequireDiagnosticObject(data, ["schemaVersion", "queriedOperationId", "completedAt", "observedAt", "diagnosticOnly", "journalState", "journalReason",
                "persistedReceiptState", "observedReceiptState", "evidence", "nativeWriteReady", "executionRequested", "transactionFinalized", "mayFinalize", "receiptUpdated", "replayRequested"]);
            var evidence = data.GetProperty("evidence");
            if (evidence.ValueKind != JsonValueKind.Null)
            { RequireDiagnosticObject(evidence, ["state", "reason", "associationState", "associationReason", "recordId", "transactionId", "nativeState", "nativeReason"]); }
            var strict = new JsonSerializerOptions(AgentProtocol.JsonOptions)
            { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
            var result = data.Deserialize<StartupOperationInspectionSnapshot>(strict) ?? throw new JsonException("Missing operation inspection.");
            StartupOperationInspectionPolicy.Validate(result, id);
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        { throw new AgentCommandException("protocol_mismatch", "Agent returned an invalid operation diagnostic; no native action is authorized."); }
    }

    private static void RequireDiagnosticObject(JsonElement value, IReadOnlyList<string> fields)
    {
        if (value.ValueKind != JsonValueKind.Object) { throw new JsonException("Diagnostic object is missing."); }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!fields.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
            { throw new JsonException("Diagnostic member is noncanonical or duplicated."); }
        }
        if (seen.Count != fields.Count) { throw new JsonException("Diagnostic member is missing."); }
    }
}
