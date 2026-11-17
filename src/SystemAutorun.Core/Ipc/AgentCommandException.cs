namespace SystemAutorun.Core.Ipc;

public sealed class AgentCommandException(string code, string message, string? revision = null, string? operationId = null, string? requestId = null)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public string? Revision { get; } = revision;
    public string? OperationId { get; } = operationId;
    public string? RequestId { get; } = requestId;
}
