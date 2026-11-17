using System.Security.Cryptography;
using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Windows.Startup;

internal sealed record StartupHistoryLedgerResult(string State, string Reason, bool NativeWriteAttempted,
    bool NativeWritePerformed, bool LedgerPublicationAttempted, bool LedgerVerified)
{
    public bool Committed => false;
    public bool NativeWriteReady => false;
    public bool ExecutionRequested => false;
}

public sealed partial class StartupManager
{
    // Internal bridge only. The draft must be NEW in the complete ledger. This is not
    // old-record backfill, a prepared user request, an operation receipt or a commit.
    internal Task<HistoryDisableTransaction> PrepareHistoryDisableAsync(StartupHistoryPayload candidate, StartupBackupRecord draft,
        Func<string>? ownedDirectory = null, CancellationToken token = default, Action<string>? checkpoint = null) =>
        PrepareHistoryDisableCoreAsync(candidate, draft, ownedDirectory, token, checkpoint, expectedRevision: null);

    private async Task<HistoryDisableTransaction> PrepareHistoryDisableCoreAsync(StartupHistoryPayload candidate, StartupBackupRecord draft,
        Func<string>? ownedDirectory, CancellationToken token, Action<string>? checkpoint, string? expectedRevision, HistoryOperationDraft? operation = null)
    {
        RootWriteLease.Held? root = null; IDisposable? mutation = null; SshInputFileLease? config = null;
        StartupShortcutCapture? capture = null; StartupHistoryCandidateSessionAttempt? attempt = null;
        HistoryDisableTransaction? transaction = null;
        try
        {
            // Native admission is outermost: never hold the configuration root while
            // waiting for a legacy scan/mutation. Callers must not already hold Root.
            operation?.Require(this, candidate, draft);
            mutation = await StartupMutationLease.AcquireAsync(_paths, token).ConfigureAwait(false);
            root = await RootWriteLease.AcquireAsync(_paths, token).ConfigureAwait(false);
            operation?.Require(this, candidate, draft);
            root.Require(_paths); EnsureNoUnresolvedTransaction(); StartupHistoryFormat.Validate(candidate);
            if (candidate.Phase != "candidate" || candidate.Sequence != 1 || candidate.Binding.Root.Path != StartupShortcutPathLease.LocalPath(_paths.RootDirectory)
                || draft.Disposition != "disabled" || draft.Id != candidate.Binding.RecordId || draft.Source != candidate.Binding.Source.Source
                || draft.Name != candidate.Binding.Name || draft.Origin != Path.Combine(candidate.Binding.Source.Directory.Path, draft.Name + ".lnk")
                || draft.BackupPath != Path.Combine(candidate.Binding.Archive.Path, draft.Id + ".lnk")
                || draft.DisabledAt.UtcTicks != candidate.CandidateAt.UtcTicks || StartupHistoryFormat.RecordDigest(draft) != candidate.Binding.RecordDigest)
            { throw new InvalidOperationException("A new exact disable draft and candidate are required."); }
            EnsureCanModify(draft.Source);
            config = SshInputFileLease.Open(_paths.ConfigPath, ConfigurationDocumentRequest.MaximumBytes);
            await RequireHistoryDraftRevisionAsync(expectedRevision, token).ConfigureAwait(false);
            var configuration = await _configurationLoader.LoadAsync(_paths.RootDirectory, token).ConfigureAwait(false);
            var archiveBase = StartupShortcutPathLease.LocalPath(configuration.Settings.BackupDirectory);
            if (archiveBase != candidate.Binding.ArchiveBase.Path) { throw new InvalidOperationException("Current archive configuration does not match."); }
            var before = HistoryDisableTransaction.ReadRootBytes(_paths.StartupBackupPath, StartupBackupLedger.MaximumBytes);
            IReadOnlyList<StartupBackupRecord> backups = before is null ? [] : StartupBackupLedger.Read(before, requireComplete: true).Records;
            if (backups.Any(record => record.Id.Equals(draft.Id, StringComparison.OrdinalIgnoreCase)))
            { throw new InvalidOperationException("An existing record cannot be issued new history."); }
            var ledger = StartupBackupLedger.Write(backups.Append(draft).ToArray());
            capture = CaptureShortcutForDisable(draft.Source, draft.Name, token); capture.VerifyCurrent(token);
            var scope = capture.Scope; var input = capture.Input;
            if (new StartupHistorySource(scope.Source, scope.SourceKind, scope.KnownFolder!, scope.SourceUserSid,
                    new(scope.DirectoryPath!, scope.DirectoryIdentity!, scope.CaseSensitivity)) != candidate.Binding.Source
                || scope.ObserverSid != candidate.Binding.ActorSid
                || new StartupHistoryOriginal(input.FileIdentity, input.Length, input.LinkCount, input.Sha256) != candidate.Binding.Original)
            { throw new InvalidOperationException("The draft is not bound to the live original."); }
            await RequireHistoryDraftRevisionAsync(expectedRevision, token).ConfigureAwait(false);
            if (expectedRevision is not null)
            {
                RequireHistoryDraftLayout(draft, archiveBase, scope.DirectoryPath!);
                using var archiveAdmission = StartupNativeSlotProbe.ShortcutArchiveLease.Open(candidate.Binding, token);
                archiveAdmission.RequireMissingDestination(token);
            }
            operation?.Require(this, candidate, draft);
            attempt = new StartupHistoryCandidatePublisher(_paths.RootDirectory, ownedDirectory).OpenSession(candidate, token);
            if (attempt.Session is null || attempt.Publication.State != "candidate")
            { throw new InvalidOperationException("Candidate publication was not verified: " + attempt.Publication.Reason); }
            transaction = new(this, root, mutation, config, capture, attempt, draft, before, ledger, archiveBase, operation);
            root = null; mutation = null; config = null; capture = null; attempt = null;
            await transaction.PublishIntentAsync(token, checkpoint).ConfigureAwait(false); return transaction;
        }
        catch
        {
            if (transaction is not null) { await transaction.DisposeAsync().ConfigureAwait(false); }
            else
            {
                try { try { capture?.Dispose(); } finally { attempt?.Dispose(); } }
                finally { try { config?.Dispose(); } finally { try { root?.Dispose(); } finally { mutation?.Dispose(); } } }
            }
            throw;
        }
    }

