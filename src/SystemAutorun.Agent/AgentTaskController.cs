using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;
using SystemAutorun.Core.Recovery;

namespace SystemAutorun.Agent;

// The daemon's only interactive/automatic task-control surface. Boot mode remains a
// separate, explicit one-shot controller. Never hold a root lease while waiting on a task.
internal sealed class AgentTaskController
{
    private readonly ConfigurationWorkspace _workspace;
    private readonly TaskOrchestrator _orchestrator;
    private readonly RecoveryControlStore _store;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _admissionGate = new(1, 1);
    private readonly object _operationsGate = new();
    private readonly HashSet<Task> _operations = [];
    private volatile bool _accepting = true;
    internal const int MaximumInteractiveOperations = 8; // Leave pipe capacity for reads/status.
    internal static readonly TimeSpan EvidenceLifetime = TimeSpan.FromSeconds(10);

    internal AgentTaskController(ConfigurationWorkspace workspace, TaskOrchestrator orchestrator,
        RecoveryControlStore? store = null, TimeProvider? clock = null)
    {
        _workspace = workspace; _orchestrator = orchestrator;
        _clock = clock ?? TimeProvider.System;
        _store = store ?? new(workspace.Paths, _clock);
    }

    internal Task<RecoveryControlSnapshot> ReadRecoveryAsync(CancellationToken token = default) => _store.ReadAsync(token);
    internal Task<RecoveryControlSnapshot> InitializeRuntimeAsync(CancellationToken token = default) => _store.MarkInterruptedAttemptsUnknownAsync(token);

    internal Task<CommandResult> StartAsync(string name, CancellationToken token = default) => ManualAsync("start", name, token);
    internal Task<CommandResult> StopAsync(string name, CancellationToken token = default) => ManualAsync("stop", name, token);
    internal Task<CommandResult> RestartAsync(string name, CancellationToken token = default) => ManualAsync("restart", name, token);

    private async Task<CommandResult> ManualAsync(string command, string name, CancellationToken requestToken)
    {
        Task<CommandResult> operation;
        await _admissionGate.WaitAsync(requestToken).ConfigureAwait(false);
        try
        {
            RequireAccepting();
            lock (_operationsGate)
            {
                if (_operations.Count >= MaximumInteractiveOperations)
                { throw new AgentCommandException("controller_busy", "Task control is at capacity; no task intent was admitted."); }
            }
            ManualIntent intent;
            await using (var root = await RootWriteLease.AcquireAsync(_workspace.Paths, requestToken).ConfigureAwait(false))
            {
                var configuration = await _workspace.ReadWithLeaseAsync(root, requestToken).ConfigureAwait(false);
                var task = configuration.Configuration.GetTask(name);
                RequireUserLogon(task, requireEnabled: command != "stop");
                var plan = command == "stop" ? new[] { task.Name } : Plan(configuration.Configuration, task.Name);
                var state = await InitializeForManualControlAsync(configuration, root, requestToken).ConfigureAwait(false);
                if (command == "stop")
                {
                    state = await _store.SetPausedAsync(task.Name, true, state.Revision, requestToken, root, recordIntent: true).ConfigureAwait(false);
                    intent = new(command, task.Name, configuration, plan, Entry(state, task.Name)!.IntentId!, null);
                }
                else
                {
                    var admission = await _store.BeginManualStartAsync(task.Name, MonitoringTaskIdentity.For(task),
                        state.Revision, requestToken, root).ConfigureAwait(false);
                    intent = new(command, task.Name, configuration, plan, admission.Attempt!.IntentId!, admission.Attempt.AttemptId);
                }
            }
            // From durable intent onward the Agent, not the requesting pipe's timeout, owns
            // execution and receipt completion. A lost client acknowledgement is not a rollback.
            operation = Task.Run(() => ExecuteManualAsync(intent));
            Track(operation);
        }
        finally { _admissionGate.Release(); }
        return await operation.ConfigureAwait(false);
    }

    private async Task<RecoveryControlSnapshot> InitializeForManualControlAsync(ConfigurationWorkspaceSnapshot configuration,
        RootWriteLease.Held root, CancellationToken token)
    {
        var state = await _store.ReadAsync(token, root).ConfigureAwait(false);
        return state.Initialized ? state : await _store.InitializePausedAsync(
            configuration.Configuration.Tasks.Select(task => task.Name).ToArray(), state.Revision, token, root).ConfigureAwait(false);
    }

