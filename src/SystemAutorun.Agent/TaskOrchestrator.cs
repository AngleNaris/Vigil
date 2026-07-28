using System.Collections.Concurrent;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;
using SystemAutorun.Windows.Execution;

namespace SystemAutorun.Agent;

internal sealed class TaskOrchestrator
{
    private readonly ConfigurationCache _configurationCache;
    private readonly OverrideStore _overrideStore;
    private readonly ITaskExecutor _executor;
    private readonly IHealthProbe _healthProbe;
    private readonly FileLog _log;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _taskLocks =
        new(StringComparer.OrdinalIgnoreCase);

    internal TaskOrchestrator(
        ConfigurationCache configurationCache,
        OverrideStore overrideStore,
        ITaskExecutor executor,
        IHealthProbe healthProbe,
        FileLog log)
    {
        _configurationCache = configurationCache;
        _overrideStore = overrideStore;
        _executor = executor;
        _healthProbe = healthProbe;
        _log = log;
    }

    internal async Task<IReadOnlyList<TaskSnapshot>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var configuration = await _configurationCache.GetAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var probes = configuration.Tasks.Select(async task =>
        {
            if (!task.Enabled)
            {
                return TaskSnapshot.From(task, HealthResult.Fail("disabled", "Task is disabled."));
            }

            var health = await _healthProbe.CheckAsync(task, quick: true, cancellationToken)
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
        var health = await _healthProbe.CheckAsync(task, quick: true, cancellationToken)
            .ConfigureAwait(false);
        var processIds = _executor.FindProcessIds(task);
        var logs = await _log.ReadTailAsync(
            configuration.Settings.LogDirectory,
            task.Name,
            10,
            cancellationToken).ConfigureAwait(false);
        return new TaskStatusDetail(task, health, processIds, logs);
    }

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
            ? await _healthProbe.CheckAsync(updated, quick: true, cancellationToken).ConfigureAwait(false)
            : HealthResult.Fail("disabled", "Task is disabled.");
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

        try
        {
            var wsl = ExecutableResolver.Resolve("wsl.exe");
            checks.Add(new DoctorCheck("wsl.exe", true, wsl));
        }
        catch (Exception exception)
        {
            checks.Add(new DoctorCheck("wsl.exe", false, exception.Message));
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

        return new DoctorReport(checks);
    }

    private async Task<CommandResult> RunLockedAsync(
        string name,
        Func<AutorunConfiguration, AutorunTask, CancellationToken, Task<CommandResult>> operation,
        CancellationToken cancellationToken)
    {
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
        do
        {
            health = await _healthProbe.CheckAsync(task, quick: true, cancellationToken)
                .ConfigureAwait(false);
            if (health.Healthy)
            {
                return new CommandResult(true, health.Detail, startResult.ProcessId);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);

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
