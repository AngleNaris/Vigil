using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Recovery;

namespace SystemAutorun.Cli;

public sealed partial class CliApplication
{
    private static async Task WriteRecoveryPendingDiagnosticsAsync(ProjectPaths paths, string rootId,
        RecoveryControlSnapshot state, TextWriter diagnostic, CancellationToken token)
    {
        // Names come only from a validated live snapshot. This is not a directory
        // inventory, an admission check or authority to resolve/clear an operation.
        foreach (var task in state.Tasks)
        {
            var store = new RecoveryPendingStore(paths, task.TaskName);
            var pending = await store.ReadAsync(token);
            if (!pending.BlocksSubmission) { continue; }
            await diagnostic.WriteLineAsync($"local recovery hint | task: {JsonSerializer.Serialize(task.TaskName, AgentProtocol.JsonOptions)} | state: {pending.State} | rootId: {rootId}");
            if (pending.Operation is { } original)
            {
                await diagnostic.WriteLineAsync($"original operationId: {original.OperationId} | reservedAt: {original.ReservedAt:O} | expectedRevision: {original.ExpectedRevision} (local hint, NOT admission or current-state proof)");
                await diagnostic.WriteLineAsync($"Only query operation show {original.OperationId} with the same --root. History/current state does not unlock recovery; do not resubmit, replace the ID or clear the hint to retry.");
            }
            else
            {
                await diagnostic.WriteLineAsync($"pending unavailable: {pending.Reason}; no operation ID or permission is inferred. Do not clear the hint or submit a replacement recovery request.");
            }
            await diagnostic.WriteLineAsync("Local hint observations cover returned recovery task names only, not offline/orphan hints or an atomic snapshot. Agent JSON and exit status remain separate.");
        }
    }

    private static async Task<AgentResponse> SendRecoveryResumeAsync(ProjectPaths paths, string rootId, CliRequest request,
        Func<ICliTransport> connect, TextWriter diagnostic, CancellationToken token)
    {
        var id = OperationJournal.NormalizeId(request.OperationId);
        var pending = new RecoveryPendingStore(paths, request.Name!);
        if (request.Revision is not { Length: 64 } || !request.Revision.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f'))
        { throw new ArgumentException("A canonical lower-case recovery revision is required."); }
        RecoveryPendingStore.Reservation reserved;
        try
        {
            var leaseTimeout = request.Timeout < TimeSpan.FromSeconds(15) ? request.Timeout : TimeSpan.FromSeconds(15);
            reserved = await pending.ReserveAsync(id, request.Revision, token, leaseTimeout);
        }
        catch (Exception error)
        {
            // Publication may have happened without returning a capability. Recover
            // only an actually readable record; never clear or dispatch a candidate.
            await RequireNoRecoveryPendingAsync(paths, request.Name!, CancellationToken.None);
            if (error is OperationCanceledException)
            { throw RecoveryNotSentCancellation(token); }
            throw new AgentCommandException("pending_storage_unavailable", "Recovery reservation could not be verified; no resume request was sent and no recovery ID is inferred.");
        }
        ICliTransport transport;
        try
        {
            if (reserved.Operation.RootId != rootId) { throw new ArgumentException("Recovery reservation and captured root differ."); }
            await diagnostic.WriteLineAsync($"reserved operationId: {id} (not admission proof; retain for original-ID recovery if dispatch becomes uncertain)");
            await diagnostic.FlushAsync(token);
            transport = connect();
        }
        catch (Exception error)
        {
            await ReleaseRecoveryPendingAsync(reserved, null);
            if (error is OperationCanceledException) { throw RecoveryNotSentCancellation(token); }
            if (error is AgentOutcomeUnknownException or AgentCommandException)
            { throw new AgentCommandException("connection_failed", "Recovery transport was not entered; no resume request was sent."); }
            throw;
        }
        AgentResponse response;
        try { response = await transport.SendAsync(request, token); }
        catch (Exception error)
        {
            // Only concrete AgentClient timeout/cancellation escapes before its
            // attemptedWrite guard. IO/argument/permission stay conservative here.
            // This catch never includes ACK parsing or an arbitrary factory call.
            if (transport is PipeTransport && error is not AgentOutcomeUnknownException && error is TimeoutException or OperationCanceledException)
            {
                await ReleaseRecoveryPendingAsync(reserved, null);
                if (error is OperationCanceledException) { throw RecoveryNotSentCancellation(token); }
                throw;
            }
            throw RecoveryUnknown(id, error);
        }
        try
        {
            // Validate only: preserve the existing CLI wire payload/unknown fields,
            // not a DTO projection or a fresh observation of current recovery intent.
            _ = AgentRecoveryClient.ReadAcknowledgement(response, reserved.Operation.TaskName, id, rootId);
        }
        catch (Exception error) { throw RecoveryUnknown(id, error); }
        if (response.Ok && !response.Replayed) { await ReleaseRecoveryPendingAsync(reserved, response); }
        else
        {
            // No self-specific not_executed exception; even fresh errors retain ID.
            try { await diagnostic.WriteLineAsync($"pending retained: {id} in {pending.FileName}; only inspect operation show {id}. History/current state does not unlock recovery; do not resubmit or replace the ID."); }
            catch (Exception error) { throw new AgentOutcomeUnknownException(id, error, response.Id); }
        }
        return response;
    }
    private static AgentCommandException RecoveryNotSentCancellation(CancellationToken token) => new(token.IsCancellationRequested ? "cancelled" : "timeout",
        "Recovery request ended before transport dispatch; no resume request was sent. No retry or replacement ID is inferred.");
    private static AgentOutcomeUnknownException RecoveryUnknown(string id, Exception error)
    {
        var requestId = error is AgentOutcomeUnknownException unknown && unknown.RequestId is { Length: > 0 and <= AgentProtocol.MaximumRequestIdCharacters }
            ? unknown.RequestId : null;
        return new(id, error, requestId);
    }
    private static async Task ReleaseRecoveryPendingAsync(RecoveryPendingStore.Reservation reserved, AgentResponse? acknowledgement)
    {
        try { await reserved.ReleaseAfterKnownOutcomeAsync(CancellationToken.None); }
        catch (Exception)
        {
            throw new AgentCommandException("pending_clear_unverified", acknowledgement is null
                ? "Recovery request was not dispatched, but pending clearance is unverified; do not retry or replace its original ID."
                : "A fresh recovery acknowledgement was received, but pending clearance is unverified; inspect the original receipt without resubmitting.",
                operationId: reserved.Operation.OperationId, requestId: acknowledgement?.Id);
        }
    }
}
