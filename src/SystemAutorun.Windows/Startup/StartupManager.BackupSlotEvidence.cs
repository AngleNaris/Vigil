using System.Security.Cryptography;
using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Windows.Startup;

public sealed partial class StartupManager
{
    // Compatibility path: still archive-only; never opens the original entry or target.
    public async Task<StartupBackupSlotEvidence> ObserveBackupSlotAsync(string backupId, CancellationToken cancellationToken = default)
    {
        var slots = await ObserveShortcutSlotsCoreAsync(backupId, includeOriginal: false, cancellationToken).ConfigureAwait(false);
        return new(1, slots.BackupId, slots.Source, slots.Name, slots.RecordMetadataDigest, slots.Archive,
            NativeWriteReady: false, NativeWritePerformed: false, ExecutionRequested: false);
    }

    // Backend-only. Two sequential observations, no root/native CAS, no commit token or
    // link-byte parsing. This is NOT the metadata IPC/CLI preparation contract.
    public Task<StartupShortcutSlotEvidence> ObserveShortcutSlotsAsync(string backupId, CancellationToken cancellationToken = default) =>
        ObserveShortcutSlotsCoreAsync(backupId, includeOriginal: true, cancellationToken);

    // Same backend observation, followed by a second complete-ledger read. Equality only
    // detects observed known-record drift; it is not a byte CAS, provenance or an ABA fence.
    internal async Task<StartupSlotEvidenceRead> ReadSlotEvidenceAsync(string backupId, CancellationToken cancellationToken)
    {
        var evidence = await ObserveShortcutSlotsAsync(backupId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNoUnresolvedTransaction();
        var after = (await ReadBackupsAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(row => row.Id.Equals(backupId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("The selected record disappeared during native observation.");
        return new(evidence, BackupRecordMetadataDigest(after));
    }

    private static string BackupRecordMetadataDigest(StartupBackupRecord record) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new object[]
        { "startup-backup-metadata-v1", record }, AgentProtocol.JsonOptions)));

    private async Task<StartupShortcutSlotEvidence> ObserveShortcutSlotsCoreAsync(string backupId, bool includeOriginal, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(backupId, "N", out var id) || id == Guid.Empty)
        { throw new ArgumentException("An original N-format backup ID is required."); }
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNoUnresolvedTransaction();
        var records = await ReadBackupsAsync(cancellationToken).ConfigureAwait(false);
        var record = records.SingleOrDefault(row => row.Id.Equals(backupId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("The selected backup is not in the current complete ledger.");
        var metadataDigest = BackupRecordMetadataDigest(record);
        StartupArchiveSlotObservation NoArchive(string state, string reason) => new(state, reason, null, null, "unknown", null, null, null, null, DateTimeOffset.UtcNow);
        StartupOriginalSlotObservation NoOriginal(string state, string reason) => new(state, reason, null, null, "unknown", null, null, null, null, DateTimeOffset.UtcNow);
        var original = NoOriginal("not_checked", "archive_only");
        StartupArchiveSlotObservation archive;
        if (record.EffectiveDisposition != "disabled")
        { archive = NoArchive("not_applicable", "record_not_disabled"); if (includeOriginal) { original = NoOriginal("not_applicable", "record_not_disabled"); } }
        else if (!IsShortcutSource(record.Source))
        { archive = NoArchive("not_applicable", "shortcut_required"); if (includeOriginal) { original = NoOriginal("not_applicable", "shortcut_required"); } }
        else
        {
            var configuration = await _configurationLoader.LoadAsync(_paths.RootDirectory, cancellationToken).ConfigureAwait(false);
            (string Origin, string BackupPath) location;
            try
            {
                location = StartupShortcutPathLease.Resolve(configuration.Settings.BackupDirectory, record.Id, record.Name,
                    record.Origin, record.BackupPath, _native.GetShortcutDirectory(record.Source),
                    [_native.GetShortcutDirectory(UserStartupSource), _native.GetShortcutDirectory(CommonStartupSource)]);
            }
            catch (InvalidOperationException)
            {
                // Complete layout validation precedes IO to EITHER namespace.
                archive = NoArchive("unknown", "archive_context_invalid");
                if (includeOriginal) { original = NoOriginal("unknown", "original_context_invalid"); }
                return Evidence();
            }
            cancellationToken.ThrowIfCancellationRequested();
            archive = StartupArchiveSlotProbe.Observe(location.BackupPath, cancellationToken);
            if (includeOriginal)
            {
                cancellationToken.ThrowIfCancellationRequested();
                original = StartupOriginalSlotProbe.Observe(location.Origin, cancellationToken);
            }
        }
        return Evidence();

        StartupShortcutSlotEvidence Evidence() => new(1, record.Id, record.Source, record.Name, metadataDigest, archive, original,
            NativeWriteReady: false, NativeWritePerformed: false, ExecutionRequested: false);
    }
}
