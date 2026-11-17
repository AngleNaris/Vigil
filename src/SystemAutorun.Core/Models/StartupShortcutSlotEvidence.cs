namespace SystemAutorun.Core.Models;

// The CURRENT name slot in the fixed original-entry namespace. It does not prove this
// object/source root was captured historically, nor inspect target/content/archived bytes.
public sealed record StartupOriginalSlotObservation(string State, string? Reason, int? NativeErrorCode,
    string? SourceDirectory, string CaseSensitivity, StartupNativeFileIdentity? DirectoryIdentity,
    StartupNativeFileIdentity? FileIdentity, long? Length, uint? LinkCount, DateTimeOffset ObservedAt);

// Sequential point-in-time observations, NOT an atomic combined snapshot. All leases have
// been released by return; even both slots missing cannot authenticate an editable record.
public sealed record StartupShortcutSlotEvidence(int SchemaVersion, string BackupId, string Source, string Name,
    string RecordMetadataDigest, StartupArchiveSlotObservation Archive, StartupOriginalSlotObservation Original,
    bool NativeWriteReady, bool NativeWritePerformed, bool ExecutionRequested);
