using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Core.Models;

// Compact reference to DISPLAYED metadata, never a native identity or write token.
public sealed record StartupInspectionRequest(string Source, string Name, [property: JsonRequired] bool IsDisabled, string MetadataDigest,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StartupRebuildInput? RebuildInput = null)
{
    public static StartupInspectionRequest FromEntry(StartupEntry entry)
    {
        if (entry is null || !StartupListQuery.ValidEntry(entry)) { throw new ArgumentException("A valid observed startup entry is required."); }
        return new(entry.Source, entry.Name, entry.IsDisabled, entry.MetadataDigest);
    }

    public StartupListQuery Validate(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId) || entryId.Length > 256 || !StartupListQuery.KnownSource(Source)
            || string.IsNullOrWhiteSpace(Name) || Name.Length > 1024 || MetadataDigest is not { Length: 64 }
            || MetadataDigest.Any(value => !char.IsAsciiHexDigit(value) || char.IsAsciiLetterUpper(value)))
        { throw new ArgumentException("Inspection requires the selected ID, fixed source/name/state and lowercase metadataDigest from startup list."); }
        if (RebuildInput is not null)
        {
            if (!IsDisabled || Source is not ("Startup" or "Common Startup"))
            { throw new ArgumentException("Rebuild input preview requires a selected disabled shortcut record; it grants no native eligibility."); }
            _ = StartupRebuildInputPolicy.Preview(RebuildInput);
        }
        var scope = Source is "HKCU" or "Startup" ? "user" : "machine";
        var kind = Source is "Startup" or "Common Startup" ? "shortcut" : "registry";
        var search = Name.Length <= 256 && !Name.Any(char.IsControl) ? Name : null;
        return new StartupListQuery(scope, kind, IsDisabled ? "disabled" : "active", search, 200).Validate();
    }
}

public sealed record StartupInspectionAction(string Action, string State, string? Reason, bool RequiresAdministrator);

public sealed record StartupInspectionSnapshot(int SchemaVersion, string ConfigurationRevision,
    DateTimeOffset CompletedAt, DateTimeOffset ObservedAt, StartupEntry Entry, string MetadataDigest,
    string OriginalBackupState, bool IsElevated, bool NativeWriteReady, bool NativeWritePerformed,
    bool ExecutionRequested, IReadOnlyList<StartupInspectionAction> Actions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StartupRebuildPreview? RebuildPreview = null) : IRevisionedResult
{
    string IRevisionedResult.Revision => ConfigurationRevision;
}

public static class StartupInspectionPolicy
{
    // Versioned fixed field order excludes computed properties and serializer member ordering.
    // This does NOT include registry kind/raw backup bytes, archive identity or ledger disposition.
    public static string Digest(StartupEntry entry) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            "startup-entry-metadata-v1", entry.Id, entry.Source, entry.Name, entry.RawCommand, entry.Target,
            entry.Arguments, entry.WorkingDirectory, entry.Origin, entry.Error, entry.IsDisabled, entry.BackupId,
            entry.DisabledAt, entry.RequiresAdministrator, entry.CanModify, entry.ManagedTask,
        }, AgentProtocol.JsonOptions)));

    // "reviewable" means eligible to enter the configuration-only adoption form, not permission
    // to commit it. Native operations stay blocked until the separate write-safety gates close.
    public static IReadOnlyList<StartupInspectionAction> Describe(StartupEntry entry)
    {
        var admin = entry.Source is "HKLM" or "HKLM-WOW64" or "Common Startup";
        var unreadable = !string.IsNullOrEmpty(entry.Error);
        var adoptionReason = entry.IsDisabled ? "entry_disabled" : unreadable ? "entry_unreadable"
            : entry.IsManaged ? "already_managed" : string.IsNullOrWhiteSpace(entry.Target) ? "target_metadata_unavailable" : null;
        return
        [
            new("adopt", adoptionReason is null ? "reviewable" : "blocked", adoptionReason, false),
            new("disable", entry.IsDisabled ? "not_applicable" : "blocked",
                entry.IsDisabled ? "entry_disabled" : unreadable ? "entry_unreadable" : "native_control_unavailable", admin),
            new("restore", entry.IsDisabled ? "blocked" : "not_applicable",
                entry.IsDisabled ? "native_control_unavailable" : "entry_active", admin),
            new("rebuild", !entry.IsDisabled || entry.Source is not ("Startup" or "Common Startup") ? "not_applicable" : "blocked",
                !entry.IsDisabled ? "entry_active" : entry.Source is not ("Startup" or "Common Startup") ? "shortcut_required" : "native_control_unavailable", admin),
        ];
    }
}
