using System.Collections.Concurrent;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;
using SystemAutorun.Windows.Execution;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Agent;

internal sealed class TaskOrchestrator
{
    private readonly ConfigurationCache _configurationCache;
    private readonly OverrideStore _overrideStore;
    private readonly ITaskExecutor _executor;
    private readonly IHealthProbe _healthProbe;
    private readonly FileLog _log;
    private readonly ManagedProcessReader _managedProcesses;
    private readonly ManagedProcessReader _observationProcesses = new();
    private readonly ExceptionLogReader _exceptionLogs;
    private readonly WindowsServiceController _windowsServices = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _taskLocks =
        new(StringComparer.OrdinalIgnoreCase);

    internal TaskOrchestrator(
        ConfigurationCache configurationCache,
        OverrideStore overrideStore,
        ITaskExecutor executor,
        IHealthProbe healthProbe,
        FileLog log,
        ManagedProcessReader? managedProcesses = null,
        ExceptionLogReader? exceptionLogs = null)
    {
        _configurationCache = configurationCache;
        _overrideStore = overrideStore;
        _executor = executor;
        _healthProbe = healthProbe;
        _log = log;
        _managedProcesses = managedProcesses ?? new ManagedProcessReader();
        _exceptionLogs = exceptionLogs ?? new ExceptionLogReader();
    }

    internal async Task<IReadOnlyList<TaskSnapshot>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var configuration = await _configurationCache.GetAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return await ObserveTasksAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<TaskSnapshot>> ObserveTasksAsync(
        AutorunConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var probes = configuration.Tasks.Select(async task =>
        {
            if (!task.Enabled)
            {
                return TaskSnapshot.From(task, HealthResult.Fail("disabled", "工作负载已禁用。"));
            }

            var health = await _healthProbe.CheckPassiveAsync(task, cancellationToken)
                .ConfigureAwait(false);
            return TaskSnapshot.From(task, health);
        });
        return await Task.WhenAll(probes).ConfigureAwait(false);
    }

