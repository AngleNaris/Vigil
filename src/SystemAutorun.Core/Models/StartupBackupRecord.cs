using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace SystemAutorun.Core.Models;

// Persisted, editable ledger metadata. Valid shape/disposition is NOT authentication,
// archive availability, native identity, or permission to restore/rebuild.
public sealed record StartupBackupRecord
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("action")] public string Action { get; init; } = "disable";
    [JsonPropertyName("disabled_at")] public DateTimeOffset DisabledAt { get; init; }
    [JsonPropertyName("restored_at")] public DateTimeOffset? RestoredAt { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("raw_command")] public string? RawCommand { get; init; }
    [JsonPropertyName("registry_value_kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RegistryValueKind? RegistryKind { get; init; }
    [JsonPropertyName("target")] public string? Target { get; init; }
    [JsonPropertyName("args")] public string? Arguments { get; init; }
    [JsonPropertyName("working_dir")] public string? WorkingDirectory { get; init; }
    [JsonPropertyName("origin")] public string Origin { get; init; } = "";
    [JsonPropertyName("backup_path")] public string? BackupPath { get; init; }

    // Absent/null disposition is legacy, derived only from restored_at. New writers
    // set it explicitly. Never rewrite a rebuilt record as an original-byte restore.
    [JsonPropertyName("disposition"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Disposition { get; init; }
    [JsonPropertyName("rebuilt_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? RebuiltAt { get; init; }
    [JsonPropertyName("rebuild"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StartupRebuildAudit? Rebuild { get; init; }

    [JsonIgnore] public string EffectiveDisposition => Disposition ?? (RestoredAt is null ? "disabled" : "restored");
}

// An untrusted historical summary, not the validated request for a future native write.
public sealed record StartupRebuildAudit(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("args")] string? Arguments = null,
    [property: JsonPropertyName("working_dir")] string? WorkingDirectory = null);
