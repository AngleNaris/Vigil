using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Recovery;
using SystemAutorun.Windows.Execution;

namespace SystemAutorun.Agent;

internal sealed class AgentAutoRestartWorker : IDisposable
{
    internal static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(10);
    private const string StateFileName = "auto-restart-state.json";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly ConfigurationWorkspace _workspace;
    private readonly AgentTaskController _tasks;
    private readonly ProcessMaintenanceExecutor _maintenance;
    private readonly FileLog _fileLog;
    private readonly Func<AgentMonitoringSnapshot?>? _monitoringSnapshotProvider;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _localTimeZone;
    private readonly TimeSpan _tickInterval;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private const int MaxSamplesPerRule = 90;
    private readonly Dictionary<(int Pid, long CreateTime), (DateTimeOffset CapturedAt, long CpuTicks)> _cpuSamples = new();
    private readonly Dictionary<(int Pid, long CreateTime), ProcessHandleActivity> _handleActivity = new();
    private readonly Dictionary<string, DateTimeOffset> _breachedSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PersistedRuleState> _persisted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LiveRuleMetrics> _liveMetrics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RuleTelemetryHistory> _telemetryHistory = new(StringComparer.OrdinalIgnoreCase);
    private bool _stateLoaded;

    private sealed class ProcessHandleActivity
    {
        public DateTimeOffset LastActiveUtc { get; set; }
        public int MinHandleCount { get; set; }
        public DateTimeOffset LastHandleDropUtc { get; set; }
        public bool IsLongIdle { get; set; }
        public int ConsecutiveActiveTicks { get; set; }
        public int ConsecutiveIdleTicks { get; set; }
        public int LastComputedInactive { get; set; }
    }

    private sealed class RuleTelemetryHistory
    {
        public double? PeakCpuPercent { get; set; }
        public long? PeakWorkingSetBytes { get; set; }
        public int? PeakHandleCount { get; set; }
        public int? PeakInactiveHandleCount { get; set; }
        public Queue<AutoRestartMetricSample> Samples { get; } = new(MaxSamplesPerRule + 1);
        public DateTimeOffset LastSampleRecordedUtc { get; set; }

        public void ResetPeaks()
        {
            PeakCpuPercent = null;
            PeakWorkingSetBytes = null;
            PeakHandleCount = null;
            PeakInactiveHandleCount = null;
        }
    }

    internal sealed record PersistedRuleState
    {
        public DateTimeOffset ArmedAtUtc { get; init; }
        public DateTimeOffset? LastTriggeredAtUtc { get; init; }
        public string? LastTriggerReason { get; init; }
        public bool? LastSucceeded { get; init; }
        public string? LastMessage { get; init; }
        public string? LastDailyRunLocalDate { get; init; }
    }

    private sealed record PersistedStateDocument(
        int SchemaVersion,
        Dictionary<string, PersistedRuleState> Rules);

    private sealed record LiveRuleMetrics(
        int MatchedProcessCount,
        double? CpuPercent,
        long? WorkingSetBytes,
        int? HandleCount,
        long? OsPeakWorkingSetBytes,
        int? InactiveHandleCount,
        int? ThreadCount,
        int? GdiHandleCount,
        int? UserHandleCount);

    internal AgentAutoRestartWorker(
        ConfigurationWorkspace workspace,
        AgentTaskController tasks,
        ProcessMaintenanceExecutor maintenance,
        FileLog fileLog,
        Func<AgentMonitoringSnapshot?>? monitoringSnapshotProvider = null,
        TimeProvider? clock = null,
        TimeZoneInfo? localTimeZone = null,
        TimeSpan? tickInterval = null)
    {
        _workspace = workspace;
        _tasks = tasks;
        _maintenance = maintenance;
        _fileLog = fileLog;
        _monitoringSnapshotProvider = monitoringSnapshotProvider;
        _clock = clock ?? TimeProvider.System;
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
        _tickInterval = tickInterval ?? DefaultTickInterval;
    }

