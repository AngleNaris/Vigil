using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.Agent;

internal sealed partial class AgentMonitor : IDisposable
{
    // Resource sampling targets half-second intervals; health, recovery and durable history remain independent 10-second cycles.
    internal static readonly TimeSpan SamplingInterval = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan HealthSamplingInterval = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MaximumFreshness = TimeSpan.FromSeconds(30);
    private readonly ConfigurationWorkspace _workspace;
    private readonly IAgentMonitoringSource _source;
    private readonly MonitoringHistoryStore? _history;
    private readonly Func<MonitoringHistoryFrame, CancellationToken, Task>? _appendHistory;
    private readonly AgentRemoteHostMonitor? _remoteMonitor;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _healthInterval;
    private readonly TimeSpan _cycleTimeout;
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<string, Continuity> _continuity = new(StringComparer.Ordinal);
    private readonly ResourceHistoryBuffer _resources = new();
    private AgentMonitoringSnapshot _state;
    private int _configurationGeneration;
    private int _healthGeneration = -1;
    private DateTimeOffset? _healthAttemptedAt;
    private DateTimeOffset? _nextHealthAttemptAt;
    private ConfigurationWorkspaceSnapshot? _healthConfiguration;
    internal event Action<AgentMonitoringSnapshot>? SamplePublished;

    internal AgentMonitor(ConfigurationWorkspace workspace, IAgentMonitoringSource source, string instanceId,
        MonitoringHistoryStore? history, string? historyError = null, TimeProvider? clock = null,
        TimeSpan? interval = null, TimeSpan? cycleTimeout = null, TimeSpan? healthInterval = null,
        Func<MonitoringHistoryFrame, CancellationToken, Task>? appendHistory = null,
        AgentRemoteHostMonitor? remoteMonitor = null)
    {
        if (!Guid.TryParseExact(instanceId, "N", out _)) { throw new ArgumentException("Invalid Agent instance ID.", nameof(instanceId)); }
        _workspace = workspace;
        _source = source;
        _history = history;
        _appendHistory = history is null ? null : appendHistory ?? history.AppendAsync;
        _remoteMonitor = remoteMonitor;
        _clock = clock ?? TimeProvider.System;
        _interval = interval ?? SamplingInterval;
        _healthInterval = healthInterval ?? HealthSamplingInterval;
        _cycleTimeout = cycleTimeout ?? TimeSpan.FromSeconds(8);
        if (_interval <= TimeSpan.Zero || _interval > TimeSpan.FromMinutes(1)
            || _healthInterval < _interval || _healthInterval > TimeSpan.FromMinutes(10)
            || _cycleTimeout <= TimeSpan.Zero || _cycleTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }
        var now = _clock.GetUtcNow();
        _state = new AgentMonitoringSnapshot(1, AgentProtocol.ForRoot(workspace.Paths.RootDirectory).RootId,
            instanceId, Environment.ProcessId, typeof(AgentMonitor).Assembly.GetName().Version?.ToString() ?? "unknown",
            now, now, now, null, (int)Math.Ceiling(_interval.TotalSeconds),
            Empty<IReadOnlyList<TaskSnapshot>>(), Empty<IReadOnlyList<ManagedProcessSnapshot>>(), Empty<HostStatusSnapshot>(), [],
            new MonitoringPersistenceStatus(history is not null, null,
                history is null ? historyError ?? "history-unavailable" : null,
                history?.MaximumBytes ?? new MonitoringHistoryLimits().MaximumBytes, 30),
            ResourceSamplingIntervalMilliseconds: SupportedRecentInterval(_interval),
            RecentResourceSchemaVersion: SupportedRecentInterval(_interval) is not null ? 2 : null);
    }

    private static int? SupportedRecentInterval(TimeSpan interval) => interval == TimeSpan.FromMilliseconds(500) ? 500
        : interval == TimeSpan.FromSeconds(1) ? 1000 : null;

    internal AgentMonitoringSnapshot GetSnapshot()
    {
        AgentMonitoringSnapshot state;
        lock (_stateGate) { state = _state; }
        var now = _clock.GetUtcNow();
        var tasks = Fresh(state.Tasks, now);
        var processes = FreshProcesses(state.Processes, now);
        var runtime = !tasks.Available ? [] : processes.Available ? state.Runtime
            : state.Runtime.Select(row => row with
            {
                ProcessStartedAt = null, ProcessIdentity = null, ProcessUptimeSource = "process-source-unavailable",
            }).ToArray();
        return state with { ReturnedAt = now, Tasks = tasks, Processes = processes, Host = FreshHost(state.Host, now), Runtime = runtime, RemoteHosts = _remoteMonitor?.GetFreshSnapshots(now) };
    }

