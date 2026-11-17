using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Windows.Startup;

// Independent DATA axes, never a recovered receipt or permission to continue work.
// ReceiptSnapshotState describes the supplied snapshot, not a live Journal read.
internal sealed record StartupHistoryOperationEvidence(string State, string Reason, string ReceiptSnapshotState,
    StartupHistoryIntentObservation Metadata, StartupHistoryOperationCheck Association, StartupHistoryNativeCheck Native)
{
    public bool NativeWriteReady => false;
    public bool ExecutionRequested => false;
    public bool TransactionFinalized => false;
    public bool MayFinalize => false;
    public bool ReceiptUpdated => false;
    public bool ReplayRequested => false;
}

internal sealed partial class StartupHistoryIntentReader
{
    // No Journal.GetAsync: even that query acquires a writer lease/cleans temporary
    // files. This bounded internal query accepts an explicitly separate snapshot.
    internal async Task<StartupHistoryOperationEvidence> InspectOperationAsync(OperationReceipt snapshot, IStartupNativeAccess native,
        CancellationToken token = default, Action<string>? checkpoint = null)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(snapshot); var receiptState = "unobserved";
        StartupHistoryOperationEvidence Result(string state, string reason, StartupHistoryIntentObservation? metadata = null,
            StartupHistoryOperationCheck? association = null, StartupHistoryNativeCheck? observed = null) => new(state, reason, receiptState,
                metadata ?? new("unavailable", "history_operation_inputs_unobserved"), association ?? new("unverified", reason),
                observed ?? new("unobserved", "history_operation_native_unobserved"));
        try
        {
            if (snapshot.SchemaVersion != OperationReceipt.CurrentSchemaVersion || !Enum.IsDefined(snapshot.State)
                || OperationJournal.NormalizeId(snapshot.OperationId) != snapshot.OperationId
                || snapshot.Fingerprint is not { Length: 64 } || snapshot.Fingerprint.Any(value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                || !string.Equals(snapshot.Command, "startup-disable", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(snapshot.Name))
            { return Result("unverified", "history_operation_receipt_invalid"); }
            receiptState = snapshot.State.ToString().ToLowerInvariant();
            using var root = SshInputFileLease.OpenDirectory(_paths.RootDirectory);
            using var mutation = SshInputFileLease.OpenOptional(Path.Combine(_paths.RootDirectory, StartupMutationLease.FileName), 0);
            using var workspace = SshInputFileLease.OpenOptional(Path.Combine(_paths.RootDirectory, RootWriteLease.FileName), 0);
            using var intentImage = RootImage.Open(_paths.StartupBackupPath + ".transaction", StartupHistoryIntentFormat.MaximumBytes);
            if (intentImage.Bytes is null)
            {
                checkpoint?.Invoke("operation_intent_absent"); token.ThrowIfCancellationRequested(); intentImage.VerifyCurrent();
                return Result("none", "history_operation_intent_absent", new("none", "history_intent_absent"), new("unobserved", "history_operation_intent_absent"));
            }
            if (mutation is null || workspace is null) { return Result("unavailable", "history_query_coordination_missing"); }
            var intent = StartupHistoryIntentFormat.Read(intentImage.Bytes); var actor = StartupHistoryProtection.Actor();
            if (intent.Candidate.Binding.Root.Path != _paths.RootDirectory || intent.Candidate.Binding.ActorSid != actor)
            { return Result("unverified", "history_intent_context_changed"); }
            using var configuration = RootImage.Open(_paths.ConfigPath, ConfigurationDocumentRequest.MaximumBytes);
            using var ledger = RootImage.Open(_paths.StartupBackupPath, SystemAutorun.Core.Models.StartupBackupLedger.MaximumBytes);
            var metadata = Inspect(token, checkpoint);
            if (!NativeEligible(metadata) || metadata.RecordId != intent.Record.Id || metadata.TransactionId != intent.TransactionId)
            { return Result(metadata.State == "busy" ? "busy" : "unverified", "history_operation_metadata_unverified", metadata); }
            var locate = _directory ?? StartupHistoryStoreReader.DefaultDirectory; var directory = StartupShortcutPathLease.LocalPath(locate());
            var prefix = Path.EndsInDirectorySeparator(_paths.RootDirectory) ? _paths.RootDirectory : _paths.RootDirectory + Path.DirectorySeparatorChar;
            if (directory.Equals(_paths.RootDirectory, StringComparison.OrdinalIgnoreCase) || directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            { return Result("unverified", "history_store_inside_root"); }
            using var side = StartupNativeSlotProbe.HistoryDirectoryReadLease.Open(directory, intent.Candidate.Binding.Root, actor, token);
            using var index = side.OpenFile(StartupHistoryStoreReader.IndexName, StartupHistoryProtection.MaximumProtectedIndexBytes, token);
            var row = StartupHistoryProtection.UnprotectIndex(index.Bytes, token).Entries.SingleOrDefault(value => value.Binding.RecordId == intent.Record.Id);
            if (row is null || row.Binding != intent.Candidate.Binding) { return Result("unverified", "history_store_intent_changed"); }
            using var current = side.OpenFile(StartupHistoryStoreReader.ProofName(intent.Record.Id, row.Latest.Phase), StartupHistoryProtection.MaximumProtectedBytes, token);
            using var link = OpenOperationEvidenceLink(side, intent.Record.Id, token);
            using var parent = link is not null && row.Latest.Phase == "committed"
                ? side.OpenFile(StartupHistoryStoreReader.ProofName(intent.Record.Id, "candidate"), StartupHistoryProtection.MaximumProtectedBytes, token) : null;
            var reference = new StartupHistoryOperationReference(snapshot.OperationId, snapshot.Fingerprint, intent.TransactionId);
            var association = link is null ? new StartupHistoryOperationCheck("unlinked", "history_store_operation_missing")
                : new StartupHistoryOperationStoreReader(_paths.RootDirectory, locate).Inspect(intent.Candidate.Binding.Context, reference, token,
                    beforeSeal: () => checkpoint?.Invoke("operation_association_sealing"));
            var associated = association.State is "associated_candidate" or "associated_committed";
            if (Inspect(token, checkpoint) != metadata) { return Result("unverified", "history_operation_join_changed"); }
            if (associated && (link is null || association.RecordId != intent.Record.Id || association.TransactionId != intent.TransactionId
                || StartupHistoryProtection.Unprotect(parent?.Bytes ?? current.Bytes, token) != intent.Candidate))
            { return Result("unverified", "history_operation_intent_changed"); }
            using var nativeObservation = associated
                ? new StartupHistoryNativeReconciler(_paths.RootDirectory, await OperationArchiveBase(configuration.Bytes!, token).ConfigureAwait(false), native, token, checkpoint) : null;
            if (nativeObservation is not null)
            {
                checkpoint?.Invoke("operation_associated"); token.ThrowIfCancellationRequested();
                // R53 has sealed these same held bytes; no editable root path drives IO.
                nativeObservation.Observe(StartupHistoryProtection.Unprotect(current.Bytes, token));
            }
            checkpoint?.Invoke("operation_evidence_sealing"); token.ThrowIfCancellationRequested();
            var observed = nativeObservation?.InspectCurrent();
            intentImage.VerifyCurrent(); configuration.VerifyCurrent(); ledger.VerifyCurrent();
            index.VerifyCurrent(token); current.VerifyCurrent(token); link?.VerifyCurrent(token); parent?.VerifyCurrent(token); side.VerifyCurrent(token);
            if (StartupShortcutPathLease.LocalPath(locate()) != directory) { return Result("unverified", "history_store_mapping_changed"); }
            StartupHistoryProtection.RequireActor(actor);
            if (associated && observed?.State is not ("original_matches" or "archive_matches"))
            { return Result("unverified", "history_operation_native_unverified", metadata, observed: observed); }
            return associated ? Result("observed", "history_operation_evidence_only", metadata, association, observed)
                : Result(association.State == "unlinked" ? "unlinked" : "unverified", association.Reason, metadata, association);
        }
        catch (OperationCanceledException) { throw; }
        catch (AgentCommandException) { return Result("unverified", "history_operation_receipt_invalid"); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 || error.InnerException is Win32Exception { NativeErrorCode: 32 or 33 })
        { return Result("busy", "history_operation_query_busy"); }
        catch (Win32Exception error) when (error.NativeErrorCode is 32 or 33) { return Result("busy", "history_operation_query_busy"); }
        catch (Exception error) when (error is ConfigurationException or IOException or UnauthorizedAccessException or SecurityException or Win32Exception
            or InvalidOperationException or ArgumentException or CryptographicException or JsonException)
        { return Result("unverified", "history_operation_query_unavailable"); }
    }

    private async Task<string> OperationArchiveBase(byte[] bytes, CancellationToken token)
    {
        using var text = new StreamReader(new MemoryStream(bytes, writable: false)); var yaml = await text.ReadToEndAsync(token).ConfigureAwait(false);
        return StartupShortcutPathLease.LocalPath(new ConfigurationLoader().ReadSettingsText(yaml, _paths.RootDirectory, token).BackupDirectory);
    }
    private static StartupNativeSlotProbe.HistoryFileReadLease? OpenOperationEvidenceLink(StartupNativeSlotProbe.HistoryDirectoryReadLease side, string recordId, CancellationToken token)
    {
        try { return side.OpenFile(StartupHistoryOperationStoreReader.LinkName(recordId), StartupHistoryProtection.MaximumProtectedBytes, token); }
        catch (Win32Exception error) when (error.NativeErrorCode == 2) { return null; }
        catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: 2 }) { return null; }
    }
}
