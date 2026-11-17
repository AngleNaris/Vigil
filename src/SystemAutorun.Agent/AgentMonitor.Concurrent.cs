using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Agent;

internal sealed partial class AgentMonitor
{
    private Task? _healthWorker;
    private TaskCompletionSource? _historyPending;
    private DateTimeOffset? _historyUnsafeSince, _lastResourceCapture;
    private long _historyUnsafeRevision, _clockEpoch, _healthScheduledEpoch = -1, _healthEvidenceTimestamp;
    private CancellationToken _concurrentToken;
    private bool _concurrentRunning;

    // Only this loop calls the Host/Processes readers. Health and disk I/O have one independent,
    // retained worker, so a slow probe or flush cannot take over the resource sampling gate.
    private async Task RunConcurrentAsync(CancellationToken cancellationToken)
    {
        lock (_stateGate) { _concurrentRunning = true; _concurrentToken = cancellationToken; }
        using var timer = new PeriodicTimer(_interval, _clock);
        var initial = true;
        try
        {
            do
            {
                if (!initial) { ScheduleHealth(cancellationToken); }
                try { await SampleResourcesOnlyAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    lock (_stateGate)
                    {
                        var at = _clock.GetUtcNow();
                        _state = _state with { Host = Failed(_state.Host, at, exception), Processes = Failed(_state.Processes, at, exception) };
                    }
                }
                if (initial)
                {
                    initial = false;
                    ScheduleHealth(cancellationToken);
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            // Cancellation does not prove an OS probe or Flush has stopped. Keep the worker owned
            // until it really completes; the caller disposes the reader/store only after this return.
            try { if (_healthWorker is { } worker) { await worker.ConfigureAwait(false); } }
            finally { lock (_stateGate) { _concurrentRunning = false; _concurrentToken = default; } }
        }
    }

    internal void RequestImmediateHealthCheck()
    {
        CancellationToken token = default;
        var shouldSchedule = false;
        lock (_stateGate)
        {
            _nextHealthAttemptAt = _clock.GetUtcNow();
            if (_concurrentRunning)
            {
                token = _concurrentToken;
                shouldSchedule = true;
            }
        }
        if (shouldSchedule) { ScheduleHealth(token); }
    }

    private void ScheduleHealth(CancellationToken cancellationToken)
    {
        if (_healthWorker is { IsCompleted: false } || cancellationToken.IsCancellationRequested) { return; }
        var attemptedAt = _clock.GetUtcNow(); var attemptedTimestamp = _clock.GetTimestamp();
        int generation; long epoch; TaskCompletionSource? pending = null;
        lock (_stateGate)
        {
            generation = _configurationGeneration; epoch = _clockEpoch;
            var reset = generation != _healthGeneration || epoch != _healthScheduledEpoch || _healthAttemptedAt is { } last && attemptedAt < last;
            if (!reset && _nextHealthAttemptAt is { } next && attemptedAt < next) { return; }
            var scheduled = !reset && _nextHealthAttemptAt is { } due ? due : attemptedAt;
            var skipped = (attemptedAt - scheduled).Ticks / _healthInterval.Ticks;
            _nextHealthAttemptAt = scheduled.AddTicks((skipped + 1) * _healthInterval.Ticks);
            _healthAttemptedAt = attemptedAt; _healthGeneration = generation; _healthScheduledEpoch = epoch;
            if (_history is not null) { _historyPending = pending = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        }
        _healthWorker = Task.Run(() => CaptureHealthAndPersistAsync(attemptedAt, attemptedTimestamp, generation, epoch, pending, cancellationToken));
    }

    private async Task CaptureHealthAndPersistAsync(DateTimeOffset attemptedAt, long attemptedTimestamp, int generation, long epoch,
        TaskCompletionSource? pending, CancellationToken cancellationToken)
    {
        var persisted = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_cycleTimeout);
            ConfigurationWorkspaceSnapshot configuration;
            try { configuration = await _workspace.ReadAsync(deadline.Token).ConfigureAwait(false); }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                lock (_stateGate)
                {
                    if (generation == _configurationGeneration && epoch == _clockEpoch)
                    {
                        _healthConfiguration = null; _continuity.Clear();
                        _state = _state with { Tasks = Failed(_state.Tasks, attemptedAt, exception), Runtime = [] };
                    }
                    MarkHistoryUnsafeLocked(attemptedAt);
                }
                return;
            }
            lock (_stateGate)
            {
                if (generation != _configurationGeneration || epoch != _clockEpoch) { MarkHistoryUnsafeLocked(attemptedAt); return; }
                // External file changes can alter revision without going through the IPC mutation path.
                if (_state.ConfigurationRevision is { } revision && revision != configuration.Revision)
                {
                    generation = ++_configurationGeneration; _healthGeneration = generation; _continuity.Clear();
                    _state = _state with { Tasks = _state.Tasks with { Available = false, Error = "configuration-changed" },
                        Processes = _state.Processes with { Available = false, Error = "configuration-changed" }, Runtime = [] };
                    MarkHistoryUnsafeLocked(attemptedAt);
                }
                _healthConfiguration = configuration;
                _state = _state with { ConfigurationRevision = configuration.Revision };
            }

            MonitoringDataGroup<IReadOnlyList<TaskSnapshot>> tasks;
            AgentMonitoringSnapshot before;
            lock (_stateGate) { before = _state; }
            try
            {
                var rows = await _source.ObserveTasksAsync(configuration.Configuration, deadline.Token).ConfigureAwait(false);
                tasks = deadline.IsCancellationRequested
                    ? Failed(before.Tasks, attemptedAt, new OperationCanceledException("Health observation exceeded its deadline."))
                    : new(true, _clock.GetUtcNow(), attemptedAt, rows, null);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            { tasks = Failed(before.Tasks, attemptedAt, exception); }
            cancellationToken.ThrowIfCancellationRequested();

            AgentMonitoringSnapshot cycle; WorkloadCapture capture; MonitoringDataGroup<HostStatusSnapshot> host;
            DateTimeOffset observedAt; DateTimeOffset? unsafeSince; long unsafeRevision;
            lock (_stateGate)
            {
                if (generation != _configurationGeneration || epoch != _clockEpoch || _healthConfiguration?.Revision != configuration.Revision)
                { MarkHistoryUnsafeLocked(attemptedAt); return; }
                observedAt = _clock.GetUtcNow(); tasks = Fresh(tasks, observedAt);
                if (observedAt < attemptedAt || observedAt - attemptedAt >= _healthInterval)
                { tasks = tasks with { Available = false, Error = "health-observation-expired" }; }
                var processes = FreshProcesses(_state.Processes, observedAt); host = FreshHost(_state.Host, observedAt);
                var runtime = BuildRuntime(tasks, processes, observedAt);
                _state = _state with { Tasks = tasks, Runtime = runtime, HealthConfigurationGeneration = generation, HealthClockEpoch = epoch };
                _healthEvidenceTimestamp = attemptedTimestamp;
                cycle = _state; capture = new(configuration, tasks, processes);
                // Capture the missing-history boundary in the same generation/epoch snapshot as
                // the cycle. An invalidation after this point must never be consumed by this cycle.
                unsafeSince = _historyUnsafeSince; unsafeRevision = _historyUnsafeRevision;
            }

            if (_history is not null)
            {
                try
                {
                    if (unsafeSince is { } barrier)
                    {
                        // Never append an older health result after a later boundary: that would
                        // look like a clock reset and could resurrect the invalidated observation.
                        if (barrier >= observedAt) { throw new InvalidOperationException("History boundary is not before this observation."); }
                        // This is an explicit unknown boundary, not an invented healthy/fault sample.
                        await _appendHistory!(new MonitoringHistoryFrame(1, cycle.RootId, cycle.InstanceId, barrier, []), cancellationToken).ConfigureAwait(false);
                    }
                    var frame = BuildFrame(capture, host, observedAt, cycle, generation);
                    await _appendHistory!(frame, cancellationToken).ConfigureAwait(false);
                    lock (_stateGate)
                    {
                        if (unsafeRevision == _historyUnsafeRevision && generation == _configurationGeneration
                            && epoch == _clockEpoch && _healthConfiguration?.Revision == configuration.Revision)
                        { _historyUnsafeSince = null; }
                        _state = _state with { Persistence = _state.Persistence with { Available = _historyUnsafeSince is null,
                            LastWrittenAt = frame.ObservedAt, Error = _historyUnsafeSince is null ? null : "history-boundary-pending" } };
                    }
                    persisted = true;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    lock (_stateGate)
                    {
                        MarkHistoryUnsafeLocked(attemptedAt);
                        _state = _state with { Persistence = _state.Persistence with { Available = false, Error = ErrorText(exception) } };
                    }
                }
            }
            if (!cancellationToken.IsCancellationRequested && RecoveryEvidenceCurrent(cycle))
            { SamplePublished?.Invoke(cycle); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            lock (_stateGate)
            {
                MarkHistoryUnsafeLocked(attemptedAt);
                _state = _state with { Tasks = Failed(_state.Tasks, attemptedAt, exception), Runtime = [] };
            }
        }
        finally
        {
            lock (_stateGate)
            {
                if (_history is not null && !persisted) { MarkHistoryUnsafeLocked(attemptedAt); }
                if (ReferenceEquals(_historyPending, pending)) { _historyPending = null; }
            }
            pending?.TrySetResult();
        }
    }

    private async Task SampleResourcesOnlyAsync(CancellationToken cancellationToken)
    {
        await _sampleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var attemptedAt = _clock.GetUtcNow(); AgentMonitoringSnapshot before; ConfigurationWorkspaceSnapshot? configuration; int generation;
            lock (_stateGate)
            {
                before = _state; configuration = _healthConfiguration; generation = _configurationGeneration;
                _state = _state with { HeartbeatAt = attemptedAt };
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_cycleTimeout);
            var hostTask = CaptureAsync(() => _source.ObserveHostAsync(deadline.Token), before.Host, attemptedAt, cancellationToken);
            var observedHealth = Fresh(before.Tasks, attemptedAt);
            if (observedHealth.AttemptedAt is not { } began || attemptedAt - began >= _healthInterval)
            { observedHealth = observedHealth with { Available = false, Error = "health-observation-expired" }; }
            var processesTask = configuration is null ? Task.FromResult(before.Processes with { Available = false, Error = "configuration-unavailable" })
                : CaptureAsync(() => _source.ObserveProcessesAsync(configuration.Configuration,
                    observedHealth is { Available: true, Data: { } rows } && before.ConfigurationRevision == configuration.Revision ? rows : [], deadline.Token),
                    before.Processes, attemptedAt, cancellationToken);
            await Task.WhenAll(hostTask, processesTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var at = _clock.GetUtcNow(); var host = FreshHost(await hostTask.ConfigureAwait(false), at);
            var processes = FreshProcesses(await processesTask.ConfigureAwait(false), at);
            if (deadline.IsCancellationRequested)
            {
                var timeout = new OperationCanceledException("Resource observation exceeded its deadline.");
                host = Failed(host, attemptedAt, timeout); processes = Failed(processes, attemptedAt, timeout);
            }
            var processLivenessChanged = false;
            lock (_stateGate)
            {
                if (_lastResourceCapture is { } last && at <= last)
                {
                    _clockEpoch++; MarkHistoryUnsafeLocked(at); _continuity.Clear();
                    _state = _state with { Tasks = _state.Tasks with { Available = false, Error = "clock-changed" }, Runtime = [] };
                }
                _lastResourceCapture = at;
                if (generation != _configurationGeneration || configuration?.Revision != _healthConfiguration?.Revision)
                { configuration = null; processes = processes with { Available = false, Error = "configuration-changed" }; }
                var runtime = !_state.Tasks.Available ? [] : processes.Available ? _state.Runtime
                    : _state.Runtime.Select(row => row with { ProcessStartedAt = null, ProcessIdentity = null, ProcessUptimeSource = "process-source-unavailable" }).ToArray();
                _state = _state with { Host = host, Processes = processes, Runtime = runtime };
                _resources.Append(BuildResourceFrame(new(configuration, _state.Tasks, processes), host, at, _state.InstanceId, generation));
                if (before.Processes is { Available: true, Data: { } prevList }
                    && processes is { Available: true, Data: { } curList }
                    && configuration is not null
                    && HasProcessLivenessChange(prevList, curList))
                {
                    _nextHealthAttemptAt = at;
                    processLivenessChanged = true;
                }
            }
            if (processLivenessChanged) { ScheduleHealth(cancellationToken); }
        }
        finally { _sampleGate.Release(); }
    }

    private static bool HasProcessLivenessChange(IReadOnlyList<ManagedProcessSnapshot> previous, IReadOnlyList<ManagedProcessSnapshot> current)
    {
        if (previous.Count == 0 && current.Count == 0) { return false; }
        var prevMap = new Dictionary<string, bool>(StringComparer.Ordinal);
        for (var i = 0; i < previous.Count; i++)
        {
            var item = previous[i];
            prevMap[item.TaskIdentity ?? item.TaskName] = item.ProcessCount > 0;
        }
        var seen = 0;
        for (var i = 0; i < current.Count; i++)
        {
            var item = current[i];
            var key = item.TaskIdentity ?? item.TaskName;
            if (!prevMap.TryGetValue(key, out var wasAlive)) { return true; }
            if (wasAlive != (item.ProcessCount > 0)) { return true; }
            seen++;
        }
        return seen != prevMap.Count;
    }

    private void MarkHistoryUnsafeLocked(DateTimeOffset at)
    {
        if (_history is null || !_concurrentRunning) { return; }
        if (_historyUnsafeSince is null || at < _historyUnsafeSince) { _historyUnsafeSince = at; }
        _historyUnsafeRevision++;
    }

    private async Task AwaitSafeHistoryAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? pending; bool unsafeHistory;
            lock (_stateGate) { pending = _historyPending?.Task; unsafeHistory = _historyUnsafeSince is not null; }
            if (pending is not null) { await pending.WaitAsync(cancellationToken).ConfigureAwait(false); continue; }
            if (unsafeHistory) { throw new AgentCommandException("history_unavailable", "History has an uncommitted or missing boundary; the affected interval is unknown."); }
            return;
        }
    }

    internal bool RecoveryEvidenceCurrent(AgentMonitoringSnapshot cycle)
    {
        lock (_stateGate)
        {
            var elapsed = _clock.GetElapsedTime(_healthEvidenceTimestamp, _clock.GetTimestamp());
            return cycle.InstanceId == _state.InstanceId && cycle.ConfigurationRevision == _state.ConfigurationRevision
                && cycle.HealthConfigurationGeneration == _configurationGeneration && cycle.HealthClockEpoch == _clockEpoch
                && cycle.Tasks.AttemptedAt == _state.Tasks.AttemptedAt && _state.Tasks.Available
                && (_lastResourceCapture is not { } last || _clock.GetUtcNow() >= last)
                && elapsed >= TimeSpan.Zero && elapsed < AgentTaskController.EvidenceLifetime;
        }
    }
}
