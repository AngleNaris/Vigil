using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Windows.Startup;

// Internal protected-data format, NOT a history issuer, current native observation,
// latest-state store or permission token. V1 describes new same-volume disable facts
// only; later dispositions/transactions are not admitted. No business IO is connected.
internal sealed record StartupHistoryDirectory(string Path, StartupNativeFileIdentity Identity, string CaseSensitivity);
internal sealed record StartupHistorySource(string Source, string Kind, string KnownFolder, string? UserSid, StartupHistoryDirectory Directory);
internal sealed record StartupHistoryOriginal(StartupNativeFileIdentity Identity, int Length, uint LinkCount, string Sha256);
internal sealed record StartupHistoryContext(string ActorSid, StartupHistoryDirectory Root, StartupHistorySource Source,
    StartupHistoryDirectory ArchiveBase, StartupHistoryDirectory Archive, string RecordId, string Name, string RecordDigest)
{
    internal StartupHistoryBinding Bind(StartupHistoryOriginal original) => new(ActorSid, Root, Source, ArchiveBase, Archive, RecordId, Name, RecordDigest, original);
}
internal sealed record StartupHistoryBinding(string ActorSid, StartupHistoryDirectory Root, StartupHistorySource Source,
    StartupHistoryDirectory ArchiveBase, StartupHistoryDirectory Archive, string RecordId, string Name,
    string RecordDigest, StartupHistoryOriginal Original)
{
    internal StartupHistoryContext Context => new(ActorSid, Root, Source, ArchiveBase, Archive, RecordId, Name, RecordDigest);
}
internal sealed record StartupHistoryPayload(int SchemaVersion, string Phase, StartupHistoryBinding Binding,
    string Nonce, long Sequence, string Disposition, DateTimeOffset CandidateAt, DateTimeOffset? CommittedAt);

// MUST come from an independent protected latest-state authority, never from the
// editable ledger or the inspected blob itself. The external index reader supplies it;
// internal native-result publication does not enable public restore/rebuild requests.
internal sealed record StartupHistoryLatest(long Sequence, string Nonce, string Phase, string ProtectedDigest);
internal sealed record StartupHistoryCheck(string State, string? Reason)
{
    public bool NativeWriteReady => false;
    public bool NativeWritePerformed => false;
    public bool ExecutionRequested => false;
}

