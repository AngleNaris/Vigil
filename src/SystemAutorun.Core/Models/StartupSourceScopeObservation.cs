namespace SystemAutorun.Core.Models;

// CURRENT local Windows source identity, not historical provenance, ownership of the
// directory, a durable lease, a cross-machine identity or native write authority.
// SourceUserSid binds only the user known-folder; machine scope has an ObserverSid,
// not a fabricated owner SID. All directory leases have been released by return.
public sealed record StartupSourceScopeObservation(int SchemaVersion, string Source, string SourceKind,
    string KnownFolder, string State, string? Reason, int? NativeErrorCode,
    string? ObserverSid, string? SourceUserSid, string? DirectoryPath, string CaseSensitivity,
    StartupNativeFileIdentity? DirectoryIdentity, DateTimeOffset ObservedAt,
    bool NativeWriteReady, bool NativeWritePerformed, bool ExecutionRequested);
