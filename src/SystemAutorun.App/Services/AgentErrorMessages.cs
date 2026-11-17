using SystemAutorun.Core.Ipc;

namespace SystemAutorun.App.Services;

internal static class AgentErrorMessages
{
    internal static string Format(Exception exception) => exception switch
    {
        AgentCommandException { Code: "revision_conflict" } =>
            "配置已被其他客户端修改，本次修改未覆盖。请重新读取最新配置，核对后再提交。",
        AgentOutcomeUnknownException unknown =>
            $"操作结果未知（{unknown.OperationId}）。请先查询操作结果并核对当前状态，不要直接重做。",
        AgentCommandException { Code: "outcome_unknown", OperationId: var id } =>
            $"操作结果未知（{id}）。请先查询操作结果并核对当前状态，不要直接重做。",
        _ => exception.Message,
    };
}
