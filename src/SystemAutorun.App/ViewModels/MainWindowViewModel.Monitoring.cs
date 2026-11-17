using SystemAutorun.App.Controls;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.App.ViewModels;

public partial class MainWindowViewModel
{
    private readonly IDashboardMonitoringClient _monitoring;
    private ConfigurationWorkspaceSnapshot? _monitoringConfiguration;
    private AgentMonitoringSnapshot? _monitoringSnapshot;
    private bool _applyingMonitoring;
    private bool _tasksAvailable;
    private bool _processesAvailable;
    private bool _hostAvailable;
    private string? _monitoringProblem;
    private DateTimeOffset _lastFullRefreshAt = DateTimeOffset.MinValue;
    private CancellationTokenSource? _liveCancellation;
    private long? _lastSnapshotStartTimestamp;
    private int _coreMonitoringReads;
    private DateTimeOffset? _appliedTasksObservedAt;
    private string? _appliedTasksInstanceId;
    private ConfigurationWorkspaceSnapshot? _appliedTasksConfiguration;
    private readonly Dictionary<string, DateTimeOffset> _highCpuSince = new(StringComparer.OrdinalIgnoreCase);

    internal const double HighCpuThresholdPercent = 85.0;
    internal const double HighCpuResetThresholdPercent = 80.0;
    internal static readonly TimeSpan HighCpuSustainDuration = TimeSpan.FromMinutes(1);
    internal const double HostHighMemoryThresholdPercent = 85.0;
    internal const double ProcessHighMemoryThresholdPercent = 25.0;
    internal const long ProcessHighMemoryThresholdBytes = 2L * 1024 * 1024 * 1024;

    // The Agent targets half-second resource samples and ten-second health observations. A visible
    // local view reads snapshots at the selected GUI cadence; full/history work stays on its own clock.
    internal static readonly TimeSpan LiveRefreshInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan FullRefreshInterval = TimeSpan.FromSeconds(10);

