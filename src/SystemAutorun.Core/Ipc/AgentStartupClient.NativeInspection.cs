using System.Text.Json;
using System.Text.Json.Serialization;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Ipc;

public sealed partial class AgentStartupClient
{
    public async Task<StartupNativeInspectionSnapshot> InspectNativeAsync(string entryId, StartupInspectionRequest request,
        string expectedRevision, CancellationToken cancellationToken = default)
    {
        _ = StartupNativeInspectionPolicy.ValidateRequest(entryId, request);
        var response = await client.SendResponseAsync("startup-inspect-native", entryId, request,
            expectedRevision: expectedRevision, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ReadNativeInspection(response, entryId, request, expectedRevision);
    }

    public static StartupNativeInspectionSnapshot ReadNativeInspection(AgentResponse response, string entryId,
        StartupInspectionRequest request, string expectedRevision)
    {
        _ = StartupNativeInspectionPolicy.ValidateRequest(entryId, request);
        if (!response.Ok)
        { throw new AgentCommandException(response.ErrorCode ?? "command_failed", response.Error ?? "Native observation failed.", response.Revision, response.OperationId, response.Id); }
        try
        {
            var data = response.Data is JsonElement element ? element : JsonSerializer.SerializeToElement(response.Data, AgentProtocol.JsonOptions);
            var strict = new JsonSerializerOptions(AgentProtocol.JsonOptions)
            { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
            var result = data.Deserialize<StartupNativeInspectionSnapshot>(strict) ?? throw new JsonException("Missing native observation.");
            // Reuse the full schema-1 validator, including RAW computed entry fields, actions,
            // requested lexical preview and the unchanged no-native-authority contract.
            var metadata = ReadInspection(response with { Data = data.GetProperty("metadata") }, entryId, request, expectedRevision);
            if (result.SchemaVersion != 2 || result.ConfigurationRevision != expectedRevision
                || !StartupNativeInspectionPolicy.LowerHex(expectedRevision, 64)
                || result.ValidationScope != "sequential-independent" || result.HistoricalIdentity != "unverified"
                || result.NativeWriteReady || result.NativeWritePerformed || result.ExecutionRequested
                || result.CompletedAt == default || result.CompletedAt < metadata.CompletedAt
                || DateTimeOffset.UtcNow - result.CompletedAt > TimeSpan.FromSeconds(30)
                || result.CompletedAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(5)
                || !StartupNativeInspectionPolicy.ValidEvidence(new(result.Slots, result.RecordMetadataDigestAfter), metadata.Entry)
                || metadata.CompletedAt < result.Slots.Original.ObservedAt)
            { throw new JsonException("Mismatched native evidence or unexpected authority."); }
            return result with { Metadata = metadata };
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new AgentCommandException("protocol_mismatch", "Agent returned invalid native startup evidence; no native submission is authorized."); }
    }
}
