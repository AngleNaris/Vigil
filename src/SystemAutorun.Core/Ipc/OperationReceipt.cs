namespace SystemAutorun.Core.Ipc;

public enum OperationState { Running, Completed, Unknown }

public sealed record OperationReceipt(
    int SchemaVersion,
    string OperationId,
    string Command,
    string? Name,
    string Fingerprint,
    string OwnerInstanceId,
    OperationState State,
    DateTimeOffset AcceptedAt,
    DateTimeOffset? CompletedAt = null,
    AgentResponse? Response = null,
    bool ResultOmitted = false)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record OperationJournalLimits(
    int MaximumEntries = 4096,
    long MaximumBytes = 16 * 1024 * 1024,
    int MaximumReceiptBytes = 256 * 1024,
    int MaximumResultBytes = 128 * 1024);

public sealed class AgentOutcomeUnknownException(string operationId, Exception innerException, string? requestId = null)
    : IOException($"Operation outcome is unknown. Query operation {operationId}; do not retry with a new ID.", innerException)
{
    public string OperationId { get; } = operationId;
    public string? RequestId { get; } = requestId;
}
