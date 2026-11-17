using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Services;

public interface IHealthProbe
{
    Task<HealthResult> CheckAsync(
        AutorunTask task,
        bool quick,
        CancellationToken cancellationToken = default);

    // Passive observations must not activate a workload just to determine its health.
    Task<HealthResult> CheckPassiveAsync(AutorunTask task, CancellationToken cancellationToken = default) =>
        CheckAsync(task, quick: true, cancellationToken);
}

public interface ITaskExecutor
{
    Task<CommandResult> StartAsync(AutorunTask task, CancellationToken cancellationToken = default);

    Task<CommandResult> StopAsync(AutorunTask task, CancellationToken cancellationToken = default);

    IReadOnlyList<int> FindProcessIds(AutorunTask task);
}
