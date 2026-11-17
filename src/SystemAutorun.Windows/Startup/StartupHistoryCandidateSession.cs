using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace SystemAutorun.Windows.Startup;

internal sealed record StartupHistoryCandidateSessionAttempt(StartupHistoryPublication Publication, StartupHistoryCandidateSession? Session) : IDisposable
{
    public void Dispose() => Session?.Dispose();
}

internal sealed record StartupHistoryArchiveResult(string State, string Reason, bool NativeWriteAttempted, bool NativeWritePerformed, int? NativeError = null)
{
    public bool NativeWriteReady => false;
    public bool ExecutionRequested => false;
    public bool Committed => false;
}

// Internal continuous IO session, not a business/transport entry point. Only the
// live ledger-verified owner can advance history; no production request is connected.
// One native attempt only; never roll back on uncertainty.
internal sealed partial class StartupHistoryCandidateSession : IDisposable
{
    private readonly object _gate = new();
    private readonly StartupNativeSlotProbe.HistoryStoreWriteLease _lease;
    private readonly Func<string> _directory;
    private StartupShortcutCapture? _capture;
    private StartupNativeSlotProbe.ShortcutArchiveLease? _archive;
    private Func<string>? _configuredArchive;
    private StartupHistoryArchiveResult? _result;
    private bool _used;
    private bool _disposed;
    internal StartupHistoryPayload Candidate { get; }
    private StartupHistoryPayload _expected;

    internal StartupHistoryCandidateSession(StartupHistoryPayload candidate, StartupNativeSlotProbe.HistoryStoreWriteLease lease, Func<string> directory)
    { Candidate = _expected = candidate; _lease = lease; _directory = directory; }

