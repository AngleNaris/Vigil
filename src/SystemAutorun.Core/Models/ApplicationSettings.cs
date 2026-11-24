using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemAutorun.Core.Models;

// The existing on-disk names remain stable when ownership moves from WPF to Agent.
public record ApplicationSettings
{
    [JsonPropertyName("auto_start_missing_on_launch")]
    public bool AutoStartMissingOnLaunch { get; init; }

    [JsonPropertyName("auto_recover_failed_tasks")]
    public bool AutoRecoverFailedTasks { get; init; }

    [JsonPropertyName("remote_hosts")]
    public IReadOnlyList<RemoteHostDefinition> RemoteHosts { get; init; } = [];

    [JsonPropertyName("log_sources")]
    public IReadOnlyList<LogSourceDefinition> LogSources { get; init; } = [];

    [JsonPropertyName("alert_settings")]
    public ResourceAlertSettings AlertSettings { get; init; } = new();

    [JsonPropertyName("auto_restart_settings")]
    public AutoRestartSettings AutoRestartSettings { get; init; } = new();

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalSettings { get; init; }
}
