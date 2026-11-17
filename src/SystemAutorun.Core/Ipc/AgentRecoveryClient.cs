using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Recovery;

namespace SystemAutorun.Core.Ipc;

// Transport contract only. Recovery intent is name-scoped, not configuration CAS,
// current health or authority to launch. The workflow owns eligibility and pending IDs.
public sealed class AgentRecoveryClient(AgentClient client)
{
    public async Task<RecoveryControlSnapshot> ReadAsync(TimeSpan? timeout = null, CancellationToken token = default)
    {
        var rootId = client.RootId;
        try
        {
            var response = await client.SendResponseAsync("recovery-state", timeout: timeout,
                cancellationToken: token, requireData: true).ConfigureAwait(false);
            return ReadState(response, rootId);
        }
        catch (Exception error) when (Malformed(error))
        { throw new AgentCommandException("protocol_mismatch", "Agent returned a malformed recovery state envelope."); }
    }

    // Caller confirms a fresh initialized/paused observation and durably reserves ID
    // BEFORE dispatch. No generated ID, persistence, initialization, retry or rebase here.
    public async Task<AgentResponse> SubmitResumeAsync(string name, string expectedRevision, string operationId,
        bool confirmed, TimeSpan? timeout = null, CancellationToken token = default)
    {
        RequireName(name);
        if (!Digest(expectedRevision)) { throw new ArgumentException("A recovery-state revision is required.", nameof(expectedRevision)); }
        if (!confirmed) { throw new AgentCommandException("authorization_required", "Explicit confirmation is required before resuming recovery."); }
        var id = OperationJournal.NormalizeId(operationId);
        var rootId = client.RootId;
        var response = await client.SendResponseAsync("recovery-resume", name, timeout: timeout,
            cancellationToken: token, expectedRevision: expectedRevision, operationId: id, requireData: true).ConfigureAwait(false);
        return ReadAcknowledgement(response, name, id, rootId);
    }

    public static RecoveryControlSnapshot ReadState(AgentResponse response, string expectedRootId)
    {
        try
        {
            RequireEnvelope(response, expectedRootId, null);
            if (response.Replayed) { throw new JsonException("A state read cannot be an operation replay."); }
            if (!response.Ok) { throw new AgentCommandException(response.ErrorCode!, "Recovery state is unavailable.", response.Revision, requestId: response.Id); }
            return ReadSnapshot(response);
        }
        catch (Exception error) when (Malformed(error))
        { throw new AgentCommandException("protocol_mismatch", "Agent returned invalid recovery state; no mutation was requested."); }
    }

    // A validated ACK (possibly replayed history), never a current-state observation or
    // permission to clear pending. Error codes including outcome_unknown stay intact.
    public static AgentResponse ReadAcknowledgement(AgentResponse response, string name, string operationId, string expectedRootId)
    {
        RequireName(name);
        var id = OperationJournal.NormalizeId(operationId);
        try
        {
            RequireEnvelope(response, expectedRootId, id);
            if (!response.Ok) { return response with { Data = null }; }
            var state = ReadSnapshot(response);
            var entry = state.Tasks.FirstOrDefault(task => task.TaskName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!state.Initialized || entry is not { Paused: false, IntentId: not null })
            { throw new JsonException("Resume acknowledgement has no matching cleared intent."); }
            return response with { Data = state };
        }
        catch (Exception error) when (Malformed(error))
        {
            var requestId = !string.IsNullOrWhiteSpace(response.Id) && response.Id.Length <= AgentProtocol.MaximumRequestIdCharacters ? response.Id : null;
            throw new AgentOutcomeUnknownException(id, error, requestId);
        }
    }

    private static void RequireEnvelope(AgentResponse response, string rootId, string? operationId)
    {
        if (!Digest(rootId) || response.ProtocolVersion != AgentProtocol.Version || response.RootId != rootId
            || response.OperationId != operationId || string.IsNullOrWhiteSpace(response.Id)
            || response.Id.Length > AgentProtocol.MaximumRequestIdCharacters
            || response.Ok && (response.Error is not null || response.ErrorCode is not null)
            || !response.Ok && (response.ErrorCode is not { Length: >= 1 and <= 64 } code
                || code.Any(value => value is not (>= 'a' and <= 'z') and not '_'))
            || response.Revision is not null && !Digest(response.Revision))
        { throw new JsonException("Invalid recovery envelope."); }
    }

