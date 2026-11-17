using System.Collections.Concurrent;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Ipc;

// One live coordinator per physical root. No Dispatcher/window ownership: closing
// a view must not dispose/cancel an already confirmed workflow or unlock its ID.
public sealed class SelfStartupWorkflow
{
    private static readonly ConcurrentDictionary<string, SelfStartupWorkflow> Roots = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly SelfStartupPendingStore _pending;
    private readonly AgentSelfStartupClient _self;
    private readonly AgentOperationClient _operations;
    private Task<SelfStartupWorkflowSnapshot>? _opening;
    private SelfStartupWorkflowSnapshot _snapshot = new(SelfStartupWorkflowState.Uninitialized);

    private SelfStartupWorkflow(ProjectPaths paths)
    {
        var client = new AgentClient(paths.RootDirectory);
        _pending = new(paths); _self = new(client, paths.RootDirectory); _operations = new(client);
    }

    public static SelfStartupWorkflow ForRoot(ProjectPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var endpoint = AgentProtocol.ForRoot(ProjectPaths.Resolve(paths.RootDirectory).RootDirectory);
        return Roots.GetOrAdd(endpoint.RootId, _ => new(new ProjectPaths(endpoint.RootDirectory)));
    }

    public SelfStartupWorkflowSnapshot Current { get { lock (_gate) { return _snapshot; } } }

    // Shared root initialization has its own lifetime, not the first view's token.
    public async Task<SelfStartupWorkflowSnapshot> OpenAsync()
    {
        Task<SelfStartupWorkflowSnapshot> opening;
        lock (_gate) { opening = _opening ??= OpenCoreAsync(); }
        await opening.ConfigureAwait(false); return Current;
    }

    private async Task<SelfStartupWorkflowSnapshot> OpenCoreAsync()
    {
        Set(new(SelfStartupWorkflowState.Loading));
        try { return Set(FromPending(await _pending.ReadAsync().ConfigureAwait(false))); }
        catch (Exception) { return Set(new(SelfStartupWorkflowState.StorageBlocked, ErrorCode: "pending_storage_unavailable")); }
    }

