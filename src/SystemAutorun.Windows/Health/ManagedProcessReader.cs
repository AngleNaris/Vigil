using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;
using SystemAutorun.Core.Services;
using SystemAutorun.Windows.Native;

namespace SystemAutorun.Windows.Health;

public sealed class ManagedProcessReader
{
    private readonly ConcurrentDictionary<ProcessKey, CpuSample> _cpuSamples = new();

    public async Task<IReadOnlyList<ManagedProcessSnapshot>> ReadAsync(
        IReadOnlyList<AutorunTask> tasks,
        ITaskExecutor executor,
        IHealthProbe healthProbe,
        CancellationToken cancellationToken = default)
    {
        // R105: every enabled workload is sampled (CPU, memory, health history). ExpectedRunning still
        // decides recovery and "常驻" alert counts elsewhere; a disabled tracked task stays reported as OFF.
        var monitored = tasks.Where(task => task.Enabled || task.ExpectedRunning).ToArray();
        // Aliases may include the same PID. One physical read per capture keeps their CPU baselines,
        // memory and first-sample unknown identical; GetOrAdd's factory alone can run more than once.
        var nativeSnapshot = new Lazy<Dictionary<int, NativeMethods.NativeProcessMetric>?>(
            NativeMethods.TryReadProcessMetrics,
            LazyThreadSafetyMode.ExecutionAndPublication);
        var captures = new ConcurrentDictionary<int, Lazy<ManagedProcessInstanceSnapshot?>>();
        var snapshots = await Task.WhenAll(monitored.Select(task =>
            ReadTaskAsync(task, executor, healthProbe, captures, nativeSnapshot, cancellationToken))).ConfigureAwait(false);
        RemoveStaleSamples(snapshots);
        return snapshots
            .OrderBy(snapshot => snapshot.State == "FAIL" ? 0 : snapshot.State == "OK" ? 1 : 2)
            .ThenBy(snapshot => snapshot.TaskName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private async Task<ManagedProcessSnapshot> ReadTaskAsync(
        AutorunTask task,
        ITaskExecutor executor,
        IHealthProbe healthProbe,
        ConcurrentDictionary<int, Lazy<ManagedProcessInstanceSnapshot?>> captures,
        Lazy<Dictionary<int, NativeMethods.NativeProcessMetric>?> nativeSnapshot,
        CancellationToken cancellationToken)
    {
        var health = task.Enabled
            ? await healthProbe.CheckPassiveAsync(task, cancellationToken).ConfigureAwait(false)
            : HealthResult.Fail("disabled", "工作负载已禁用。");
        var capturedAt = DateTimeOffset.Now;
        var isWslWorkload = task.Type is AutorunTaskType.WslBoot or AutorunTaskType.WslSystemd or AutorunTaskType.WslCommand
            || task.Health.Type is HealthCheckType.WslRunning or HealthCheckType.SystemdActive or HealthCheckType.WslProcess;
        var processIds = task.Type is AutorunTaskType.Windows or AutorunTaskType.WindowsService && !isWslWorkload
            ? executor.FindProcessIds(task)
            : isWslWorkload && health.Healthy
                ? executor.FindProcessIds(task)
                : Array.Empty<int>();
        var processes = processIds
            .Distinct()
            .Select(processId => captures.GetOrAdd(processId, id => new(() => TryReadProcess(id, capturedAt, nativeSnapshot))).Value)
            .Where(snapshot => snapshot is not null)
            .Cast<ManagedProcessInstanceSnapshot>()
            .OrderBy(snapshot => snapshot.ProcessId)
            .ToArray();
        var state = !task.Enabled ? "OFF" : !health.Observed ? "UNKNOWN" : health.Healthy ? "OK" : "FAIL";
        return new ManagedProcessSnapshot(
            task.Name,
            task.Description,
            task.Type.ToKey(),
            task.ExpectedRunning,
            task.Enabled,
            health.Healthy,
            state,
            health.Reason,
            health.Detail,
            capturedAt,
            processes,
            health.Observed,
            MonitoringTaskIdentity.For(task));
    }

    private ManagedProcessInstanceSnapshot? TryReadProcess(
        int processId,
        DateTimeOffset capturedAt,
        Lazy<Dictionary<int, NativeMethods.NativeProcessMetric>?> nativeSnapshot)
    {
        if (nativeSnapshot.Value is { } metrics
            && metrics.TryGetValue(processId, out var metric)
            && metric.CreateFileTimeUtc > 0)
        {
            var startedAt = new DateTimeOffset(DateTime.FromFileTime(metric.CreateFileTimeUtc));
            var key = new ProcessKey(processId, startedAt.UtcTicks);
            var totalProcessorTime = TimeSpan.FromTicks(metric.TotalProcessorTimeTicks);
            var current = new CpuSample(capturedAt, totalProcessorTime);
            var cpuUsage = _cpuSamples.TryGetValue(key, out var previous)
                ? CalculateCpuUsage(previous, current)
                : null;
            _cpuSamples[key] = current;

            return new ManagedProcessInstanceSnapshot(
                processId,
                metric.ProcessName,
                NativeMethods.TryGetProcessImagePath(processId),
                startedAt,
                cpuUsage ?? 0,
                Math.Max(0, metric.WorkingSetBytes),
                Math.Max(0, metric.PrivateBytes),
                metric.ThreadCount,
                CpuUsageAvailable: cpuUsage.HasValue);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return null;
            }

            var startedAt = new DateTimeOffset(process.StartTime);
            var key = new ProcessKey(processId, startedAt.UtcTicks);
            var totalProcessorTime = process.TotalProcessorTime;
            var current = new CpuSample(capturedAt, totalProcessorTime);
            var cpuUsage = _cpuSamples.TryGetValue(key, out var previous)
                ? CalculateCpuUsage(previous, current)
                : null;
            _cpuSamples[key] = current;

            return new ManagedProcessInstanceSnapshot(
                processId,
                process.ProcessName,
                NativeMethods.TryGetProcessImagePath(processId),
                startedAt,
                cpuUsage ?? 0,
                Math.Max(0, process.WorkingSet64),
                Math.Max(0, process.PrivateMemorySize64),
                process.Threads.Count,
                CpuUsageAvailable: cpuUsage.HasValue);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or Win32Exception
                or NotSupportedException)
        {
            return null;
        }
    }

    private static double? CalculateCpuUsage(CpuSample previous, CpuSample current)
    {
        var elapsed = current.CapturedAt - previous.CapturedAt;
        var processor = current.TotalProcessorTime - previous.TotalProcessorTime;
        if (elapsed <= TimeSpan.Zero || processor < TimeSpan.Zero)
        {
            return null;
        }

        return Math.Clamp(
            processor.TotalMilliseconds
            / elapsed.TotalMilliseconds
            / Math.Max(1, Environment.ProcessorCount)
            * 100d,
            0,
            100);
    }

    private void RemoveStaleSamples(IReadOnlyList<ManagedProcessSnapshot> snapshots)
    {
        var active = snapshots
            .SelectMany(snapshot => snapshot.Processes)
            .Where(process => process.StartedAt is not null)
            .Select(process => new ProcessKey(process.ProcessId, process.StartedAt!.Value.UtcTicks))
            .ToHashSet();
        foreach (var key in _cpuSamples.Keys)
        {
            if (!active.Contains(key))
            {
                _cpuSamples.TryRemove(key, out _);
            }
        }
    }

    private readonly record struct ProcessKey(int ProcessId, long StartedAtUtcTicks);

    private readonly record struct CpuSample(
        DateTimeOffset CapturedAt,
        TimeSpan TotalProcessorTime);
}
