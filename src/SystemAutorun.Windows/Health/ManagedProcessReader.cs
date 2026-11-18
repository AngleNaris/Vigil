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
    private readonly ConcurrentDictionary<ProcessKey, ProcessSample> _samples = new();
    private readonly Func<IProcessNetworkCounterSource> _networkCounterAccessor;
    private readonly Func<IReadOnlyDictionary<int, NativeMethods.NativeProcessMetric>?> _nativeMetricsProvider;

    public ManagedProcessReader()
        : this(null, null)
    {
    }

    internal ManagedProcessReader(
        IProcessNetworkCounterSource? networkCounterSource = null,
        Func<IReadOnlyDictionary<int, NativeMethods.NativeProcessMetric>?>? nativeMetricsProvider = null)
    {
        _networkCounterAccessor = networkCounterSource is null
            ? static () => ProcessNetworkCounterTracker.Shared
            : () => networkCounterSource;
        _nativeMetricsProvider = nativeMetricsProvider ?? NativeMethods.TryReadProcessMetrics;
    }

    public async Task<IReadOnlyList<ManagedProcessSnapshot>> ReadAsync(
        IReadOnlyList<AutorunTask> tasks,
        ITaskExecutor executor,
        IHealthProbe healthProbe,
        CancellationToken cancellationToken = default)
    {
        // R105: every enabled workload is sampled (CPU, memory, disk/network IO, health history).
        // ExpectedRunning still decides recovery and "常驻" alert counts elsewhere; a disabled tracked task stays reported as OFF.
        var monitored = tasks.Where(task => task.Enabled || task.ExpectedRunning).ToArray();
        var probes = await Task.WhenAll(monitored.Select(task =>
            ProbeTaskAsync(task, executor, healthProbe, cancellationToken))).ConfigureAwait(false);
        var allRootPids = probes.SelectMany(probe => probe.ProcessIds).ToHashSet();

        // Aliases may include the same PID. One physical read per capture keeps their CPU/IO baselines,
        // memory and first-sample unknown identical; GetOrAdd's factory alone can run more than once.
        var nativeSnapshot = new Lazy<IReadOnlyDictionary<int, NativeMethods.NativeProcessMetric>?>(
            _nativeMetricsProvider,
            LazyThreadSafetyMode.ExecutionAndPublication);
        var childrenByParent = new Lazy<Dictionary<int, List<int>>>(
            () => BuildChildrenByParent(nativeSnapshot.Value, allRootPids),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var captures = new ConcurrentDictionary<int, Lazy<ManagedProcessInstanceSnapshot?>>();
        var activeKeys = new ConcurrentDictionary<ProcessKey, byte>();

        var snapshots = probes.Select(probe => BuildSnapshot(probe, captures, nativeSnapshot, childrenByParent, activeKeys))
            .OrderBy(snapshot => snapshot.State == "FAIL" ? 0 : snapshot.State == "OK" ? 1 : 2)
            .ThenBy(snapshot => snapshot.TaskName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        RemoveStaleSamples(activeKeys);
        return snapshots;
    }

    private static async Task<TaskProbeResult> ProbeTaskAsync(
        AutorunTask task,
        ITaskExecutor executor,
        IHealthProbe healthProbe,
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
        return new TaskProbeResult(task, capturedAt, health, processIds.Distinct().ToArray());
    }

    private ManagedProcessSnapshot BuildSnapshot(
        TaskProbeResult probe,
        ConcurrentDictionary<int, Lazy<ManagedProcessInstanceSnapshot?>> captures,
        Lazy<IReadOnlyDictionary<int, NativeMethods.NativeProcessMetric>?> nativeSnapshot,
        Lazy<Dictionary<int, List<int>>> childrenByParent,
        ConcurrentDictionary<ProcessKey, byte> activeKeys)
    {
        var processes = probe.ProcessIds
            .Select(processId => captures.GetOrAdd(
                processId,
                id => new(() => TryReadProcess(id, probe.CapturedAt, nativeSnapshot, childrenByParent, activeKeys),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value)
            .Where(snapshot => snapshot is not null)
            .Cast<ManagedProcessInstanceSnapshot>()
            .OrderBy(snapshot => snapshot.ProcessId)
            .ToArray();
        var state = !probe.Task.Enabled ? "OFF" : !probe.Health.Observed ? "UNKNOWN" : probe.Health.Healthy ? "OK" : "FAIL";
        return new ManagedProcessSnapshot(
            probe.Task.Name,
            probe.Task.Description,
            probe.Task.Type.ToKey(),
            probe.Task.ExpectedRunning,
            probe.Task.Enabled,
            probe.Health.Healthy,
            state,
            probe.Health.Reason,
            probe.Health.Detail,
            probe.CapturedAt,
            processes,
            probe.Health.Observed,
            MonitoringTaskIdentity.For(probe.Task));
    }

    private static Dictionary<int, List<int>> BuildChildrenByParent(
        IReadOnlyDictionary<int, NativeMethods.NativeProcessMetric>? metrics,
        HashSet<int> allRootPids)
    {
        var children = new Dictionary<int, List<int>>();
        if (metrics is null || metrics.Count == 0 || allRootPids.Count == 0)
        {
            return children;
        }

        foreach (var metric in metrics.Values)
        {
            if (metric.ProcessId <= 4
                || metric.ParentProcessId <= 0
                || metric.ParentProcessId == metric.ProcessId
                || metric.CreateFileTimeUtc <= 0
                || allRootPids.Contains(metric.ProcessId))
            {
                continue;
            }

            if (!metrics.TryGetValue(metric.ParentProcessId, out var parent)
                || metric.CreateFileTimeUtc < parent.CreateFileTimeUtc)
            {
                continue;
            }

            if (!children.TryGetValue(metric.ParentProcessId, out var list))
            {
                list = [];
                children[metric.ParentProcessId] = list;
            }
            list.Add(metric.ProcessId);
        }

        return children;
    }

    private ManagedProcessInstanceSnapshot? TryReadProcess(
        int processId,
        DateTimeOffset capturedAt,
        Lazy<IReadOnlyDictionary<int, NativeMethods.NativeProcessMetric>?> nativeSnapshot,
        Lazy<Dictionary<int, List<int>>> childrenByParent,
        ConcurrentDictionary<ProcessKey, byte> activeKeys)
    {
        var root = TryReadSingleProcess(processId, capturedAt, nativeSnapshot.Value, includeImagePath: true, activeKeys);
        if (root is null)
        {
            return null;
        }

        var cpuPercent = root.CpuUsagePercent;
        var workingSetBytes = root.WorkingSetBytes;
        var privateMemoryBytes = root.PrivateMemoryBytes;
        var threadCount = root.ThreadCount;
        var diskReadRate = root.DiskReadBytesPerSecond;
        var diskWriteRate = root.DiskWriteBytesPerSecond;
        var netRecvRate = root.NetworkReceiveBytesPerSecond;
        var netSendRate = root.NetworkSendBytesPerSecond;

        var tree = childrenByParent.Value;
        if (tree.TryGetValue(processId, out var directChildren) && directChildren.Count > 0)
        {
            var visited = new HashSet<int> { processId };
            var queue = new Queue<int>(directChildren);
            while (queue.Count > 0)
            {
                var childPid = queue.Dequeue();
                if (!visited.Add(childPid))
                {
                    continue;
                }

                var child = TryReadSingleProcess(childPid, capturedAt, nativeSnapshot.Value, includeImagePath: false, activeKeys);
                if (child is null)
                {
                    continue;
                }

                if (child.CpuUsageAvailable)
                {
                    cpuPercent += child.CpuUsagePercent;
                }
                workingSetBytes += child.WorkingSetBytes;
                privateMemoryBytes += child.PrivateMemoryBytes;
                threadCount += child.ThreadCount;
                diskReadRate += child.DiskReadBytesPerSecond;
                diskWriteRate += child.DiskWriteBytesPerSecond;
                netRecvRate += child.NetworkReceiveBytesPerSecond;
                netSendRate += child.NetworkSendBytesPerSecond;

                if (tree.TryGetValue(childPid, out var nestedChildren))
                {
                    foreach (var nested in nestedChildren)
                    {
                        if (!visited.Contains(nested))
                        {
                            queue.Enqueue(nested);
                        }
                    }
                }
            }
        }

        return new ManagedProcessInstanceSnapshot(
            processId,
            root.ProcessName,
            root.ExecutablePath,
            root.StartedAt,
            Math.Clamp(cpuPercent, 0, 100),
            workingSetBytes,
            privateMemoryBytes,
            threadCount,
            CpuUsageAvailable: root.CpuUsageAvailable,
            DiskReadBytesPerSecond: Math.Round(Math.Max(0, diskReadRate), 2),
            DiskWriteBytesPerSecond: Math.Round(Math.Max(0, diskWriteRate), 2),
            NetworkReceiveBytesPerSecond: Math.Round(Math.Max(0, netRecvRate), 2),
            NetworkSendBytesPerSecond: Math.Round(Math.Max(0, netSendRate), 2));
    }

    private SingleProcessObservation? TryReadSingleProcess(
        int processId,
        DateTimeOffset capturedAt,
        IReadOnlyDictionary<int, NativeMethods.NativeProcessMetric>? metrics,
        bool includeImagePath,
        ConcurrentDictionary<ProcessKey, byte> activeKeys)
    {
        var (netRecvBytes, netSendBytes) = _networkCounterAccessor().ReadCumulativeBytes(processId);

        if (metrics is not null
            && metrics.TryGetValue(processId, out var metric)
            && metric.CreateFileTimeUtc > 0)
        {
            var startedAt = new DateTimeOffset(DateTime.FromFileTime(metric.CreateFileTimeUtc));
            var key = new ProcessKey(processId, startedAt.UtcTicks);
            activeKeys.TryAdd(key, 0);

            var current = new ProcessSample(
                capturedAt,
                TimeSpan.FromTicks(metric.TotalProcessorTimeTicks),
                metric.DiskReadBytes,
                metric.DiskWriteBytes,
                netRecvBytes,
                netSendBytes);
            return ComputeObservation(
                metric.ProcessName,
                includeImagePath ? NativeMethods.TryGetProcessImagePath(processId) : null,
                startedAt,
                key,
                current,
                Math.Max(0, metric.WorkingSetBytes),
                Math.Max(0, metric.PrivateBytes),
                metric.ThreadCount);
        }

        if (!includeImagePath)
        {
            return null;
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
            activeKeys.TryAdd(key, 0);

            var current = new ProcessSample(
                capturedAt,
                process.TotalProcessorTime,
                0,
                0,
                netRecvBytes,
                netSendBytes);
            return ComputeObservation(
                process.ProcessName,
                NativeMethods.TryGetProcessImagePath(processId),
                startedAt,
                key,
                current,
                Math.Max(0, process.WorkingSet64),
                Math.Max(0, process.PrivateMemorySize64),
                process.Threads.Count);
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

    private SingleProcessObservation ComputeObservation(
        string processName,
        string? executablePath,
        DateTimeOffset startedAt,
        ProcessKey key,
        ProcessSample current,
        long workingSetBytes,
        long privateMemoryBytes,
        int threadCount)
    {
        double? cpuUsage = null;
        double diskReadRate = 0;
        double diskWriteRate = 0;
        double netRecvRate = 0;
        double netSendRate = 0;

        if (_samples.TryGetValue(key, out var previous))
        {
            var elapsedSeconds = (current.CapturedAt - previous.CapturedAt).TotalSeconds;
            cpuUsage = CalculateCpuUsage(previous, current);
            diskReadRate = ComputeRate(previous.DiskReadBytes, current.DiskReadBytes, elapsedSeconds);
            diskWriteRate = ComputeRate(previous.DiskWriteBytes, current.DiskWriteBytes, elapsedSeconds);
            netRecvRate = ComputeRate(previous.NetworkReceivedBytes, current.NetworkReceivedBytes, elapsedSeconds);
            netSendRate = ComputeRate(previous.NetworkSentBytes, current.NetworkSentBytes, elapsedSeconds);
        }

        _samples[key] = current;

        return new SingleProcessObservation(
            processName,
            executablePath,
            startedAt,
            cpuUsage ?? 0,
            workingSetBytes,
            privateMemoryBytes,
            threadCount,
            cpuUsage.HasValue,
            diskReadRate,
            diskWriteRate,
            netRecvRate,
            netSendRate);
    }

    private static double ComputeRate(long previousBytes, long currentBytes, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0)
        {
            return 0;
        }

        var delta = currentBytes - previousBytes;
        if (delta <= 0)
        {
            return 0;
        }

        var rate = delta / elapsedSeconds;
        return double.IsFinite(rate) ? rate : 0;
    }

    private static double? CalculateCpuUsage(ProcessSample previous, ProcessSample current)
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

    private void RemoveStaleSamples(ConcurrentDictionary<ProcessKey, byte> activeKeys)
    {
        foreach (var key in _samples.Keys)
        {
            if (!activeKeys.ContainsKey(key))
            {
                _samples.TryRemove(key, out _);
            }
        }
    }

    private readonly record struct TaskProbeResult(
        AutorunTask Task,
        DateTimeOffset CapturedAt,
        HealthResult Health,
        int[] ProcessIds);

    private sealed record SingleProcessObservation(
        string ProcessName,
        string? ExecutablePath,
        DateTimeOffset StartedAt,
        double CpuUsagePercent,
        long WorkingSetBytes,
        long PrivateMemoryBytes,
        int ThreadCount,
        bool CpuUsageAvailable,
        double DiskReadBytesPerSecond,
        double DiskWriteBytesPerSecond,
        double NetworkReceiveBytesPerSecond,
        double NetworkSendBytesPerSecond);

    private readonly record struct ProcessKey(int ProcessId, long StartedAtUtcTicks);

    private readonly record struct ProcessSample(
        DateTimeOffset CapturedAt,
        TimeSpan TotalProcessorTime,
        long DiskReadBytes,
        long DiskWriteBytes,
        long NetworkReceivedBytes,
        long NetworkSentBytes);
}
