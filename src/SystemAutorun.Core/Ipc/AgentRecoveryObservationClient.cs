using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Recovery;

namespace SystemAutorun.Core.Ipc;

// Two fresh Agent reads, NOT a new RPC or configuration CAS. Only the eligibility
// fields used here are interpreted; unrelated configuration/preferences stay opaque.
public sealed class AgentRecoveryObservationClient
{
    private readonly AgentClient _client;
    private readonly string _root;
    private readonly string _rootId;
    private readonly string _subject;
    private readonly string _name;
    public AgentRecoveryObservationClient(AgentClient client, ProjectPaths paths, string taskName)
    {
        if (!RecoveryPendingOperation.Name(taskName)) { throw new ArgumentException("A valid task name is required.", nameof(taskName)); }
        _client = client ?? throw new ArgumentNullException(nameof(client)); _root = ProjectPaths.Resolve(paths.RootDirectory).RootDirectory;
        _rootId = AgentProtocol.ForRoot(_root).RootId; _subject = CurrentUserIdentity.Key; _name = taskName;
    }

    public async Task<RecoveryObservation> ReadAsync(TimeSpan? timeout = null, CancellationToken token = default)
    {
        RequireScope();
        try
        {
            var response = await _client.SendResponseAsync("configuration", timeout: timeout, cancellationToken: token, requireData: true).ConfigureAwait(false);
            RequireScope();
            if (response.RootId != _rootId || response.OperationId is not null || response.Replayed)
            { throw new JsonException("Configuration read scope changed."); }
            if (!response.Ok) { throw new AgentCommandException(response.ErrorCode ?? "command_failed", "Current recovery eligibility is unavailable.", requestId: response.Id); }
            var data = response.Data is JsonElement element ? element : JsonSerializer.SerializeToElement(response.Data, AgentProtocol.JsonOptions);
            Object(data); var revision = data.GetProperty("revision").GetString();
            if (!RecoveryPendingOperation.Digest(revision) || response.Revision != revision || data.GetProperty("capturedAt").GetDateTimeOffset() == default)
            { throw new JsonException("Missing current configuration revision/time."); }
            var configuration = data.GetProperty("configuration"); Object(configuration);
            if (configuration.GetProperty("rootDirectory").GetString() != _root)
            { throw new JsonException("Configuration root/schema mismatch."); }
            var tasks = configuration.GetProperty("tasks");
            if (tasks.ValueKind != JsonValueKind.Array) { throw new JsonException("Missing tasks."); }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); string? reason = "not_found";
            foreach (var task in tasks.EnumerateArray())
            {
                Object(task); var name = task.GetProperty("name").GetString();
                if (!RecoveryPendingOperation.Name(name) || !names.Add(name!)) { throw new JsonException("Invalid/duplicate task identity."); }
                if (!name!.Equals(_name, StringComparison.OrdinalIgnoreCase)) { continue; }
                // Only the selected task's actual admission fields are authority.
                // Schema version, task type, target and unrelated rows stay opaque.
                var enabled = task.GetProperty("enabled").GetBoolean();
                var launch = task.GetProperty("launch"); Object(launch);
                var runAs = launch.GetProperty("runAs").GetString(); var trigger = launch.GetProperty("trigger").GetString();
                if (runAs is not ("current_user" or "system") || trigger is not ("logon" or "manual" or "boot"))
                { throw new JsonException("Unknown task eligibility shape."); }
                reason = !enabled ? "disabled" : runAs != "current_user" || trigger != "logon" ? "unsupported_scope" : null;
            }
            var recovery = await new AgentRecoveryClient(_client).ReadAsync(timeout, token).ConfigureAwait(false);
            RequireScope(); token.ThrowIfCancellationRequested();
            return new(_rootId, _subject, _name, revision!, recovery.Revision, recovery.Initialized, reason is null, reason,
                recovery.Tasks.FirstOrDefault(task => task.TaskName.Equals(_name, StringComparison.OrdinalIgnoreCase)), DateTimeOffset.UtcNow);
        }
        catch (Exception error) when (error is not AgentCommandException
            && error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new AgentCommandException("protocol_mismatch", "Agent returned malformed current recovery eligibility."); }
    }

    private void RequireScope()
    {
        if (_client.RootId != _rootId || CurrentUserIdentity.Key != _subject)
        { throw new AgentCommandException("protocol_mismatch", "Recovery observation scope changed."); }
    }
    private static void Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.EnumerateObject().Count())
        { throw new JsonException("Missing object or duplicate configuration fields."); }
    }
}
