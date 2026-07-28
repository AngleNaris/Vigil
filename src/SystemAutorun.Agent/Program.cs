using System.Security.Principal;
using SystemAutorun.Agent;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Windows.Execution;
using SystemAutorun.Windows.Health;

var arguments = AgentArguments.Parse(args);
ProjectPaths paths;
try
{
    paths = ProjectPaths.Resolve(arguments.RootDirectory);
}
catch (Exception exception)
{
    await AgentFallbackLog.WriteAsync(exception.ToString());
    return 2;
}

var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
using var mutex = new Mutex(true, $"Local\\SystemAutorun.Agent.{sid}", out var createdNew);
if (!createdNew)
{
    return 0;
}

var fileLog = new FileLog();
try
{
    var configurationCache = new ConfigurationCache(new ConfigurationLoader(), paths);
    var configuration = await configurationCache.GetAsync();
    using var executor = new WindowsTaskExecutor(fileLog, configuration.Settings.LogDirectory);
    var healthProbe = new WindowsHealthProbe();
    var orchestrator = new TaskOrchestrator(
        configurationCache,
        new OverrideStore(),
        executor,
        healthProbe,
        fileLog);

    if (arguments.BootMode)
    {
        var results = await orchestrator.StartScheduledAsync(
            runAsSystem: true,
            CancellationToken.None);
        return results.All(result => result.Succeeded) ? 0 : 1;
    }

    using var shutdown = new CancellationTokenSource();
    var dispatcher = new AgentCommandDispatcher(orchestrator);
    var server = new AgentServer(dispatcher, shutdown);
    await fileLog.WriteAsync(
        configuration.Settings.LogDirectory,
        "agent",
        $"Agent ready. PID={Environment.ProcessId}; Pipe={SystemAutorun.Core.Ipc.AgentProtocol.PipeName}");
    await server.RunAsync(shutdown.Token);
    await fileLog.WriteAsync(
        configuration.Settings.LogDirectory,
        "agent",
        "Agent stopped.");
    return 0;
}
catch (Exception exception)
{
    await fileLog.WriteAsync(paths.RootDirectory, "agent-fatal", exception.ToString());
    return 1;
}
