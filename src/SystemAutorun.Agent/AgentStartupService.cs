using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.Agent;

internal sealed partial class AgentStartupService(ConfigurationWorkspace workspace, IStartupSnapshotReader reader,
    IStartupSlotEvidenceReader? slotReader = null)
{
    private int _reading;
    private Task? _pendingRelease;

    internal async Task<StartupInventorySnapshot> ReadAsync(StartupListQuery query, CancellationToken cancellationToken)
    {
        query = query.Validate();
        var configuration = await workspace.ReadAsync(cancellationToken).ConfigureAwait(false);
        var revision = configuration.Revision;
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        { return Unavailable(revision, "startup_scan_busy"); }
        Task<StartupInventorySnapshot>? scan = null;
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            budget.CancelAfter(TimeSpan.FromSeconds(5));
            // Retain the WHOLE workflow, not merely the caller's wait. A getter may ignore
            // cancellation; no subsequent stage or replacement worker may then be admitted.
            scan = ReadInventoryCoreAsync(configuration, query, budget.Token);
            var result = await scan.WaitAsync(budget.Token).ConfigureAwait(false);
            budget.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Unavailable(revision, "startup_scan_timeout"); }
        catch (UnauthorizedAccessException) { return Unavailable(revision, "permission_denied"); }
        catch (IOException) { return Unavailable(revision, "startup_io_error"); }
        catch (JsonException) { return Unavailable(revision, "startup_backup_invalid"); }
        finally
        {
            if (scan is { IsCompleted: false }) { _pendingRelease = ReleaseWhenFinishedAsync(scan, budget); }
            else { budget.Dispose(); Volatile.Write(ref _reading, 0); }
        }
    }

    internal async Task<WorkspaceMutation<StartupAdoptionResult>> AdoptAsync(string entryId,
        StartupAdoptionRequest request, string expectedRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = request.Validate(entryId);
        var configuration = await workspace.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (configuration.Revision != expectedRevision)
        { throw new ConfigurationConflictException(expectedRevision, configuration.Revision); }
        var observation = await ReadAsync(query, cancellationToken).ConfigureAwait(false);
        if (observation.ConfigurationRevision != expectedRevision)
        { throw new ConfigurationConflictException(expectedRevision, observation.ConfigurationRevision); }
        if (!observation.Available)
        { throw new AgentCommandException(observation.Reason == "permission_denied" ? "startup_preflight_permission_denied" : "startup_preflight_unavailable",
            $"Startup preflight unavailable ({observation.Reason ?? "startup_source_unavailable"}); no task was added."); }
        if (observation.Truncated || observation.SourceIssues.Any(issue => issue.Source == request.ExpectedEntry.Source))
        { throw new AgentCommandException("startup_inventory_incomplete", "Selected startup source is incomplete; no task was added."); }
        var matches = observation.Entries.Where(entry => entry.Id == entryId).ToArray();
        if (matches.Length != 1 || matches[0] != request.ExpectedEntry)
        { throw new AgentCommandException("startup_entry_changed", "Selected startup metadata changed or disappeared. Re-read and review before a new intent."); }
        // This is a freshness check, not a native CAS. No native mutation/launch is made;
        // the final workspace CAS alone guards the current root's configuration write.
        var mutation = await workspace.AddTaskAsync(request.Task, expectedRevision, cancellationToken).ConfigureAwait(false);
        return new(new(entryId, mutation.Value, NativeWritePerformed: false, ExecutionRequested: false),
            mutation.Revision, mutation.CapturedAt);
    }

    private async Task ReleaseWhenFinishedAsync(Task scan, CancellationTokenSource budget)
    {
        try { await scan.ConfigureAwait(false); }
        catch (Exception) { /* The caller already received an explicit unavailable/cancelled result. */ }
        finally { budget.Dispose(); Volatile.Write(ref _reading, 0); }
    }

    private static StartupInventorySnapshot Unavailable(string revision, string reason, IReadOnlyList<StartupSourceIssue>? issues = null) =>
        new(1, revision, DateTimeOffset.UtcNow, null, false, reason, [], null, null, false, false, false, 0, issues ?? []);
}
