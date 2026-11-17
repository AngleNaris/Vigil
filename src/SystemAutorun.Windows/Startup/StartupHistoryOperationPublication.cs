using System.Security.Cryptography;

namespace SystemAutorun.Windows.Startup;

internal sealed partial class StartupHistoryCandidateSession
{
    // Fresh same-owner issuance only. Not a general context/candidate signer or
    // adoption/backfill API. The owner already holds and verifies its exact intent.
    internal void PublishNewOperationLink(StartupManager.HistoryDisableTransaction owner, CancellationToken token, Action<string>? checkpoint)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
            if (_used || _expected != Candidate) { throw new InvalidOperationException("New candidate phase is required for operation issuance."); }
            var reference = owner.RequireOperationLinkOwner(this, token); VerifyCurrent(token);
            using var index = _lease.Directory.OpenFile(StartupHistoryStoreReader.IndexName, StartupHistoryProtection.MaximumProtectedIndexBytes, token);
            using var candidate = _lease.Directory.OpenFile(StartupHistoryStoreReader.ProofName(Candidate.Binding.RecordId, "candidate"), StartupHistoryProtection.MaximumProtectedBytes, token);
            VerifyCurrent(token);
            var link = new StartupHistoryOperationLink(1, reference.OperationId, reference.Fingerprint, reference.TransactionId,
                Convert.ToHexStringLower(SHA256.HashData(candidate.Bytes)), Candidate);
            var bytes = StartupHistoryProtection.ProtectOperationLink(link, token);
            try
            {
                using (var stage = _lease.CreateStage(bytes, token))
                {
                    checkpoint?.Invoke("operation_link_flushed"); token.ThrowIfCancellationRequested(); owner.RequireOperationLinkOwner(this, token);
                    // Kernel replace=false refuses EVERY existing destination, including
                    // an identical old attempt. No retry/adoption or repair is implied.
                    stage.PublishOperationLink(Candidate.Binding.RecordId, token);
                }
                checkpoint?.Invoke("operation_link_published"); token.ThrowIfCancellationRequested(); owner.RequireOperationLinkOwner(this, token);
                using var published = _lease.Directory.OpenFile(StartupHistoryOperationStoreReader.LinkName(Candidate.Binding.RecordId), StartupHistoryProtection.MaximumProtectedBytes, token);
                if (!published.Bytes.AsSpan().SequenceEqual(bytes)) { throw new InvalidOperationException("New operation link readback changed."); }
                VerifyOperationLink(reference, token); checkpoint?.Invoke("operation_link_verified"); token.ThrowIfCancellationRequested();
                index.VerifyCurrent(token); candidate.VerifyCurrent(token); published.VerifyCurrent(token); _lease.VerifyCurrent(token);
                owner.RequireOperationLinkOwner(this, token); VerifyOperationLink(reference, token);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    internal void VerifyOperationLink(StartupHistoryOperationReference reference, CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested(); _lease.VerifyCurrent(token);
            var check = new StartupHistoryOperationStoreReader(Candidate.Binding.Root.Path, _directory).Inspect(Candidate.Binding.Context, reference, token);
            if (check.State != (_expected.Phase == "candidate" ? "associated_candidate" : "associated_committed")
                || check.RecordId != Candidate.Binding.RecordId || check.TransactionId != reference.TransactionId)
            { throw new InvalidOperationException("The live owner's operation link is no longer independently verified."); }
        }
    }
}
