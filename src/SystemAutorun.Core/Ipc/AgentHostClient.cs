using System.Text.Json;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Ipc;

// An explicit observation, not an implicit daemon launcher or revision cursor update.
public sealed class AgentHostClient(AgentClient client)
{
    public async Task<RemoteHostProbeResult> ReadRemoteAsync(string id, CancellationToken cancellationToken = default) =>
        ReadObservation(await client.SendAsync<JsonElement>("remote-status", id, cancellationToken: cancellationToken).ConfigureAwait(false), id);

    public static RemoteHostProbeResult ReadObservation(JsonElement data, string id)
    {
        try
        {
            if (data.GetProperty("schemaVersion").GetInt32() != 1 || data.GetProperty("source").GetString() != "configured-ssh")
            { throw new JsonException("Unsupported remote observation header."); }
            var result = data.Deserialize<RemoteHostProbeResult>(AgentProtocol.JsonOptions) ?? throw new JsonException("Missing remote observation.");
            if (result.Snapshot is null || result.Available != result.Snapshot.Online
                || !string.Equals(result.Snapshot.Id, id, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(result.Revision) || result.Revision.Length != 64 || !result.Revision.All(char.IsAsciiHexDigit)
                || !result.Available && string.IsNullOrWhiteSpace(result.Reason)
                || result.Available && result.Reason is not null)
            { throw new JsonException("Mismatched or incomplete remote observation."); }
            var observed = data.GetProperty("observedAt");
            if (result.Available ? observed.GetDateTimeOffset() != result.Snapshot.CapturedAt : observed.ValueKind != JsonValueKind.Null)
            { throw new JsonException("Unavailable or mismatched observation time."); }
            return result;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new AgentCommandException("protocol_mismatch", "Agent returned an invalid or mismatched remote observation."); }
    }
}
