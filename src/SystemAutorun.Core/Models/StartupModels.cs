namespace SystemAutorun.Core.Models;

public sealed record StartupEntry(
    string Id,
    string Source,
    string Name,
    string? RawCommand,
    string? Target,
    string? Arguments,
    string? WorkingDirectory,
    string Origin,
    string? Error,
    bool IsDisabled,
    string? BackupId,
    DateTimeOffset? DisabledAt,
    bool RequiresAdministrator,
    bool CanModify,
    string? ManagedTask)
{
    public bool IsManaged => !string.IsNullOrWhiteSpace(ManagedTask);
}

public sealed record StartupManagementSnapshot(
    IReadOnlyList<StartupEntry> Entries,
    int TotalActive,
    int ManagedActive,
    int Disabled,
    bool IsElevated);

public sealed record StartupMutationResult(
    bool Succeeded,
    string Action,
    string Source,
    string Name,
    string BackupId);
