using SystemAutorun.Core.Ipc;
using SystemAutorun.Agent;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Monitoring;
using System.Text.Json;
using SystemAutorun.Windows.Execution;
using SystemAutorun.Windows.Health;

AgentArguments arguments;
ProjectPaths paths;
AgentEndpoint endpoint;
try
{
    arguments = AgentArguments.Parse(args);
    paths = ProjectPaths.Resolve(arguments.RootDirectory);
    endpoint = AgentProtocol.ForRoot(paths.RootDirectory);
}
catch (Exception exception)
{
    await AgentFallbackLog.WriteAsync(exception.ToString());
    return 2;
}

var fileLog = new FileLog();
try
{
    using var mutex = new Mutex(true, endpoint.MutexName, out var createdNew);
    if (!createdNew)
    {
        return 0;
    }

    using var instanceLease = RootInstanceLease.TryAcquire(paths);
    if (instanceLease is null)
    {
        // Do not start another controller for an unresolved physical alias.
        return 3;
    }

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
    var workspace = new ConfigurationWorkspace(paths);
    var operations = new OperationJournal(paths);
    var tasks = new AgentTaskController(workspace, orchestrator);
    try { await tasks.InitializeRuntimeAsync(); }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
    {
        // Broken intent state disables recovery/control, not passive observation/readiness.
        await fileLog.WriteAsync(configuration.Settings.LogDirectory, "agent", $"Recovery state unavailable: {exception.Message}");
    }
    using var host = new HostStatusReader();
    using var monitoringHost = new HostStatusReader();
    MonitoringHistoryStore? history = null;
    string? historyError = null;
    try { history = new MonitoringHistoryStore(paths); }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or OverflowException)
    {
        // History failure must not take the command plane or live observation source down.
        historyError = $"{exception.GetType().Name}: {exception.Message}";
    }
    using var ownedHistory = history;
    var sshRemoteHostReader = new SshRemoteHostReader();
    using var remoteMonitor = new AgentRemoteHostMonitor(workspace, sshRemoteHostReader, operations.InstanceId);
    using var monitor = new AgentMonitor(workspace, new WindowsAgentMonitoringSource(orchestrator, monitoringHost),
        operations.InstanceId, history, historyError, remoteMonitor: remoteMonitor);
    var recovery = new AgentRecoveryWorker(tasks, monitor.RecoveryEvidenceCurrent);
    monitor.SamplePublished += recovery.Enqueue;
    var dispatcher = new AgentCommandDispatcher(orchestrator, host, workspace, monitor, operations, tasks,
        remoteReader: sshRemoteHostReader, remoteMonitor: remoteMonitor);
    var server = new AgentServer(dispatcher, shutdown, paths.RootDirectory);
    await fileLog.WriteAsync(
        configuration.Settings.LogDirectory,
        "agent",
        $"Agent ready. PID={Environment.ProcessId}; Pipe={endpoint.PipeName}");
    var monitoringTask = Task.Run(() => monitor.RunAsync(shutdown.Token));
    var remoteMonitoringTask = Task.Run(() => remoteMonitor.RunAsync(shutdown.Token));
    var recoveryTask = Task.Run(() => recovery.RunAsync(shutdown.Token));
    try { await server.RunAsync(shutdown.Token); }
    finally
    {
        shutdown.Cancel();
        try { await Task.WhenAll(monitoringTask, remoteMonitoringTask, recoveryTask); }
        finally { await tasks.DrainAsync(); } // Even a worker fault must not skip accepted control intents.
    }
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
