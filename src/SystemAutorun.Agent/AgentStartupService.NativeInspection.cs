using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Agent;

internal sealed partial class AgentStartupService
{
    internal async Task<StartupNativeInspectionSnapshot> InspectNativeAsync(string entryId, StartupInspectionRequest request,
        string expectedRevision, CancellationToken cancellationToken)
    {
        var query = StartupNativeInspectionPolicy.ValidateRequest(entryId, request);
        if (!StartupNativeInspectionPolicy.LowerHex(expectedRevision, 64))
        { throw new ArgumentException("Native observation requires the configuration revision from startup list."); }
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = await workspace.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (configuration.Revision != expectedRevision)
        { throw new ConfigurationConflictException(expectedRevision, configuration.Revision); }
        if (slotReader is null)
        { throw new AgentCommandException("startup_native_observation_unavailable", "No fixed-source evidence reader is available; nothing was changed or executed."); }
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        { throw new AgentCommandException("startup_scan_busy", "The shared startup observation worker is busy; no request was queued or native write prepared."); }
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<StartupNativeInspectionSnapshot>? scan = null;
        try
        {
            budget.CancelAfter(TimeSpan.FromSeconds(5));
            scan = ObserveWorkflowAsync(configuration, query, entryId, request, expectedRevision, budget.Token);
            var result = await scan.WaitAsync(budget.Token).ConfigureAwait(false);
            budget.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new AgentCommandException("startup_native_observation_timeout", "Startup evidence budget expired; no evidence or native write authority is returned. The worker remains occupied until actual completion."); }
        catch (KeyNotFoundException)
        { throw new AgentCommandException("startup_entry_changed", "Selected backup disappeared during observation; re-read and review. No evidence or native write authority is returned."); }
        catch (UnauthorizedAccessException)
        { throw new AgentCommandException("startup_preflight_permission_denied", "Startup metadata could not be read; no evidence or native write authority is returned."); }
        catch (IOException)
        { throw new AgentCommandException("startup_native_observation_io_error", "Startup metadata IO failed; no evidence or native write authority is returned."); }
        catch (JsonException)
        { throw new AgentCommandException("startup_backup_invalid", "The complete startup ledger is invalid; no evidence or native write authority is returned."); }
        finally
        {
            if (scan is { IsCompleted: false }) { _pendingRelease = ReleaseWhenFinishedAsync(scan, budget); }
            else { budget.Dispose(); Volatile.Write(ref _reading, 0); }
        }
    }

    private async Task<StartupNativeInspectionSnapshot> ObserveWorkflowAsync(ConfigurationWorkspaceSnapshot configuration,
        StartupListQuery query, string entryId, StartupInspectionRequest request, string revision, CancellationToken cancellationToken)
    {
        var before = ComposeInspection(await ReadInventoryCoreAsync(configuration, query, cancellationToken).ConfigureAwait(false),
            entryId, request, revision);
        if (!Guid.TryParseExact(before.Entry.BackupId, "N", out var id) || id == Guid.Empty)
        { throw new AgentCommandException("startup_backup_invalid", "Native observation requires the original N-format backup ID; nothing was changed."); }
        cancellationToken.ThrowIfCancellationRequested();
        var read = await slotReader!.ReadAsync(before.Entry.BackupId!, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (read?.Evidence is null || read.Evidence.RecordMetadataDigest != read.RecordMetadataDigestAfter)
        { throw new AgentCommandException("startup_backup_changed", "Selected known-record metadata changed during native observation; no evidence or native write authority is returned."); }
        var current = await workspace.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current.Revision != revision) { throw new ConfigurationConflictException(revision, current.Revision); }
        var after = ComposeInspection(await ReadInventoryCoreAsync(current, query, cancellationToken).ConfigureAwait(false),
            entryId, request, revision);
        cancellationToken.ThrowIfCancellationRequested();
        if (!StartupNativeInspectionPolicy.ValidEvidence(read, after.Entry))
        { throw new AgentCommandException("startup_native_evidence_invalid", "The reader returned mismatched native evidence; no native submission is authorized."); }
        return new(2, revision, DateTimeOffset.UtcNow, after, read.Evidence, read.RecordMetadataDigestAfter,
            "sequential-independent", "unverified", NativeWriteReady: false, NativeWritePerformed: false, ExecutionRequested: false);
    }
}