    internal void InvalidateConfiguration()
    {
        lock (_stateGate)
        {
            _configurationGeneration++;
            _healthConfiguration = null;
            MarkHistoryUnsafeLocked(_clock.GetUtcNow());
            _state = _state with
            {
                Tasks = _state.Tasks with { Available = false, Error = "configuration-changed" },
                Processes = _state.Processes with { Available = false, Error = "configuration-changed" },
                Runtime = [],
            };
        }
    }

    internal async Task<MonitoringHistoryResult> QueryHistoryAsync(MonitoringHistoryQuery query, CancellationToken cancellationToken)
    {
        if (_history is null) { throw new AgentCommandException("history_unavailable", GetSnapshot().Persistence.Error ?? "History source unavailable."); }
        await AwaitSafeHistoryAsync(cancellationToken).ConfigureAwait(false);
        return await _history.QueryAsync(query, _state.InstanceId, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ResourceHistoryResult> QueryResourcesAsync(ResourceHistoryQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _clock.GetUtcNow();
        ResourceHistoryProjection.ValidateQuery(query, now);
        if (query.RemoteHostId is not null)
        {
            if (query.Source == ResourceHistorySource.RecentSeconds)
            {
                if (_remoteMonitor is null) { throw new AgentCommandException("monitoring_unavailable", "Remote host monitoring is unavailable on this Agent."); }
                return await _remoteMonitor.QueryRecentAsync(query, now, _interval == TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
            if (_history is null) { throw new AgentCommandException("history_unavailable", "Durable resource history is unavailable."); }
            var workspace = await _workspace.ReadAsync(cancellationToken).ConfigureAwait(false);
            var definition = workspace.Preferences.RemoteHosts.FirstOrDefault(host => string.Equals(host.Id, query.RemoteHostId, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"Remote host ID '{query.RemoteHostId}' is not configured.");
            if (!definition.Enabled)
            {
                return ResourceHistoryProjection.ProjectDurable([], query, now, warning: "The configured remote host is disabled.");
            }
            await AwaitSafeHistoryAsync(cancellationToken).ConfigureAwait(false);
            var remoteResult = await _history.QueryResourcesAsync(query, _state.InstanceId, cancellationToken, MonitoringRemoteHostIdentity.For(definition)).ConfigureAwait(false);
            return GetSnapshot().Persistence.Available ? remoteResult : remoteResult with
            {
                Truncated = true,
                Warning = remoteResult.Warning ?? "Latest durable append is unavailable; returned records are partial and later time is unknown.",
            };
        }
        if (query.Source == ResourceHistorySource.RecentSeconds)
        { return _interval == TimeSpan.FromMilliseconds(500) ? _resources.QueryCompatible(query, now) : _resources.Query(query, now); }
        if (_history is null) { throw new AgentCommandException("history_unavailable", "Durable resource history is unavailable."); }
        await AwaitSafeHistoryAsync(cancellationToken).ConfigureAwait(false);
        var result = await _history.QueryResourcesAsync(query, _state.InstanceId, cancellationToken).ConfigureAwait(false);
        return GetSnapshot().Persistence.Available ? result : result with
        {
            Truncated = true,
            Warning = result.Warning ?? "Latest durable append is unavailable; returned records are partial and later time is unknown.",
        };
    }

    internal RecentResourceHistoryResult QueryRecentResources(ResourceHistoryQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Source != ResourceHistorySource.RecentSeconds) { throw new ArgumentException("Recent resource schema requires the recent source."); }
        var interval = SupportedRecentInterval(_interval)
            ?? throw new AgentCommandException("resource_interval_unsupported", "Recent-resource precision is unavailable for this sampling interval.");
        return _resources.QueryPrecise(query, _clock.GetUtcNow(), interval, _state.InstanceId);
    }

    internal async Task<RecentResourceHistoryResult> QueryRecentResourcesAsync(ResourceHistoryQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Source != ResourceHistorySource.RecentSeconds) { throw new ArgumentException("Recent resource schema requires the recent source."); }
        var interval = SupportedRecentInterval(_interval)
            ?? throw new AgentCommandException("resource_interval_unsupported", "Recent-resource precision is unavailable for this sampling interval.");
        var now = _clock.GetUtcNow();
        if (query.RemoteHostId is not null)
        {
            if (_remoteMonitor is null) { throw new AgentCommandException("monitoring_unavailable", "Remote host monitoring is unavailable on this Agent."); }
            return await _remoteMonitor.QueryRecentPreciseAsync(query, now, interval, _state.InstanceId, cancellationToken).ConfigureAwait(false);
        }
        return _resources.QueryPrecise(query, now, interval, _state.InstanceId);
    }

    internal Task RunAsync(CancellationToken cancellationToken) => RunConcurrentAsync(cancellationToken);

    internal async Task SampleOnceAsync(CancellationToken cancellationToken = default)
    {
        await _sampleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AgentMonitoringSnapshot before;
            int generation;
            var attemptedAt = _clock.GetUtcNow();
            lock (_stateGate) { before = _state; generation = _configurationGeneration; _state = _state with { HeartbeatAt = attemptedAt }; }
            var resetHealthCadence = _healthConfiguration is null || generation != _healthGeneration
                || _healthAttemptedAt is { } lastHealth && attemptedAt < lastHealth;
            var healthDue = resetHealthCadence || _nextHealthAttemptAt is not { } nextHealth || attemptedAt >= nextHealth;
            if (healthDue)
            {
                // Anchor to the planned slot, not the last tick's jitter. Skip missed slots;
                // never replay historical health or append invented catch-up observations.
                var scheduled = !resetHealthCadence && _nextHealthAttemptAt is { } due ? due : attemptedAt;
                var skipped = (attemptedAt - scheduled).Ticks / _healthInterval.Ticks;
                _nextHealthAttemptAt = scheduled.AddTicks((skipped + 1) * _healthInterval.Ticks);
                _healthAttemptedAt = attemptedAt; _healthGeneration = generation;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_cycleTimeout);
            var hostTask = CaptureAsync(() => _source.ObserveHostAsync(deadline.Token), before.Host,
                attemptedAt, cancellationToken);
            var workloadTask = healthDue ? CaptureWorkloadsAsync(before, attemptedAt, deadline.Token, cancellationToken)
                : CaptureResourcesAsync(before, attemptedAt, deadline.Token, cancellationToken);
            await Task.WhenAll(hostTask, workloadTask).ConfigureAwait(false);
            var workloads = await workloadTask.ConfigureAwait(false);
            var host = await hostTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var at = _clock.GetUtcNow();
            host = FreshHost(host, at);
            workloads = workloads with { Tasks = Fresh(workloads.Tasks, at), Processes = FreshProcesses(workloads.Processes, at) };
            if (healthDue) { _healthConfiguration = workloads.Configuration; }
            // Continuity counts consecutive health observations, so only a health cycle advances it.
            var runtime = healthDue ? BuildRuntime(workloads.Tasks, workloads.Processes, at)
                : !workloads.Tasks.Available ? [] : workloads.Processes.Available ? before.Runtime
                : before.Runtime.Select(row => row with
                {
                    ProcessStartedAt = null, ProcessIdentity = null, ProcessUptimeSource = "process-source-unavailable",
                }).ToArray();
            lock (_stateGate)
            {
                if (generation != _configurationGeneration)
                {
                    workloads = workloads with
                    {
                        Tasks = workloads.Tasks with { Available = false, Error = "configuration-changed" },
                        Processes = workloads.Processes with { Available = false, Error = "configuration-changed" },
                    };
                    runtime = [];
                    _continuity.Clear();
                    _healthConfiguration = null;
                }
                _state = _state with
                {
                    ConfigurationRevision = workloads.Configuration?.Revision ?? before.ConfigurationRevision,
                    Tasks = workloads.Tasks, Processes = workloads.Processes, Host = host, Runtime = runtime,
                    HealthConfigurationGeneration = healthDue ? generation : before.HealthConfigurationGeneration,
                    HealthClockEpoch = healthDue ? _clockEpoch : before.HealthClockEpoch,
                };
            }
            var resourceFrame = BuildResourceFrame(workloads, host, at, before.InstanceId, generation);
            lock (_stateGate)
            {
                if (generation != _configurationGeneration) { resourceFrame = resourceFrame with { Tasks = [] }; }
                _resources.Append(resourceFrame);
            }
            // Resource-only ticks neither persist history nor publish: recovery must act on fresh
            // health, never on a reused observation.
            if (!healthDue) { return; }
            if (_history is not null)
            {
                try
                {
                    var frame = BuildFrame(workloads, host, at, before, generation);
                    await _history.AppendAsync(frame, cancellationToken).ConfigureAwait(false);
                    lock (_stateGate) { _state = _state with { Persistence = _state.Persistence with { Available = true, LastWrittenAt = at, Error = null } }; }
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    lock (_stateGate) { _state = _state with { Persistence = _state.Persistence with { Available = false, Error = ErrorText(exception) } }; }
                }
            }
            // The sole production sink is an O(1), nonblocking DropOldest channel writer.
            SamplePublished?.Invoke(GetSnapshot());
        }
        finally { _sampleGate.Release(); }
    }

    private async Task<WorkloadCapture> CaptureWorkloadsAsync(AgentMonitoringSnapshot before, DateTimeOffset attemptedAt,
        CancellationToken cycleToken, CancellationToken shutdownToken)
    {
        ConfigurationWorkspaceSnapshot configuration;
        try { configuration = await _workspace.ReadAsync(cycleToken).ConfigureAwait(false); }
        catch (Exception exception) when (!shutdownToken.IsCancellationRequested)
        {
            return new(null, Failed(before.Tasks, attemptedAt, exception), Failed(before.Processes, attemptedAt, exception));
        }
        var tasks = await CaptureAsync(() => _source.ObserveTasksAsync(configuration.Configuration, cycleToken),
            before.Tasks, attemptedAt, shutdownToken).ConfigureAwait(false);
        // Resource reads reuse this cycle's health, including unknown, rather than running another probe.
        var processes = await CaptureAsync(() => _source.ObserveProcessesAsync(configuration.Configuration,
                tasks.Available ? tasks.Data! : [], cycleToken), before.Processes, attemptedAt, shutdownToken).ConfigureAwait(false);
        return new(configuration, tasks, processes);
    }

    private async Task<WorkloadCapture> CaptureResourcesAsync(AgentMonitoringSnapshot before, DateTimeOffset attemptedAt,
        CancellationToken cycleToken, CancellationToken shutdownToken)
    {
        var configuration = _healthConfiguration!;
        // Reuse the last health cycle's observations; a resource read never runs another probe.
        var processes = before.Tasks is { Available: true, Data: { } observed }
            ? await CaptureAsync(() => _source.ObserveProcessesAsync(configuration.Configuration, observed, cycleToken),
                before.Processes, attemptedAt, shutdownToken).ConfigureAwait(false)
            : before.Processes;
        return new(configuration, before.Tasks, processes);
    }

    private async Task<MonitoringDataGroup<T>> CaptureAsync<T>(Func<Task<T>> capture, MonitoringDataGroup<T> before,
        DateTimeOffset attemptedAt, CancellationToken shutdownToken) where T : class
    {
        try
        {
            var data = await capture().ConfigureAwait(false);
            return new(true, _clock.GetUtcNow(), attemptedAt, data, null);
        }
        catch (Exception exception) when (!shutdownToken.IsCancellationRequested) { return Failed(before, attemptedAt, exception); }
    }

    private MonitoringHistoryFrame BuildFrame(WorkloadCapture capture, MonitoringDataGroup<HostStatusSnapshot> host,
        DateTimeOffset at, AgentMonitoringSnapshot before, int generation)
    {
        // Publication may follow slow probes/resource reads. It must not grant an extra
        // health interval of validity to observations made earlier in this cycle.
        var beganAt = capture.Tasks.AttemptedAt ?? at;
        var validUntil = beganAt <= at && beganAt + _healthInterval > at ? beganAt + _healthInterval : at;
        var freshHost = host.Available && host.Data is { } hostData && hostData.CapturedAt <= at
            && at - hostData.CapturedAt <= TimeSpan.FromSeconds(3) ? host : host with { Available = false };
        var rows = capture.Configuration?.Configuration.Tasks.Where(task => task.Enabled).Select(task =>
        {
            var identity = MonitoringTaskIdentity.For(task);
            var health = capture.Tasks.Available ? capture.Tasks.Data?.FirstOrDefault(row =>
                string.Equals(row.Name, task.Name, StringComparison.OrdinalIgnoreCase) && MonitoringTaskIdentity.For(row) == identity) : null;
            var process = MatchingProcess(task.Name, identity, task.Type.ToKey(), capture.Processes, at);
            if (process is not null && at - process.CapturedAt > TimeSpan.FromSeconds(3)) { process = null; }
            var hasRates = process is { CpuUsageAvailable: true, ProcessCount: > 0 };
            return new TaskHistorySample(task.Name, identity,
                health is not { HealthObserved: true } ? ObservationHealth.Unknown : health.Healthy ? ObservationHealth.Healthy : ObservationHealth.Fault,
                hasRates ? process!.CpuUsagePercent : null,
                process is { ProcessCount: > 0 } ? process.WorkingSetBytes : null,
                freshHost.Available && freshHost.Data is { TotalMemoryBytes: > 0 } data ? data.TotalMemoryBytes : null,
                validUntil, ResourceProcessIdentity.For(process, at, generation),
                hasRates ? process!.TotalDiskReadBytesPerSecond : null,
                hasRates ? process!.TotalDiskWriteBytesPerSecond : null,
                hasRates ? process!.TotalNetworkReceiveBytesPerSecond : null,
                hasRates ? process!.TotalNetworkSendBytesPerSecond : null);
        }).ToArray() ?? [];
        var remoteHosts = _remoteMonitor?.CaptureDurableSamples(capture.Configuration, at);
        return new(1, before.RootId, before.InstanceId, at, rows, Host: BuildHostHistory(freshHost), RemoteHosts: remoteHosts);
    }

    private static ResourceHistoryFrame BuildResourceFrame(WorkloadCapture capture, MonitoringDataGroup<HostStatusSnapshot> host,
        DateTimeOffset at, string instanceId, int generation)
    {
        var rows = capture.Configuration?.Configuration.Tasks.Where(task => task.Enabled).Select(task =>
        {
            var identity = MonitoringTaskIdentity.For(task);
            var process = MatchingProcess(task.Name, identity, task.Type.ToKey(), capture.Processes, at);
            if (process is not null && at - process.CapturedAt > TimeSpan.FromSeconds(3)) { process = null; }
            var processIdentity = ResourceProcessIdentity.For(process, at, generation);
            var hasRates = processIdentity is not null && process is { CpuUsageAvailable: true, ProcessCount: > 0 };
            return new ResourceTaskSample(task.Name, identity, process?.CapturedAt ?? at, processIdentity,
                hasRates ? process!.CpuUsagePercent : null,
                processIdentity is not null ? process!.WorkingSetBytes : null,
                hasRates ? process!.TotalDiskReadBytesPerSecond : null,
                hasRates ? process!.TotalDiskWriteBytesPerSecond : null,
                hasRates ? process!.TotalNetworkReceiveBytesPerSecond : null,
                hasRates ? process!.TotalNetworkSendBytesPerSecond : null);
        }).ToArray() ?? [];
        var freshHost = host.Available && host.Data is { } data && data.CapturedAt <= at && at - data.CapturedAt <= TimeSpan.FromSeconds(3);
        return new(at, instanceId, rows, freshHost ? BuildHostHistory(host) : null, freshHost ? host.Data!.CapturedAt : null);
    }

    private static HostHistorySample? BuildHostHistory(MonitoringDataGroup<HostStatusSnapshot> host) =>
        !host.Available || host.Data is not { } data
            ? null
            : AgentRemoteHostMonitor.ToHostHistory(data.CpuUsagePercent, data.TotalMemoryBytes, data.AvailableMemoryBytes,
                data.DiskReadBytesPerSecond, data.DiskWriteBytesPerSecond,
                data.NetworkReceiveBytesPerSecond, data.NetworkSendBytesPerSecond);

    private IReadOnlyList<TaskRuntimeObservation> BuildRuntime(MonitoringDataGroup<IReadOnlyList<TaskSnapshot>> tasks,
        MonitoringDataGroup<IReadOnlyList<ManagedProcessSnapshot>> processes, DateTimeOffset at)
    {
        if (!tasks.Available || tasks.Data is null) { _continuity.Clear(); return []; }
        var result = new List<TaskRuntimeObservation>();
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks.Data)
        {
            var identity = MonitoringTaskIdentity.For(task);
            retained.Add(identity);
            var process = MatchingProcess(task.Name, identity, task.Type, processes, at);
            var identified = task.Type is "windows" or "windows_service" && process is { ProcessCount: > 0 }
                && process.Processes.All(item => item.StartedAt is not null && item.StartedAt <= at);
            // A service host replacement is a restart witness, but its start time is NOT
            // the service start time (shared hosts can outlive individual services).
            var native = task.Type == AutorunTaskType.Windows.ToKey() && identified;
            var processIdentity = identified ? string.Join(";", process!.Processes.OrderBy(item => item.ProcessId)
                .Select(item => $"{item.ProcessId}:{item.StartedAt!.Value.UtcTicks}")) : null;
            DateTimeOffset? since = null;
            if (task.Enabled && task.HealthObserved && task.Healthy)
            {
                var hasPrevious = _continuity.TryGetValue(identity, out var previous);
                var knownRestart = hasPrevious && previous!.ProcessIdentity is not null && processIdentity is not null
                    && previous.ProcessIdentity != processIdentity;
                since = hasPrevious && at >= previous!.LastAt && at - previous.LastAt <= TimeSpan.FromSeconds(15) && !knownRestart
                    ? previous.Since : at;
                _continuity[identity] = new(at, since.Value, processIdentity ?? (hasPrevious ? previous!.ProcessIdentity : null));
            }
            else { _continuity.Remove(identity); }
            result.Add(new(task.Name, identity, at, native ? process!.StartedAt : null,
                native ? "windows-process-observed-instances" : "unavailable", processIdentity, since,
                since is null ? "unavailable" : "consecutive-passive-health-observations"));
        }
        foreach (var key in _continuity.Keys.Where(key => !retained.Contains(key)).ToArray()) { _continuity.Remove(key); }
        return result;
    }

    private static ManagedProcessSnapshot? MatchingProcess(string name, string identity, string type,
        MonitoringDataGroup<IReadOnlyList<ManagedProcessSnapshot>> processes, DateTimeOffset at) =>
        processes.Available ? processes.Data?.FirstOrDefault(row => row.TaskIdentity == identity
            && string.Equals(row.TaskName, name, StringComparison.OrdinalIgnoreCase)
            && row.Type == type && type is "windows" or "windows_service" or "wsl_boot" or "wsl_systemd" or "wsl_command"
            && at - row.CapturedAt <= MaximumFreshness && row.CapturedAt <= at) : null;

    private static MonitoringDataGroup<T> Empty<T>() where T : class => new(false, null, null, null, "not-yet-observed");
    private static MonitoringDataGroup<T> Failed<T>(MonitoringDataGroup<T> before, DateTimeOffset attemptedAt, Exception exception) where T : class =>
        before with { Available = false, AttemptedAt = attemptedAt, Error = ErrorText(exception) };
    private static MonitoringDataGroup<T> Fresh<T>(MonitoringDataGroup<T> group, DateTimeOffset now) where T : class =>
        group.Available && group.Data is not null && group.ObservedAt is { } at
            && now - at <= MaximumFreshness && at <= now
            && (group.AttemptedAt is not { } began || began <= now && now - began <= MaximumFreshness) ? group
            : group with { Available = false, Error = group.Error ?? "stale-or-future-observation" };
    private static MonitoringDataGroup<HostStatusSnapshot> FreshHost(MonitoringDataGroup<HostStatusSnapshot> group, DateTimeOffset now) =>
        Fresh(group with { ObservedAt = group.Data?.CapturedAt ?? group.ObservedAt }, now);
    private static MonitoringDataGroup<IReadOnlyList<ManagedProcessSnapshot>> FreshProcesses(
        MonitoringDataGroup<IReadOnlyList<ManagedProcessSnapshot>> group, DateTimeOffset now) =>
        Fresh(group.Data is { Count: > 0 } rows ? group with { ObservedAt = rows.Min(row => row.CapturedAt) } : group, now);

    private static string ErrorText(Exception exception)
    {
        var text = exception is OperationCanceledException ? "observation-timeout" : $"{exception.GetType().Name}: {exception.Message}";
        return text.Length <= 512 ? text : text[..512];
    }

    public void Dispose() => _sampleGate.Dispose();
    private sealed record Continuity(DateTimeOffset LastAt, DateTimeOffset Since, string? ProcessIdentity);
    private sealed record WorkloadCapture(ConfigurationWorkspaceSnapshot? Configuration,
        MonitoringDataGroup<IReadOnlyList<TaskSnapshot>> Tasks, MonitoringDataGroup<IReadOnlyList<ManagedProcessSnapshot>> Processes);
}
