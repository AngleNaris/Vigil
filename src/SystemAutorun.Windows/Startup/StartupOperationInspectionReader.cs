using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.Windows.Startup;

// Local diagnostic only. No producer, native writer, replay or live owner-state inference.
public sealed class StartupOperationInspectionReader(ProjectPaths paths) : IStartupOperationInspectionReader
{
    private readonly SemaphoreSlim _admission = new(1, 1);
    private readonly IStartupNativeAccess _native = WindowsStartupNativeAccess.Instance;
    private readonly Func<string>? _directory;
    internal StartupOperationInspectionReader(ProjectPaths paths, IStartupNativeAccess native, Func<string> directory) : this(paths)
    { _native = native; _directory = directory; } // Owned adapter mapping, never RPC/config input.

    public async Task<StartupOperationInspectionSnapshot> ReadAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var id = SystemAutorun.Core.Ipc.OperationJournal.NormalizeId(operationId);
        if (!await _admission.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        { return Project(id, new("busy", "history_journal_query_busy")); }
        try
        {
            var evidence = await Task.Run(() => new StartupHistoryIntentReader(paths.RootDirectory, _directory)
                .InspectJournalOperationAsync(id, _native, cancellationToken), CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); return Project(id, evidence);
        }
        finally { _admission.Release(); }
    }

    private static StartupOperationInspectionSnapshot Project(string id, StartupHistoryJournalEvidence journal)
    {
        StartupOperationEvidence? evidence = null;
        if (journal.Evidence is { } source)
        {
            var associated = source.State == "observed" && source.Association.State is "associated_candidate" or "associated_committed";
            var association = !associated && source.Association.State is "associated_candidate" or "associated_committed" ? "unverified" : source.Association.State;
            var native = source.Native.State is "original_matches" or "archive_matches" or "unobserved" ? source.Native.State : "unverified";
            evidence = new(source.State, source.Reason, association, source.Association.Reason,
                associated ? source.Association.RecordId : null, associated ? source.Association.TransactionId : null, native, source.Native.Reason);
        }
        var now = DateTimeOffset.UtcNow;
        var result = new StartupOperationInspectionSnapshot(1, id, now, now, true, journal.JournalState, journal.Reason,
            journal.PersistedReceiptState, journal.ObservedReceiptState, evidence, false, false, false, false, false, false);
        StartupOperationInspectionPolicy.Validate(result, id); return result;
    }
}
