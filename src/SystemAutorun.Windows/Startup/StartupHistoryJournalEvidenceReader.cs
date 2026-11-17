using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Windows.Startup;

// The Journal is editable root data, not authentication. Disk state and conservative
// observation are separate: this reader has no access to an owner's live _active map.
internal sealed record StartupHistoryJournalEvidence(string JournalState, string Reason, string PersistedReceiptState = "unobserved",
    string ObservedReceiptState = "unobserved", StartupHistoryOperationEvidence? Evidence = null)
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
    private const int MaximumJournalImageBytes = 256 * 1024;
    private static readonly string[] JournalReceiptFields = ["schemaVersion", "operationId", "command", "name", "fingerprint", "ownerInstanceId",
        "state", "acceptedAt", "completedAt", "response", "resultOmitted"];

    // Reads one canonical leaf, never scans, repairs, prunes or invokes GetAsync/RunAsync.
    // Holding these existing pins through R55 fixes its input snapshot for that window.
    internal async Task<StartupHistoryJournalEvidence> InspectJournalOperationAsync(string operationId, IStartupNativeAccess native,
        CancellationToken token = default, Action<string>? checkpoint = null)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var id = OperationJournal.NormalizeId(operationId); var actorKey = CurrentUserIdentity.Key;
            using var root = SshInputFileLease.OpenDirectory(_paths.RootDirectory);
            using var mutation = SshInputFileLease.OpenOptional(Path.Combine(_paths.RootDirectory, StartupMutationLease.FileName), 0);
            using var workspace = SshInputFileLease.OpenOptional(Path.Combine(_paths.RootDirectory, RootWriteLease.FileName), 0);
            var directory = Path.Combine(_paths.RootDirectory, ".autorun-state", actorKey, "operations");
            var metadataPath = Path.Combine(directory, "metadata.json"); var receiptPath = Path.Combine(directory, id + ".operation.json");
            using var metadata = RootImage.Open(metadataPath, MaximumJournalImageBytes);
            using var receipt = RootImage.Open(receiptPath, MaximumJournalImageBytes);
            checkpoint?.Invoke("journal_images_read"); token.ThrowIfCancellationRequested();
            if (metadata.Bytes is not null)
            {
                using var document = JournalObject(metadata.Bytes, ["schemaVersion", "minimumTimestamp"]);
                if (!document.RootElement.GetProperty("schemaVersion").TryGetInt32(out var version) || version != OperationReceipt.CurrentSchemaVersion
                    || !document.RootElement.GetProperty("minimumTimestamp").TryGetInt64(out _))
                { throw new JsonException("Journal metadata fields are invalid."); }
            }
            if (receipt.Bytes is null)
            {
                checkpoint?.Invoke("journal_receipt_absent"); token.ThrowIfCancellationRequested();
                // A missing leaf has no retained image chain pin. Take new secure
                // images explicitly (the existing byte reader also re-admits paths),
                // and never adopt a leaf that appeared after the absent observation.
                using var metadataAgain = RootImage.Open(metadataPath, MaximumJournalImageBytes);
                using var receiptAgain = RootImage.Open(receiptPath, MaximumJournalImageBytes);
                if ((metadata.Bytes is null ? metadataAgain.Bytes is not null : metadataAgain.Bytes is null || !metadata.Bytes.AsSpan().SequenceEqual(metadataAgain.Bytes))
                    || receiptAgain.Bytes is not null) { return new("unverified", "history_journal_images_changed"); }
                if (CurrentUserIdentity.Key != actorKey) { return new("unverified", "history_journal_actor_changed"); }
                return new("missing", "history_journal_receipt_missing");
            }
            if (workspace is null) { return new("unavailable", "history_query_coordination_missing"); }
            if (metadata.Bytes is null) { return new("unverified", "history_journal_metadata_missing"); }
            using var json = JournalObject(receipt.Bytes, JournalReceiptFields);
            var persisted = json.RootElement.GetProperty("state");
            if (persisted.ValueKind != JsonValueKind.String || persisted.GetString() is not ("running" or "completed" or "unknown"))
            { throw new JsonException("Journal receipt state is invalid."); }
            // Reuse the shared SDK terminal/header/history checks, rather than a
            // second partial receipt validator that could accept impossible skeletons.
            var snapshot = AgentOperationClient.ReadReceipt(new("history-journal-snapshot", true, Data: json.RootElement), id)
                ?? throw new JsonException("Journal receipt is null.");
            if (!string.Equals(snapshot.Command, "startup-disable", StringComparison.OrdinalIgnoreCase))
            { return new("unavailable", "history_journal_command_unsupported"); }
            if (snapshot.SchemaVersion != OperationReceipt.CurrentSchemaVersion || snapshot.OperationId != id || string.IsNullOrWhiteSpace(snapshot.OwnerInstanceId)
                || snapshot.AcceptedAt == default || !string.Equals(snapshot.Command, "startup-disable", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(snapshot.Name) || snapshot.Fingerprint is not { Length: 64 }
                || snapshot.Fingerprint.Any(value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            { throw new JsonException("Journal receipt identity is invalid."); }
            var diskState = snapshot.State.ToString().ToLowerInvariant();
            // Persisted running is not evidence of a live callback, nor proof of a crash.
            // Unknown is conservative even if another owner is in fact still active.
            if (snapshot.State == OperationState.Running) { snapshot = snapshot with { State = OperationState.Unknown }; }
            checkpoint?.Invoke("journal_snapshot_read"); token.ThrowIfCancellationRequested();
            var evidence = await InspectOperationAsync(snapshot, native, token, checkpoint).ConfigureAwait(false);
            checkpoint?.Invoke("journal_evidence_sealing"); token.ThrowIfCancellationRequested(); metadata.VerifyCurrent(); receipt.VerifyCurrent();
            if (CurrentUserIdentity.Key != actorKey) { return new("unverified", "history_journal_actor_changed"); }
            return new("observed", diskState == "running" ? "history_journal_running_unconfirmed" : "history_journal_snapshot_only",
                diskState, snapshot.State.ToString().ToLowerInvariant(), evidence);
        }
        catch (OperationCanceledException) { throw; }
        catch (AgentCommandException error)
        { return new("unverified", error.Code == "invalid_operation_id" ? "history_journal_operation_id_invalid" : "history_journal_receipt_invalid"); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 || error.InnerException is Win32Exception { NativeErrorCode: 32 or 33 })
        { return new("busy", "history_journal_query_busy"); }
        catch (Win32Exception error) when (error.NativeErrorCode is 32 or 33) { return new("busy", "history_journal_query_busy"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or Win32Exception
            or InvalidOperationException or ArgumentException or CryptographicException or JsonException or FormatException)
        { return new("unverified", "history_journal_query_unavailable"); }
    }

    private static JsonDocument JournalObject(byte[] bytes, IReadOnlyList<string> fields)
    {
        var document = JsonDocument.Parse(bytes);
        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new JsonException("Journal image must be an object."); }
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!fields.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                { throw new JsonException("Journal image has noncanonical fields."); }
            }
            if (seen.Count != fields.Count) { throw new JsonException("Journal image fields are missing."); }
            return document;
        }
        catch { document.Dispose(); throw; }
    }
}