    private static RecoveryControlSnapshot ReadSnapshot(AgentResponse response)
    {
        var data = response.Data is JsonElement element ? element : JsonSerializer.SerializeToElement(response.Data, AgentProtocol.JsonOptions);
        Object(data);
        if (Encoding.UTF8.GetByteCount(data.GetRawText()) > RecoveryControlStore.MaximumBytes + 512)
        { throw new JsonException("Recovery snapshot exceeds its bound."); }
        var revision = data.GetProperty("revision").GetString();
        var initialized = data.GetProperty("initialized").GetBoolean();
        var tasks = data.GetProperty("tasks");
        if (!Digest(revision) || revision != response.Revision || tasks.ValueKind != JsonValueKind.Array
            || tasks.GetArrayLength() > RecoveryControlStore.MaximumEntries || !initialized && tasks.GetArrayLength() != 0)
        { throw new JsonException("Invalid recovery snapshot identity or collection."); }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks.EnumerateArray())
        {
            Object(task);
            var name = task.GetProperty("taskName").GetString();
            if (!ValidName(name) || !names.Add(name!)) { throw new JsonException("Invalid or duplicate recovery task name."); }
            _ = task.GetProperty("paused").GetBoolean();
            foreach (var field in new[] { "pausedAt", "attemptId", "taskIdentity", "lastAttemptAt", "outcome", "completedAt", "detail", "intentId" })
            { _ = task.GetProperty(field); } // Required presence: DTO defaults are not observations.
            foreach (var field in new[] { "intentId", "attemptId" })
            {
                var value = task.GetProperty(field);
                if (value.ValueKind != JsonValueKind.Null && !CanonicalId(value.GetString()))
                { throw new JsonException("Invalid recovery intent/attempt identity."); }
            }
            var identity = task.GetProperty("taskIdentity");
            if (identity.ValueKind != JsonValueKind.Null && !Digest(identity.GetString())) { throw new JsonException("Invalid workload identity."); }
            var outcome = task.GetProperty("outcome");
            if (outcome.ValueKind != JsonValueKind.Null && (outcome.ValueKind != JsonValueKind.String
                || outcome.GetString() is not ("admitted" or "succeeded" or "failed" or "outcome_unknown")))
            { throw new JsonException("Invalid recovery outcome."); }
            var detail = task.GetProperty("detail");
            if (detail.ValueKind != JsonValueKind.Null && (detail.ValueKind != JsonValueKind.String || detail.GetString()!.Length > 512))
            { throw new JsonException("Invalid recovery detail."); }
        }
        var state = data.Deserialize<RecoveryControlSnapshot>(AgentProtocol.JsonOptions) ?? throw new JsonException("Missing recovery state.");
        foreach (var task in state.Tasks)
        {
            // Wire integrity matches the persisted record skeleton, not recovery policy.
            if (task.Paused != (task.PausedAt is not null)
                || task.AttemptId is null && (task.TaskIdentity is not null || task.LastAttemptAt is not null
                    || task.Outcome is not null || task.CompletedAt is not null || task.Detail is not null)
                || task.AttemptId is not null && (task.TaskIdentity is null || task.LastAttemptAt is null || task.Outcome is null
                    || (task.Outcome == RecoveryAttemptOutcome.Admitted) != (task.CompletedAt is null)))
            { throw new JsonException("Contradictory recovery record skeleton."); }
        }
        return state with { Tasks = Array.AsReadOnly(state.Tasks.ToArray()) };
    }

    private static void Object(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || element.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != element.EnumerateObject().Count())
        { throw new JsonException("Missing object or ambiguous recovery fields."); }
    }
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 256 && !name.Any(char.IsControl);
    private static void RequireName(string name)
    { if (!ValidName(name)) { throw new ArgumentException("A task name of 1–256 non-control characters is required.", nameof(name)); } }
    private static bool CanonicalId(string? value) => value is { Length: 32 } && Guid.TryParseExact(value, "N", out var id) && id.Version == 7 && id.ToString("N") == value;
    private static bool Malformed(Exception error) => error is not AgentCommandException
        && error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or NotSupportedException;
}
