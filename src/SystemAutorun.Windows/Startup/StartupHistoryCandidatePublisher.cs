using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace SystemAutorun.Windows.Startup;

// Publication flags describe the requested exact candidate observed in this call,
// including adopted prior attempts, not arbitrary files sharing its record ID.
internal sealed record StartupHistoryPublication(string State, string Reason, bool ProofPublished, bool IndexPublished, int? NativeError = null)
{
    public bool NativeWriteReady => false;
    public bool NativeWritePerformed => false;
    public bool ExecutionRequested => false;
}

// Candidate-only private-store IO. The retained session/ledger owner, not this
// publisher, owns later phases. No candidate or old-record backfill grants rights.
internal sealed class StartupHistoryCandidatePublisher
{
    private readonly string _root;
    private readonly Func<string> _directory;
    internal StartupHistoryCandidatePublisher(string projectRoot, Func<string>? ownedDirectory = null)
    { _root = StartupShortcutPathLease.LocalPath(projectRoot); _directory = ownedDirectory ?? StartupHistoryStoreReader.DefaultDirectory; }

    internal StartupHistoryPublication Publish(StartupHistoryPayload candidate, CancellationToken token = default, Action<string>? checkpoint = null)
        => PublishCore(candidate, token, checkpoint, retain: false, out _);

    internal StartupHistoryCandidateSessionAttempt OpenSession(StartupHistoryPayload candidate, CancellationToken token = default, Action<string>? checkpoint = null)
    {
        var publication = PublishCore(candidate, token, checkpoint, retain: true, out var lease);
        return new(publication, lease is null ? null : new StartupHistoryCandidateSession(candidate, lease, _directory));
    }

