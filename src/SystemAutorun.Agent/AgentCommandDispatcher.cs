using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Agent;

internal sealed class AgentCommandDispatcher
{
    private readonly TaskOrchestrator _orchestrator;

    internal AgentCommandDispatcher(TaskOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    internal async Task<AgentResponse> DispatchAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            object data = request.Command.ToLowerInvariant() switch
            {
                "ping" => new { status = "ready", pid = Environment.ProcessId },
                "list" => await _orchestrator.ListAsync(cancellationToken).ConfigureAwait(false),
                "status" => await _orchestrator.StatusAsync(RequiredName(request), cancellationToken)
                    .ConfigureAwait(false),
                "start" => await _orchestrator.StartAsync(RequiredName(request), cancellationToken)
                    .ConfigureAwait(false),
                "stop" => await _orchestrator.StopAsync(RequiredName(request), cancellationToken)
                    .ConfigureAwait(false),
                "restart" => await _orchestrator.RestartAsync(RequiredName(request), cancellationToken)
                    .ConfigureAwait(false),
                "enable" => await _orchestrator.SetEnabledAsync(RequiredName(request), true, cancellationToken)
                    .ConfigureAwait(false),
                "disable" => await _orchestrator.SetEnabledAsync(RequiredName(request), false, cancellationToken)
                    .ConfigureAwait(false),
                "start-missing" => await _orchestrator.StartScheduledAsync(false, cancellationToken)
                    .ConfigureAwait(false),
                "doctor" => await _orchestrator.DoctorAsync(cancellationToken).ConfigureAwait(false),
                "reload" => await _orchestrator.ListAsync(cancellationToken).ConfigureAwait(false),
                "shutdown" => new { stopped = true },
                _ => throw new InvalidOperationException($"Unknown command: {request.Command}"),
            };
            return new AgentResponse(request.Id, true, data);
        }
        catch (Exception exception)
        {
            return new AgentResponse(request.Id, false, Error: exception.Message);
        }
    }

    private static string RequiredName(AgentRequest request) =>
        string.IsNullOrWhiteSpace(request.Name)
            ? throw new InvalidOperationException($"Command '{request.Command}' requires a task name.")
            : request.Name;
}