    internal async Task RunAsync(CancellationToken token)
    {
        try
        {
            await EvaluateOnceAsync(token).ConfigureAwait(false);
            using var timer = new PeriodicTimer(_tickInterval, _clock);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await EvaluateOnceAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    internal async Task<AutoRestartStatusSnapshot> GetStatusAsync(CancellationToken token = default)
    {
        await _runGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureStateLoadedAsync(token).ConfigureAwait(false);
            var workspace = await _workspace.ReadAsync(token).ConfigureAwait(false);
            var settings = workspace.Preferences.AutoRestartSettings ?? new AutoRestartSettings();
            var now = _clock.GetUtcNow();
            var allProcesses = _maintenance.ReadAllProcessMetrics();
            UpdateLiveMetrics(workspace, settings, allProcesses, now);
            return BuildStatusSnapshot(settings, now);
        }
        finally
        {
            _runGate.Release();
        }
    }

    internal async Task<AutoRestartRunResult> RunRuleNowAsync(string ruleId, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        await _runGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureStateLoadedAsync(token).ConfigureAwait(false);
            var workspace = await _workspace.ReadAsync(token).ConfigureAwait(false);
            var settings = workspace.Preferences.AutoRestartSettings ?? new AutoRestartSettings();
            var rule = settings.Rules.FirstOrDefault(item => string.Equals(item.Id, ruleId.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"Auto-restart rule not found: {ruleId}");
            var now = _clock.GetUtcNow();
            return await ExecuteAndRecordAsync(workspace, rule, "手动触发", isDailyTrigger: false, now, token).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    internal async Task EvaluateOnceAsync(CancellationToken token = default)
    {
        await _runGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureStateLoadedAsync(token).ConfigureAwait(false);
            ConfigurationWorkspaceSnapshot workspace;
            try
            {
                workspace = await _workspace.ReadAsync(token).ConfigureAwait(false);
            }
            catch (Exception exception) when (!token.IsCancellationRequested && exception is not OutOfMemoryException)
            {
                return;
            }

            var settings = workspace.Preferences.AutoRestartSettings ?? new AutoRestartSettings();
            var now = _clock.GetUtcNow();
            var activeRuleIds = new HashSet<string>(settings.Rules.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
            var stateDirty = PruneRemovedRules(activeRuleIds);

            if (settings.Rules.Count == 0)
            {
                _breachedSince.Clear();
                if (stateDirty) { await SaveStateAsync(token).ConfigureAwait(false); }
                return;
            }

            // Always collect passive telemetry (peaks, inactive handles, trend samples) for configured rules
            // so the Automation dashboard displays live curves even when auto-triggering is paused.
            var allProcesses = _maintenance.ReadAllProcessMetrics();
            UpdateLiveMetrics(workspace, settings, allProcesses, now);

            if (!settings.Enabled)
            {
                _breachedSince.Clear();
                if (stateDirty) { await SaveStateAsync(token).ConfigureAwait(false); }
                return;
            }

            RecoveryControlSnapshot? recoveryState = null;
            if (settings.Rules.Any(r => r.Enabled && r.TargetKind == AutoRestartTargetKind.ManagedTask))
            {
                try { recoveryState = await _tasks.ReadRecoveryAsync(token).ConfigureAwait(false); }
                catch (Exception) { recoveryState = null; }
            }

            foreach (var rule in settings.Rules)
            {
                token.ThrowIfCancellationRequested();
                if (!rule.Enabled)
                {
                    _breachedSince.Remove(rule.Id);
                    continue;
                }

                if (!_persisted.TryGetValue(rule.Id, out var persisted))
                {
                    persisted = new PersistedRuleState { ArmedAtUtc = now };
                    _persisted[rule.Id] = persisted;
                    stateDirty = true;
                }

                if (persisted.LastTriggeredAtUtc is { } lastRun
                    && now - lastRun < TimeSpan.FromMinutes(rule.CooldownMinutes))
                {
                    _breachedSince.Remove(rule.Id);
                    continue;
                }

                if (rule.TargetKind == AutoRestartTargetKind.ManagedTask)
                {
                    var taskDef = workspace.Configuration.Tasks.FirstOrDefault(t =>
                        string.Equals(t.Name, rule.TaskName, StringComparison.OrdinalIgnoreCase));
                    if (taskDef is null || !taskDef.Enabled)
                    {
                        _breachedSince.Remove(rule.Id);
                        continue;
                    }

                    var recoveryEntry = recoveryState?.Tasks.FirstOrDefault(e =>
                        string.Equals(e.TaskName, taskDef.Name, StringComparison.OrdinalIgnoreCase));
                    if (recoveryEntry?.Paused == true)
                    {
                        _breachedSince.Remove(rule.Id);
                        continue;
                    }
                }

                var trigger = EvaluateRuleTrigger(rule, persisted, now);
                if (trigger is null)
                {
                    continue;
                }

                _breachedSince.Remove(rule.Id);
                await ExecuteAndRecordAsync(
                    workspace,
                    rule,
                    trigger.Value.Reason,
                    trigger.Value.IsDailySchedule,
                    now,
                    token).ConfigureAwait(false);
                stateDirty = false; // ExecuteAndRecordAsync persists state immediately.
            }

            if (stateDirty)
            {
                await SaveStateAsync(token).ConfigureAwait(false);
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    private (string Reason, bool IsDailySchedule)? EvaluateRuleTrigger(
        AutoRestartRule rule,
        PersistedRuleState persisted,
        DateTimeOffset nowUtc)
    {
        if (rule.HasScheduleTrigger)
        {
            var next = ComputeNextScheduledAt(
                rule,
                nowUtc,
                persisted.ArmedAtUtc,
                persisted.LastTriggeredAtUtc,
                persisted.LastDailyRunLocalDate,
                _localTimeZone);
            if (next is not null && nowUtc >= next.Value)
            {
                var label = rule.ScheduleKind == AutoRestartScheduleKind.Daily
                    ? $"定时触发（每日 {rule.DailyTime}）"
                    : $"定时触发（间隔 {rule.IntervalMinutes} 分钟）";
                return (label, rule.ScheduleKind == AutoRestartScheduleKind.Daily);
            }
        }

        if (rule.HasResourceTrigger && _liveMetrics.TryGetValue(rule.Id, out var live) && live.MatchedProcessCount > 0)
        {
            var reasons = new List<string>(3);
            if (rule.CpuPercentThreshold is > 0 and var cpuLimit && live.CpuPercent is { } cpu && cpu >= cpuLimit)
            {
                reasons.Add($"CPU {cpu:0.0}% >= {cpuLimit:0.#}%");
            }

            if (rule.MemoryMbThreshold is > 0 and var memLimit && live.WorkingSetBytes is { } bytes)
            {
                var memMb = bytes / (1024.0 * 1024.0);
                if (memMb >= memLimit)
                {
                    reasons.Add($"内存 {memMb:0} MB >= {memLimit} MB");
                }
            }

            if (rule.HandleCountThreshold is > 0 and var handleLimit && live.HandleCount is { } handles && handles >= handleLimit)
            {
                reasons.Add($"句柄 {handles} >= {handleLimit}");
            }

            if (rule.InactiveHandleCountThreshold is > 0 and var inactiveLimit
                && live.InactiveHandleCount is { } inactiveHandles && inactiveHandles >= inactiveLimit)
            {
                reasons.Add($"不活跃句柄 {inactiveHandles} >= {inactiveLimit}");
            }

            if (reasons.Count > 0)
            {
                if (!_breachedSince.TryGetValue(rule.Id, out var since))
                {
                    _breachedSince[rule.Id] = nowUtc;
                    since = nowUtc;
                }

                if (nowUtc - since >= TimeSpan.FromSeconds(rule.SustainSeconds))
                {
                    return ($"资源超标持续 {(int)(nowUtc - since).TotalSeconds}s（{string.Join("，", reasons)}）", false);
                }
            }
            else
            {
                _breachedSince.Remove(rule.Id);
            }
        }
        else
        {
            _breachedSince.Remove(rule.Id);
        }

        return null;
    }

    private async Task<AutoRestartRunResult> ExecuteAndRecordAsync(
        ConfigurationWorkspaceSnapshot workspace,
        AutoRestartRule rule,
        string reason,
        bool isDailyTrigger,
        DateTimeOffset nowUtc,
        CancellationToken token)
    {
        AutoRestartRunResult result;
        try
        {
            if (rule.TargetKind == AutoRestartTargetKind.ManagedTask)
            {
                var commandResult = await _tasks.RestartAsync(rule.TaskName!, token).ConfigureAwait(false);
                result = new AutoRestartRunResult(
                    rule.Id,
                    commandResult.Succeeded,
                    commandResult.Message,
                    commandResult.Succeeded ? 1 : 0,
                    UsedElevation: false,
                    _clock.GetUtcNow());
            }
            else
            {
                result = await _maintenance.ExecuteProcessRuleAsync(_workspace.Paths, rule, token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            result = new AutoRestartRunResult(
                rule.Id,
                false,
                $"执行异常：{exception.Message}",
                0,
                rule.RequireElevation,
                _clock.GetUtcNow());
        }

        var localDate = TimeZoneInfo.ConvertTime(nowUtc, _localTimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var existing = _persisted.GetValueOrDefault(rule.Id);
        _persisted[rule.Id] = new PersistedRuleState
        {
            ArmedAtUtc = existing?.ArmedAtUtc ?? nowUtc,
            LastTriggeredAtUtc = nowUtc,
            LastTriggerReason = reason,
            LastSucceeded = result.Succeeded,
            LastMessage = result.Message,
            LastDailyRunLocalDate = isDailyTrigger ? localDate : existing?.LastDailyRunLocalDate,
        };
        await SaveStateAsync(token).ConfigureAwait(false);
        if (result.Succeeded && _telemetryHistory.TryGetValue(rule.Id, out var history))
        {
            history.ResetPeaks();
        }

        var level = result.Succeeded ? "WARN" : "ERROR";
        try
        {
            await _fileLog.WriteAsync(
                workspace.Configuration.Settings.LogDirectory,
                "auto-restart",
                $"[{level}] 规则「{rule.Name}」({rule.Id}) {reason} -> {(result.Succeeded ? "成功" : "失败")}：{result.Message}",
                token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        return result;
    }

    private void UpdateLiveMetrics(
        ConfigurationWorkspaceSnapshot workspace,
        AutoRestartSettings settings,
        IReadOnlyDictionary<int, ObservedProcessMatch> allProcesses,
        DateTimeOffset nowUtc)
    {
        var activeKeys = new HashSet<(int Pid, long CreateTime)>();
        var cpuCount = Math.Max(1, Environment.ProcessorCount);
        var monitoringSnapshot = _monitoringSnapshotProvider?.Invoke();

        foreach (var rule in settings.Rules)
        {
            LiveRuleMetrics metrics;
            if (rule.TargetKind == AutoRestartTargetKind.ManagedTask)
            {
                var managed = monitoringSnapshot?.Processes.Data?.FirstOrDefault(p =>
                    string.Equals(p.TaskName, rule.TaskName, StringComparison.OrdinalIgnoreCase));
                if (managed is not null && managed.Processes.Count > 0)
                {
                    var totalCpu = managed.Processes.Where(p => p.CpuUsageAvailable).Sum(p => p.CpuUsagePercent);
                    var hasCpu = managed.Processes.Any(p => p.CpuUsageAvailable);
                    var totalMem = managed.Processes.Sum(p => p.WorkingSetBytes);
                    long osPeakMem = totalMem;
                    var totalHandles = 0;
                    var inactiveHandles = 0;
                    var totalThreads = 0;
                    var gdiSum = 0;
                    var userSum = 0;
                    var hasGui = false;
                    foreach (var p in managed.Processes)
                    {
                        if (allProcesses.TryGetValue(p.ProcessId, out var metric))
                        {
                            var key = (metric.ProcessId, metric.CreateFileTimeUtc);
                            activeKeys.Add(key);
                            totalHandles += metric.HandleCount;
                            osPeakMem += Math.Max(0, metric.PeakWorkingSetBytes - metric.WorkingSetBytes);
                            totalThreads += metric.ThreadCount;
                            inactiveHandles += ComputeInactiveHandlesForProcess(key, metric.HandleCount, p.CpuUsageAvailable ? p.CpuUsagePercent : null, nowUtc);
                            if (ProcessMaintenanceExecutor.TryReadGuiResourceCounts(metric.ProcessId, out var gdi, out var user))
                            {
                                gdiSum += gdi;
                                userSum += user;
                                hasGui = true;
                            }
                        }
                    }

                    metrics = new LiveRuleMetrics(
                        managed.Processes.Count,
                        hasCpu ? Math.Round(Math.Clamp(totalCpu, 0, 100), 1) : null,
                        totalMem,
                        totalHandles,
                        Math.Max(totalMem, osPeakMem),
                        inactiveHandles,
                        totalThreads,
                        hasGui ? gdiSum : null,
                        hasGui ? userSum : null);
                }
                else
                {
                    metrics = new LiveRuleMetrics(0, null, null, null, null, null, null, null, null);
                }
            }
            else
            {
                var matches = _maintenance.MatchProcesses(rule, allProcesses);
                if (matches.Count == 0)
                {
                    metrics = new LiveRuleMetrics(0, null, null, null, null, null, null, null, null);
                }
                else
                {
                    double cpuSum = 0;
                    var hasCpuSample = false;
                    long memSum = 0;
                    long osPeakMemSum = 0;
                    var totalHandles = 0;
                    var inactiveHandles = 0;
                    var totalThreads = 0;
                    var gdiSum = 0;
                    var userSum = 0;
                    var hasGui = false;
                    foreach (var match in matches)
                    {
                        var key = (match.ProcessId, match.CreateFileTimeUtc);
                        activeKeys.Add(key);
                        double? procCpu = null;
                        if (_cpuSamples.TryGetValue(key, out var previous))
                        {
                            var dt = (nowUtc - previous.CapturedAt).TotalSeconds;
                            var deltaTicks = match.TotalProcessorTimeTicks - previous.CpuTicks;
                            if (dt > 0.2 && deltaTicks >= 0)
                            {
                                var pct = (deltaTicks / (double)TimeSpan.TicksPerSecond) / (dt * cpuCount) * 100.0;
                                procCpu = Math.Clamp(pct, 0, 100);
                                cpuSum += procCpu.Value;
                                hasCpuSample = true;
                            }
                        }

                        _cpuSamples[key] = (nowUtc, match.TotalProcessorTimeTicks);
                        memSum += match.WorkingSetBytes;
                        osPeakMemSum += Math.Max(match.WorkingSetBytes, match.PeakWorkingSetBytes);
                        totalHandles += match.HandleCount;
                        totalThreads += match.ThreadCount;
                        inactiveHandles += ComputeInactiveHandlesForProcess(key, match.HandleCount, procCpu, nowUtc);
                        if (ProcessMaintenanceExecutor.TryReadGuiResourceCounts(match.ProcessId, out var gdi, out var user))
                        {
                            gdiSum += gdi;
                            userSum += user;
                            hasGui = true;
                        }
                    }

                    metrics = new LiveRuleMetrics(
                        matches.Count,
                        hasCpuSample ? Math.Round(Math.Clamp(cpuSum, 0, 100), 1) : null,
                        memSum,
                        totalHandles,
                        Math.Max(memSum, osPeakMemSum),
                        inactiveHandles,
                        totalThreads,
                        hasGui ? gdiSum : null,
                        hasGui ? userSum : null);
                }
            }

            _liveMetrics[rule.Id] = metrics;
            RecordRuleSample(rule, metrics, nowUtc);
        }

        // Remove stale per-process trackers for exited processes.
        if (_cpuSamples.Count > activeKeys.Count + 64)
        {
            var stale = _cpuSamples.Keys.Where(k => !activeKeys.Contains(k)).ToArray();
            foreach (var key in stale) { _cpuSamples.Remove(key); }
        }
        if (_handleActivity.Count > activeKeys.Count + 64)
        {
            var stale = _handleActivity.Keys.Where(k => !activeKeys.Contains(k)).ToArray();
            foreach (var key in stale) { _handleActivity.Remove(key); }
        }
    }

    private int ComputeInactiveHandlesForProcess(
        (int Pid, long CreateTime) key,
        int handleCount,
        double? cpuPercent,
        DateTimeOffset nowUtc)
    {
        if (handleCount <= 0) { return 0; }

        if (!_handleActivity.TryGetValue(key, out var activity))
        {
            var processAge = key.CreateTime > 0
                ? nowUtc - DateTimeOffset.FromFileTime(key.CreateTime)
                : TimeSpan.Zero;
            var startsIdle = cpuPercent is < 0.15 && processAge >= TimeSpan.FromMinutes(1);
            activity = new ProcessHandleActivity
            {
                LastActiveUtc = startsIdle ? nowUtc - TimeSpan.FromMinutes(2) : nowUtc,
                MinHandleCount = handleCount,
                LastHandleDropUtc = nowUtc,
                IsLongIdle = startsIdle,
                ConsecutiveActiveTicks = 0,
                ConsecutiveIdleTicks = startsIdle ? 3 : 0,
                LastComputedInactive = startsIdle ? handleCount : 0,
            };
            _handleActivity[key] = activity;
        }

        if (handleCount < activity.MinHandleCount)
        {
            activity.MinHandleCount = handleCount;
            activity.LastHandleDropUtc = nowUtc;
        }

        // Without a CPU delta yet (first sample), preserve the last stable value instead of jumping.
        if (cpuPercent is not { } cpu)
        {
            activity.LastComputedInactive = Math.Min(activity.LastComputedInactive, handleCount);
            return activity.LastComputedInactive;
        }

        // Apply hysteresis so minor background CPU blips never make inactive handles bounce between 0 and full handle count:
        // - Enter long-idle after sustained low CPU (< 0.25%) for >= 60s and >= 3 consecutive low samples.
        // - Exit long-idle only after sustained real activity (>= 0.8%) for >= 3 consecutive samples.
        if (cpu >= 0.8)
        {
            activity.LastActiveUtc = nowUtc;
            activity.ConsecutiveActiveTicks++;
            activity.ConsecutiveIdleTicks = 0;
            if (activity.IsLongIdle && activity.ConsecutiveActiveTicks >= 3)
            {
                activity.IsLongIdle = false;
            }
        }
        else if (cpu < 0.25)
        {
            activity.ConsecutiveIdleTicks++;
            activity.ConsecutiveActiveTicks = 0;
            if (!activity.IsLongIdle
                && activity.ConsecutiveIdleTicks >= 3
                && nowUtc - activity.LastActiveUtc >= TimeSpan.FromSeconds(60))
            {
                activity.IsLongIdle = true;
            }
        }
        else
        {
            // Deadband (0.25% .. 0.8%): hold current state steadily without flipping.
            activity.ConsecutiveActiveTicks = 0;
            activity.ConsecutiveIdleTicks = 0;
        }

        int computed;
        if (activity.IsLongIdle)
        {
            // Sustained idle/zombie process: all currently held handles are inactive.
            computed = handleCount;
        }
        else
        {
            // Active process: handles accumulated above observed low-water baseline without being released for >= 30s.
            var retainedAboveFloor = Math.Max(0, handleCount - activity.MinHandleCount);
            computed = nowUtc - activity.LastHandleDropUtc >= TimeSpan.FromSeconds(30) ? retainedAboveFloor : 0;
        }

        activity.LastComputedInactive = Math.Clamp(computed, 0, handleCount);
        return activity.LastComputedInactive;
    }

    private void RecordRuleSample(AutoRestartRule rule, LiveRuleMetrics metrics, DateTimeOffset nowUtc)
    {
        if (!_telemetryHistory.TryGetValue(rule.Id, out var history))
        {
            history = new RuleTelemetryHistory();
            _telemetryHistory[rule.Id] = history;
        }

        if (metrics.CpuPercent is { } cpu)
        {
            history.PeakCpuPercent = history.PeakCpuPercent is { } prev ? Math.Max(prev, cpu) : cpu;
        }

        var memCandidate = Math.Max(metrics.WorkingSetBytes ?? 0, metrics.OsPeakWorkingSetBytes ?? 0);
        if (memCandidate > 0)
        {
            history.PeakWorkingSetBytes = history.PeakWorkingSetBytes is { } prev ? Math.Max(prev, memCandidate) : memCandidate;
        }

        if (metrics.HandleCount is { } handles)
        {
            history.PeakHandleCount = history.PeakHandleCount is { } prev ? Math.Max(prev, handles) : handles;
        }

        if (metrics.InactiveHandleCount is { } inactive)
        {
            history.PeakInactiveHandleCount = history.PeakInactiveHandleCount is { } prev ? Math.Max(prev, inactive) : inactive;
        }

        // Throttle chart point recording to >= 1.5s so rapid UI polls don't flood the ring buffer.
        if (nowUtc - history.LastSampleRecordedUtc < TimeSpan.FromMilliseconds(1500))
        {
            return;
        }

        var breaching = IsMetricsBreaching(rule, metrics);
        history.Samples.Enqueue(new AutoRestartMetricSample(
            nowUtc,
            metrics.CpuPercent,
            metrics.WorkingSetBytes,
            metrics.HandleCount,
            metrics.InactiveHandleCount,
            metrics.MatchedProcessCount,
            breaching));
        while (history.Samples.Count > MaxSamplesPerRule)
        {
            _ = history.Samples.Dequeue();
        }
        history.LastSampleRecordedUtc = nowUtc;
    }

    private static bool IsMetricsBreaching(AutoRestartRule rule, LiveRuleMetrics live)
    {
        if (live.MatchedProcessCount <= 0) { return false; }
        if (rule.CpuPercentThreshold is > 0 and var cpuLimit && live.CpuPercent is { } cpu && cpu >= cpuLimit) { return true; }
        if (rule.MemoryMbThreshold is > 0 and var memLimit && live.WorkingSetBytes is { } bytes && bytes / (1024.0 * 1024.0) >= memLimit) { return true; }
        if (rule.HandleCountThreshold is > 0 and var handleLimit && live.HandleCount is { } handles && handles >= handleLimit) { return true; }
        if (rule.InactiveHandleCountThreshold is > 0 and var inactiveLimit && live.InactiveHandleCount is { } inactive && inactive >= inactiveLimit) { return true; }
        return false;
    }

    private AutoRestartStatusSnapshot BuildStatusSnapshot(AutoRestartSettings settings, DateTimeOffset nowUtc)
    {
        var maint = _maintenance.GetMaintenanceTaskStatus(_workspace.Paths.RootDirectory);
        var rows = new List<AutoRestartRuleStatus>(settings.Rules.Count);
        foreach (var rule in settings.Rules)
        {
            _persisted.TryGetValue(rule.Id, out var persisted);
            _liveMetrics.TryGetValue(rule.Id, out var live);
            _telemetryHistory.TryGetValue(rule.Id, out var history);
            var breachedSince = _breachedSince.TryGetValue(rule.Id, out var since) ? since : (DateTimeOffset?)null;
            var armedAt = persisted?.ArmedAtUtc ?? nowUtc;
            var nextScheduled = settings.Enabled && rule.Enabled
                ? ComputeNextScheduledAt(rule, nowUtc, armedAt, persisted?.LastTriggeredAtUtc, persisted?.LastDailyRunLocalDate, _localTimeZone)
                : null;
            DateTimeOffset? cooldownUntil = persisted?.LastTriggeredAtUtc is { } last
                && last.AddMinutes(rule.CooldownMinutes) > nowUtc
                ? last.AddMinutes(rule.CooldownMinutes)
                : null;
            rows.Add(new AutoRestartRuleStatus(
                rule.Id,
                rule.Name,
                rule.Enabled,
                live?.MatchedProcessCount ?? 0,
                live?.CpuPercent,
                live?.WorkingSetBytes,
                live?.HandleCount,
                breachedSince,
                persisted?.LastTriggeredAtUtc,
                persisted?.LastTriggerReason,
                persisted?.LastSucceeded,
                persisted?.LastMessage,
                nextScheduled,
                cooldownUntil,
                history?.PeakCpuPercent,
                history?.PeakWorkingSetBytes,
                history?.PeakHandleCount,
                live?.InactiveHandleCount,
                history?.PeakInactiveHandleCount,
                live?.ThreadCount,
                live?.GdiHandleCount,
                live?.UserHandleCount,
                history is { Samples.Count: > 0 } ? history.Samples.ToArray() : null));
        }

        return new AutoRestartStatusSnapshot(
            settings.Enabled,
            ProcessMaintenanceExecutor.IsCurrentProcessElevated(),
            maint.Installed && maint.MatchesRoot,
            maint.Installed && !maint.MatchesRoot
                ? "已安装的维护任务指向其他工作区，请重新启用提权通道。"
                : maint.Error,
            nowUtc,
            rows);
    }

    internal static DateTimeOffset? ComputeNextScheduledAt(
        AutoRestartRule rule,
        DateTimeOffset nowUtc,
        DateTimeOffset armedAtUtc,
        DateTimeOffset? lastTriggeredAtUtc,
        string? lastDailyRunLocalDate,
        TimeZoneInfo localTimeZone)
    {
        if (rule.ScheduleKind == AutoRestartScheduleKind.None)
        {
            return null;
        }

        if (rule.ScheduleKind == AutoRestartScheduleKind.Interval)
        {
            var minutes = rule.IntervalMinutes ?? AutoRestartRule.DefaultIntervalMinutes;
            var baseline = lastTriggeredAtUtc ?? armedAtUtc;
            return baseline.AddMinutes(minutes);
        }

        if (rule.ScheduleKind == AutoRestartScheduleKind.Daily
            && AutoRestartSettingsValidator.TryParseDailyTime(rule.DailyTime, out var targetTime))
        {
            var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, localTimeZone);
            var armedLocal = TimeZoneInfo.ConvertTime(armedAtUtc, localTimeZone);
            var todayStr = nowLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var todaySlot = ToOffset(nowLocal.Date.Add(targetTime.ToTimeSpan()), localTimeZone);
            if (string.Equals(lastDailyRunLocalDate, todayStr, StringComparison.Ordinal)
                || armedLocal > todaySlot)
            {
                return ToOffset(nowLocal.Date.AddDays(1).Add(targetTime.ToTimeSpan()), localTimeZone).ToUniversalTime();
            }

            return todaySlot.ToUniversalTime();
        }

        return null;
    }

    private static DateTimeOffset ToOffset(DateTime localUnspecified, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(localUnspecified, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        return new DateTimeOffset(unspecified, tz.GetUtcOffset(unspecified));
    }

    private bool PruneRemovedRules(HashSet<string> activeRuleIds)
    {
        var removed = false;
        foreach (var id in _persisted.Keys.Where(id => !activeRuleIds.Contains(id)).ToArray())
        {
            _persisted.Remove(id);
            removed = true;
        }

        foreach (var id in _breachedSince.Keys.Where(id => !activeRuleIds.Contains(id)).ToArray())
        {
            _breachedSince.Remove(id);
        }

        foreach (var id in _liveMetrics.Keys.Where(id => !activeRuleIds.Contains(id)).ToArray())
        {
            _liveMetrics.Remove(id);
        }

        foreach (var id in _telemetryHistory.Keys.Where(id => !activeRuleIds.Contains(id)).ToArray())
        {
            _telemetryHistory.Remove(id);
        }

        return removed;
    }

    private string StateFilePath =>
        Path.Combine(_workspace.Paths.RootDirectory, ".autorun-state", CurrentUserIdentity.Key, StateFileName);

    private async Task EnsureStateLoadedAsync(CancellationToken token)
    {
        if (_stateLoaded) { return; }
        _stateLoaded = true;
        try
        {
            await using var stream = SnapshotFile.OpenRead(StateFilePath);
            var doc = await JsonSerializer.DeserializeAsync<PersistedStateDocument>(stream, JsonOptions, token).ConfigureAwait(false);
            if (doc?.Rules is not null)
            {
                foreach (var (id, state) in doc.Rules)
                {
                    if (!string.IsNullOrWhiteSpace(id) && state is not null)
                    {
                        _persisted[id] = state;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or IOException or JsonException)
        {
        }
    }

    private async Task SaveStateAsync(CancellationToken token)
    {
        try
        {
            var directory = Path.GetDirectoryName(StateFilePath)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".{StateFileName}.{Guid.NewGuid():N}.tmp");
            var doc = new PersistedStateDocument(1, new Dictionary<string, PersistedRuleState>(_persisted, StringComparer.OrdinalIgnoreCase));
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(doc, JsonOptions);
                await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
                SnapshotFile.Publish(temporary, StateFilePath);
            }
            finally
            {
                try { if (File.Exists(temporary)) { File.Delete(temporary); } } catch { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    public void Dispose() => _runGate.Dispose();
}
