using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace SystemAutorun.Windows.Startup;

// A snapshot of THIS live owner's explicit cleanup, never a read-side permission.
// False can mean an already removed intent with an unacknowledged result.
internal sealed record StartupHistoryFinalizationResult(string State, string Reason, bool IntentCleanupDispatched,
    bool IntentCleanupReturned, bool TransactionFinalized, int? NativeError = null)
{
    public bool NativeWriteReady => false;
    public bool ExecutionRequested => false;
}

public sealed partial class StartupManager
{
    internal sealed partial class HistoryDisableTransaction
    {
        private StartupHistoryFinalizationResult? _finalizationResult;

        // LIVE owner only, not restart reconciliation or an OperationJournal entry.
        // One terminal attempt; never recreate an intent, replay native or roll back.
        internal async Task<StartupHistoryFinalizationResult> FinalizeCommittedAsync(CancellationToken token = default, Action<string>? checkpoint = null)
        {
            // Do not abandon an in-flight finalization/Dispose, or let later cancellation
            // obscure an already cached terminal result from this exact owner.
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_finalizationResult is not null) { return _finalizationResult; }
                if (_commitResult is not { State: "committed", Committed: true, ProofPublished: true, IndexPublished: true })
                { return new("rejected", "history_live_committed_owner_required", false, false, false); }
                var dispatched = false; var returned = false;
                StartupHistoryFinalizationResult Failure(string reason, int? error = null) =>
                    new(dispatched ? "uncertain" : "rejected", reason, dispatched, returned, false, error);
                try
                {
                    VerifyForCommit(token); Session.VerifyForFinalization(token);
                    using var cleanup = StartupNativeSlotProbe.HistoryIntentCleanupLease.Open(_manager._paths, _root, _intent, token);
                    void VerifyInputs()
                    {
                        // The DELETE-reserving intent handle cannot coexist with the
                        // old path reader's no-DELETE-sharing lease. Read its held bytes.
                        VerifyContext(token); Session.VerifyForFinalization(token);
                        RequireImage(_manager._paths.StartupBackupPath, _ledger); cleanup.VerifyCurrent(_intent, token);
                    }
                    VerifyInputs(); checkpoint?.Invoke("finalization_prepared"); VerifyInputs(); token.ThrowIfCancellationRequested();
                    // Latch unknown BEFORE dispatch, not only in a known-exception catch.
                    // Unexpected/fatal caller failures must not leave a replayable owner.
                    _finalizationResult = new("uncertain", "history_intent_cleanup_unacknowledged", true, false, false);
                    dispatched = true;
                    _manager._operations.Execute(StartupMutationOperation.IntentCleanup, () =>
                    { VerifyInputs(); cleanup.RemoveAndClose(_intent, token); });
                    returned = true; checkpoint?.Invoke("intent_cleanup_returned");
                    void VerifyResult()
                    {
                        VerifyContext(token); Session.VerifyForFinalization(token);
                        RequireImage(_manager._paths.StartupBackupPath, _ledger); cleanup.VerifyRemoved(token);
                    }
                    VerifyResult(); checkpoint?.Invoke("finalization_verified"); VerifyResult();
                    cleanup.Dispose(); // Complete local releases BEFORE publishing terminal success.
                    return _finalizationResult = new("finalized", "history_live_owner_finalized", true, true, true);
                }
                catch (OperationCanceledException) { return _finalizationResult = Failure("history_finalization_canceled"); }
                catch (Win32Exception error) { return _finalizationResult = Failure("history_finalization_unavailable", error.NativeErrorCode); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                    or SecurityException or CryptographicException or JsonException or AggregateException)
                { return _finalizationResult = Failure(dispatched ? "history_intent_cleanup_uncertain" : "history_finalization_unverified"); }
            }
            finally { _gate.Release(); }
        }
    }
}
