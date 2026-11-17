using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.Windows.Execution;

public sealed class WindowsTaskExecutor : ITaskExecutor, IDisposable
{
    private readonly NativeProcessLauncher _launcher = new();
    private readonly ProcessIdentity _processIdentity = new();
    private readonly WslController _wsl = new();
    private readonly WindowsServiceController _services = new();
    private readonly FileLog _log;
    private readonly string _logDirectory;
    private readonly ConcurrentDictionary<string, TrackedProcess> _startedProcesses =
        new(StringComparer.OrdinalIgnoreCase);

    public WindowsTaskExecutor(FileLog log, string logDirectory)
    {
        _log = log;
        _logDirectory = logDirectory;
    }

    public async Task<CommandResult> StartAsync(
        AutorunTask task,
        CancellationToken cancellationToken = default)
    {
        await _log.WriteAsync(_logDirectory, task.Name, "Starting task.", cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var result = task.Type switch
            {
                AutorunTaskType.Windows => StartWindows(task),
                AutorunTaskType.WindowsService => await _services.StartAsync(
                    Required(task.Service, task.Name, "service"),
                    cancellationToken).ConfigureAwait(false),
                AutorunTaskType.WslBoot => await _wsl.EnsureRunningAsync(
                    Required(task.Distro, task.Name, "distro"),
                    cancellationToken).ConfigureAwait(false),
                AutorunTaskType.WslSystemd => await _wsl.StartSystemdAsync(
                    Required(task.Distro, task.Name, "distro"),
                    Required(task.Service, task.Name, "service"),
                    cancellationToken).ConfigureAwait(false),
                AutorunTaskType.WslCommand => await _wsl.StartCommandAsync(
                    Required(task.Distro, task.Name, "distro"),
                    Required(task.Command, task.Name, "command"),
                    cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(task), task.Type, null),
            };
            await _log.WriteAsync(
                _logDirectory,
                task.Name,
                result.Succeeded ? $"Start succeeded. {result.Message}" : $"Start failed. {result.Message}",
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            await _log.WriteAsync(
                _logDirectory,
                task.Name,
                $"Start failed: {exception.Message}",
                cancellationToken).ConfigureAwait(false);
            return new CommandResult(false, exception.Message);
        }
    }

    public async Task<CommandResult> StopAsync(
        AutorunTask task,
        CancellationToken cancellationToken = default)
    {
        await _log.WriteAsync(_logDirectory, task.Name, "Stopping task.", cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var result = task.Type switch
            {
                AutorunTaskType.Windows => await StopWindowsAsync(task, cancellationToken).ConfigureAwait(false),
                AutorunTaskType.WindowsService => await _services.StopAsync(
                    Required(task.Service, task.Name, "service"),
                    cancellationToken).ConfigureAwait(false),
                AutorunTaskType.WslBoot => await _wsl.TerminateAsync(
                    Required(task.Distro, task.Name, "distro"),
                    cancellationToken).ConfigureAwait(false),
                AutorunTaskType.WslSystemd => await _wsl.StopSystemdAsync(
                    Required(task.Distro, task.Name, "distro"),
                    Required(task.Service, task.Name, "service"),
                    cancellationToken).ConfigureAwait(false),
                AutorunTaskType.WslCommand => await _wsl.StopCommandAsync(
                    Required(task.Distro, task.Name, "distro"),
                    Required(task.Health.Pattern, task.Name, "health.pattern"),
                    cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(task), task.Type, null),
            };
            await _log.WriteAsync(
                _logDirectory,
                task.Name,
                result.Succeeded ? $"Stop succeeded. {result.Message}" : $"Stop failed. {result.Message}",
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            await _log.WriteAsync(
                _logDirectory,
                task.Name,
                $"Stop failed: {exception.Message}",
                cancellationToken).ConfigureAwait(false);
            return new CommandResult(false, exception.Message);
        }
    }

    public IReadOnlyList<int> FindProcessIds(AutorunTask task) => task.Type switch
    {
        AutorunTaskType.Windows => _processIdentity.FindMatchingProcessIds(task),
        AutorunTaskType.WindowsService when task.Service is not null =>
            FindServiceProcessIds(task.Service),
        _ when task.Distro is not null => _wsl.GetKeepaliveProcessIds(task.Distro),
        _ => Array.Empty<int>(),
    };

    public void Dispose() => _wsl.Dispose();

    private CommandResult StartWindows(AutorunTask task)
    {
        var existing = _processIdentity.FindMatchingProcessIds(task);
        if (existing.Count > 0)
        {
            return new CommandResult(true, $"Already running (PID {existing[0]}).", existing[0]);
        }

        var processId = _launcher.Start(task);
        try
        {
            using var process = Process.GetProcessById(processId);
            _startedProcesses[task.Name] = new TrackedProcess(
                processId,
                process.StartTime.ToUniversalTime());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // A short-lived process may exit before its identity can be recorded.
        }

        return new CommandResult(true, $"Started PID {processId}.", processId);
    }

    private async Task<CommandResult> StopWindowsAsync(
        AutorunTask task,
        CancellationToken cancellationToken)
    {
        var processIds = _processIdentity.FindMatchingProcessIds(task);
        TrackedProcess? trackedFallback = null;
        if (processIds.Count == 0 && _startedProcesses.TryGetValue(task.Name, out var trackedProcess))
        {
            if (IsTrackedProcessRunning(trackedProcess))
            {
                trackedFallback = trackedProcess;
                processIds = new[] { trackedProcess.ProcessId };
            }
            else
            {
                _startedProcesses.TryRemove(task.Name, out _);
            }
        }

        if (processIds.Count == 0)
        {
            return new CommandResult(true, "Process is not running.");
        }

        try
        {
            foreach (var processId in processIds)
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    if (trackedFallback is not null
                        && processId == trackedFallback.ProcessId
                        && !IsSameProcess(process, trackedFallback))
                    {
                        continue;
                    }

                    process.Kill(entireProcessTree: true);
                    using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutSource.CancelAfter(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                }
                catch (ArgumentException)
                {
                    // The process exited between identity validation and termination.
                }
            }
        }
        finally
        {
            if (_startedProcesses.TryGetValue(task.Name, out var current)
                && !IsTrackedProcessRunning(current))
            {
                _startedProcesses.TryRemove(task.Name, out _);
            }
        }

        return new CommandResult(true, $"Stopped {processIds.Count} process(es).");
    }

    private static bool IsTrackedProcessRunning(TrackedProcess trackedProcess)
    {
        try
        {
            using var process = Process.GetProcessById(trackedProcess.ProcessId);
            return IsSameProcess(process, trackedProcess);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static bool IsSameProcess(Process process, TrackedProcess trackedProcess) =>
        !process.HasExited && process.StartTime.ToUniversalTime() == trackedProcess.StartTimeUtc;

    private IReadOnlyList<int> FindServiceProcessIds(string serviceName)
    {
        try
        {
            return _services.GetStatus(serviceName).ProcessId is int processId
                ? new[] { processId }
                : Array.Empty<int>();
        }
        catch (Win32Exception)
        {
            return Array.Empty<int>();
        }
    }

    private static string Required(string? value, string taskName, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Task '{taskName}' requires {field}.")
            : value;

    private sealed record TrackedProcess(int ProcessId, DateTime StartTimeUtc);
}