    // All native/ledger/commit work must remain inside this lifetime. Do not hold it
    // across user confirmation, create another root lease, or call OperationJournal.
    internal sealed partial class HistoryDisableTransaction : IAsyncDisposable
    {
        private readonly StartupManager _manager;
        private readonly RootWriteLease.Held _root;
        private readonly IDisposable _mutation;
        private readonly SshInputFileLease _config;
        private readonly StartupShortcutCapture _capture;
        private readonly StartupHistoryCandidateSessionAttempt _attempt;
        private readonly StartupBackupRecord _record;
        private readonly byte[]? _before;
        private readonly byte[] _ledger;
        private readonly byte[] _configuration;
        private readonly byte[] _intent;
        private readonly string _archiveBase;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly PublicationProgress _ledgerProgress = new();
        private StartupHistoryArchiveResult? _native;
        private StartupHistoryLedgerResult? _result;
        private bool _used;
        private bool _disposed;
        private bool _ledgerVerified;
        private bool _commitActive;
        private StartupHistoryCommitPublication? _commitResult;
        private StartupHistoryCandidateSession Session => _attempt.Session!;
        internal StartupHistoryPayload Candidate => Session.Candidate;
        internal string TransactionId { get; } = Guid.CreateVersion7().ToString("N");

        internal HistoryDisableTransaction(StartupManager manager, RootWriteLease.Held root, IDisposable mutation, SshInputFileLease config,
            StartupShortcutCapture capture, StartupHistoryCandidateSessionAttempt attempt, StartupBackupRecord record,
            byte[]? before, byte[] ledger, string archiveBase, HistoryOperationDraft? operation = null)
        {
            operation?.Require(manager, attempt.Session!.Candidate, record);
            _operationContext = operation?.Context;
            _manager = manager; _root = root; _mutation = mutation; _config = config; _capture = capture;
            _attempt = attempt; _record = record; _before = before; _ledger = ledger; _archiveBase = archiveBase;
            _configuration = ReadRootBytes(manager._paths.ConfigPath, ConfigurationDocumentRequest.MaximumBytes)
                ?? throw new IOException("Pinned configuration disappeared.");
            _intent = StartupHistoryIntentFormat.Write(new(1, "history-disable", TransactionId, record, Candidate,
                before is null, before is null ? null : Hash(before), Hash(ledger), Hash(_configuration)));
        }

        internal async Task PublishIntentAsync(CancellationToken token, Action<string>? checkpoint)
        {
            VerifyContext(token); RequireImage(_manager._paths.StartupBackupPath, _before);
            var progress = new PublicationProgress();
            await _manager.WriteBytesAtomicallyAsync(_manager._transactionPath, _intent, StartupMutationOperation.IntentPublication, progress, token,
                () => { VerifyContext(token); _manager.EnsureNoUnresolvedTransaction(); RequireImage(_manager._paths.StartupBackupPath, _before); }).ConfigureAwait(false);
            checkpoint?.Invoke("intent_published"); token.ThrowIfCancellationRequested();
            VerifyPending(token); checkpoint?.Invoke("intent_verified"); token.ThrowIfCancellationRequested(); VerifyPending(token);
        }

