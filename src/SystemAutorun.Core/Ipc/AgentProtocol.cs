using System.Security.Cryptography;
using SystemAutorun.Core.Infrastructure;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemAutorun.Core.Ipc;

public sealed record AgentRequest(
    string Id,
    string Command,
    string? Name = null,
    JsonElement? Payload = null,
    int? TimeoutMilliseconds = null,
    int ProtocolVersion = AgentProtocol.Version,
    string? RootId = null,
    string? ExpectedRevision = null,
    string? OperationId = null);

public sealed record AgentResponse(
    string Id,
    bool Ok,
    object? Data = null,
    string? Error = null,
    int ProtocolVersion = AgentProtocol.Version,
    string? RootId = null,
    string? ErrorCode = null,
    string? Revision = null,
    string? OperationId = null,
    bool Replayed = false);

public sealed record AgentEndpoint(string RootDirectory, string RootId, string PipeName, string AppInstanceName)
{
    public string MutexName => $"Local\\{PipeName}";
}

public static class AgentProtocol
{
    public const int Version = 2;
    public const int MaximumRequestCharacters = 64 * 1024;
    public const int MaximumResponseCharacters = 4 * 1024 * 1024;
    public const int MaximumRequestTimeoutMilliseconds = 10 * 60 * 1000;
    public const int InitialRequestReadTimeoutMilliseconds = 5 * 1000;
    public const int MaximumRequestIdCharacters = 128;
    public const int MaximumCommandCharacters = 64;

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    public static bool IsConfigurationMutation(string command) => command.ToLowerInvariant() is
        "settings-set" or "settings-patch" or "remote-upsert" or "remote-add" or "remote-update" or "remote-remove"
        or "task-add" or "task-update" or "task-reorder" or "task-remove" or "task-monitor" or "task-enable" or "task-automation" or "configuration-apply" or "startup-adopt";

    // Wire operation-ID classification, NOT a promise of generic dispatcher wrapping.
    // self-set uses the context-aware self pipeline on the exact shared Journal once.
    public static bool IsMutation(string command) => IsConfigurationMutation(command)
        || command.ToLowerInvariant() is "start" or "stop" or "restart" or "start-missing" or "start-on-app-launch" or "recovery-resume" or "shutdown" or "startup-self-set";

    public static string NewOperationId() => Guid.CreateVersion7().ToString("N");

    // Callers resolve/validate the configuration root before creating an endpoint. The path
    // hash is an instance namespace, not authentication; CurrentUserOnly remains required.
    public static AgentEndpoint ForRoot(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var root = PhysicalDirectory.Resolve(rootDirectory);
        // The final path already has canonical casing; folding again would merge
        // distinct directories on case-sensitive NTFS subtrees.
        var rootId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
        var identity = CurrentUserIdentity.Key;
        return new AgentEndpoint(
            root,
            rootId,
            $"Vigil.Agent.v{Version}.{identity}.{rootId}",
            $"Vigil.App.v{Version}.{identity}.{rootId}");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
