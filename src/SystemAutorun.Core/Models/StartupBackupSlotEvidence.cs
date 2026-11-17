namespace SystemAutorun.Core.Models;

// Opaque machine-local identifiers of observed open objects, not portable credentials.
public sealed record StartupNativeFileIdentity(string VolumeSerialNumber, string FileId);

// Present/missing are sealed while operation handles are held; unknown can be sealed after
// failed-open cleanup. All leases are closed by return; ObservedAt is NOT future validity.
// No link bytes are read or interpreted. "present" does not mean authentic/restorable.
public sealed record StartupArchiveSlotObservation(string State, string? Reason, int? NativeErrorCode,
    string? ArchiveDirectory, string CaseSensitivity, StartupNativeFileIdentity? DirectoryIdentity,
    StartupNativeFileIdentity? FileIdentity, long? Length, uint? LinkCount, DateTimeOffset ObservedAt);

// RecordMetadataDigest fingerprints editable metadata only; neither it nor missing grants authority.
// This is not the schema-1 metadata inspection/CLI response and is not exposed as a commit token.
public sealed record StartupBackupSlotEvidence(int SchemaVersion, string BackupId, string Source, string Name,
    string RecordMetadataDigest, StartupArchiveSlotObservation Observation,
    bool NativeWriteReady, bool NativeWritePerformed, bool ExecutionRequested);
