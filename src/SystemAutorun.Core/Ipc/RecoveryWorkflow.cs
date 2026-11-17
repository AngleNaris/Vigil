using System.Collections.Concurrent;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Recovery;

namespace SystemAutorun.Core.Ipc;

// A process-owned coordinator per physical root/subject/name. Views own only their
// unconfirmed read/preparation, never a confirmed send or its finalization lifetime.
public sealed class RecoveryWorkflow
{
    private static readonly ConcurrentDictionary<(string Root, string Subject, string Name), RecoveryWorkflow> Instances = new();
    private readonly object _gate = new();
    private readonly AgentClient _client;
    private readonly RecoveryPendingStore _pending;
    private readonly AgentRecoveryObservationClient _observations;
    private Task<RecoveryWorkflowSnapshot>? _opening;
    private RecoveryWorkflowSnapshot _snapshot = new(RecoveryWorkflowState.Uninitialized);
    private long _readGeneration;
    private CancellationToken _readOwner;
    private RecoveryWorkflowState _readReturnState;
    public string RootDirectory { get; }
    public string RootId { get; }
    public string SubjectKey { get; }
    public string TaskName { get; }

    private RecoveryWorkflow(ProjectPaths paths, string taskName)
    {
        RootDirectory = paths.RootDirectory; RootId = AgentProtocol.ForRoot(RootDirectory).RootId;
        SubjectKey = CurrentUserIdentity.Key; TaskName = taskName; _client = new(RootDirectory);
        _pending = new(paths, taskName); _observations = new(_client, paths, taskName);
    }
    public static RecoveryWorkflow ForTask(ProjectPaths paths, string taskName)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!RecoveryPendingOperation.Name(taskName)) { throw new ArgumentException("A valid task name is required.", nameof(taskName)); }
        var physical = ProjectPaths.Resolve(paths.RootDirectory); var rootId = AgentProtocol.ForRoot(physical.RootDirectory).RootId;
        return Instances.GetOrAdd((rootId, CurrentUserIdentity.Key, taskName.ToUpperInvariant()), _ => new(physical, taskName));
    }
    public RecoveryWorkflowSnapshot Current { get { lock (_gate) { return _snapshot; } } }

    // Initial disk recovery has its own lifetime, not the first view's cancellation.
    public async Task<RecoveryWorkflowSnapshot> OpenAsync()
    {
        Task<RecoveryWorkflowSnapshot> opening; lock (_gate) { opening = _opening ??= OpenCoreAsync(); }
        await opening.ConfigureAwait(false); return Current;
    }
    private async Task<RecoveryWorkflowSnapshot> OpenCoreAsync()
    {
        Set(new(RecoveryWorkflowState.Loading));
        try { RequireScope(); return Set(FromPending(await _pending.ReadAsync().ConfigureAwait(false))); }
        catch (Exception) { return Set(new(RecoveryWorkflowState.StorageBlocked, ErrorCode: "pending_storage_unavailable")); }
    }

    public Task<RecoveryWorkflowSnapshot> RefreshAsync(TimeSpan? timeout = null, CancellationToken token = default) => ReadCoreAsync(false, timeout, token);
    // Always reads eligibility + recovery again. An old visible observation never
    // becomes confirmation or reserves an ID just because a view opened.
    public Task<RecoveryWorkflowSnapshot> PrepareAsync(TimeSpan? timeout = null, CancellationToken token = default) => ReadCoreAsync(true, timeout, token);
    private async Task<RecoveryWorkflowSnapshot> ReadCoreAsync(bool prepare, TimeSpan? timeout, CancellationToken token)
    {
        await OpenAsync().ConfigureAwait(false); token.ThrowIfCancellationRequested();
        long generation; RecoveryWorkflowSnapshot prior;
        lock (_gate)
        {
            if (!_snapshot.CanRead || prepare && _snapshot.IsLocked) { throw Busy(); }
            prior = _snapshot; generation = ++_readGeneration; _readOwner = token;
            _readReturnState = prior.State is RecoveryWorkflowState.Completed or RecoveryWorkflowState.NotSent ? prior.State : RecoveryWorkflowState.Ready;
            _snapshot = _snapshot with { Reading = true, Observation = null, Preparation = null, ReadErrorCode = null };
        }
        try
        {
            RequireScope(); var pending = await _pending.ReadAsync(token).ConfigureAwait(false);
            if (prior.IsLocked && prior.Pending is not null)
            {
                // Losing/changing a hint cannot unlock an operation already known to
                // this process, nor redirect its query to somebody else's new ID.
                if (pending.State != RecoveryPendingState.Present || pending.Operation != prior.Pending)
                { return ApplyRead(generation, prior with { State = RecoveryWorkflowState.StorageBlocked, Observation = null, ErrorCode = "pending_changed" }); }
            }
            else if (pending.BlocksSubmission)
            { return ApplyRead(generation, FromPending(pending)); }
            var observed = await _observations.ReadAsync(timeout, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var preparation = prepare && observed.CanResume ? new RecoveryResumePreparation(Guid.NewGuid().ToString("N"), observed) : null;
            var keepOutcome = prior.IsLocked || !prepare && prior.State is RecoveryWorkflowState.Completed or RecoveryWorkflowState.NotSent;
            return ApplyRead(generation, prior with { State = keepOutcome ? prior.State : preparation is null ? RecoveryWorkflowState.Ready : RecoveryWorkflowState.AwaitingConfirmation,
                Observation = observed, Preparation = preparation, ReadErrorCode = null, ErrorCode = keepOutcome ? prior.ErrorCode : null }, token);
        }
        catch (Exception error)
        {
            var code = ErrorCode(error, token);
            var keepOutcome = prior.IsLocked || prior.State is RecoveryWorkflowState.Completed or RecoveryWorkflowState.NotSent;
            return ApplyRead(generation, prior with { State = keepOutcome ? prior.State : RecoveryWorkflowState.ReadFailed,
                Observation = null, Preparation = null, ReadErrorCode = code, ErrorCode = keepOutcome ? prior.ErrorCode : code });
        }
    }

    // A view supplies its own cancellable read token. Another view/default token
    // cannot discard this read or a late-created preparation. This does NOT claim
    // the read's resources exited. Known completion/unknown IDs remain unchanged.
    public bool DismissUnconfirmed(CancellationToken owner)
    {
        lock (_gate)
        {
            if (!owner.CanBeCanceled || owner != _readOwner || (!_snapshot.Reading && !_snapshot.CanConfirm)) { return false; }
            ++_readGeneration; _readOwner = default;
            _snapshot = _snapshot with { State = _snapshot.CanConfirm ? _readReturnState : _snapshot.State,
                Reading = false, Preparation = null, ReadErrorCode = null };
            return true;
        }
    }
    // A delayed cleanup may discard only its exact still-unconfirmed preparation,
    // never a later view's preparation or a send that won the gate race.
    public bool TryDiscardPreparation(RecoveryResumePreparation expected)
    {
        lock (_gate)
        {
            if (!_snapshot.CanConfirm || !ReferenceEquals(_snapshot.Preparation, expected) || expected.Confirmed) { return false; }
            ++_readGeneration; _readOwner = default; _snapshot = _snapshot with { State = _readReturnState, Preparation = null }; return true;
        }
    }

    // No view cancellation parameter: once explicitly confirmed/claimed, the owned
    // operation outlives closing/cancelling an awaiter. Transport still has a timeout.
    public async Task<RecoveryWorkflowSnapshot> SubmitAsync(RecoveryResumePreparation confirmed, TimeSpan? timeout = null)
    {
        if (confirmed is not { Confirmed: true, Observation: { CanResume: true } })
        { throw new AgentCommandException("authorization_required", "Explicit confirmation of an eligible current preparation is required."); }
        if (timeout is { } limit && (limit <= TimeSpan.Zero || limit.TotalMilliseconds > AgentProtocol.MaximumRequestTimeoutMilliseconds))
        { throw new ArgumentOutOfRangeException(nameof(timeout)); }
        string id;
        lock (_gate)
        {
            if (!_snapshot.CanConfirm) { throw Busy(); }
            if (confirmed with { Confirmed = false } != _snapshot.Preparation)
            { throw new AgentCommandException("invalid_argument", "Confirmation does not match this workflow's preparation."); }
            RequireScope(); ++_readGeneration; _readOwner = default;
            _snapshot = new(RecoveryWorkflowState.CheckingConfirmation);
        }
        try
        {
            // Re-observe before minting/reserving. Changed recovery intent or lost
            // eligibility requires a NEW explicit preparation/confirmation, no send.
            // Configuration revision stays diagnostic, not a target/config CAS:
            // unrelated/target edits with the same name + admission remain valid.
            var current = await _observations.ReadAsync(timeout).ConfigureAwait(false);
            if (!current.CanResume || current.RecoveryRevision != confirmed.Observation.RecoveryRevision)
            { return Set(new(RecoveryWorkflowState.Ready, Observation: current, ErrorCode: "preparation_changed")); }
            RequireScope(); id = AgentProtocol.NewOperationId(); Set(new(RecoveryWorkflowState.Reserving, OperationId: id));
        }
        catch (Exception error)
        { return Set(new(RecoveryWorkflowState.ReadFailed, ErrorCode: ErrorCode(error), ReadErrorCode: ErrorCode(error))); }
        RecoveryPendingStore.Reservation reserved;
        try { reserved = await _pending.ReserveAsync(id, confirmed.Observation.RecoveryRevision).ConfigureAwait(false); }
        catch (Exception)
        {
            // No client was called. An ambiguous publication is not a release
            // capability: recover only a readable original record, never invent one.
            try
            {
                var disk = await _pending.ReadAsync().ConfigureAwait(false);
                return Set(disk.State == RecoveryPendingState.Present ? FromPending(disk)
                    : new(RecoveryWorkflowState.StorageBlocked, ErrorCode: disk.Reason ?? "pending_storage_unavailable"));
            }
            catch (Exception) { return Set(new(RecoveryWorkflowState.StorageBlocked, ErrorCode: "pending_storage_unavailable")); }
        }
        var pending = reserved.Operation; Set(new(RecoveryWorkflowState.Submitting, Pending: pending, OperationId: id));
        AgentResponse response;
        try
        {
            RequireScope(); response = await new AgentRecoveryClient(_client).SubmitResumeAsync(pending.TaskName,
                pending.ExpectedRevision, pending.OperationId, true, timeout).ConfigureAwait(false);
        }
        catch (AgentOutcomeUnknownException error)
        { return Set(new(RecoveryWorkflowState.OutcomeUnknown, Pending: pending, OperationId: id, ErrorCode: "outcome_unknown", RequestId: error.RequestId)); }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or ArgumentException)
        {
            // Only this concrete AgentClient: attempted-write errors are wrapped as
            // Unknown. An unwrapped instance here is proven pre-send, not a generic
            // classifier for arbitrary injected transports or server business codes.
            return await FinishKnownAsync(reserved, RecoveryWorkflowState.NotSent, ErrorCode(error), null, timeout).ConfigureAwait(false);
        }
        catch (Exception)
        { return Set(new(RecoveryWorkflowState.OutcomeUnknown, Pending: pending, OperationId: id, ErrorCode: "outcome_unknown")); }
        // Recovery has no self-specific no-call refusal marker. Fresh failures stay
        // conservatively locked; do not copy Self's not_executed special-case here.
        if (response.Replayed || !response.Ok)
        { return Set(new(RecoveryWorkflowState.OutcomeUnknown, Pending: pending, OperationId: id, RequestId: response.Id, ErrorCode: response.ErrorCode ?? "replayed_result")); }
        return await FinishKnownAsync(reserved, RecoveryWorkflowState.Completed, null, response.Id, timeout).ConfigureAwait(false);
    }

    private async Task<RecoveryWorkflowSnapshot> FinishKnownAsync(RecoveryPendingStore.Reservation reserved,
        RecoveryWorkflowState state, string? errorCode, string? requestId, TimeSpan? timeout)
    {
        var pending = reserved.Operation; Set(new(RecoveryWorkflowState.Finalizing, Pending: pending, OperationId: pending.OperationId, ErrorCode: errorCode, RequestId: requestId));
        try { await reserved.ReleaseAfterKnownOutcomeAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { return Set(new(RecoveryWorkflowState.StorageBlocked, Pending: pending, OperationId: pending.OperationId, ErrorCode: "pending_clear_unverified", RequestId: requestId)); }
        // Never project ACK Data. A newer Stop can already exist: only these new
        // reads may supply Observation, and a read failure cannot erase known ACK.
        RecoveryObservation? observed = null; string? readError = null;
        if (state == RecoveryWorkflowState.Completed)
        {
            try { observed = await _observations.ReadAsync(timeout).ConfigureAwait(false); }
            catch (Exception error) { readError = ErrorCode(error); }
        }
        return Set(new(state, OperationId: pending.OperationId, ErrorCode: errorCode, RequestId: requestId, Observation: observed, ReadErrorCode: readError));
    }

    public async Task<RecoveryWorkflowSnapshot> QueryAsync(CancellationToken token = default)
    {
        await OpenAsync().ConfigureAwait(false); RecoveryPendingOperation pending;
        lock (_gate)
        {
            if (!_snapshot.CanQuery) { throw Busy(); }
            pending = _snapshot.Pending!; _snapshot = _snapshot with { Querying = true, Receipt = null, QueryErrorCode = null };
        }
        RecoveryReceiptObservation? observed = null; string? errorCode = null;
        try { RequireScope(); observed = await new AgentRecoveryReceiptClient(_client).ReadAsync(pending, token).ConfigureAwait(false); }
        catch (Exception error) { errorCode = ErrorCode(error, token); }
        lock (_gate)
        {
            // Query does not read/clear/replace pending, create confirmation, apply
            // historical Data or unlock even CompletedHistory/NotFound.
            return _snapshot = _snapshot with { Querying = false, Receipt = observed, QueryErrorCode = errorCode };
        }
    }

    private RecoveryWorkflowSnapshot ApplyRead(long generation, RecoveryWorkflowSnapshot result, CancellationToken token = default)
    {
        lock (_gate)
        {
            if (generation != _readGeneration) { return _snapshot; }
            // Called only inside ReadCoreAsync's try/catch. Cancellation here flows
            // to its catch and the token-free ApplyRead there always resets Reading.
            token.ThrowIfCancellationRequested(); if (!result.CanConfirm) { _readOwner = default; }
            return _snapshot = result with { Reading = false };
        }
    }
    private void RequireScope()
    { if (CurrentUserIdentity.Key != SubjectKey || _client.RootId != RootId) { throw new AgentCommandException("protocol_mismatch", "Recovery workflow scope changed."); } }
    private static RecoveryWorkflowSnapshot FromPending(RecoveryPendingSnapshot pending) => pending.State switch
    {
        RecoveryPendingState.Missing => new(RecoveryWorkflowState.Ready),
        RecoveryPendingState.Present => new(RecoveryWorkflowState.OutcomeUnknown, Pending: pending.Operation, OperationId: pending.Operation!.OperationId, ErrorCode: "outcome_unknown"),
        _ => new(RecoveryWorkflowState.StorageBlocked, ErrorCode: pending.Reason ?? "pending_storage_unavailable")
    };
    private RecoveryWorkflowSnapshot Set(RecoveryWorkflowSnapshot snapshot) { lock (_gate) { return _snapshot = snapshot; } }
    private static AgentCommandException Busy() => new("recovery_busy", "Recovery workflow is busy, awaiting confirmation or locked for observation.");
    private static string ErrorCode(Exception error, CancellationToken token = default) => error switch
    { AgentCommandException command => command.Code, OperationCanceledException => token.IsCancellationRequested ? "cancelled" : "timeout", TimeoutException => "timeout", _ => "read_failed" };
}
