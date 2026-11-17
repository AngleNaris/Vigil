using System.Text.Json;
using System.Text.Json.Serialization;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Ipc;

public sealed partial class AgentStartupClient
{
    public async Task<StartupInspectionSnapshot> InspectAsync(string entryId, StartupInspectionRequest request,
        string expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = request.Validate(entryId);
        var response = await client.SendResponseAsync("startup-inspect", entryId, request,
            expectedRevision: expectedRevision, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ReadInspection(response, entryId, request, expectedRevision);
    }

    public static StartupInspectionSnapshot ReadInspection(AgentResponse response, string entryId,
        StartupInspectionRequest request, string expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = request.Validate(entryId);
        if (!response.Ok)
        { throw new AgentCommandException(response.ErrorCode ?? "command_failed", response.Error ?? "Inspection failed.", response.Revision, response.OperationId, response.Id); }
        try
        {
            var data = response.Data is JsonElement element ? element : JsonSerializer.SerializeToElement(response.Data, AgentProtocol.JsonOptions);
            var strict = new JsonSerializerOptions(AgentProtocol.JsonOptions)
            { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
            var result = data.Deserialize<StartupInspectionSnapshot>(strict) ?? throw new JsonException("Missing inspection.");
            if (result.SchemaVersion != 1 || expectedRevision is not { Length: 64 } || !expectedRevision.All(char.IsAsciiHexDigit)
                || result.ConfigurationRevision != expectedRevision || response.Revision != expectedRevision
                || response.OperationId is not null || response.Replayed || response.Error is not null || response.ErrorCode is not null
                || result.NativeWriteReady || result.NativeWritePerformed || result.ExecutionRequested
                || result.CompletedAt == default || result.ObservedAt != result.CompletedAt
                || DateTimeOffset.UtcNow - result.CompletedAt > TimeSpan.FromSeconds(30)
                || result.CompletedAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(5)
                || result.Entry is null || !StartupListQuery.ValidEntry(result.Entry)
                || result.Entry.Id != entryId || result.Entry.Source != request.Source || result.Entry.Name != request.Name
                || result.Entry.IsDisabled != request.IsDisabled || result.MetadataDigest != request.MetadataDigest
                || result.MetadataDigest != result.Entry.MetadataDigest
                || data.GetProperty("entry").GetProperty("metadataDigest").GetString() != result.MetadataDigest
                || data.GetProperty("entry").GetProperty("isManaged").GetBoolean() != result.Entry.IsManaged
                || result.OriginalBackupState != (result.Entry.IsDisabled ? "not_checked" : "not_applicable")
                || result.Actions is null || !result.Actions.SequenceEqual(StartupInspectionPolicy.Describe(result.Entry))
                || !StartupRebuildInputPolicy.Matches(result.RebuildPreview, request.RebuildInput))
            { throw new JsonException("Mismatched inspection or unexpected native authority/side effects."); }
            return result;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new AgentCommandException("protocol_mismatch", "Agent returned an invalid startup inspection; no native submission is authorized."); }
    }
}
