using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace SystemAutorun.Windows.Startup;

internal sealed record StartupHistoryCommitPublication(string State, string Reason, bool ProofPublished, bool IndexPublished,
    bool Committed, int? NativeError = null)
{
    public bool NativeWriteReady => false;
    public bool ExecutionRequested => false;
    public bool TransactionFinalized => false;
}

internal sealed partial class StartupHistoryCandidateSession
{
    private bool _commitUsed;
    private byte[]? _committedIndexImageDigest;
    // There is deliberately no payload/callback-only signing API. A sealed live
    // owner must prove this exact session, actual native success and ledger readback.
    internal StartupHistoryCommitPublication PublishCommitted(StartupManager.HistoryDisableTransaction owner,
        CancellationToken token, Action<string>? checkpoint)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
            var proofPublished = false; var indexPublished = false; byte[] proof = []; byte[] nextBytes = []; byte[] previous = [];
            StartupHistoryCommitPublication Failure(string reason, int? error = null) =>
                new(indexPublished ? "uncertain" : proofPublished ? "partial" : "rejected", reason, proofPublished, indexPublished, false, error);
            try
            {
                owner.RequireCommitOwner(this, token);
                if (_commitUsed || _expected != Candidate || _result?.State != "archived_uncommitted" || _archive is not { Attempted: true, Moved: true })
                { throw new InvalidOperationException("Only one uncommitted verified native result can advance history."); }
                _commitUsed = true;
                var when = DateTimeOffset.UtcNow;
                if (when < Candidate.CandidateAt) { throw new InvalidOperationException("History clock moved backwards."); }
                var committed = Candidate with { Phase = "committed", Sequence = checked(Candidate.Sequence + 1),
                    Nonce = Guid.NewGuid().ToString("N"), CommittedAt = when };
                var leaf = StartupHistoryStoreReader.ProofName(Candidate.Binding.RecordId, "committed");
                StartupHistoryIndex index;
                using (var current = _lease.Directory.OpenFile(StartupHistoryStoreReader.IndexName, StartupHistoryProtection.MaximumProtectedIndexBytes, token))
                {
                    index = StartupHistoryProtection.UnprotectIndex(current.Bytes, token); previous = current.Bytes.ToArray(); current.VerifyCurrent(token);
                }
                // A pre-existing committed slot is reconciliation evidence, never an
                // invitation to replace/adopt it or mint a different native attempt.
                try
                {
                    using var occupied = _lease.Directory.OpenFile(leaf, StartupHistoryProtection.MaximumProtectedBytes, token);
                    return new("conflict", "history_commit_slot_occupied", false, false, false);
                }
                catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { }
                proof = StartupHistoryProtection.Protect(committed, token);
                var latest = new StartupHistoryLatest(committed.Sequence, committed.Nonce, committed.Phase, Convert.ToHexStringLower(SHA256.HashData(proof)));
                var rows = index.Entries.Select(row => row.Binding.RecordId == Candidate.Binding.RecordId
                    ? new StartupHistoryIndexEntry(Candidate.Binding, latest) : row).ToArray();
                var next = index with { Generation = checked(index.Generation + 1), Nonce = Guid.NewGuid().ToString("N"), Entries = rows };
                // Bounded encoding and generation checks precede the new immutable proof.
                nextBytes = StartupHistoryProtection.ProtectIndex(next, token);
                void Checkpoint(string stage, byte[] expectedIndex)
                {
                    owner.RequireCommitOwner(this, token); VerifyIndexImage(expectedIndex, token);
                    checkpoint?.Invoke(stage); token.ThrowIfCancellationRequested();
                    owner.RequireCommitOwner(this, token); VerifyIndexImage(expectedIndex, token);
                }
                Checkpoint("commit_prepared", previous);
                using (var stage = _lease.CreateStage(proof, token))
                {
                    Checkpoint("commit_proof_flushed", previous);
                    stage.Publish(leaf, replaceIndex: false, token); proofPublished = true;
                }
                Checkpoint("commit_proof_published", previous);
                using (var published = _lease.Directory.OpenFile(leaf, StartupHistoryProtection.MaximumProtectedBytes, token))
                {
                    if (!proof.AsSpan().SequenceEqual(published.Bytes) || StartupHistoryProtection.Unprotect(published.Bytes, token) != committed)
                    { throw new IOException("Committed proof readback changed."); }
                    published.VerifyCurrent(token);
                }
                using (var stage = _lease.CreateStage(nextBytes, token))
                {
                    Checkpoint("commit_index_flushed", previous);
                    stage.Publish(StartupHistoryStoreReader.IndexName, replaceIndex: true, token); indexPublished = true;
                    // Switch expected phase immediately after the known index rename.
                    // A later failure is uncertain, not permission to regress the index.
                    _expected = committed;
                }
                Checkpoint("commit_index_published", nextBytes);
                Checkpoint("commit_verified", nextBytes);
                _committedIndexImageDigest = SHA256.HashData(nextBytes);
                return new("committed", "history_transaction_pending", true, true, true);
            }
            catch (OperationCanceledException) { return Failure("history_commit_canceled"); }
            catch (Win32Exception error) { return Failure("history_commit_unavailable", error.NativeErrorCode); }
            catch (OverflowException) { return Failure("history_commit_generation_exhausted"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException
                or ArgumentException or CryptographicException or JsonException or AggregateException)
            { return Failure("history_commit_unverified"); }
            finally
            {
                CryptographicOperations.ZeroMemory(proof); CryptographicOperations.ZeroMemory(nextBytes); CryptographicOperations.ZeroMemory(previous);
            }
        }
    }

    internal void VerifyForFinalization(CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_commitUsed || _committedIndexImageDigest is null || _expected.Phase != "committed"
                || _result?.State != "archived_uncommitted" || _archive is not { Attempted: true, Moved: true })
            { throw new InvalidOperationException("Only this session's acknowledged committed publication can finalize."); }
            VerifyCurrent(token);
            using var index = _lease.Directory.OpenFile(StartupHistoryStoreReader.IndexName, StartupHistoryProtection.MaximumProtectedIndexBytes, token);
            var digest = SHA256.HashData(index.Bytes);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(digest, _committedIndexImageDigest))
                { throw new IOException("The complete committed index image changed."); }
                index.VerifyCurrent(token); _lease.VerifyCurrent(token);
            }
            finally { CryptographicOperations.ZeroMemory(digest); }
        }
    }

    private void VerifyIndexImage(byte[] expected, CancellationToken token)
    {
        using var index = _lease.Directory.OpenFile(StartupHistoryStoreReader.IndexName, StartupHistoryProtection.MaximumProtectedIndexBytes, token);
        if (!expected.AsSpan().SequenceEqual(index.Bytes)) { throw new IOException("The complete independent history index changed."); }
        index.VerifyCurrent(token); _lease.VerifyCurrent(token);
    }
}