    internal bool FullRefreshDue => DateTimeOffset.UtcNow - _lastFullRefreshAt is var age
        && (age >= FullRefreshInterval || age < TimeSpan.Zero);
    private bool LiveSnapshotDue => _lastSnapshotStartTimestamp is not { } last
        || System.Diagnostics.Stopwatch.GetElapsedTime(last) >= AutoRefreshInterval;
    private void MarkSnapshotReadStarted() => _lastSnapshotStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>Reads only the Agent's cached monitoring snapshot. Silent: no busy state, no status text;
    /// a failure is left for the next full refresh to report, and never clears current observations.</summary>
    internal async Task RefreshLiveAsync()
    {
        if (_liveCancellation is not null || !CanReadLive() || !LiveSnapshotDue) { return; }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        _liveCancellation = cancellation; MarkSnapshotReadStarted();
        try
        {
            if (IsLocalHostSelected)
            {
                var beforeStatus = SelectedTask?.Status;
                var beforeTasksAt = _appliedTasksObservedAt;
                var snapshot = await _monitoring.ReadSnapshotAsync(cancellation.Token);
                if (cancellation.IsCancellationRequested || !CanReadLive() || !IsLocalHostSelected) { return; }
                _dashboardNow = DateTimeOffset.UtcNow;
                ApplyMonitoringSnapshot(snapshot);
                if (IsWorkloadsPage && !IsBusy && SelectedTask?.Status != beforeStatus)
                {
                    _ = QueueDashboardHistoryAsync(automatic: true);
                }
            }
            else if (SelectedHostTab is { IsLocal: false, Definition: { Enabled: true } definition } tab)
            {
                var generation = _remoteGeneration;
                var revision = _monitoringConfiguration?.Revision;
                var snapshot = await _remoteHostReader.ReadAsync(definition, cancellation.Token);
                if (cancellation.IsCancellationRequested || _dashboardDisposed || !_presentationActive
                    || generation != _remoteGeneration || !ReferenceEquals(SelectedHostTab, tab)
                    || revision is null || revision != _configuration.Revision || revision != _monitoringConfiguration?.Revision)
                { return; }
                if (!string.Equals(snapshot.Id, definition.Id, StringComparison.OrdinalIgnoreCase)
                    || snapshot.Platform != definition.Platform || snapshot.Host != definition.Host
                    || snapshot.Online && !RemoteObservationFresh(snapshot.CapturedAt))
                { return; }
                _dashboardNow = DateTimeOffset.UtcNow;
                _remoteResumeGraceUntilUtc = null;
                tab.ApplyRemoteStatus(snapshot);
                ApplyRemoteHostStatus(snapshot);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { System.Diagnostics.Trace.TraceInformation("Live monitoring read skipped: {0}", exception.GetType().Name); }
        finally { if (ReferenceEquals(_liveCancellation, cancellation)) { _liveCancellation = null; } }
    }

    private bool CanReadLive() => !_dashboardDisposed && _presentationActive && AutoRefresh && _coreMonitoringReads == 0
        && _remoteCancellation is null && !IsBusy && _monitoringConfiguration is not null
        && (IsLocalHostSelected || SelectedHostTab is { IsLocal: false, Definition.Enabled: true });

    private void CancelLiveRead()
    {
        _liveCancellation?.Cancel();
    }

    public string HostMemoryPercentText => !HostStatusAvailable ? "--"
        : IsLocalHostSelected && _lastLocalHostSnapshot is { TotalMemoryBytes: <= 0 } ? "--"
        : IsRemoteHostSelected && SelectedHostTab?.RemoteSnapshot is { TotalMemoryBytes: <= 0 } ? "--"
        : $"{Math.Clamp(MemoryUsagePercent, 0, 100):0}%";

    private string LocalMonitoringRefreshMessage => _monitoringProblem is null
        // Exception messages usually end with a period; avoid "unavailable.；" in the joined sentence.
        ? "本机家庭服务器状态已刷新。" : $"监测部分不可用：{_monitoringProblem.TrimEnd('.', '。', ';', '；', ' ')}；未知不代表正常。";

    private async Task RefreshMonitoringCoreAsync(CancellationToken cancellationToken = default)
    {
        // Full work keeps its independent ten-second clock, but must not add an extra snapshot
        // between GUI cadence slots unless configuration identity changed or the read is manual.
        var readSnapshot = !_refreshIsAutomatic || LiveSnapshotDue || !ConfigurationMatchesMonitoring;
        if (readSnapshot)
        {
            // A live read started earlier must not land after this newer one and be rejected as stale.
            CancelLiveRead(); _coreMonitoringReads++; MarkSnapshotReadStarted();
            try
            {
                var snapshot = await _monitoring.ReadSnapshotAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _dashboardNow = DateTimeOffset.UtcNow;
                ApplyMonitoringSnapshot(snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { MarkMonitoringUnavailable(exception.Message); }
            finally { _coreMonitoringReads--; }
        }
        if (_presentationActive)
        {
            try { await _cache.SaveAsync(Tasks.Select(row => row.Snapshot).ToArray(), cancellationToken); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { _monitoringProblem = $"当前观测已读取，展示缓存写入失败：{exception.Message}"; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        await QueueDashboardHistoryAsync(automatic: _refreshIsAutomatic);
        cancellationToken.ThrowIfCancellationRequested();
        UpdateResourceTrends(force: !_refreshIsAutomatic);
        if (_resourceCancellation is not null) { await ResourceHistoryTask; }
    }

    private void MarkMonitoringUnavailable(string reason)
    {
        _localResumeGraceUntilUtc = null;
        var hadSnapshot = _monitoringSnapshot is not null;
        _monitoringSnapshot = null;
        _monitoringProblem = reason;
        ApplyMonitoringGroups();
        if (hadSnapshot) { RefreshCapabilityChanged(); }
    }

    internal void ApplyMonitoringConfiguration(ConfigurationWorkspaceSnapshot configuration)
    {
        var changed = _monitoringConfiguration?.Revision != configuration.Revision;
        _monitoringConfiguration = configuration;
        NotifyTaskAutomation();
        if (!changed) { return; }
        if (_monitoringSnapshot is not null) { ApplyMonitoringGroups(); }
        if (!IsRefreshing) { _ = QueueDashboardHistoryAsync(); }
    }

    internal void ApplyMonitoringSnapshot(AgentMonitoringSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != 1 || snapshot.RootId != AgentProtocol.ForRoot(_paths.RootDirectory).RootId
            || !Guid.TryParseExact(snapshot.InstanceId, "N", out _)
            || snapshot.ReturnedAt > DateTimeOffset.UtcNow.AddSeconds(5)
            || (snapshot.RecentResourceSchemaVersion is null) != (snapshot.ResourceSamplingIntervalMilliseconds is null)
            || snapshot.RecentResourceSchemaVersion is { } schema && schema != 2
            || snapshot.ResourceSamplingIntervalMilliseconds is { } milliseconds && milliseconds is not (500 or 1000)
            || snapshot.RemoteHosts is { Count: > 64 })
        { throw new InvalidDataException("监测快照身份、版本或时间无效。"); }
        if (_monitoringSnapshot is { } before && before.InstanceId == snapshot.InstanceId
            && snapshot.ReturnedAt < before.ReturnedAt)
        { throw new InvalidDataException("拒绝过时的监测快照。"); }
        var previousCapability = (_monitoringSnapshot?.InstanceId, _monitoringSnapshot?.RecentResourceSchemaVersion,
            _monitoringSnapshot?.ResourceSamplingIntervalMilliseconds);
        var instanceChanged = _monitoringSnapshot is { } previous && previous.InstanceId != snapshot.InstanceId;
        _localResumeGraceUntilUtc = null;
        _monitoringSnapshot = snapshot;
        if (instanceChanged) { InvalidateDashboardHistory("后台实例已变更；等待重新读取真实历史。"); }
        ApplyMonitoringGroups();
        if (instanceChanged && _coreMonitoringReads == 0 && AutoRefresh && _presentationActive)
        { _ = QueueDashboardHistoryAsync(automatic: true); }
        if (previousCapability != (snapshot.InstanceId, snapshot.RecentResourceSchemaVersion, snapshot.ResourceSamplingIntervalMilliseconds))
        { RefreshCapabilityChanged(); }
    }

    private bool ConfigurationMatchesMonitoring => _monitoringConfiguration is { } configuration
        && configuration.Revision == _configuration.Revision
        && configuration.Revision == _monitoringSnapshot?.ConfigurationRevision;

    private bool GroupFresh<T>(MonitoringDataGroup<T>? group) where T : class => group is
        { Available: true, Data: not null, ObservedAt: { } observed }
        && Fresh(observed) && (group.AttemptedAt is not { } attempted || Fresh(attempted));

    private void ApplyMonitoringGroups()
    {
        if (_applyingMonitoring) { return; }
        _applyingMonitoring = true;
        var tasksChangedForRealtime = false;
        try
        {
            var beforeTasksObservedAt = _appliedTasksObservedAt;
            var beforeSelectedStatus = SelectedTask?.Status;
            var snapshot = _monitoringSnapshot;
            _tasksAvailable = ConfigurationMatchesMonitoring && GroupFresh(snapshot?.Tasks);
            _processesAvailable = _tasksAvailable && GroupFresh(snapshot?.Processes);
            _hostAvailable = GroupFresh(snapshot?.Host) && snapshot!.Host.Data is { } host && Fresh(host.CapturedAt);
            var definitions = _monitoringConfiguration?.Configuration.Tasks;
            var rows = definitions is not null
                ? definitions.Select(task =>
                {
                    var identity = MonitoringTaskIdentity.For(task);
                    var observed = _tasksAvailable ? snapshot!.Tasks.Data?.FirstOrDefault(row =>
                        string.Equals(row.Name, task.Name, StringComparison.OrdinalIgnoreCase)
                        && MonitoringTaskIdentity.For(row) == identity) : null;
                    return observed ?? TaskSnapshot.From(task,
                        HealthResult.Unknown("monitoring-unavailable", "等待当前配置的有效后台观测。"));
                }).ToArray()
                : Tasks.Select(row => Unobserved(row.Snapshot, "monitoring-unavailable", "当前观测不可用。")).ToArray();
            // Health changes every ten seconds. Skip unchanged resource-only ticks, but never
            // reuse a prior Agent's rows when a restart happens at the same wall-clock timestamp.
            var observedAt = _tasksAvailable ? snapshot!.Tasks.ObservedAt : null;
            if (observedAt is null || observedAt != _appliedTasksObservedAt
                || snapshot?.InstanceId != _appliedTasksInstanceId || !ReferenceEquals(_monitoringConfiguration, _appliedTasksConfiguration))
            { ReplaceTasks(rows, notifyFailures: _notificationsEnabled && _tasksAvailable); }
            _appliedTasksObservedAt = observedAt;
            _appliedTasksInstanceId = snapshot?.InstanceId;
            _appliedTasksConfiguration = _monitoringConfiguration;

            var selectedName = SelectedManagedProcess?.TaskName;
            ManagedProcesses.Clear();
            if (_processesAvailable)
            {
                foreach (var process in snapshot!.Processes.Data!)
                {
                    if (Fresh(process.CapturedAt) && rows.Any(task => task.Type == process.Type
                        && string.Equals(task.Name, process.TaskName, StringComparison.OrdinalIgnoreCase)
                        && MonitoringTaskIdentity.For(task) == process.TaskIdentity))
                    { ManagedProcesses.Add(new ManagedProcessRowViewModel(process)); }
                }
            }
            ManagedProcessesView.Refresh();
            SelectedManagedProcess = ManagedProcesses.FirstOrDefault(row =>
                string.Equals(row.TaskName, selectedName, StringComparison.OrdinalIgnoreCase)) ?? ManagedProcesses.FirstOrDefault();
            // Every enabled workload is sampled, but the "常驻" assurance counts only user-tracked ones.
            var tracked = ManagedProcesses.Where(row => row.Snapshot.ExpectedRunning).ToArray();
            ManagedProcessCount = tracked.Length;
            HealthyManagedProcessCount = tracked.Count(row => row.Snapshot.State == "OK");
            FailedManagedProcessCount = tracked.Count(row => row.Snapshot.State == "FAIL");
            ManagedProcessStatusText = !_processesAvailable
                ? $"常驻进程读取失败：{snapshot?.Processes.Error ?? "当前配置的进程观测不可用"}"
                : ManagedProcessCount == 0 ? "尚未指定应持续运行的工作负载。"
                : $"观测于 {snapshot!.Processes.ObservedAt?.ToLocalTime():HH:mm:ss} · {HealthyManagedProcessCount} 正常 / {FailedManagedProcessCount} 异常";

            _lastLocalHostSnapshot = _hostAvailable ? snapshot!.Host.Data : null;
            if (IsLocalHostSelected)
            {
                if (_lastLocalHostSnapshot is { } currentHost) { ApplyHostStatus(currentHost); }
                else { ClearLocalHostObservation(snapshot?.Host.Error ?? "主机观测不可用或已过期"); }
            }
            if (ConfigurationMatchesMonitoring && snapshot?.RemoteHosts is { Count: > 0 } remoteSnapshots)
            {
                foreach (var tab in Hosts)
                {
                    if (tab.IsLocal || tab.Definition is not { Enabled: true } definition) { continue; }
                    var matched = remoteSnapshots.FirstOrDefault(item =>
                        string.Equals(item.Id, definition.Id, StringComparison.OrdinalIgnoreCase)
                        && item.Online && item.Platform == definition.Platform
                        && string.Equals(item.Host, definition.Host, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(item.Name, definition.Name, StringComparison.Ordinal)
                        && RemoteObservationFresh(item.CapturedAt));
                    if (matched is null) { continue; }
                    var newer = tab.RemoteSnapshot is not { Online: true } existing || matched.CapturedAt >= existing.CapturedAt;
                    if (!ReferenceEquals(SelectedHostTab, tab))
                    {
                        if (newer) { tab.ApplyRemoteStatus(matched); }
                    }
                    else if (_remoteCancellation is null && (!HostStatusAvailable || newer))
                    {
                        _remoteResumeGraceUntilUtc = null;
                        tab.ApplyRemoteStatus(matched);
                        ApplyRemoteHostStatus(matched);
                    }
                }
            }
            _monitoringProblem = snapshot is null ? _monitoringProblem ?? "后台未连接"
                : !_tasksAvailable ? snapshot.Tasks.Error ?? "任务观测不可用，或配置正在重新采样"
                : !_processesAvailable ? snapshot.Processes.Error ?? "进程观测不可用"
                : !_hostAvailable ? snapshot.Host.Error ?? "主机观测不可用" : null;
            UpdateResourceAlerts();
            UpdateServerStatus();
            tasksChangedForRealtime = !DashboardHistoryMode
                && (_appliedTasksObservedAt != beforeTasksObservedAt || SelectedTask?.Status != beforeSelectedStatus);
        }
        finally { _applyingMonitoring = false; }
        TickDashboard();
        if (tasksChangedForRealtime) { NotifyHistoryChanged(); }
    }

    private void ExpireMonitoringGroups()
    {
        if (_applyingMonitoring || _monitoringSnapshot is not { } snapshot) { return; }
        var tasks = ConfigurationMatchesMonitoring && GroupFresh(snapshot.Tasks);
        var processes = tasks && GroupFresh(snapshot.Processes);
        var host = GroupFresh(snapshot.Host) && snapshot.Host.Data is { } data && Fresh(data.CapturedAt);
        if (tasks != _tasksAvailable || processes != _processesAvailable || host != _hostAvailable)
        { ApplyMonitoringGroups(); }
        else { UpdateResourceAlerts(); }
    }

    internal void UpdateResourceAlerts()
    {
        var localTab = Hosts.FirstOrDefault(host => host.IsLocal);
        var hasLocalHost = _hostAvailable && _lastLocalHostSnapshot is { } local;
        if (hasLocalHost)
        {
            var localSnapshot = _lastLocalHostSnapshot!;
            var highCpu = TrackSustainedHighCpu("host:local", true, localSnapshot.CpuUsagePercent, localSnapshot.CapturedAt);
            var highMem = localSnapshot.TotalMemoryBytes > 0 && localSnapshot.MemoryUsedPercent >= HostHighMemoryThresholdPercent;
            localTab?.UpdateResourceAlert(highCpu || highMem,
                FormatHostResourceSummary(highCpu, localSnapshot.CpuUsagePercent, highMem, localSnapshot.MemoryUsedPercent));
        }
        else
        {
            _highCpuSince.Remove("host:local");
            localTab?.UpdateResourceAlert(false, string.Empty);
        }

        foreach (var tab in Hosts)
        {
            if (tab.IsLocal) { continue; }
            var key = "host:remote:" + tab.Id;
            if (tab.RemoteSnapshot is { Online: true } remote && IsRemoteObservationFresh(remote.CapturedAt))
            {
                var highCpu = TrackSustainedHighCpu(key, true, remote.CpuUsagePercent, remote.CapturedAt);
                var highMem = remote.TotalMemoryBytes > 0 && remote.MemoryUsedPercent >= HostHighMemoryThresholdPercent;
                tab.UpdateResourceAlert(highCpu || highMem,
                    FormatHostResourceSummary(highCpu, remote.CpuUsagePercent, highMem, remote.MemoryUsedPercent));
            }
            else
            {
                _highCpuSince.Remove(key);
                tab.UpdateResourceAlert(false, string.Empty);
            }
        }

        var localTotalMemory = hasLocalHost ? _lastLocalHostSnapshot!.TotalMemoryBytes : 0;
        foreach (var row in Tasks)
        {
            var identity = MonitoringTaskIdentity.For(row.Snapshot);
            var key = "task:" + identity;
            var proc = ManagedProcessForAlert(row, identity);
            if (proc is not null)
            {
                var highCpu = TrackSustainedHighCpu(key, proc.CpuUsageAvailable, proc.CpuUsagePercent, proc.CapturedAt);
                double? memPct = localTotalMemory > 0 ? proc.WorkingSetBytes * 100d / localTotalMemory : null;
                var highMem = proc.WorkingSetBytes >= ProcessHighMemoryThresholdBytes
                    || memPct is >= ProcessHighMemoryThresholdPercent;
                row.UpdateResourceAlert(highCpu || highMem,
                    FormatProcessResourceSummary(highCpu, proc.CpuUsagePercent, highMem, proc.WorkingSetBytes, memPct));
            }
            else
            {
                _highCpuSince.Remove(key);
                row.UpdateResourceAlert(false, string.Empty);
            }
        }
    }

    private ManagedProcessSnapshot? ManagedProcessForAlert(TaskRowViewModel row, string identity)
    {
        if (!_processesAvailable || row.Type is not ("windows" or "windows_service" or "wsl_boot" or "wsl_systemd" or "wsl_command")
            || ManagedProcessStatusText.StartsWith("常驻进程读取失败", StringComparison.Ordinal))
        {
            return null;
        }

        var process = ManagedProcesses.FirstOrDefault(item =>
            string.Equals(item.TaskName, row.Name, StringComparison.OrdinalIgnoreCase))?.Snapshot;
        return process is { ProcessCount: > 0 }
            && string.Equals(process.Type, row.Type, StringComparison.Ordinal)
            && process.TaskIdentity == identity
            && Fresh(process.CapturedAt) ? process : null;
    }

    private bool TrackSustainedHighCpu(string key, bool available, double cpuPercent, DateTimeOffset observedAt)
    {
        if (!available || double.IsNaN(cpuPercent) || double.IsInfinity(cpuPercent) || cpuPercent < HighCpuResetThresholdPercent)
        {
            _highCpuSince.Remove(key);
            return false;
        }

        if (cpuPercent >= HighCpuThresholdPercent)
        {
            if (!_highCpuSince.TryGetValue(key, out var since) || observedAt < since)
            {
                _highCpuSince[key] = observedAt;
                return false;
            }

            return (observedAt - since) >= HighCpuSustainDuration;
        }

        return _highCpuSince.TryGetValue(key, out var existing) && (observedAt - existing) >= HighCpuSustainDuration;
    }

    private void SeedHighCpuFromRecentPlot(string key, IReadOnlyList<PlotPoint>? points, DateTimeOffset queryTo)
    {
        if (points is not { Count: > 0 }) { return; }
        DateTimeOffset? tailLatest = null;
        DateTimeOffset? tailEarliest = null;
        var hasAboveTrigger = false;
        var freshnessFloor = queryTo.AddSeconds(-30);
        for (var i = points.Count - 1; i >= 0; i--)
        {
            var point = points[i];
            if (point.Value is not { } value || value < HighCpuResetThresholdPercent) { break; }
            if (tailEarliest is { } nextAt && (nextAt - point.At) > TimeSpan.FromSeconds(30)) { break; }
            if (tailLatest is null && point.At < freshnessFloor) { break; }
            tailLatest ??= point.At;
            tailEarliest = point.At;
            if (value >= HighCpuThresholdPercent) { hasAboveTrigger = true; }
            if (point.BreakBefore) { break; }
        }

        if (hasAboveTrigger && tailLatest is { } latest && tailEarliest is { } earliest
            && (latest - earliest) >= HighCpuSustainDuration)
        {
            if (!_highCpuSince.TryGetValue(key, out var recorded) || earliest < recorded)
            {
                _highCpuSince[key] = earliest;
            }
        }
    }

    private static string FormatHostResourceSummary(bool highCpu, double cpuPercent, bool highMem, double memoryPercent)
    {
        if (!highCpu && !highMem) { return string.Empty; }
        var parts = new List<string>(2);
        if (highCpu) { parts.Add($"CPU 持续高占用 ({cpuPercent:0.0}% ≥ 1m)"); }
        if (highMem) { parts.Add($"内存高占用 ({memoryPercent:0.0}%)"); }
        return "资源高占用：" + string.Join(" · ", parts);
    }

    private static string FormatProcessResourceSummary(bool highCpu, double cpuPercent, bool highMem, long workingSetBytes, double? memoryPercent)
    {
        if (!highCpu && !highMem) { return string.Empty; }
        var parts = new List<string>(2);
        if (highCpu) { parts.Add($"CPU 持续高占用 ({cpuPercent:0.0}% ≥ 1m)"); }
        if (highMem)
        {
            parts.Add(memoryPercent is { } pct
                ? $"内存高占用 ({FormatBytes(workingSetBytes)} / {pct:0.0}%)"
                : $"内存高占用 ({FormatBytes(workingSetBytes)})");
        }
        return "资源高占用：" + string.Join(" · ", parts);
    }

    private TaskRuntimeObservation? RuntimeFor(TaskRowViewModel? task)
    {
        if (!IsLocalHostSelected || task is null || !_tasksAvailable || _monitoringSnapshot is not { } snapshot
            || !GroupFresh(snapshot.Tasks) || !ConfigurationMatchesMonitoring) { return null; }
        var identity = MonitoringTaskIdentity.For(task.Snapshot);
        return snapshot.Runtime.FirstOrDefault(row => row.TaskIdentity == identity
            && string.Equals(row.TaskName, task.Name, StringComparison.OrdinalIgnoreCase) && Fresh(row.ObservedAt));
    }

    private static TaskSnapshot Unobserved(TaskSnapshot snapshot, string reason, string detail) => snapshot with
    { Status = snapshot.Enabled ? "UNKNOWN" : "OFF", Healthy = false, HealthObserved = false, Reason = reason, Detail = detail };

    private void ClearLocalHostObservation(string reason)
    {
        HostName = Environment.MachineName;
        HostConnectionText = string.Empty;
        HostStatusAvailable = false;
        HostOs = "主机资源数据不可用";
        HostUptime = HostLogicalProcessorsText = HostProcessCountText = HostThreadCountText = "--";
        CpuUsagePercent = MemoryUsagePercent = StorageUsagePercent = 0;
        CpuUsageText = MemoryUsageText = StorageUsageText = StorageCapacityText = NetworkAddress = NetworkRateText = "--";
        StorageDetail = NetworkDetail = "当前观测未知。";
        HostUpdatedText = reason;
        Drives.Clear();
        Networks.Clear();
        NotifyHostResourceReadouts();
    }
}
