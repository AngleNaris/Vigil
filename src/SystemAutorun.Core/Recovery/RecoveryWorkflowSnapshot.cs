namespace SystemAutorun.Core.Recovery;

// Captured facts, not an atomic configuration/recovery CAS, health or launch proof.
public sealed record RecoveryObservation(string RootId, string SubjectKey, string TaskName,
    string ConfigurationRevision, string RecoveryRevision, bool Initialized, bool Eligible,
    string? EligibilityReason, RecoveryTaskState? Entry, DateTimeOffset ObservedAt)
{
    public bool CanResume => Initialized && Eligible && Entry is { Paused: true };
}

// The unique preparation identity prevents an old, equal-looking confirmation from
// authorizing a later preparation. Pending/history can never reconstruct this record.
public sealed record RecoveryResumePreparation(string PreparationId, RecoveryObservation Observation, bool Confirmed = false);

public enum RecoveryWorkflowState { Uninitialized, Loading, Ready, AwaitingConfirmation, CheckingConfirmation, Reserving, Submitting,
    Finalizing, Completed, NotSent, ReadFailed, OutcomeUnknown, StorageBlocked }

// Current observation and historical receipt are deliberately separate. ACK Data is
// never put into Observation; even Completed says only that the request completed.
public sealed record RecoveryWorkflowSnapshot(RecoveryWorkflowState State,
    RecoveryObservation? Observation = null, RecoveryResumePreparation? Preparation = null,
    RecoveryPendingOperation? Pending = null, string? OperationId = null, string? RequestId = null,
    string? ErrorCode = null, string? ReadErrorCode = null, RecoveryReceiptObservation? Receipt = null,
    string? QueryErrorCode = null, bool Reading = false, bool Querying = false)
{
    public bool IsBusy => Reading || Querying || State is RecoveryWorkflowState.Loading or RecoveryWorkflowState.CheckingConfirmation or RecoveryWorkflowState.Reserving
        or RecoveryWorkflowState.Submitting or RecoveryWorkflowState.Finalizing;
    public bool IsLocked => State is RecoveryWorkflowState.OutcomeUnknown or RecoveryWorkflowState.StorageBlocked;
    public bool CanRead => !IsBusy && State is RecoveryWorkflowState.Ready or RecoveryWorkflowState.Completed
        or RecoveryWorkflowState.NotSent or RecoveryWorkflowState.ReadFailed or RecoveryWorkflowState.OutcomeUnknown or RecoveryWorkflowState.StorageBlocked;
    public bool CanPrepare => CanRead && !IsLocked && Observation?.CanResume == true;
    public bool CanConfirm => !IsBusy && State == RecoveryWorkflowState.AwaitingConfirmation;
    public bool CanQuery => !IsBusy && IsLocked && Pending is not null;
}