        internal async Task<StartupHistoryLedgerResult> ArchiveAndPublishLedgerAsync(CancellationToken token = default, Action<string>? checkpoint = null)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_used) { return _result!; } _used = true;
                StartupHistoryLedgerResult Result(string state, string reason) => new(state, reason, _native?.NativeWriteAttempted == true,
                    _native?.NativeWritePerformed == true, _ledgerProgress.Started, _ledgerVerified);
                try
                {
                    if (_operationContext is not null && !_operationLinked) { throw new InvalidOperationException("Operation association must precede native work."); }
                    VerifyPending(token);
                    _native = Session.ArchiveCapturedShortcut(_capture, () => { VerifyConfiguration(); return _archiveBase; }, token,
                        stage => { VerifyPending(token); checkpoint?.Invoke(stage); VerifyPending(token); });
                    if (_native.State != "archived_uncommitted") { return _result = Result(_native.State, _native.Reason); }
                    VerifyPending(token); checkpoint?.Invoke("before_ledger"); token.ThrowIfCancellationRequested();
                    await _manager.WriteBytesAtomicallyAsync(_manager._paths.StartupBackupPath, _ledger, StartupMutationOperation.LedgerPublication,
                        _ledgerProgress, token, () => VerifyPending(token)).ConfigureAwait(false);
                    checkpoint?.Invoke("ledger_published"); token.ThrowIfCancellationRequested();
                    VerifyContext(token); RequireImage(_manager._transactionPath, _intent); RequireImage(_manager._paths.StartupBackupPath, _ledger);
                    var readback = StartupBackupLedger.Read(ReadRootBytes(_manager._paths.StartupBackupPath, StartupBackupLedger.MaximumBytes)!, requireComplete: true);
                    var record = readback.Records.Single(row => row.Id == _record.Id);
                    if (StartupHistoryFormat.RecordDigest(record) != Candidate.Binding.RecordDigest) { throw new InvalidOperationException("Published ledger record changed."); }
                    checkpoint?.Invoke("ledger_verified"); token.ThrowIfCancellationRequested();
                    VerifyContext(token); RequireImage(_manager._transactionPath, _intent); RequireImage(_manager._paths.StartupBackupPath, _ledger);
                    _ledgerVerified = true;
                    return _result = Result("ledger_verified_uncommitted", "history_commit_required");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException
                    or System.Security.SecurityException or CryptographicException or System.ComponentModel.Win32Exception or OperationCanceledException or AggregateException)
                { return _result = Result(_native?.NativeWriteAttempted == true ? "unverified_result" : "rejected", "history_transaction_unverified"); }
            }
            finally { _gate.Release(); }
        }

        // History phase only. The root intent remains until THIS live owner explicitly
        // finalizes; restart reconciliation and user write routes are still disabled.
        internal async Task<StartupHistoryCommitPublication> PublishCommittedHistoryAsync(CancellationToken token = default, Action<string>? checkpoint = null)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_commitResult is not null) { return _commitResult; }
                if (!_ledgerVerified || _native?.State != "archived_uncommitted")
                { return new("rejected", "history_native_and_ledger_required", false, false, false); }
                _commitActive = true;
                return _commitResult = Session.PublishCommitted(this, token, checkpoint);
            }
            finally { _commitActive = false; _gate.Release(); }
        }

        internal void RequireCommitOwner(StartupHistoryCandidateSession session, CancellationToken token)
        {
            if (!_commitActive || !ReferenceEquals(Session, session)) { throw new InvalidOperationException("A live matching ledger owner is required."); }
            VerifyForCommit(token);
        }

        internal void VerifyForCommit(CancellationToken token = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_ledgerVerified || _native?.State != "archived_uncommitted") { throw new InvalidOperationException("Native and ledger verification must precede commit."); }
            VerifyContext(token); RequireImage(_manager._transactionPath, _intent); RequireImage(_manager._paths.StartupBackupPath, _ledger);
        }

        private void VerifyPending(CancellationToken token)
        { VerifyContext(token); RequireImage(_manager._transactionPath, _intent); RequireImage(_manager._paths.StartupBackupPath, _before); }
        private void VerifyContext(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); _operationContext?.RequireActiveFor(_manager._paths);
            _root.Require(_manager._paths); VerifyConfiguration(); _capture.VerifyCurrent(token); Session.VerifyCurrent(token);
            if (OperationReference is { } reference) { Session.VerifyOperationLink(reference, token); }
        }
        private void VerifyConfiguration() => RequireImage(_manager._paths.ConfigPath, _configuration, ConfigurationDocumentRequest.MaximumBytes);
        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        private static void RequireImage(string path, byte[]? expected, int maximum = StartupBackupLedger.MaximumBytes)
        {
            var actual = ReadRootBytes(path, maximum);
            try
            {
                if (expected is null ? actual is not null : actual is null || !actual.AsSpan().SequenceEqual(expected))
                { throw new InvalidOperationException("A transaction input or published image changed."); }
            }
            finally { if (actual is not null) { CryptographicOperations.ZeroMemory(actual); } }
        }
        internal static byte[]? ReadRootBytes(string path, int maximum)
        {
            using var pin = SshInputFileLease.OpenOptional(path, maximum); if (pin is null) { return null; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximum) { throw new IOException("Transaction input exceeds its byte limit."); }
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) { return; } _disposed = true;
                try { try { _capture.Dispose(); } finally { _attempt.Dispose(); } }
                finally
                {
                    try { try { _config.Dispose(); } finally { try { _root.Dispose(); } finally { _mutation.Dispose(); } } }
                    finally
                    {
                        foreach (var bytes in new[] { _before, _ledger, _configuration, _intent })
                        { if (bytes is not null) { CryptographicOperations.ZeroMemory(bytes); } }
                    }
                }
            }
            finally { _gate.Release(); }
        }
    }
}
