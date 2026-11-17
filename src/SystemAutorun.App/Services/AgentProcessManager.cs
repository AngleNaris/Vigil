using System.Diagnostics;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.App.Services;

internal sealed class AgentProcessManager : IAsyncDisposable
{
    private readonly AgentDaemonLauncher _launcher;

    internal AgentProcessManager(AgentClient client, ProjectPaths paths, Func<ProcessStartInfo, Process?>? processStarter = null) =>
        _launcher = new AgentDaemonLauncher(client, paths, processStarter);

    internal Task EnsureRunningAsync(CancellationToken cancellationToken = default) => _launcher.EnsureRunningAsync(cancellationToken);

    // Closing the UI never sends shutdown or kills the Agent/workload process tree.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
