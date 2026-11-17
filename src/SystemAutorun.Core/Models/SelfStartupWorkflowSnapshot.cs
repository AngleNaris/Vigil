namespace SystemAutorun.Core.Models;

public enum SelfStartupWorkflowState
{
    Uninitialized, Loading, Ready, Preparing, AwaitingConfirmation, Reserving,
    Submitting, Finalizing, Completed, Refused, NotSent, PreparationFailed,
    OutcomeUnknown, StorageBlocked
}

public enum SelfStartupReceiptState { NotFound, Running, Unknown, CompletedWithResponse, ResultUnavailable, QueryFailed }
public sealed record SelfStartupReceiptObservation(SelfStartupReceiptState State);

// No response Data/registration bool: history is never applied to current UI state.
public sealed record SelfStartupWorkflowSnapshot(SelfStartupWorkflowState State,
    SelfStartupMutationRequest? Preparation = null, string? OperationId = null,
    string? ErrorCode = null, string? RequestId = null,
    SelfStartupReceiptObservation? Observation = null, bool Querying = false)
{
    public bool IsBusy => Querying || State is SelfStartupWorkflowState.Loading or SelfStartupWorkflowState.Preparing
        or SelfStartupWorkflowState.Reserving or SelfStartupWorkflowState.Submitting or SelfStartupWorkflowState.Finalizing;
    public bool IsLocked => State is SelfStartupWorkflowState.OutcomeUnknown or SelfStartupWorkflowState.StorageBlocked;
    public bool CanPrepare => !IsBusy && State is SelfStartupWorkflowState.Ready or SelfStartupWorkflowState.Completed
        or SelfStartupWorkflowState.Refused or SelfStartupWorkflowState.NotSent or SelfStartupWorkflowState.PreparationFailed;
    public bool CanConfirm => State == SelfStartupWorkflowState.AwaitingConfirmation;
    public bool CanQuery => IsLocked && !IsBusy && OperationId is not null;
}
