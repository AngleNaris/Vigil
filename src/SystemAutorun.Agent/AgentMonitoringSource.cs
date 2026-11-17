using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Agent;

internal interface IAgentMonitoringSource
{
    Task<IReadOnlyList<TaskSnapshot>> ObserveTasksAsync(AutorunConfiguration configuration, CancellationToken cancellationToken);
    Task<IReadOnlyList<ManagedProcessSnapshot>> ObserveProcessesAsync(AutorunConfiguration configuration,
        IReadOnlyList<TaskSnapshot> observations, CancellationToken cancellationToken);
    Task<HostStatusSnapshot> ObserveHostAsync(CancellationToken cancellationToken);
}

internal sealed class WindowsAgentMonitoringSource(TaskOrchestrator orchestrator, HostStatusReader host) : IAgentMonitoringSource
{
    public Task<IReadOnlyList<TaskSnapshot>> ObserveTasksAsync(AutorunConfiguration configuration, CancellationToken cancellationToken) =>
        orchestrator.ObserveTasksAsync(configuration, cancellationToken);

    public Task<IReadOnlyList<ManagedProcessSnapshot>> ObserveProcessesAsync(AutorunConfiguration configuration,
        IReadOnlyList<TaskSnapshot> observations, CancellationToken cancellationToken) =>
        orchestrator.ObserveProcessesAsync(configuration, observations, cancellationToken);

    public Task<HostStatusSnapshot> ObserveHostAsync(CancellationToken cancellationToken) => host.ReadAsync(cancellationToken);
}
