using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace SystemAutorun.Windows.Startup;

// Real read-only side-store IO. It authenticates given correlation, not issuance by
// a Journal/owner, a native outcome, an operation scan or permission to write anything.
internal sealed class StartupHistoryOperationStoreReader
{
    private const string LinkSuffix = ".operation-v1.dpapi";
    private readonly string _root;
    private readonly Func<string> _directory;
    internal StartupHistoryOperationStoreReader(string projectRoot, Func<string>? ownedDirectory = null)
    { _root = StartupShortcutPathLease.LocalPath(projectRoot); _directory = ownedDirectory ?? StartupHistoryStoreReader.DefaultDirectory; }

    internal static string LinkName(string recordId)
    {
        if (!StartupHistoryFormat.Identifier(recordId)) { throw new ArgumentException("A canonical history record ID is required."); }
        return recordId + LinkSuffix;
    }
    internal static bool IsLinkName(string leaf) => leaf is { Length: 51 } && StartupHistoryFormat.Identifier(leaf[..32]) && leaf[32..] == LinkSuffix;

    internal StartupHistoryOperationCheck Inspect(StartupHistoryContext current, StartupHistoryOperationReference expected,
        CancellationToken token = default, Action? afterIndexRead = null, Action? beforeSeal = null)
    {
        token.ThrowIfCancellationRequested(); var stage = "location";
        try
        {
            StartupHistoryFormat.ValidateContext(current); StartupHistoryOperationFormat.ValidateReference(expected);
            var actor = StartupHistoryProtection.Actor();
            if (current.ActorSid != actor || current.Root.Path != _root) { return new("unverified", "history_store_context_changed"); }
            var directory = StartupShortcutPathLease.LocalPath(_directory());
            var prefix = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
            if (directory.Equals(_root, StringComparison.OrdinalIgnoreCase) || directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            { return new("unverified", "history_store_inside_root"); }
            using var lease = StartupNativeSlotProbe.HistoryDirectoryReadLease.Open(directory, current.Root, actor, token);
            stage = "index";
            using var indexFile = lease.OpenFile(StartupHistoryStoreReader.IndexName, StartupHistoryProtection.MaximumProtectedIndexBytes, token);
            var index = StartupHistoryProtection.UnprotectIndex(indexFile.Bytes, token);
            var row = index.Entries.SingleOrDefault(entry => entry.Binding.RecordId == current.RecordId);
            if (row is null) { return new("unverified", "history_store_record_not_current"); }
            if (row.Binding.Context != current) { return new("unverified", "history_store_context_changed"); }
            afterIndexRead?.Invoke(); token.ThrowIfCancellationRequested(); stage = "record";
            using var recordFile = lease.OpenFile(StartupHistoryStoreReader.ProofName(current.RecordId, row.Latest.Phase), StartupHistoryProtection.MaximumProtectedBytes, token);
            var history = StartupHistoryProtection.Inspect(recordFile.Bytes, row.Binding, row.Latest, token);
            if (history.State is not ("candidate" or "protected_committed")) { return new("unverified", history.Reason ?? "history_store_record_invalid"); }
            // Missing link is absence, not authority. Check it before requiring a parent
            // that an unlinked legacy committed record is not obliged to retain here.
            stage = "operation";
            using var operationFile = OpenOptionalLink(lease, current.RecordId, token);
            stage = "parent";
            using var parent = operationFile is not null && row.Latest.Phase == "committed"
                ? lease.OpenFile(StartupHistoryStoreReader.ProofName(current.RecordId, "candidate"), StartupHistoryProtection.MaximumProtectedBytes, token) : null;
            var result = operationFile is null ? new StartupHistoryOperationCheck("unlinked", "history_store_operation_missing")
                : operationFile.Bytes.Length == 0 ? new("unverified", "history_operation_payload_invalid")
                : StartupHistoryProtection.InspectOperationLink(operationFile.Bytes, expected, parent?.Bytes ?? recordFile.Bytes,
                    recordFile.Bytes, row.Binding, row.Latest, token);
            // No observer receives payloads or association IDs before this seal. Even
            // a locally matched result is discarded on any later verification failure.
            beforeSeal?.Invoke(); token.ThrowIfCancellationRequested();
            indexFile.VerifyCurrent(token); recordFile.VerifyCurrent(token); parent?.VerifyCurrent(token); operationFile?.VerifyCurrent(token); lease.VerifyCurrent(token);
            if (StartupShortcutPathLease.LocalPath(_directory()) != directory) { return new("unverified", "history_store_mapping_changed"); }
            StartupHistoryProtection.RequireActor(actor); return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return Missing(stage); }
        catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: 2 or 3 }) { return Missing(stage); }
        catch (UnauthorizedAccessException) { return new("unverified", "history_store_permissions_invalid"); }
        catch (SecurityException) { return new("unverified", "history_store_permissions_invalid"); }
        catch (CryptographicException) { return new("unverified", "history_store_protection_invalid"); }
        catch (JsonException) { return new("unverified", "history_store_data_invalid"); }
        catch (InvalidOperationException) { return new("unverified", "history_store_context_invalid"); }
        catch (ArgumentException) { return new("unverified", "history_store_context_invalid"); }
        catch (Win32Exception) { return new("unverified", "history_store_unavailable"); }
        catch (IOException) { return new("unverified", "history_store_unavailable"); }
    }

    private static StartupHistoryOperationCheck Missing(string stage) => new("unverified", "history_store_" + stage + "_missing");
    private static StartupNativeSlotProbe.HistoryFileReadLease? OpenOptionalLink(StartupNativeSlotProbe.HistoryDirectoryReadLease lease,
        string recordId, CancellationToken token)
    {
        try { return lease.OpenFile(LinkName(recordId), StartupHistoryProtection.MaximumProtectedBytes, token); }
        catch (Win32Exception error) when (error.NativeErrorCode == 2) { return null; }
        catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: 2 }) { return null; }
    }
}