    private StartupHistoryPublication PublishCore(StartupHistoryPayload candidate, CancellationToken token, Action<string>? checkpoint,
        bool retain, out StartupNativeSlotProbe.HistoryStoreWriteLease? retained)
    {
        retained = null; StartupNativeSlotProbe.HistoryStoreWriteLease? owned = null;
        token.ThrowIfCancellationRequested(); var proofPublished = false; var indexPublished = false;
        byte[] proof = []; byte[] indexBytes = [];
        StartupHistoryPublication Failure(string reason, int? nativeError = null) => new(indexPublished ? "uncertain" : proofPublished ? "partial" : "rejected", reason, proofPublished, indexPublished, nativeError);
        StartupHistoryPublication Classified(string state, string reason) => new(state, reason, proofPublished, indexPublished);
        try
        {
            ArgumentNullException.ThrowIfNull(candidate); StartupHistoryFormat.Validate(candidate); var actor = StartupHistoryProtection.Actor();
            if (candidate.Phase != "candidate" || candidate.Binding.ActorSid != actor || candidate.Binding.Root.Path != _root)
            { return Failure("history_candidate_context_invalid"); }
            var directory = StartupShortcutPathLease.LocalPath(_directory());
            var prefix = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
            if (directory.Equals(_root, StringComparison.OrdinalIgnoreCase) || directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            { return Failure("history_store_inside_root"); }
            proof = StartupHistoryProtection.Protect(candidate, token);
            var lease = owned = StartupNativeSlotProbe.HistoryStoreWriteLease.Open(directory, candidate.Binding.Root, actor, token);
            checkpoint?.Invoke("store_opened"); token.ThrowIfCancellationRequested();
            StartupHistoryIndex? existing = null;
            try
            {
                using var current = lease.Directory.OpenFile(StartupHistoryStoreReader.IndexName, StartupHistoryProtection.MaximumProtectedIndexBytes, token);
                existing = StartupHistoryProtection.UnprotectIndex(current.Bytes, token);
            }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
            {
                try { lease.RequireEmptyBootstrap(); }
                catch (InvalidOperationException) { return Classified("damaged", "history_candidate_index_missing"); }
            }
            checkpoint?.Invoke("index_read"); token.ThrowIfCancellationRequested();
            if (existing is null)
            {
                // Establish a valid independent empty baseline before any proof can
                // become an orphan. A missing nonempty store is never reconstructed.
                existing = new(1, actor, 1, Guid.NewGuid().ToString("N"), []);
                var initial = StartupHistoryProtection.ProtectIndex(existing, token);
                try
                {
                    using (var stage = lease.CreateStage(initial, token))
                    {
                        checkpoint?.Invoke("bootstrap_flushed"); token.ThrowIfCancellationRequested();
                        stage.Publish(StartupHistoryStoreReader.IndexName, replaceIndex: true, token, initialIndex: true);
                    }
                    using var verified = lease.Directory.OpenFile(StartupHistoryStoreReader.IndexName, StartupHistoryProtection.MaximumProtectedIndexBytes, token);
                    if (!initial.AsSpan().SequenceEqual(verified.Bytes)) { return Classified("damaged", "history_candidate_bootstrap_changed"); }
                    checkpoint?.Invoke("bootstrap_published"); token.ThrowIfCancellationRequested();
                }
                finally { CryptographicOperations.ZeroMemory(initial); }
            }

            var row = existing.Entries.SingleOrDefault(entry => entry.Binding.RecordId == candidate.Binding.RecordId);
            if (row is not null && (row.Binding != candidate.Binding || row.Latest.Sequence != candidate.Sequence
                || row.Latest.Nonce != candidate.Nonce || row.Latest.Phase != "candidate"))
            { return Classified("conflict", "history_candidate_latest_conflict"); }
            try
            {
                using var stored = lease.Directory.OpenFile(candidate.Binding.RecordId + ".dpapi", StartupHistoryProtection.MaximumProtectedBytes, token);
                StartupHistoryPayload actual;
                try { actual = StartupHistoryProtection.Unprotect(stored.Bytes, token); }
                catch (Exception error) when (error is CryptographicException or JsonException or ArgumentException)
                { return Classified("conflict", "history_candidate_proof_conflict"); }
                if (actual != candidate) { return Classified("conflict", "history_candidate_proof_conflict"); }
                proofPublished = true; CryptographicOperations.ZeroMemory(proof); proof = stored.Bytes.ToArray();
                stored.VerifyCurrent(token);
            }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
            {
                if (row is not null) { indexPublished = true; return Classified("damaged", "history_candidate_record_missing"); }
            }
            if (row is not null)
            {
                var match = StartupHistoryProtection.Inspect(proof, candidate.Binding, row.Latest, token);
                if (row.Binding != candidate.Binding || match.State != "candidate")
                { return Classified("conflict", "history_candidate_latest_conflict"); }
                indexPublished = true;
            }
            else
            {
                var latest = new StartupHistoryLatest(candidate.Sequence, candidate.Nonce, candidate.Phase, Convert.ToHexStringLower(SHA256.HashData(proof)));
                var rows = existing.Entries.Append(new StartupHistoryIndexEntry(candidate.Binding, latest)).ToArray();
                var next = new StartupHistoryIndex(1, actor, checked(existing.Generation + 1), Guid.NewGuid().ToString("N"), rows);
                // Validate capacity/duplicate native slots before publishing any proof.
                try { indexBytes = StartupHistoryProtection.ProtectIndex(next, token); }
                catch (JsonException) { return Classified("conflict", "history_candidate_index_conflict"); }
                if (!proofPublished)
                {
                    using (var stage = lease.CreateStage(proof, token))
                    {
                        checkpoint?.Invoke("proof_flushed"); token.ThrowIfCancellationRequested();
                        stage.Publish(candidate.Binding.RecordId + ".dpapi", replaceIndex: false, token); proofPublished = true;
                    }
                }
                checkpoint?.Invoke("proof_published"); token.ThrowIfCancellationRequested();
                using (var stage = lease.CreateStage(indexBytes, token))
                {
                    checkpoint?.Invoke("index_flushed"); token.ThrowIfCancellationRequested();
                    stage.Publish(StartupHistoryStoreReader.IndexName, replaceIndex: true, token); indexPublished = true;
                }
                checkpoint?.Invoke("index_published"); token.ThrowIfCancellationRequested();
            }
            using (var published = lease.Directory.OpenFile(StartupHistoryStoreReader.IndexName, StartupHistoryProtection.MaximumProtectedIndexBytes, token))
            {
                if (indexBytes.Length != 0 && !CryptographicOperations.FixedTimeEquals(SHA256.HashData(indexBytes), SHA256.HashData(published.Bytes)))
                { return Failure("history_candidate_index_readback_changed"); }
                var check = new StartupHistoryStoreReader(_root, () => directory).Inspect(candidate.Binding.Context, token);
                if (check.State != "candidate" || check.Reason != "history_not_committed") { return Failure("history_candidate_readback_failed"); }
                published.VerifyCurrent(token); lease.VerifyCurrent(token);
            }
            if (StartupShortcutPathLease.LocalPath(_directory()) != directory) { return Failure("history_store_mapping_changed"); }
            StartupHistoryProtection.RequireActor(actor); checkpoint?.Invoke("index_verified"); token.ThrowIfCancellationRequested();
            lease.VerifyCurrent(token);
            if (retain) { retained = owned; owned = null; }
            return new("candidate", "history_not_committed", true, true);
        }
        catch (OperationCanceledException) { return Failure("history_candidate_canceled"); }
        catch (UnauthorizedAccessException) { return Failure("history_candidate_permissions_invalid"); }
        catch (SecurityException) { return Failure("history_candidate_permissions_invalid"); }
        catch (CryptographicException) { return Failure("history_candidate_protection_invalid"); }
        catch (JsonException) { return Failure("history_candidate_data_invalid"); }
        catch (OverflowException) { return Failure("history_candidate_generation_exhausted"); }
        catch (InvalidOperationException) { return Failure("history_candidate_context_invalid"); }
        catch (ArgumentException) { return Failure("history_candidate_context_invalid"); }
        catch (Win32Exception error) { return Failure("history_candidate_unavailable", error.NativeErrorCode); }
        catch (IOException) { return Failure("history_candidate_unavailable"); }
        finally { try { owned?.Dispose(); } finally { CryptographicOperations.ZeroMemory(proof); CryptographicOperations.ZeroMemory(indexBytes); } }
    }
}