    internal async Task<TaskStatusDetail> StatusAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var configuration = await _configurationCache.GetAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var task = configuration.GetTask(name);
        var health = await _healthProbe.CheckPassiveAsync(task, cancellationToken)
            .ConfigureAwait(false);
        var processIds = task.Type is AutorunTaskType.Windows or AutorunTaskType.WindowsService
            || (health.Healthy && (task.Type is AutorunTaskType.WslBoot or AutorunTaskType.WslSystemd or AutorunTaskType.WslCommand
                || task.Health.Type is HealthCheckType.WslRunning or HealthCheckType.SystemdActive or HealthCheckType.WslProcess))
            ? _executor.FindProcessIds(task) : Array.Empty<int>();
        var logs = await _log.ReadTailAsync(
            configuration.Settings.LogDirectory,
            task.Name,
            10,
            cancellationToken).ConfigureAwait(false);
        return new TaskStatusDetail(task, health, processIds, logs);
    }

    internal async Task<IReadOnlyList<ManagedProcessSnapshot>> ManagedProcessesAsync(
        CancellationToken cancellationToken = default)
    {
        var configuration = await _configurationCache.GetAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return await _managedProcesses.ReadAsync(
            configuration.Tasks,
            _executor,
            _healthProbe,
            cancellationToken).ConfigureAwait(false);
    }

    internal Task<IReadOnlyList<ManagedProcessSnapshot>> ObserveProcessesAsync(
        AutorunConfiguration configuration, IReadOnlyList<TaskSnapshot> observations,
        CancellationToken cancellationToken = default) =>
        _observationProcesses.ReadAsync(configuration.Tasks, _executor,
            new CapturedHealthProbe(observations), cancellationToken);

    private sealed class CapturedHealthProbe(IReadOnlyList<TaskSnapshot> observations) : IHealthProbe
    {
        private readonly IReadOnlyDictionary<string, TaskSnapshot> _observations = observations
            .ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);

        public Task<HealthResult> CheckAsync(AutorunTask task, bool quick = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_observations.TryGetValue(task.Name, out var result)
                ? new HealthResult(result.Healthy, result.Reason, result.Detail, result.HealthObserved)
                : HealthResult.Unknown("health-source-unavailable", "This cycle has no health observation."));
        }

        public Task<HealthResult> CheckPassiveAsync(AutorunTask task, CancellationToken cancellationToken = default) =>
            CheckAsync(task, cancellationToken: cancellationToken);
    }

    internal async Task<ExceptionLogSnapshot> ExceptionLogsAsync(
        int count = 200,
        CancellationToken cancellationToken = default)
    {
        var configuration = await _configurationCache.GetAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return await _exceptionLogs.ReadAsync(
            configuration.RootDirectory,
            configuration.Settings.LogDirectory,
            Math.Clamp(count, 1, 500),
            cancellationToken).ConfigureAwait(false);
    }

    // Daemon control/recovery plans take and release a shared lock for EACH node. Never
    // call the legacy recursive boot-start entrypoint while holding a node lock.
    internal async Task<IDisposable> AcquireTaskAsync(string name, CancellationToken cancellationToken)
    {
        var gate = _taskLocks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new TaskLease(gate);
    }

    private sealed class TaskLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    internal Task<HealthResult> ObserveTaskAsync(AutorunTask task, CancellationToken cancellationToken) =>
        _healthProbe.CheckPassiveAsync(task, cancellationToken);

    internal Task<HealthResult> ObservePresenceAsync(AutorunTask task, CancellationToken cancellationToken)
    {
        var health = task.Type switch
        {
            AutorunTaskType.Windows => task.Health with { Type = HealthCheckType.Process, Pattern = Path.GetFileName(task.Executable) },
            AutorunTaskType.WindowsService => task.Health with { Type = HealthCheckType.WindowsService, Service = task.Service },
            AutorunTaskType.WslBoot => task.Health with { Type = HealthCheckType.WslRunning },
            _ => null,
        };
        return health is null
            ? Task.FromResult(HealthResult.Unknown("presence-unavailable", "Cannot passively confirm this target is absent."))
            : _healthProbe.CheckPassiveAsync(task with { Health = health }, cancellationToken);
    }

    internal static bool ConfirmedMissing(HealthResult presence) => presence.Observed && !presence.Healthy
        && presence.Reason is "process-not-found" or "windows-service-stopped" or "wsl-stopped";

    internal async Task<CommandResult> ExecuteStartNodeAsync(AutorunTask task, bool observedFault, CancellationToken cancellationToken)
    {
        // A recovery admission is based ONLY on captured passive evidence. In particular
        // do not run an active WSL probe before the authorized start side effect.
        if (!observedFault)
        {
            var current = await _healthProbe.CheckAsync(task, quick: true, cancellationToken).ConfigureAwait(false);
            if (current.Healthy) { return new(true, $"Already healthy: {task.Name}"); }
        }
        var started = await _executor.StartAsync(task, cancellationToken).ConfigureAwait(false);
        return started.Succeeded ? await WaitForHealthyAsync(task, started, cancellationToken).ConfigureAwait(false) : started;
    }

    internal Task<CommandResult> ExecuteStopNodeAsync(AutorunTask task, CancellationToken cancellationToken) =>
        _executor.StopAsync(task, cancellationToken);

    internal Task<CommandResult> StartAsync(string name, CancellationToken cancellationToken = default) =>
        RunLockedAsync(name, StartWithDependenciesAsync, cancellationToken);

    internal Task<CommandResult> StopAsync(string name, CancellationToken cancellationToken = default) =>
        RunLockedAsync(name, StopCoreAsync, cancellationToken);

    internal Task<CommandResult> RestartAsync(string name, CancellationToken cancellationToken = default) =>
        RunLockedAsync(name, RestartCoreAsync, cancellationToken);

    internal async Task<TaskSnapshot> SetEnabledAsync(
        string name,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var configuration = await _configurationCache.GetAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        _ = configuration.GetTask(name);
        await _overrideStore.SetEnabledAsync(
            _configurationCache.Paths.RootDirectory,
            name,
            enabled,
            cancellationToken).ConfigureAwait(false);
        configuration = await _configurationCache.GetAsync(force: true, cancellationToken)
            .ConfigureAwait(false);
        var updated = configuration.GetTask(name);
        var health = updated.Enabled
            ? await _healthProbe.CheckPassiveAsync(updated, cancellationToken).ConfigureAwait(false)
            : HealthResult.Fail("disabled", "工作负载已禁用。");
        return TaskSnapshot.From(updated, health);
    }

    internal async Task<IReadOnlyList<CommandResult>> StartScheduledAsync(
        bool runAsSystem,
        CancellationToken cancellationToken)
    {
        var configuration = await _configurationCache.GetAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var expectedRunAs = runAsSystem ? RunAsMode.System : RunAsMode.CurrentUser;
        var expectedTrigger = runAsSystem ? LaunchTrigger.Boot : LaunchTrigger.Logon;
        var tasks = configuration.Tasks
            .Where(task => task.Enabled
                && task.Launch.RunAs == expectedRunAs
                && task.Launch.Trigger == expectedTrigger)
            .OrderBy(task => task.StartupDelay)
            .ToArray();

        var results = new List<CommandResult>(tasks.Length);
        var previousDelay = 0;
        foreach (var task in tasks)
        {
            var delay = Math.Max(0, task.StartupDelay - previousDelay);
            if (delay > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
            }

            var health = await _healthProbe.CheckAsync(task, quick: true, cancellationToken)
                .ConfigureAwait(false);
            results.Add(health.Healthy
                ? new CommandResult(true, $"Already healthy: {task.Name}")
                : await StartAsync(task.Name, cancellationToken).ConfigureAwait(false));
            previousDelay = task.StartupDelay;
        }

        return results;
    }

    internal async Task<DoctorReport> DoctorAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<DoctorCheck>();
        AutorunConfiguration configuration;
        try
        {
            configuration = await _configurationCache.GetAsync(force: true, cancellationToken)
                .ConfigureAwait(false);
            checks.Add(new DoctorCheck("Configuration", true, $"Loaded {configuration.Tasks.Count} tasks."));
        }
        catch (Exception exception)
        {
            checks.Add(new DoctorCheck("Configuration", false, exception.Message));
            return new DoctorReport(checks);
        }

        checks.Add(new DoctorCheck(
            "Log directory",
            EnsureDirectoryWritable(configuration.Settings.LogDirectory, out var logDetail),
            logDetail));
        checks.Add(new DoctorCheck(
            "Backup directory",
            EnsureDirectoryWritable(configuration.Settings.BackupDirectory, out var backupDetail),
            backupDetail));

        var requiresWsl = configuration.Tasks.Any(task =>
            task.Type is AutorunTaskType.WslBoot
                or AutorunTaskType.WslSystemd
                or AutorunTaskType.WslCommand
            || task.Health.Type is HealthCheckType.WslRunning
                or HealthCheckType.SystemdActive
                or HealthCheckType.WslProcess);
        if (requiresWsl)
        {
            try
            {
                var wsl = ExecutableResolver.Resolve("wsl.exe");
                checks.Add(new DoctorCheck("wsl.exe", true, wsl));
            }
            catch (Exception exception)
            {
                checks.Add(new DoctorCheck("wsl.exe", false, exception.Message));
            }
        }

        foreach (var task in configuration.Tasks.Where(task => task.Type == AutorunTaskType.Windows))
        {
            try
            {
                var executable = ExecutableResolver.Resolve(task.Executable!, task.WorkingDirectory);
                checks.Add(new DoctorCheck($"Executable: {task.Name}", true, executable));
            }
            catch (Exception exception)
            {
                checks.Add(new DoctorCheck($"Executable: {task.Name}", false, exception.Message));
            }
        }

        foreach (var task in configuration.Tasks.Where(task =>
            task.Type == AutorunTaskType.WindowsService
            || task.Health.Type == HealthCheckType.WindowsService))
        {
            var serviceName = task.Health.Service ?? task.Service;
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                checks.Add(new DoctorCheck(
                    $"Windows service: {task.Name}",
                    false,
                    "Windows service name is missing."));
                continue;
            }

            try
            {
                var status = _windowsServices.GetStatus(serviceName);
                checks.Add(status.Exists
                    ? new DoctorCheck(
                        $"Windows service: {task.Name}",
                        true,
                        $"{serviceName} ({status.StateLabel})")
                    : new DoctorCheck(
                        $"Windows service: {task.Name}",
                        false,
                        $"Windows service was not found: {serviceName}"));
            }
            catch (Exception exception)
            {
                checks.Add(new DoctorCheck(
                    $"Windows service: {task.Name}",
                    false,
                    exception.Message));
            }
        }

        return new DoctorReport(checks);
    }

    private async Task<CommandResult> RunLockedAsync(
        string name,
        Func<AutorunConfiguration, AutorunTask, CancellationToken, Task<CommandResult>> operation,
        CancellationToken cancellationToken)
    {
        var initialConfiguration = await _configurationCache.GetAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        _ = initialConfiguration.GetTask(name);

        var gate = _taskLocks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = await _configurationCache.GetAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return await operation(configuration, configuration.GetTask(name), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CommandResult> StartWithDependenciesAsync(
        AutorunConfiguration configuration,
        AutorunTask task,
        CancellationToken cancellationToken)
    {
        var started = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task<CommandResult> StartNodeAsync(AutorunTask node)
        {
            if (!started.Add(node.Name))
            {
                return new CommandResult(true, $"Dependency already handled: {node.Name}");
            }

            foreach (var dependencyName in node.DependsOn)
            {
                var dependencyResult = await StartNodeAsync(configuration.GetTask(dependencyName))
                    .ConfigureAwait(false);
                if (!dependencyResult.Succeeded)
                {
                    return new CommandResult(
                        false,
                        $"Dependency '{dependencyName}' failed: {dependencyResult.Message}");
                }
            }

            var currentHealth = await _healthProbe.CheckAsync(node, quick: true, cancellationToken)
                .ConfigureAwait(false);
            if (currentHealth.Healthy)
            {
                return new CommandResult(true, $"Already healthy: {node.Name}");
            }

            var start = await _executor.StartAsync(node, cancellationToken).ConfigureAwait(false);
            if (!start.Succeeded)
            {
                return start;
            }

            return await WaitForHealthyAsync(node, start, cancellationToken).ConfigureAwait(false);
        }

        return await StartNodeAsync(task).ConfigureAwait(false);
    }

    private async Task<CommandResult> StopCoreAsync(
        AutorunConfiguration configuration,
        AutorunTask task,
        CancellationToken cancellationToken)
    {
        _ = configuration;
        return await _executor.StopAsync(task, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandResult> RestartCoreAsync(
        AutorunConfiguration configuration,
        AutorunTask task,
        CancellationToken cancellationToken)
    {
        var stopped = await _executor.StopAsync(task, cancellationToken).ConfigureAwait(false);
        if (!stopped.Succeeded)
        {
            return stopped;
        }

        return await StartWithDependenciesAsync(configuration, task, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CommandResult> WaitForHealthyAsync(
        AutorunTask task,
        CommandResult startResult,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(task.Health.TimeoutSeconds);
        HealthResult health;
        while (true)
        {
            health = await _healthProbe.CheckAsync(task, quick: true, cancellationToken)
                .ConfigureAwait(false);
            if (health.Healthy)
            {
                return new CommandResult(true, health.Detail, startResult.ProcessId);
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(
                remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1),
                cancellationToken).ConfigureAwait(false);
        }

        return new CommandResult(
            false,
            $"Started but health check failed: {health.Reason} - {health.Detail}",
            startResult.ProcessId);
    }

    private static bool EnsureDirectoryWritable(string directory, out string detail)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            detail = directory;
            return true;
        }
        catch (Exception exception)
        {
            detail = exception.Message;
            return false;
        }
    }
}