internal static class StartupHistoryFormat
{
    internal const int MaximumBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Strict)
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 8 };

    internal static byte[] Write(StartupHistoryPayload payload)
    {
        Validate(payload);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Options);
        if (bytes.Length <= MaximumBytes) { return bytes; }
        CryptographicOperations.ZeroMemory(bytes); throw new JsonException("History payload exceeds its byte limit.");
    }

    internal static StartupHistoryPayload Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) { throw new JsonException("History payload byte limit is invalid."); }
        var payload = JsonSerializer.Deserialize<StartupHistoryPayload>(bytes, Options)
            ?? throw new JsonException("History payload is missing.");
        Validate(payload); return payload;
    }

    internal static void Validate(StartupHistoryPayload payload)
    {
        if (payload is null || payload.SchemaVersion != 1 || payload.Phase is not ("candidate" or "committed")
            || !Identifier(payload.Nonce) || payload.Sequence < 1
            || payload.Disposition != "disabled"
            || payload.CandidateAt <= DateTimeOffset.UnixEpoch || payload.CandidateAt.Offset != TimeSpan.Zero
            || (payload.Phase == "candidate" ? payload.CommittedAt.HasValue
                : payload.CommittedAt is null || payload.CommittedAt < payload.CandidateAt || payload.CommittedAt.Value.Offset != TimeSpan.Zero))
        { throw new JsonException("History stage fields are invalid."); }
        ValidateBinding(payload.Binding);
    }

    internal static void ValidateBinding(StartupHistoryBinding binding)
    {
        if (binding is null || binding.Original is not { } original || original.Length is < 0 or > 65536
            || original.LinkCount != 1 || !Hex(original.Sha256, 64)) { throw new JsonException("History original facts are invalid."); }
        ValidateContext(binding.Context); Identity(original.Identity);
        if (original.Identity.VolumeSerialNumber != binding.Source.Directory.Identity.VolumeSerialNumber)
        { throw new JsonException("History original facts require the same volume."); }
    }

    internal static void ValidateContext(StartupHistoryContext context)
    {
        if (context is null || !Sid(context.ActorSid) || !Identifier(context.RecordId) || !Hex(context.RecordDigest, 64)
            || string.IsNullOrWhiteSpace(context.Name) || context.Name.Length > 251 || context.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || context.Source is not { } source || source.Directory is null)
        { throw new JsonException("History context is invalid."); }
        if (source.Source == "Startup" ? source.Kind != "user" || source.KnownFolder != "Startup" || source.UserSid != context.ActorSid
            : source.Source != "Common Startup" || source.Kind != "machine" || source.KnownFolder != "CommonStartup" || source.UserSid is not null)
        { throw new JsonException("History source scope is invalid."); }
        Directory(context.Root); Directory(source.Directory); Directory(context.ArchiveBase); Directory(context.Archive);
        if (context.Archive.Path != Path.Combine(context.ArchiveBase.Path, "startup")
            || context.Archive.Identity.VolumeSerialNumber != context.ArchiveBase.Identity.VolumeSerialNumber
            || source.Directory.Identity.VolumeSerialNumber != context.Archive.Identity.VolumeSerialNumber)
        { throw new JsonException("History requires its fixed same-volume archive context."); }
        try
        {
            _ = StartupShortcutPathLease.Resolve(context.ArchiveBase.Path, context.RecordId, context.Name,
                Path.Combine(source.Directory.Path, context.Name + ".lnk"), Path.Combine(context.Archive.Path, context.RecordId + ".lnk"),
                source.Directory.Path, [source.Directory.Path]);
        }
        catch (InvalidOperationException) { throw new JsonException("History shortcut layout is invalid."); }
    }

    internal static StartupHistoryCheck Match(StartupHistoryPayload payload, StartupHistoryBinding current, StartupHistoryLatest? latest)
    {
        Validate(payload); ValidateBinding(current);
        if (latest is null) { return new("unverified", "history_latest_unavailable"); }
        if (!ValidLatest(latest)) { return new("unverified", "history_latest_invalid"); }
        if (payload.Binding != current) { return new("unverified", "history_context_changed"); }
        if (payload.Sequence != latest.Sequence || payload.Nonce != latest.Nonce || payload.Phase != latest.Phase)
        { return new("unverified", "history_latest_changed"); }
        return payload.Phase == "candidate" ? new("candidate", "history_not_committed")
            : new("protected_committed", "history_producer_not_connected");
    }

    internal static bool ValidLatest(StartupHistoryLatest latest) => latest.Sequence > 0 && Identifier(latest.Nonce)
        && latest.Phase is "candidate" or "committed" && Hex(latest.ProtectedDigest, 64);

    // Separate domain from the existing IPC metadata digest. Explicit ordered scalar
    // projection normalizes UTC instants and effective disposition; no arbitrary graph,
    // target probing or raw JSON formatting determines this record association.
    internal static string RecordDigest(StartupBackupRecord record)
    {
        if (record is null || !StartupBackupLedger.Valid(record) || record.Source is not ("Startup" or "Common Startup")
            || !Identifier(record.Id) || record.DisabledAt <= DateTimeOffset.UnixEpoch)
        { throw new ArgumentException("A complete shortcut record is required.", nameof(record)); }
        var encoded = JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            "startup-history-record-v1", record.Id, record.Action, record.DisabledAt.UtcTicks,
            record.RestoredAt?.UtcTicks, record.Source, record.Name, record.RawCommand, record.RegistryKind,
            record.Target, record.Arguments, record.WorkingDirectory, record.Origin, record.BackupPath,
            record.EffectiveDisposition, record.RebuiltAt?.UtcTicks, record.Rebuild?.Target,
            record.Rebuild?.Arguments, record.Rebuild?.WorkingDirectory,
        });
        try
        {
            if (encoded.Length > StartupBackupLedger.MaximumBytes) { throw new JsonException("History record exceeds its byte limit."); }
            return Convert.ToHexStringLower(SHA256.HashData(encoded));
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    private static void Directory(StartupHistoryDirectory directory)
    {
        if (directory is null || directory.Path is not { Length: <= 4096 } || directory.CaseSensitivity is not ("sensitive" or "insensitive"))
        { throw new JsonException("History directory binding is invalid."); }
        try
        {
            if (StartupShortcutPathLease.LocalPath(directory.Path) != directory.Path) { throw new JsonException("History path is not canonical."); }
        }
        catch (InvalidOperationException) { throw new JsonException("History path is not a literal local path."); }
        Identity(directory.Identity);
    }

    private static void Identity(StartupNativeFileIdentity identity)
    {
        if (identity is null || !Hex(identity.VolumeSerialNumber, 16) || !Hex(identity.FileId, 32) || identity.FileId.All(character => character == '0'))
        { throw new JsonException("History native identity is invalid."); }
    }

    private static bool Hex(string? value, int length) => value is not null && value.Length == length
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool Identifier(string? value) => value is not null && Guid.TryParseExact(value, "N", out var id)
        && id != Guid.Empty && id.ToString("N", CultureInfo.InvariantCulture) == value;
    internal static bool Sid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 184) { return false; }
        try { return new SecurityIdentifier(value).Value == value; }
        catch (ArgumentException) { return false; }
    }
}
