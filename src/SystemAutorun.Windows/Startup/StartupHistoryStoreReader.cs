using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace SystemAutorun.Windows.Startup;

// A real external-store READ path, not a producer, bootstrap, repair or migration.
// Stored original facts come from the protected index, not from an unsigned ledger
// or a fabricated current-leaf observation. No check here can enable a native write.
internal sealed class StartupHistoryStoreReader
{
    internal const string IndexName = "latest-v1.dpapi";
    private readonly string _projectRoot;
    private readonly Func<string> _directory;

    internal StartupHistoryStoreReader(string projectRoot, Func<string>? ownedDirectory = null)
    { _projectRoot = StartupShortcutPathLease.LocalPath(projectRoot); _directory = ownedDirectory ?? DefaultDirectory; }

    // The authenticated latest phase chooses a fixed leaf; never probe another phase
    // as a fallback. Candidate and committed envelopes are immutable separate files.
    internal static string ProofName(string recordId, string phase)
    {
        if (!StartupHistoryFormat.Identifier(recordId) || phase is not ("candidate" or "committed"))
        { throw new ArgumentException("A canonical history record ID and phase are required."); }
        return recordId + (phase == "candidate" ? ".dpapi" : ".committed.dpapi");
    }
    internal static bool IsProofName(string leaf) => leaf.Length is 38 or 48 && StartupHistoryFormat.Identifier(leaf[..32])
        && leaf[32..] is ".dpapi" or ".committed.dpapi";

    internal static string DefaultDirectory() => Path.Combine(StartupShortcutPathLease.LocalPath(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify)),
        "Vigil", "startup-history-v1");

    internal StartupHistoryCheck Inspect(StartupHistoryContext current, CancellationToken token = default,
        Action? afterIndexRead = null, Action? beforeSeal = null, StartupHistoryPayload? expectedCandidate = null,
        Action<StartupHistoryPayload>? observeAuthenticated = null)
    {
        token.ThrowIfCancellationRequested(); var stage = "location";
        try
        {
            StartupHistoryFormat.ValidateContext(current); var actor = StartupHistoryProtection.Actor();
            if (expectedCandidate is not null)
            {
                StartupHistoryFormat.Validate(expectedCandidate);
                if (expectedCandidate.Phase != "candidate" || expectedCandidate.Sequence != 1 || expectedCandidate.Binding.Context != current)
                { return new("unverified", "history_store_intent_changed"); }
            }
            if (current.ActorSid != actor || current.Root.Path != _projectRoot)
            { return new("unverified", "history_store_context_changed"); }
            var directory = StartupShortcutPathLease.LocalPath(_directory());
            var prefix = Path.EndsInDirectorySeparator(_projectRoot) ? _projectRoot : _projectRoot + Path.DirectorySeparatorChar;
            if (directory.Equals(_projectRoot, StringComparison.OrdinalIgnoreCase) || directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            { return new("unverified", "history_store_inside_root"); }
            using var lease = StartupNativeSlotProbe.HistoryDirectoryReadLease.Open(directory, current.Root, actor, token);
            stage = "index";
            using var indexFile = lease.OpenFile(IndexName, StartupHistoryProtection.MaximumProtectedIndexBytes, token);
            var index = StartupHistoryProtection.UnprotectIndex(indexFile.Bytes, token);
            var row = index.Entries.SingleOrDefault(entry => entry.Binding.RecordId == current.RecordId);
            if (row is null) { return new("unverified", "history_store_record_not_current"); }
            if (row.Binding.Context != current) { return new("unverified", "history_store_context_changed"); }
            if (expectedCandidate is not null && row.Binding != expectedCandidate.Binding)
            { return new("unverified", "history_store_intent_changed"); }
            afterIndexRead?.Invoke(); token.ThrowIfCancellationRequested(); stage = "record";
            using var recordFile = lease.OpenFile(ProofName(current.RecordId, row.Latest.Phase), StartupHistoryProtection.MaximumProtectedBytes, token);
            var result = StartupHistoryProtection.Inspect(recordFile.Bytes, row.Binding, row.Latest, token);
            using var parent = expectedCandidate is not null && row.Latest.Phase == "committed"
                ? lease.OpenFile(ProofName(current.RecordId, "candidate"), StartupHistoryProtection.MaximumProtectedBytes, token) : null;
            if ((expectedCandidate is not null || observeAuthenticated is not null) && result.State is "candidate" or "protected_committed")
            {
                var payload = StartupHistoryProtection.Unprotect(recordFile.Bytes, token);
                if (expectedCandidate is not null && (payload.Phase == "candidate" ? payload != expectedCandidate
                    : payload.CandidateAt != expectedCandidate.CandidateAt || payload.Sequence != expectedCandidate.Sequence + 1
                        || parent is null || StartupHistoryProtection.Unprotect(parent.Bytes, token) != expectedCandidate))
                { return new("unverified", "history_store_intent_changed"); }
                // Read-only observer, not a signing/commit callback. It runs while the
                // independent index/proofs are held; failure of the later seal still wins.
                observeAuthenticated?.Invoke(payload);
            }
            beforeSeal?.Invoke(); token.ThrowIfCancellationRequested();
            indexFile.VerifyCurrent(token); recordFile.VerifyCurrent(token); parent?.VerifyCurrent(token); lease.VerifyCurrent(token);
            if (StartupShortcutPathLease.LocalPath(_directory()) != directory)
            { return new("unverified", "history_store_mapping_changed"); }
            StartupHistoryProtection.RequireActor(actor); return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
        { return new("unverified", "history_store_" + stage + "_missing"); }
        catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: 2 or 3 })
        { return new("unverified", "history_store_" + stage + "_missing"); }
        catch (UnauthorizedAccessException) { return new("unverified", "history_store_permissions_invalid"); }
        catch (SecurityException) { return new("unverified", "history_store_permissions_invalid"); }
        catch (CryptographicException) { return new("unverified", "history_store_protection_invalid"); }
        catch (JsonException) { return new("unverified", "history_store_data_invalid"); }
        catch (InvalidOperationException) { return new("unverified", "history_store_context_invalid"); }
        catch (ArgumentException) { return new("unverified", "history_store_context_invalid"); }
        catch (Win32Exception) { return new("unverified", "history_store_unavailable"); }
        catch (IOException) { return new("unverified", "history_store_unavailable"); }
    }
}
