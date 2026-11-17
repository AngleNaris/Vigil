using SystemAutorun.Core.Configuration;

namespace SystemAutorun.Core.Models;

// Successful read-only evidence envelope. Failure/busy/timeout uses the existing Agent
// error envelope with NO evidence, never fabricated missing or last-good observations.
public sealed record StartupNativeInspectionSnapshot(int SchemaVersion, string ConfigurationRevision,
    DateTimeOffset CompletedAt, StartupInspectionSnapshot Metadata, StartupShortcutSlotEvidence Slots,
    string RecordMetadataDigestAfter, string ValidationScope, string HistoricalIdentity,
    bool NativeWriteReady, bool NativeWritePerformed, bool ExecutionRequested) : IRevisionedResult
{
    string IRevisionedResult.Revision => ConfigurationRevision;
}

// Internal reader result, not an RPC request. Both digests use the SAME full known-record
// serializer/version. Neither equality nor a slot identity authenticates editable history.
public sealed record StartupSlotEvidenceRead(StartupShortcutSlotEvidence Evidence, string RecordMetadataDigestAfter);

public static class StartupNativeInspectionPolicy
{
    public static StartupListQuery ValidateRequest(string entryId, StartupInspectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = request.Validate(entryId);
        if (!request.IsDisabled || request.Source is not ("Startup" or "Common Startup"))
        { throw new ArgumentException("Native slot observation requires a selected disabled shortcut record; no native write is available."); }
        return query;
    }

    public static bool ValidEvidence(StartupSlotEvidenceRead? read, StartupEntry entry)
    {
        if (read?.Evidence is not { } evidence || !entry.IsDisabled || entry.Source is not ("Startup" or "Common Startup")
            || !Guid.TryParseExact(entry.BackupId, "N", out var id) || id == Guid.Empty
            || evidence.SchemaVersion != 1 || evidence.BackupId != entry.BackupId || evidence.Source != entry.Source || evidence.Name != entry.Name
            || !LowerHex(evidence.RecordMetadataDigest, 64) || evidence.RecordMetadataDigest != read.RecordMetadataDigestAfter
            || evidence.NativeWriteReady || evidence.NativeWritePerformed || evidence.ExecutionRequested
            || evidence.Archive is not { } archive || evidence.Original is not { } original)
        { return false; }
        return ValidSlot("archive", archive.State, archive.Reason, archive.NativeErrorCode, archive.ArchiveDirectory,
                archive.CaseSensitivity, archive.DirectoryIdentity, archive.FileIdentity, archive.Length, archive.LinkCount, archive.ObservedAt)
            && ValidSlot("original", original.State, original.Reason, original.NativeErrorCode, original.SourceDirectory,
                original.CaseSensitivity, original.DirectoryIdentity, original.FileIdentity, original.Length, original.LinkCount, original.ObservedAt)
            && original.ObservedAt >= archive.ObservedAt;
    }

    private static bool ValidSlot(string prefix, string state, string? reason, int? error, string? directory,
        string casing, StartupNativeFileIdentity? parent, StartupNativeFileIdentity? file, long? length, uint? links, DateTimeOffset observedAt)
    {
        if (casing is not ("unknown" or "insensitive" or "sensitive") || observedAt == default
            || DateTimeOffset.UtcNow - observedAt > TimeSpan.FromSeconds(30) || observedAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(5)
            || parent is not null && !ValidIdentity(parent) || file is not null && !ValidIdentity(file)
            || directory is not null && !OrdinaryLocalDirectory(directory))
        { return false; }
        if (state == "present")
        { return reason is null && error is null && directory is not null && parent is not null && file is not null
            && parent.VolumeSerialNumber == file.VolumeSerialNumber && length is >= 0 and <= 65536 && links is >= 1; }
        if (state == "missing")
        { return reason is null && error == 2 && casing == "insensitive" && directory is not null && parent is not null
            && file is null && length is null && links is null; }
        if (state != "unknown" || reason is null || file is not null || length is not null || links is not null || error is <= 0)
        { return false; }
        if (reason == prefix + "_context_invalid")
        { return directory is null && parent is null && casing == "unknown" && error is null; }
        if (directory is null) { return false; }
        var cause = reason.StartsWith(prefix + "_", StringComparison.Ordinal) ? reason[(prefix.Length + 1)..] : "";
        return cause switch
        {
            "permission_denied" => error == 5,
            "occupied" => error is 32 or 33,
            "reparse_rejected" => error == 4395,
            "directory_missing" => error is 2 or 3,
            "case_semantics_changed" or "case_semantics_unavailable" => error == 2 && parent is not null,
            "context_changed" or "not_regular" or "too_large" => error is null && parent is not null,
            "directory_unavailable" => error is null,
            "unavailable" => error is > 0,
            _ => false,
        };
    }

    private static bool OrdinaryLocalDirectory(string value) => value.Length is >= 3 and <= 32766
        && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\'
        && !value.Any(char.IsControl) && !value.Contains('/') && !value.AsSpan(3).Contains(':');

    private static bool ValidIdentity(StartupNativeFileIdentity identity) =>
        LowerHex(identity.VolumeSerialNumber, 16) && LowerHex(identity.FileId, 32);
    public static bool LowerHex(string? value, int length) => value?.Length == length
        && value.All(character => char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character));
}