    internal async Task<RecoveryControlSnapshot> ResumeAsync(string name, string expectedRevision, CancellationToken token)
    {
        await _admissionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireAccepting();
            await using var root = await RootWriteLease.AcquireAsync(_workspace.Paths, token).ConfigureAwait(false);
            var configuration = await _workspace.ReadWithLeaseAsync(root, token).ConfigureAwait(false);
            RequireUserLogon(configuration.Configuration.GetTask(name), requireEnabled: true);
            var state = await _store.ReadAsync(token, root).ConfigureAwait(false);
            if (state.Revision != expectedRevision)
            { throw new AgentCommandException("recovery_revision_conflict", "Recovery intent changed; reload it before resuming.", state.Revision); }
            state = await InitializeForManualControlAsync(configuration, root, token).ConfigureAwait(false);
            return await _store.SetPausedAsync(name, false, state.Revision, token, root, recordIntent: true).ConfigureAwait(false);
        }
        finally { _admissionGate.Release(); }
    }

    private async Task<CommandResult> ExecuteManualAsync(ManualIntent intent)
    {
        CommandResult result;
        try
        {
            result = intent.Command == "stop" ? await ExecuteStopAsync(intent).ConfigureAwait(false)
                : await ExecuteManualPlanAsync(intent).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (intent.AttemptId is not null)
            { await CompleteAsync(intent.Name, intent.AttemptId, RecoveryAttemptOutcome.OutcomeUnknown, exception.Message).ConfigureAwait(false); }
            throw new AgentCommandException("outcome_unknown", $"Accepted task intent has an unknown outcome: {exception.Message}. Inspect state; do not replay blindly.");
        }
        if (intent.AttemptId is not null)
        { await CompleteAsync(intent.Name, intent.AttemptId, result.Succeeded ? RecoveryAttemptOutcome.Succeeded : RecoveryAttemptOutcome.Failed, result.Message).ConfigureAwait(false); }
        return result;
    }

    private async Task<CommandResult> ExecuteStopAsync(ManualIntent intent)
    {
        using var taskLock = await _orchestrator.AcquireTaskAsync(intent.Name, CancellationToken.None).ConfigureAwait(false);
        AutorunTask task;
        await using (var root = await RootWriteLease.AcquireAsync(_workspace.Paths).ConfigureAwait(false))
        {
            var current = await _workspace.ReadWithLeaseAsync(root).ConfigureAwait(false);
            var state = await _store.ReadAsync(heldLease: root).ConfigureAwait(false);
            var entry = Entry(state, intent.Name);
            if (entry?.IntentId != intent.Token || !entry.Paused) { return Superseded(); }
            task = current.Configuration.GetTask(intent.Name);
            // A persisted name-scoped pause survives target edits. Never stop a newly edited
            // target using an old request: report the unapplied intent instead.
            if (MonitoringTaskIdentity.For(task) != MonitoringTaskIdentity.For(intent.Configuration.Configuration.GetTask(intent.Name)))
            { return new(false, "Stop intent is paused, but the target changed before execution; inspect and issue a new explicit Stop."); }
            RequireUserLogon(task, requireEnabled: false);
        }
        return await _orchestrator.ExecuteStopNodeAsync(task, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<CommandResult> ExecuteManualPlanAsync(ManualIntent intent)
    {
        foreach (var name in intent.Plan)
        {
            using var taskLock = await _orchestrator.AcquireTaskAsync(name, CancellationToken.None).ConfigureAwait(false);
            AutorunTask node;
            string? attempt = name.Equals(intent.Name, StringComparison.OrdinalIgnoreCase) ? intent.AttemptId : null;
            await using (var root = await RootWriteLease.AcquireAsync(_workspace.Paths).ConfigureAwait(false))
            {
                var current = await _workspace.ReadWithLeaseAsync(root).ConfigureAwait(false);
                var state = await _store.ReadAsync(heldLease: root).ConfigureAwait(false);
                if (!ManualStillCurrent(intent, current, state)) { return Superseded(); }
                node = current.Configuration.GetTask(name);
                RequireUserLogon(node, requireEnabled: true);
                if (Plan(current.Configuration, name).Any(dependency => Entry(state, dependency)?.Paused == true))
                { return new(false, $"Dependency/control is paused: {name}. Explicitly start or resume that entry first."); }
                if (attempt is null)
                {
                    var admission = await _store.BeginManualStartAsync(name, MonitoringTaskIdentity.For(node), state.Revision,
                        heldLease: root).ConfigureAwait(false);
                    attempt = admission.Attempt!.AttemptId;
                }
            }
            CommandResult result;
            try
            {
                if (intent.Command == "restart" && name.Equals(intent.Name, StringComparison.OrdinalIgnoreCase))
                {
                    result = await _orchestrator.ExecuteStopNodeAsync(node, CancellationToken.None).ConfigureAwait(false);
                    if (!result.Succeeded) { return result; }
                    // Stop may have arrived while restart was stopping. Do not resurrect it.
                    await using var root = await RootWriteLease.AcquireAsync(_workspace.Paths).ConfigureAwait(false);
                    var current = await _workspace.ReadWithLeaseAsync(root).ConfigureAwait(false);
                    var state = await _store.ReadAsync(heldLease: root).ConfigureAwait(false);
                    if (!ManualStillCurrent(intent, current, state)
                        || Plan(current.Configuration, name).Any(dependency => Entry(state, dependency)?.Paused == true))
                    { return Superseded(); }
                }
                result = await _orchestrator.ExecuteStartNodeAsync(node, observedFault: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (!name.Equals(intent.Name, StringComparison.OrdinalIgnoreCase))
                { await CompleteAsync(name, attempt!, RecoveryAttemptOutcome.OutcomeUnknown, exception.Message).ConfigureAwait(false); }
                throw;
            }
            if (!name.Equals(intent.Name, StringComparison.OrdinalIgnoreCase))
            { await CompleteAsync(name, attempt!, result.Succeeded ? RecoveryAttemptOutcome.Succeeded : RecoveryAttemptOutcome.Failed, result.Message).ConfigureAwait(false); }
            if (!result.Succeeded) { return new(false, $"Dependency/start failed: {name}: {result.Message}"); }
        }
        return new(true, $"Started: {intent.Name}");
    }

    private static bool ManualStillCurrent(ManualIntent intent, ConfigurationWorkspaceSnapshot current, RecoveryControlSnapshot state) =>
        current.Revision == intent.Configuration.Revision && state.Initialized
        && Entry(state, intent.Name) is { Paused: false } entry && entry.AttemptId == intent.AttemptId;

    internal async Task<IReadOnlyList<CommandResult>> RecoverObservedAsync(AgentMonitoringSnapshot cycle,
        CancellationToken token, bool requireRecoveryPreference = true, bool appLaunchOnly = false,
        Func<AgentMonitoringSnapshot, bool>? evidenceCurrent = null)
    {
        var results = new List<CommandResult>();
        bool EvidenceValid() => FreshEvidence(cycle) && (evidenceCurrent?.Invoke(cycle) ?? true);
        if (!_accepting || !EvidenceValid()) { return results; }
        var configuration = await _workspace.ReadAsync(token).ConfigureAwait(false);
        if (configuration.Revision != cycle.ConfigurationRevision) { return results; }
        var strictPresence = requireRecoveryPreference || appLaunchOnly;
        bool Allowed(AutorunTask task, ApplicationSettings preferences) => Eligible(task, requireTracking: false)
            && (appLaunchOnly ? TaskAutomationPolicy.StartsOnAppLaunch(task, preferences)
                : !requireRecoveryPreference || TaskAutomationPolicy.Recovers(task, preferences));
        var handled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in configuration.Configuration.Tasks.Where(task => Allowed(task, configuration.Preferences)))
        {
            var observation = Observation(cycle, candidate);
            if (observation is not { HealthObserved: true } || !strictPresence && observation.Healthy) { continue; }
            var plan = Plan(configuration.Configuration, candidate.Name);
            foreach (var name in plan)
            {
                if (handled.TryGetValue(name, out var succeeded)) { if (!succeeded) { break; } continue; }
                token.ThrowIfCancellationRequested();
                if (!_accepting || !EvidenceValid()) { return results; }
                using var taskLock = await _orchestrator.AcquireTaskAsync(name, token).ConfigureAwait(false);
                if (strictPresence)
                {
                    var presence = await _orchestrator.ObservePresenceAsync(configuration.Configuration.GetTask(name), token).ConfigureAwait(false);
                    if (!TaskOrchestrator.ConfirmedMissing(presence))
                    {
                        handled[name] = presence.Healthy && presence.Observed;
                        if (!handled[name]) { break; }
                        continue;
                    }
                }
                AutorunTask node;
                string? attemptId = null;
                await using (var root = await RootWriteLease.AcquireAsync(_workspace.Paths, token).ConfigureAwait(false))
                {
                    var current = await _workspace.ReadWithLeaseAsync(root, token).ConfigureAwait(false);
                    var state = await _store.ReadAsync(token, root).ConfigureAwait(false);
                    if (!_accepting || !EvidenceValid() || current.Revision != cycle.ConfigurationRevision) { return results; }
                    node = current.Configuration.GetTask(name);
                    if (!state.Initialized)
                    {
                        results.Add(new(false, "Recovery control state is missing; no start was executed. Explicitly Start/Resume entries before using recovery or group startup."));
                        return results;
                    }
                    // Validate the WHOLE dependency closure while holding root. No recursive
                    // executor can bypass per-node eligibility, pause, revision, or known health.
                    var valid = state.Initialized && Plan(current.Configuration, name).All(dependency =>
                    {
                        var configured = current.Configuration.GetTask(dependency);
                        return (Allowed(configured, current.Preferences)
                                || strictPresence && Eligible(configured, requireTracking: false) && Observation(cycle, configured) is { HealthObserved: true, Healthy: true })
                            && Entry(state, dependency)?.Paused != true
                            && (dependency.Equals(name, StringComparison.OrdinalIgnoreCase) || Entry(state, dependency)?.Outcome != RecoveryAttemptOutcome.Admitted)
                            && Observation(cycle, configured) is { HealthObserved: true };
                    });
                    if (!valid) { handled[name] = false; break; }
                    var known = Observation(cycle, node)!;
                    if (!strictPresence && known.Healthy) { handled[name] = true; continue; }
                    if (!Allowed(node, current.Preferences)) { handled[name] = false; break; }
                    var admission = await _store.TryAdmitAutomaticAsync(name, MonitoringTaskIdentity.For(node), state.Revision, token, root).ConfigureAwait(false);
                    if (admission.Attempt is null) { handled[name] = false; break; }
                    attemptId = admission.Attempt.AttemptId;
                }
                CommandResult result;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!EvidenceValid())
                    { result = new(false, "Observation expired during durable admission; no start was executed."); }
                    else if (strictPresence)
                    {
                        var presence = await _orchestrator.ObservePresenceAsync(node, token).ConfigureAwait(false);
                        result = TaskOrchestrator.ConfirmedMissing(presence) && EvidenceValid()
                            ? await _orchestrator.ExecuteStartNodeAsync(node, observedFault: true, token).ConfigureAwait(false)
                            : new(presence.Observed && presence.Healthy, "Target is present or absence is no longer confirmed; no automatic start was executed.");
                    }
                    else { result = await _orchestrator.ExecuteStartNodeAsync(node, observedFault: true, token).ConfigureAwait(false); }
                }
                catch (Exception exception)
                {
                    await CompleteAsync(name, attemptId!, RecoveryAttemptOutcome.OutcomeUnknown, exception.Message).ConfigureAwait(false);
                    throw;
                }
                await CompleteAsync(name, attemptId!, result.Succeeded ? RecoveryAttemptOutcome.Succeeded : RecoveryAttemptOutcome.Failed,
                    result.Message).ConfigureAwait(false);
                handled[name] = result.Succeeded;
                results.Add(result);
                if (!result.Succeeded) { break; }
            }
        }
        return results;
    }

    internal Task<IReadOnlyList<CommandResult>> StartMissingAsync(CancellationToken requestToken) =>
        StartMissingCoreAsync(requestToken, appLaunchOnly: false);

    internal Task<IReadOnlyList<CommandResult>> StartOnAppLaunchAsync(CancellationToken requestToken) =>
        StartMissingCoreAsync(requestToken, appLaunchOnly: true);

    private async Task<IReadOnlyList<CommandResult>> StartMissingCoreAsync(CancellationToken requestToken, bool appLaunchOnly)
    {
        // A startup/group request is not a per-task resume. It never clears durable pauses,
        // never wakes an unknown WSL guest, and does not require the automatic-recovery flag.
        var configuration = await _workspace.ReadAsync(requestToken).ConfigureAwait(false);
        if (appLaunchOnly)
        {
            if (!configuration.Configuration.Tasks.Any(task => Eligible(task, requireTracking: false)
                && TaskAutomationPolicy.StartsOnAppLaunch(task, configuration.Preferences))) { return []; }
            if (!(await _store.ReadAsync(requestToken).ConfigureAwait(false)).Initialized)
            { return [new(false, "持久控制状态缺失，不能确认历史手动停止意图；请先明确手动启动目标，后台不会自动重置状态。")]; }
        }
        var began = _clock.GetUtcNow();
        var observations = appLaunchOnly
            ? await Task.WhenAll(configuration.Configuration.Tasks.Select(async task => TaskSnapshot.From(task,
                await _orchestrator.ObservePresenceAsync(task, requestToken).ConfigureAwait(false)))).ConfigureAwait(false)
            : await _orchestrator.ObserveTasksAsync(configuration.Configuration, requestToken).ConfigureAwait(false);
        var cycle = new AgentMonitoringSnapshot(1, AgentProtocol.ForRoot(_workspace.Paths.RootDirectory).RootId, "group-start", 0, "",
            began, began, _clock.GetUtcNow(), configuration.Revision, 10,
            new(true, _clock.GetUtcNow(), began, observations, null), new(false, null, null, null, null),
            new(false, null, null, null, null), [], new(false, null, null, 0, 30));
        return await RecoverObservedAsync(cycle, requestToken, requireRecoveryPreference: false, appLaunchOnly).ConfigureAwait(false);
    }

    private bool FreshEvidence(AgentMonitoringSnapshot cycle)
    {
        var now = _clock.GetUtcNow();
        return cycle.RootId == AgentProtocol.ForRoot(_workspace.Paths.RootDirectory).RootId
            && cycle.ConfigurationRevision is not null && cycle.Tasks is { Available: true, Data: not null, ObservedAt: { } observed, AttemptedAt: { } began }
            && began <= observed && observed <= now && began <= now && now - began < EvidenceLifetime;
    }

    private static TaskSnapshot? Observation(AgentMonitoringSnapshot cycle, AutorunTask task) => cycle.Tasks.Data?.FirstOrDefault(row =>
        row.Name.Equals(task.Name, StringComparison.OrdinalIgnoreCase) && row.Type == task.Type.ToKey()
        && MonitoringTaskIdentity.For(row) == MonitoringTaskIdentity.For(task));
    private static bool Eligible(AutorunTask task, bool requireTracking) => task.Enabled && (!requireTracking || task.ExpectedRunning)
        && task.Launch.RunAs == RunAsMode.CurrentUser && task.Launch.Trigger == LaunchTrigger.Logon;
    private static void RequireUserLogon(AutorunTask task, bool requireEnabled)
    {
        if (task.Launch.RunAs != RunAsMode.CurrentUser || task.Launch.Trigger != LaunchTrigger.Logon)
        { throw new UnauthorizedAccessException("Interactive daemon control cannot execute SYSTEM/boot tasks; use the explicit elevated boot path."); }
        if (requireEnabled && !task.Enabled) { throw new ArgumentException($"Task is disabled: {task.Name}"); }
    }
    private static RecoveryTaskState? Entry(RecoveryControlSnapshot state, string name) => state.Tasks.FirstOrDefault(
        task => task.TaskName.Equals(name, StringComparison.OrdinalIgnoreCase));
    private static CommandResult Superseded() => new(false, "Task intent/configuration was superseded; inspect current state before another explicit action.");
    private Task<RecoveryControlSnapshot> CompleteAsync(string name, string attempt, RecoveryAttemptOutcome outcome, string detail) =>
        _store.CompleteAsync(name, attempt, outcome, detail, CancellationToken.None);

    private static string[] Plan(AutorunConfiguration configuration, string name)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        void Visit(string item)
        {
            if (visited.Contains(item)) { return; }
            if (visiting.Count >= 128 || !visiting.Add(item)) { throw new ArgumentException("Task dependency graph is cyclic or exceeds its control depth bound."); }
            var task = configuration.GetTask(item);
            foreach (var dependency in task.DependsOn) { Visit(dependency); }
            visiting.Remove(item); visited.Add(item); result.Add(task.Name);
        }
        Visit(name);
        return result.ToArray();
    }

    private void RequireAccepting()
    { if (!_accepting) { throw new AgentCommandException("controller_stopping", "Agent task control is stopping; no new intent was admitted."); } }
    private void Track(Task task)
    {
        lock (_operationsGate) { _operations.Add(task); }
        _ = task.ContinueWith(done => { lock (_operationsGate) { _operations.Remove(done); } },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    internal async Task DrainAsync()
    {
        Task[] pending;
        await _admissionGate.WaitAsync().ConfigureAwait(false);
        try { _accepting = false; lock (_operationsGate) { pending = _operations.ToArray(); } }
        finally { _admissionGate.Release(); }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }
    private sealed record ManualIntent(string Command, string Name, ConfigurationWorkspaceSnapshot Configuration,
        IReadOnlyList<string> Plan, string Token, string? AttemptId);
}
