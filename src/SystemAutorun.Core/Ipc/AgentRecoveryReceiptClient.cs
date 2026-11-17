using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Recovery;

namespace SystemAutorun.Core.Ipc;

// Query only: no pending-store ownership, business ACK, state application or unlock.
public sealed class AgentRecoveryReceiptClient(AgentClient client)
{
    public async Task<RecoveryReceiptObservation> ReadAsync(RecoveryPendingOperation pending, CancellationToken token = default)
    {
        if (!RecoveryPendingOperation.Valid(pending) || pending.SubjectKey != CurrentUserIdentity.Key)
        { throw new AgentCommandException("invalid_argument", "A valid original recovery pending identity is required."); }
        if (client.RootId != pending.RootId)
        { throw new AgentCommandException("protocol_mismatch", "Recovery observation belongs to another root."); }
        try
        {
            var response = await client.SendResponseAsync("operation", name: pending.OperationId,
                cancellationToken: token, requireData: true).ConfigureAwait(false);
            // Preserve the LIVE query root instead of losing it with the receipt.
            // AgentClient may have resolved an explicit/environment alias again.
            if (response.RootId != pending.RootId || response.OperationId is not null || CurrentUserIdentity.Key != pending.SubjectKey)
            { throw new AgentCommandException("protocol_mismatch", "Recovery query scope changed."); }
            var receipt = AgentOperationClient.ReadReceipt(response, pending.OperationId);
            if (receipt is null) { return new(RecoveryReceiptState.NotFound, pending.OperationId); }
            // The Journal fingerprints the EXACT sent name, not the case-folded pending
            // namespace. Reuse its algorithm; never invent a client-side approximation.
            var expected = OperationJournal.Fingerprint(new AgentRequest("", "recovery-resume", pending.TaskName,
                ExpectedRevision: pending.ExpectedRevision));
            if (receipt.OperationId != pending.OperationId || receipt.Command != "recovery-resume"
                || receipt.Name != pending.TaskName || receipt.Fingerprint != expected
                || receipt.Response?.RootId is { } historicalRoot && historicalRoot != pending.RootId)
            { throw new AgentCommandException("protocol_mismatch", "Journal observation does not match the original recovery intent."); }
            // Historical responses may omit rootId (the validated live query carries it).
            // Data remains opaque history; neither Ok nor current pause equality clears ID.
            return new(receipt.State switch
            {
                OperationState.Running => RecoveryReceiptState.Running,
                OperationState.Unknown => RecoveryReceiptState.Unknown,
                _ => receipt.ResultOmitted || receipt.Response is null ? RecoveryReceiptState.ResultUnavailable : RecoveryReceiptState.CompletedHistory,
            }, pending.OperationId);
        }
        catch (Exception error) when (error is not AgentCommandException
            && error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new AgentCommandException("protocol_mismatch", "Agent returned a malformed recovery operation observation."); }
    }
}