    public async Task<SelfStartupWorkflowSnapshot> PrepareAsync(bool enabled, TimeSpan? timeout = null, CancellationToken token = default)
    {
        await OpenAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (!_snapshot.CanPrepare) { throw Busy(); }
            _snapshot = new(SelfStartupWorkflowState.Preparing);
        }
        try
        {
            // Another process may have reserved since this coordinator opened.
            var pending = await _pending.ReadAsync(token).ConfigureAwait(false);
            if (pending.BlocksSubmission) { return Set(FromPending(pending)); }
            var request = await _self.PrepareAsync(enabled, timeout, token).ConfigureAwait(false);
            return Set(new(SelfStartupWorkflowState.AwaitingConfirmation, Preparation: request));
        }
        catch (OperationCanceledException) { return Set(new(SelfStartupWorkflowState.PreparationFailed, ErrorCode: token.IsCancellationRequested ? "cancelled" : "timeout")); }
        catch (AgentCommandException error) { return Set(new(SelfStartupWorkflowState.PreparationFailed, ErrorCode: error.Code)); }
        catch (Exception) { return Set(new(SelfStartupWorkflowState.PreparationFailed, ErrorCode: "preparation_failed")); }
    }

    public SelfStartupWorkflowSnapshot DiscardPreparation()
    {
        lock (_gate)
        {
            if (!_snapshot.CanConfirm) { throw Busy(); }
            return _snapshot = new(SelfStartupWorkflowState.Ready);
        }
    }

    // A closing view can discard only its exact, still-unconfirmed preparation.
    // The check and transition share the gate; losing a race never touches a send.
    public bool TryDiscardPreparation(SelfStartupMutationRequest expected)
    {
        lock (_gate)
        {
            if (!_snapshot.CanConfirm || expected is not { Confirmed: false } || !ReferenceEquals(_snapshot.Preparation, expected)) { return false; }
            _snapshot = new(SelfStartupWorkflowState.Ready); return true;
        }
    }

    // The caller's explicit confirm action constructs Confirmed=true. Core never
    // promotes an unconfirmed preparation or reconstructs one from pending/history.
    public async Task<SelfStartupWorkflowSnapshot> SubmitAsync(SelfStartupMutationRequest confirmed,
        TimeSpan? timeout = null, CancellationToken token = default)
    {
        if (SelfStartupMutationPolicy.ValidateRequest(confirmed) is { } validation)
        { throw new AgentCommandException(validation, "Explicit confirmation of the current preparation is required."); }
        token.ThrowIfCancellationRequested();
        string id;
        lock (_gate)
        {
            if (!_snapshot.CanConfirm) { throw Busy(); }
            if (confirmed with { Confirmed = false } != _snapshot.Preparation)
            { throw new AgentCommandException("invalid_argument", "Confirmation does not match this workflow's preparation."); }
            id = AgentProtocol.NewOperationId();
            _snapshot = new(SelfStartupWorkflowState.Reserving, OperationId: id);
        }
        SelfStartupPendingStore.Reservation reserved;
        try { reserved = await _pending.ReserveAsync(id, confirmed.Enabled, token).ConfigureAwait(false); }
        catch (Exception error)
        {
            // Do not send, invent recovery ownership or clean after an ambiguous
            // publication. Recover only the actually readable stored original ID.
            try
            {
                var pending = await _pending.ReadAsync().ConfigureAwait(false);
                // Cancellation before publication is not a storage fault. No client
                // was called and the strict same-root read proves no hint remains.
                // Published/unreadable records stay locked; IO failures stay blocked.
                if (error is OperationCanceledException && pending.State == SelfStartupPendingState.Missing)
                { return Set(new(SelfStartupWorkflowState.NotSent, OperationId: id, ErrorCode: "cancelled")); }
                return Set(pending.State == SelfStartupPendingState.Present ? FromPending(pending)
                    : new(SelfStartupWorkflowState.StorageBlocked, ErrorCode: pending.Reason ?? "pending_storage_unavailable"));
            }
            catch (Exception) { return Set(new(SelfStartupWorkflowState.StorageBlocked, ErrorCode: "pending_storage_unavailable")); }
        }
        Set(new(SelfStartupWorkflowState.Submitting, OperationId: id));
        AgentResponse response;
        try { response = await _self.SubmitAsync(confirmed, id, timeout, token).ConfigureAwait(false); }
        catch (AgentOutcomeUnknownException error)
        { return Set(new(SelfStartupWorkflowState.OutcomeUnknown, OperationId: id, ErrorCode: "outcome_unknown", RequestId: error.RequestId)); }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or ArgumentException)
        {
            // AgentClient wraps these types only AFTER attemptedWrite; an unwrapped
            // instance from this concrete client is proven pre-send. Never generalize
            // this classification to injected transports or arbitrary exceptions.
            var code = error is OperationCanceledException ? token.IsCancellationRequested ? "cancelled" : "timeout" : error is TimeoutException ? "timeout" : "not_sent";
            return await FinishKnownAsync(reserved, SelfStartupWorkflowState.NotSent, code, null).ConfigureAwait(false);
        }
        catch (Exception)
        { return Set(new(SelfStartupWorkflowState.OutcomeUnknown, OperationId: id, ErrorCode: "outcome_unknown")); }
        // A replay is historical even when Ok=true. Only this fresh success or the
        // self Journal's explicit no-call refusal is sufficient to clear our hint.
        if (response.Replayed || !response.Ok && response.ErrorCode != "not_executed")
        { return Set(new(SelfStartupWorkflowState.OutcomeUnknown, OperationId: id, ErrorCode: response.ErrorCode ?? "replayed_result", RequestId: response.Id)); }
        return await FinishKnownAsync(reserved, response.Ok ? SelfStartupWorkflowState.Completed : SelfStartupWorkflowState.Refused,
            response.ErrorCode, response.Id).ConfigureAwait(false);
    }

    private async Task<SelfStartupWorkflowSnapshot> FinishKnownAsync(SelfStartupPendingStore.Reservation reserved,
        SelfStartupWorkflowState state, string? errorCode, string? requestId)
    {
        var id = reserved.Operation.OperationId;
        Set(new(SelfStartupWorkflowState.Finalizing, OperationId: id, ErrorCode: errorCode, RequestId: requestId));
        try
        {
            // A closed/cancelled view must not interrupt this bounded root cleanup.
            await reserved.ReleaseAfterKnownOutcomeAsync(CancellationToken.None).ConfigureAwait(false);
            return Set(new(state, OperationId: id, ErrorCode: errorCode, RequestId: requestId));
        }
        catch (Exception)
        { return Set(new(SelfStartupWorkflowState.StorageBlocked, OperationId: id, ErrorCode: "pending_clear_unverified", RequestId: requestId)); }
    }

    public async Task<SelfStartupWorkflowSnapshot> QueryAsync(CancellationToken token = default)
    {
        string id;
        lock (_gate)
        {
            if (!_snapshot.CanQuery) { throw Busy(); }
            id = _snapshot.OperationId!; _snapshot = _snapshot with { Querying = true };
        }
        SelfStartupReceiptState observed;
        try
        {
            var receipt = await _operations.ReadAsync(id, token).ConfigureAwait(false);
            if (receipt is not null && (receipt.Command != "startup-self-set" || receipt.Name is not null))
            { throw new AgentCommandException("protocol_mismatch", "Not a self operation observation."); }
            observed = receipt is null ? SelfStartupReceiptState.NotFound : receipt.State switch
            {
                OperationState.Running => SelfStartupReceiptState.Running,
                OperationState.Unknown => SelfStartupReceiptState.Unknown,
                _ => receipt.ResultOmitted || receipt.Response is null ? SelfStartupReceiptState.ResultUnavailable : SelfStartupReceiptState.CompletedWithResponse
            };
        }
        catch (Exception) { observed = SelfStartupReceiptState.QueryFailed; }
        lock (_gate)
        {
            // Observation is NOT an ACK, current registration or unlock permission.
            // Never read/clear pending, prepare, change the original ID or submit here.
            return _snapshot = _snapshot with { Querying = false, Observation = new(observed) };
        }
    }

    private static SelfStartupWorkflowSnapshot FromPending(SelfStartupPendingSnapshot pending) => pending.State switch
    {
        SelfStartupPendingState.Missing => new(SelfStartupWorkflowState.Ready),
        SelfStartupPendingState.Present => new(SelfStartupWorkflowState.OutcomeUnknown, OperationId: pending.Operation!.OperationId, ErrorCode: "outcome_unknown"),
        _ => new(SelfStartupWorkflowState.StorageBlocked, ErrorCode: pending.Reason ?? "pending_storage_unavailable")
    };
    private SelfStartupWorkflowSnapshot Set(SelfStartupWorkflowSnapshot snapshot)
    { lock (_gate) { return _snapshot = snapshot; } }
    private static AgentCommandException Busy() => new("self_busy", "Self workflow is busy, awaiting confirmation or locked for observation.");
}