    internal void VerifyCurrent(CancellationToken token = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
            StartupHistoryProtection.RequireActor(Candidate.Binding.ActorSid); _lease.VerifyCurrent(token);
            var directory = _lease.Directory.DirectoryPath;
            if (StartupShortcutPathLease.LocalPath(_directory()) != directory) { throw new InvalidOperationException("History store mapping changed."); }
            var state = _expected.Phase == "candidate" ? "candidate" : "protected_committed";
            var check = new StartupHistoryStoreReader(Candidate.Binding.Root.Path, () => directory).Inspect(Candidate.Binding.Context, token);
            if (check.State != state) { throw new InvalidOperationException("History is no longer independently current."); }
            // Context-only lookup is not the exact active nonce/sequence/body. Hold both
            // leaves through a second, full expected-phase comparison, including commit.
            using (var index = _lease.Directory.OpenFile(StartupHistoryStoreReader.IndexName, StartupHistoryProtection.MaximumProtectedIndexBytes, token))
            using (var proof = _lease.Directory.OpenFile(StartupHistoryStoreReader.ProofName(Candidate.Binding.RecordId, _expected.Phase), StartupHistoryProtection.MaximumProtectedBytes, token))
            {
                var row = StartupHistoryProtection.UnprotectIndex(index.Bytes, token).Entries.SingleOrDefault(entry => entry.Binding.RecordId == Candidate.Binding.RecordId);
                if (row is null || row.Binding != Candidate.Binding || row.Latest.Nonce != _expected.Nonce || row.Latest.Sequence != _expected.Sequence
                    || row.Latest.Phase != _expected.Phase || StartupHistoryProtection.Unprotect(proof.Bytes, token) != _expected
                    || StartupHistoryProtection.Inspect(proof.Bytes, Candidate.Binding, row.Latest, token).State != state)
                { throw new InvalidOperationException("The active protected history changed."); }
                index.VerifyCurrent(token); proof.VerifyCurrent(token);
            }
            if (_capture is not null) { VerifyCapture(token); }
            if (_archive is not null)
            {
                if (StartupShortcutPathLease.LocalPath(_configuredArchive!()) != Candidate.Binding.ArchiveBase.Path)
                { throw new InvalidOperationException("Current archive configuration changed."); }
                _archive.VerifyCurrent(token);
                if (_archive.Moved) { _archive.VerifyResult(_capture!.HeldInput, token); }
            }
            StartupHistoryProtection.RequireActor(Candidate.Binding.ActorSid);
        }
    }

    // After entry checks, the first attempt adopts capture even on refusal. Repeated
    // calls take no new input and never retry native. Dispose before re-opening that
    // source. Archive configuration MUST be supplied live, not read from history.
    internal StartupHistoryArchiveResult ArchiveCapturedShortcut(StartupShortcutCapture capture, Func<string> currentArchiveBase,
        CancellationToken token = default, Action<string>? checkpoint = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
            if (_used) { return _result ?? new("unverified_result", "history_native_session_used", _archive?.Attempted == true, _archive?.Moved == true); }
            _used = true; _capture = capture; _configuredArchive = currentArchiveBase;
            StartupHistoryArchiveResult Failure(string reason, int? error = null) => new(_archive?.Attempted == true ? "unverified_result" : "rejected",
                reason, _archive?.Attempted == true, _archive?.Moved == true, error);
            try
            {
                ArgumentNullException.ThrowIfNull(capture); ArgumentNullException.ThrowIfNull(currentArchiveBase);
                VerifyCurrent(token);
                if (StartupShortcutPathLease.LocalPath(currentArchiveBase()) != Candidate.Binding.ArchiveBase.Path)
                { throw new InvalidOperationException("Archive configuration does not match current binding."); }
                _archive = StartupNativeSlotProbe.ShortcutArchiveLease.Open(Candidate.Binding, token);
                VerifyCurrent(token); _archive.RequireMissingDestination(token);
                checkpoint?.Invoke("archive_prepared"); token.ThrowIfCancellationRequested();
                VerifyCurrent(token); _archive.RequireMissingDestination(token);
                checkpoint?.Invoke("before_native"); token.ThrowIfCancellationRequested();
                // Recheck after all injectable work. Kernel replace=false still guards
                // a destination created between this observation and the single rename.
                VerifyCurrent(token);
                _archive.Move(capture.HeldInput, token);
                checkpoint?.Invoke("native_renamed"); token.ThrowIfCancellationRequested();
                VerifyCurrent(token); checkpoint?.Invoke("native_verified"); token.ThrowIfCancellationRequested();
                VerifyCurrent(token);
                return _result = new("archived_uncommitted", "history_ledger_and_commit_required", true, true);
            }
            catch (OperationCanceledException) { return _result = Failure("history_native_canceled"); }
            catch (Win32Exception error) { return _result = Failure("history_native_unavailable", error.NativeErrorCode); }
            catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or CryptographicException or JsonException)
            { return _result = Failure("history_native_context_unverified"); }
        }
    }

    private void VerifyCapture(CancellationToken token)
    {
        _capture!.VerifyCurrent(token); var scope = _capture.Scope; var input = _capture.Input; var binding = Candidate.Binding;
        if (_capture.Name != binding.Name || scope.Source != binding.Source.Source || scope.SourceKind != binding.Source.Kind
            || scope.KnownFolder != binding.Source.KnownFolder || scope.ObserverSid != binding.ActorSid || scope.SourceUserSid != binding.Source.UserSid
            || scope.DirectoryPath != binding.Source.Directory.Path || scope.DirectoryIdentity != binding.Source.Directory.Identity
            || scope.CaseSensitivity != binding.Source.Directory.CaseSensitivity || input.FileIdentity != binding.Original.Identity
            || input.Length != binding.Original.Length || input.LinkCount != binding.Original.LinkCount || input.Sha256 != binding.Original.Sha256)
        { throw new InvalidOperationException("Live original does not match the exact candidate binding."); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; } _disposed = true;
            try { _capture?.Dispose(); }
            finally
            {
                try { try { _archive?.Dispose(); } finally { _lease.Dispose(); } }
                finally { if (_committedIndexImageDigest is not null) { CryptographicOperations.ZeroMemory(_committedIndexImageDigest); } }
            }
        }
    }
}
