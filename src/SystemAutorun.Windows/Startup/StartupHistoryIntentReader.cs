using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Windows.Startup;

// Image comparisons against editable intent fields are not historical verification.
internal sealed record StartupHistoryIntentObservation(string State, string Reason, string? TransactionId = null, string? RecordId = null,
    string ConfigurationImage = "unobserved", string LedgerImage = "unobserved", string HistoryState = "unobserved", string? HistoryReason = null)
{
    public string NativeState => "unobserved";
    public bool NativeWriteReady => false;
    public bool ExecutionRequested => false;
    public bool TransactionFinalized => false;
    public bool MayFinalize => false;
}

internal sealed partial class StartupHistoryIntentReader
{
    private readonly ProjectPaths _paths;
    private readonly Func<string>? _directory;
    internal StartupHistoryIntentReader(string projectRoot, Func<string>? ownedDirectory = null)
    { _paths = new(StartupShortcutPathLease.LocalPath(projectRoot)); _directory = ownedDirectory; }

    internal StartupHistoryIntentObservation Inspect(CancellationToken token = default, Action<string>? checkpoint = null) => InspectCore(token, checkpoint);

    private StartupHistoryIntentObservation InspectCore(CancellationToken token, Action<string>? checkpoint,
        Action<StartupHistoryPayload>? observeAuthenticated = null)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var root = SshInputFileLease.OpenDirectory(_paths.RootDirectory);
            // Existing read-only admission, in writer order. Never create lock files,
            // wait while pinning metadata, or disrupt an active owner's publication.
            using var mutation = SshInputFileLease.OpenOptional(Path.Combine(_paths.RootDirectory, StartupMutationLease.FileName), 0);
            using var workspace = SshInputFileLease.OpenOptional(Path.Combine(_paths.RootDirectory, RootWriteLease.FileName), 0);
            using var intentImage = RootImage.Open(_paths.StartupBackupPath + ".transaction", StartupHistoryIntentFormat.MaximumBytes);
            if (intentImage.Bytes is null)
            {
                checkpoint?.Invoke("intent_absent"); token.ThrowIfCancellationRequested(); intentImage.VerifyCurrent();
                return new("none", "history_intent_absent");
            }
            var intent = StartupHistoryIntentFormat.Read(intentImage.Bytes);
            if (intent.Candidate.Binding.Root.Path != _paths.RootDirectory || intent.Candidate.Binding.ActorSid != StartupHistoryProtection.Actor())
            { return new("unverified_pending", "history_intent_context_changed"); }
            if (mutation is null || workspace is null) { return new("unavailable", "history_query_coordination_missing"); }
            using var configuration = RootImage.Open(_paths.ConfigPath, ConfigurationDocumentRequest.MaximumBytes);
            using var ledger = RootImage.Open(_paths.StartupBackupPath, StartupBackupLedger.MaximumBytes);
            checkpoint?.Invoke("images_read"); token.ThrowIfCancellationRequested();
            var configurationImage = configuration.Bytes is null ? "missing"
                : StartupHistoryIntentFormat.Hash(configuration.Bytes) == intent.ConfigurationSha256 ? "matched_image" : "changed";
            var ledgerImage = ClassifyLedger(intent, ledger.Bytes);
            // Root candidate is only a lookup filter/coherence expectation. Optional
            // native inspection uses authenticated payload, never these editable paths.
            var nativeObserver = configurationImage == "matched_image" && ledgerImage is "declared_empty" or "previous_image" or "proposed_image"
                ? observeAuthenticated : null;
            var history = new StartupHistoryStoreReader(_paths.RootDirectory, _directory).Inspect(intent.Candidate.Binding.Context, token,
                expectedCandidate: intent.Candidate, observeAuthenticated: nativeObserver);
            checkpoint?.Invoke("history_read"); token.ThrowIfCancellationRequested();
            intentImage.VerifyCurrent(); configuration.VerifyCurrent(); ledger.VerifyCurrent();
            return new("observed_pending", "history_native_unobserved", intent.TransactionId, intent.Record.Id,
                configurationImage, ledgerImage, history.State, history.Reason);
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException) { return new("unverified_pending", "history_intent_invalid_or_unsupported"); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 || error.InnerException is Win32Exception { NativeErrorCode: 32 or 33 })
        { return new("busy", "history_intent_query_busy"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or Win32Exception
            or InvalidOperationException or ArgumentException or CryptographicException)
        { return new("unavailable", "history_intent_query_unavailable"); }
    }

    private static string ClassifyLedger(StartupHistoryDisableIntent intent, byte[]? bytes)
    {
        if (bytes is null) { return intent.PreviousLedgerMissing ? "declared_empty" : "missing_after_declared_previous"; }
        try
        {
            var records = StartupBackupLedger.Read(bytes, requireComplete: true).Records; var hash = StartupHistoryIntentFormat.Hash(bytes);
            if (hash == intent.PreviousLedgerSha256)
            { return records.All(row => !row.Id.Equals(intent.Record.Id, StringComparison.OrdinalIgnoreCase)) ? "previous_image" : "unverified"; }
            if (hash != intent.LedgerSha256) { return "changed"; }
            if (records.Count == 0 || intent.PreviousLedgerMissing && records.Count != 1 || records[^1] != intent.Record)
            { return "unverified"; }
            var canonical = StartupBackupLedger.Write(records);
            try { return bytes.AsSpan().SequenceEqual(canonical) ? "proposed_image" : "unverified"; }
            finally { CryptographicOperations.ZeroMemory(canonical); }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { return "unverified"; }
    }

    private sealed class RootImage : IDisposable
    {
        private readonly string _path;
        private readonly int _maximum;
        private readonly SshInputFileLease? _pin;
        internal byte[]? Bytes { get; }
        private RootImage(string path, int maximum, SshInputFileLease? pin, byte[]? bytes)
        { _path = path; _maximum = maximum; _pin = pin; Bytes = bytes; }
        internal static RootImage Open(string path, int maximum)
        {
            var pin = SshInputFileLease.OpenOptional(path, maximum);
            // The retained NOFOLLOW/read-only pin blocks ordinary existing/new write
            // handles and replacement across the path read. Mapped writes are excluded.
            try { return new(path, maximum, pin, pin is null ? null : StartupManager.HistoryDisableTransaction.ReadRootBytes(path, maximum)
                ?? throw new IOException("Pinned query image disappeared.")); }
            catch { pin?.Dispose(); throw; }
        }
        internal void VerifyCurrent()
        {
            var current = StartupManager.HistoryDisableTransaction.ReadRootBytes(_path, _maximum);
            try
            {
                if (Bytes is null ? current is not null : current is null || !Bytes.AsSpan().SequenceEqual(current))
                { throw new IOException("A query image changed."); }
            }
            finally { if (current is not null) { CryptographicOperations.ZeroMemory(current); } }
        }
        public void Dispose() { try { _pin?.Dispose(); } finally { if (Bytes is not null) { CryptographicOperations.ZeroMemory(Bytes); } } }
    }
}
