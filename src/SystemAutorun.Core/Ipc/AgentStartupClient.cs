using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Ipc;

public sealed partial class AgentStartupClient(AgentClient client)
{
    public async Task<StartupInventorySnapshot> ReadAsync(StartupListQuery query, CancellationToken cancellationToken = default)
    {
        query = query.Validate();
        return ReadObservation(await client.SendAsync<JsonElement>("startup-list", payload: query,
            cancellationToken: cancellationToken).ConfigureAwait(false), query);
    }

    public async Task<WorkspaceMutation<StartupAdoptionResult>> AdoptAsync(string entryId, StartupAdoptionRequest request,
        string expectedRevision, string? operationId = null, CancellationToken cancellationToken = default)
    {
        _ = request.Validate(entryId);
        var id = OperationJournal.NormalizeId(operationId ?? AgentProtocol.NewOperationId());
        var response = await client.SendResponseAsync("startup-adopt", entryId, request, cancellationToken: cancellationToken,
            expectedRevision: expectedRevision, operationId: id).ConfigureAwait(false);
        return ReadAdoptionReceipt(response, entryId, request.Task.Name, id);
    }

    public async Task<OperationReceipt?> ReadAdoptionOperationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var receipt = await new AgentOperationClient(client).ReadAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (receipt is not null && receipt.Command != "startup-adopt")
        { throw new AgentCommandException("protocol_mismatch", "The original operation is not a startup adoption."); }
        return receipt;
    }

    public static WorkspaceMutation<StartupAdoptionResult> ReadAdoptionReceipt(AgentResponse response,
        string entryId, string taskName, string operationId)
    {
        if (!response.Ok)
        { throw new AgentCommandException(response.ErrorCode ?? "command_failed", response.Error ?? "Adoption failed.", response.Revision, response.OperationId, response.Id); }
        try
        {
            var data = response.Data is JsonElement element ? element : JsonSerializer.SerializeToElement(response.Data, AgentProtocol.JsonOptions);
            var value = data.GetProperty("value");
            if (value.GetProperty("nativeWritePerformed").GetBoolean() || value.GetProperty("executionRequested").GetBoolean())
            { throw new JsonException("Adoption receipt claims an unexpected side effect."); }
            var result = data.Deserialize<WorkspaceMutation<StartupAdoptionResult>>(AgentProtocol.JsonOptions)
                ?? throw new JsonException("Missing adoption receipt.");
            if (response.OperationId != operationId || result.Revision is not { Length: 64 } revision || !revision.All(char.IsAsciiHexDigit)
                || revision != response.Revision || result.Value?.EntryId != entryId || result.Value.Configuration is not { Succeeded: true, Action: "add" } configuration
                || configuration.TaskName != taskName.Trim() || result.CapturedAt == default)
            { throw new JsonException("Mismatched adoption receipt."); }
            return result;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new AgentOutcomeUnknownException(operationId, exception, response.Id); }
    }

    public static StartupInventorySnapshot ReadObservation(JsonElement data, StartupListQuery query)
    {
        query = query.Validate();
        try
        {
            if (data.GetProperty("schemaVersion").GetInt32() != 1) { throw new JsonException("Unsupported startup schema."); }
            _ = data.GetProperty("available").GetBoolean();
            _ = data.GetProperty("hasEntryErrors").GetBoolean();
            var result = data.Deserialize<StartupInventorySnapshot>(AgentProtocol.JsonOptions) ?? throw new JsonException("Missing startup observation.");
            if (data.GetProperty("countsAreLowerBounds").GetBoolean() != result.Available
                || result.ConfigurationRevision is not { Length: 64 } revision || !revision.All(char.IsAsciiHexDigit)
                || result.Entries is null || result.Entries.Count > query.Limit
                || result.SourceIssues is null || result.SourceIssues.Count > 6 || result.ReadableSources is < 0 or > 6
                || result.SourceIssues.Any(issue => !StartupListQuery.ValidIssue(issue)
                    || (issue.Source == "backups" ? query.State == "active" : !query.IncludesSource(issue.Source)))
                || result.SourceIssues.Select(issue => issue.Source).Distinct(StringComparer.Ordinal).Count() != result.SourceIssues.Count
                || DateTimeOffset.UtcNow - result.CompletedAt > TimeSpan.FromSeconds(30)
                || result.CompletedAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(5))
            { throw new JsonException("Invalid startup header or bounds."); }
            if (result.Available)
            {
                if (result.ObservedAt != result.CompletedAt || result.ScannedEntries is not (>= 0 and <= 4096)
                    || result.MatchedEntries is not >= 0 || result.MatchedEntries > result.ScannedEntries
                    || result.Entries.Count > result.MatchedEntries
                    || result.Reason != (result.SourceIssues.Count > 0 ? "startup_source_unavailable" : result.HasEntryErrors ? "startup_entry_unavailable" : null)
                    || result.ReadableSources == 0 && result.Entries.Count == 0)
                { throw new JsonException("Incomplete startup observation."); }
            }
            else if (result.ObservedAt is not null || result.Entries.Count != 0 || result.ScannedEntries is not null
                || result.MatchedEntries is not null || string.IsNullOrWhiteSpace(result.Reason)
                || result.ReadableSources != 0 || result.HasEntryErrors || result.Truncated)
            { throw new JsonException("Unavailable startup data must remain unknown."); }
            var ids = new HashSet<string>(StringComparer.Ordinal); var bytes = 0; var index = 0;
            foreach (var entry in result.Entries)
            {
                if (entry is null || !StartupListQuery.ValidEntry(entry) || !query.Matches(entry) || !ids.Add(entry.Id)
                    || !string.IsNullOrEmpty(entry.Error) && !result.HasEntryErrors)
                { throw new JsonException("Mismatched startup entry."); }
                var wire = data.GetProperty("entries")[index++];
                if (wire.TryGetProperty("metadataDigest", out var digest) && digest.GetString() != entry.MetadataDigest)
                { throw new JsonException("Mismatched startup metadata digest."); }
                bytes += JsonSerializer.SerializeToUtf8Bytes(entry, AgentProtocol.JsonOptions).Length;
                if (bytes > StartupListQuery.MaximumSerializedEntriesBytes) { throw new JsonException("Oversized startup observation."); }
            }
            return result;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new AgentCommandException("protocol_mismatch", "Agent returned an invalid or mismatched startup observation."); }
    }
}
