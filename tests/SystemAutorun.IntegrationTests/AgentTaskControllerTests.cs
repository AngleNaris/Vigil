using System.Collections.Concurrent;
using System.IO;
using System.Text;
using SystemAutorun.Agent;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Recovery;
using SystemAutorun.Core.Services;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.IntegrationTests;

public sealed class AgentTaskControllerTests
{
    [Theory]
    [InlineData("unknown")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("unavailable")]
    [InlineData("identity")]
    [InlineData("disabled")]
    [InlineData("untracked")]
    [InlineData("system")]
    [InlineData("boot")]
    public async Task UnsafeOrIneligibleEvidenceNeverExecutes(string reason)
    {
        await using var fixture = new Fixture(new Node("a", Enabled: reason != "disabled", Tracked: reason != "untracked",
            RunAs: reason == "system" ? "system" : "current_user", Trigger: reason == "boot" ? "boot" : "logon"));
        await fixture.SeedAsync();
        var cycle = await fixture.CycleAsync();
        var row = cycle.Tasks.Data![0];
        cycle = reason switch
        {
            "unknown" => cycle with { Tasks = cycle.Tasks with { Data = [row with { HealthObserved = false }] } },
            "unavailable" => cycle with { Tasks = cycle.Tasks with { Available = false } },
            "identity" => cycle with { Tasks = cycle.Tasks with { Data = [row with { Executable = "different-never-run.exe" }] } },
            "future" => cycle with { Tasks = cycle.Tasks with { ObservedAt = fixture.Clock.GetUtcNow().AddSeconds(1) } },
            _ => cycle,
        };
        if (reason == "stale") { fixture.Clock.Advance(TimeSpan.FromSeconds(10)); }
        await fixture.Controller.RecoverObservedAsync(cycle, default);
        Assert.Empty(fixture.Executor.Events);
        Assert.Equal(0, fixture.Probe.ActiveCalls);
    }

    [Fact]
    public async Task DisabledPreferenceAndMissingStateDoNotAuthorizeRecovery()
    {
        await using var fixture = new Fixture(new Node("a"));
        await fixture.Controller.RecoverObservedAsync(await fixture.CycleAsync(), default);
        Assert.False((await fixture.Store.ReadAsync()).Initialized);
        await fixture.SeedAsync();
        await fixture.PreferenceAsync(false);
        await fixture.Controller.RecoverObservedAsync(await fixture.CycleAsync(), default);
        Assert.Empty(fixture.Executor.Events);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("disabled")]
    [InlineData("untracked")]
    [InlineData("system")]
    [InlineData("boot")]
    [InlineData("paused")]
    public async Task AnIneligibleDependencyCannotBeRecursivelyStarted(string reason)
    {
        await using var fixture = new Fixture(new Node("parent", ["dep"]),
            new Node("dep", Enabled: reason != "disabled", Tracked: reason != "untracked",
                RunAs: reason == "system" ? "system" : "current_user", Trigger: reason == "boot" ? "boot" : "logon"));
        await fixture.SeedAsync();
        if (reason == "paused") { await fixture.Store.SetPausedAsync("dep", true); }
        var cycle = await fixture.CycleAsync();
        if (reason == "unknown")
        { cycle = cycle with { Tasks = cycle.Tasks with { Data = cycle.Tasks.Data!.Select(row => row.Name == "dep" ? row with { HealthObserved = false } : row).ToArray() } }; }
        await fixture.Controller.RecoverObservedAsync(cycle, default);
        Assert.Empty(fixture.Executor.Events);
    }

    [Fact]
    public async Task DependenciesAreStartedOnceInOrderAndCooldownSurvivesFreshCycles()
    {
        await using var fixture = new Fixture(new Node("parent", ["dep"]), new Node("dep"));
        await fixture.SeedAsync();
        var cycle = await fixture.CycleAsync();
        await fixture.Controller.RecoverObservedAsync(cycle, default);
        Assert.Equal(new[] { "start:dep", "start:parent" }, fixture.Executor.Events);
        // Simulate another actual exit; a health failure while the process is still present
        // must no longer be treated as evidence that a second instance should be launched.
        fixture.Executor.Healthy["dep"] = false;
        fixture.Executor.Healthy["parent"] = false;
        await fixture.Controller.RecoverObservedAsync(cycle, default);
        Assert.Equal(2, fixture.Executor.Events.Count);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await fixture.Controller.RecoverObservedAsync(await fixture.CycleAsync(), default);
        Assert.Equal(4, fixture.Executor.Events.Count);
    }

    [Fact]
    public async Task StopDuringRecoveryPersistsPauseAndOutlivesRequestCancellation()
    {
        await using var fixture = new Fixture(new Node("a"));
        await fixture.SeedAsync();
        var entered = fixture.Signal(); var release = fixture.Signal();
        fixture.Executor.StartHandler = async _ => { entered.TrySetResult(); await release.Task; return new(true, "fake-start"); };
        var recovery = fixture.Own(fixture.Controller.RecoverObservedAsync(await fixture.CycleAsync(), default));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var request = new CancellationTokenSource();
        var stop = fixture.Own(fixture.Controller.StopAsync("a", request.Token));
        await fixture.WaitStateAsync(state => state.Tasks.Any(task => task.TaskName == "a" && task.Paused));
        request.Cancel();
        Assert.False(stop.IsCompleted);
        release.TrySetResult();
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await stop.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
        Assert.Equal(new[] { "start:a", "stop:a" }, fixture.Executor.Events);
        Assert.True(Assert.Single((await fixture.Store.ReadAsync()).Tasks).Paused);
    }

    [Fact]
    public async Task ANewStopSupersedesAPendingStartWithoutBlockingConfigurationWriters()
    {
        await using var fixture = new Fixture(new Node("a"));
        await fixture.SeedAsync();
        var gate = await fixture.Orchestrator.AcquireTaskAsync("a", default);
        try
        {
            var start = fixture.Own(fixture.Controller.StartAsync("a"));
            await fixture.WaitStateAsync(state => state.Tasks.Any(task => task.AttemptId is not null));
            var stop = fixture.Own(fixture.Controller.StopAsync("a"));
            await fixture.WaitStateAsync(state => state.Tasks.Any(task => task.Paused));
            await fixture.PreferenceAsync(false).WaitAsync(TimeSpan.FromSeconds(3));
            gate.Dispose();
            Assert.False((await start.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
            Assert.True((await stop.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
            Assert.Equal(new[] { "stop:a" }, fixture.Executor.Events);
        }
        finally { gate.Dispose(); }
    }

    [Fact]
    public async Task ANewStartSupersedesAnOlderQueuedStop()
    {
        await using var fixture = new Fixture(new Node("a"));
        await fixture.SeedAsync();
        var gate = await fixture.Orchestrator.AcquireTaskAsync("a", default);
        try
        {
            var stop = fixture.Own(fixture.Controller.StopAsync("a"));
            await fixture.WaitStateAsync(state => state.Tasks.Any(task => task.Paused));
            var start = fixture.Own(fixture.Controller.StartAsync("a"));
            await fixture.WaitStateAsync(state => state.Tasks.Any(task => !task.Paused && task.AttemptId is not null));
            gate.Dispose();
            Assert.False((await stop.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
            Assert.True((await start.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
            Assert.Equal(new[] { "start:a" }, fixture.Executor.Events);
        }
        finally { gate.Dispose(); }
    }

    [Fact]
    public async Task RestartDoesNotResurrectANewerStop()
    {
        await using var fixture = new Fixture(new Node("a"));
        await fixture.SeedAsync();
        var entered = fixture.Signal(); var release = fixture.Signal(); var count = 0;
        fixture.Executor.StopHandler = async _ =>
        { if (Interlocked.Increment(ref count) == 1) { entered.TrySetResult(); await release.Task; } return new(true, "fake-stop"); };
        var restart = fixture.Own(fixture.Controller.RestartAsync("a"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = fixture.Own(fixture.Controller.StopAsync("a"));
        await fixture.WaitStateAsync(state => state.Tasks.Any(task => task.Paused));
        release.TrySetResult();
        Assert.False((await restart.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
        Assert.True((await stop.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
        Assert.Equal(new[] { "stop:a", "stop:a" }, fixture.Executor.Events);
    }

    [Fact]
    public async Task RestartRechecksDependencyPauseAfterItsStopSideEffect()
    {
        await using var fixture = new Fixture(new Node("parent", ["dep"]), new Node("dep"));
        await fixture.SeedAsync();
        fixture.Executor.Healthy["dep"] = true;
        var entered = fixture.Signal(); var release = fixture.Signal();
        fixture.Executor.StopHandler = async task =>
        { if (task.Name == "parent") { entered.TrySetResult(); await release.Task; } return new(true, "fake-stop"); };
        var restart = fixture.Own(fixture.Controller.RestartAsync("parent"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await fixture.Controller.StopAsync("dep")).Succeeded);
        release.TrySetResult();
        Assert.False((await restart.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
        Assert.Equal(new[] { "stop:parent", "stop:dep" }, fixture.Executor.Events);
    }

    [Fact]
    public async Task RecoveryRechecksRevisionAfterWaitingForTheNodeLock()
    {
        await using var fixture = new Fixture(new Node("a"));
        await fixture.SeedAsync();
        var cycle = await fixture.CycleAsync();
        var gate = await fixture.Orchestrator.AcquireTaskAsync("a", default);
        try
        {
            var recovery = fixture.Own(fixture.Controller.RecoverObservedAsync(cycle, default));
            await fixture.PreferenceAsync(false);
            gate.Dispose();
            await recovery.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(fixture.Executor.Events);
        }
        finally { gate.Dispose(); }
    }

    [Fact]
    public async Task FirstManualControlOnMissingStateKeepsOtherNamesSafelyPaused()
    {
        await using var fixture = new Fixture(new Node("a"), new Node("b"));
        Assert.True((await fixture.Controller.StopAsync("a")).Succeeded);
        var state = await fixture.Store.ReadAsync();
        Assert.Equal(2, state.Tasks.Count);
        Assert.All(state.Tasks, entry => Assert.True(entry.Paused));
        Assert.True((await fixture.Controller.StartAsync("a")).Succeeded);
        state = await fixture.Store.ReadAsync();
        Assert.False(state.Tasks.Single(task => task.TaskName == "a").Paused);
        Assert.True(state.Tasks.Single(task => task.TaskName == "b").Paused);
        await fixture.Controller.RecoverObservedAsync(await fixture.CycleAsync(), default);
        Assert.DoesNotContain("start:b", fixture.Executor.Events);
    }

    [Fact]
    public async Task ResumeUsesRecoveryCasAndDoesNotExecuteAStart()
    {
        await using var fixture = new Fixture(new Node("a"));
        await fixture.SeedAsync();
        await fixture.Controller.StopAsync("a");
        var state = await fixture.Store.ReadAsync();
        var resumed = await fixture.Controller.ResumeAsync("a", state.Revision, default);
        Assert.False(Assert.Single(resumed.Tasks).Paused);
        await Assert.ThrowsAsync<AgentCommandException>(() => fixture.Controller.ResumeAsync("a", state.Revision, default));
        Assert.Equal(new[] { "stop:a" }, fixture.Executor.Events);
    }

    [Fact]
    public async Task ShutdownDrainsAnAcceptedStopBeforeRejectingNewControl()
    {
        await using var fixture = new Fixture(new Node("a"));
        await fixture.SeedAsync();
        var entered = fixture.Signal(); var release = fixture.Signal();
        fixture.Executor.StopHandler = async _ => { entered.TrySetResult(); await release.Task; return new(true, "fake-stop"); };
        var stop = fixture.Own(fixture.Controller.StopAsync("a"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var drain = fixture.Own(fixture.Controller.DrainAsync());
        Assert.False(drain.IsCompleted);
        await Assert.ThrowsAsync<AgentCommandException>(() => fixture.Controller.StartAsync("a"));
        release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RecoveryRechecksRevokedEvidenceBeforeAnyStart()
    {
        await using var fixture = new Fixture(new Node("a"));
        await fixture.SeedAsync(); var cycle = await fixture.CycleAsync(); var checks = 0;
        var result = await fixture.Controller.RecoverObservedAsync(cycle, default, evidenceCurrent: _ => ++checks < 3);
        Assert.True(checks >= 3); Assert.Empty(result); Assert.Empty(fixture.Executor.Events);
    }

    [Fact]
    public async Task LatestOnlyWorkerDoesNotBlockProducerOrReplayDroppedCycles()
    {
        await using var fixture = new Fixture(new Node("a"));
        await fixture.SeedAsync();
        var entered = fixture.Signal(); var release = fixture.Signal();
        fixture.Executor.StartHandler = async _ => { entered.TrySetResult(); await release.Task; return new(true, "fake-start"); };
        var worker = new AgentRecoveryWorker(fixture.Controller);
        using var stop = new CancellationTokenSource();
        var running = fixture.Own(worker.RunAsync(stop.Token));
        try
        {
            var cycle = await fixture.CycleAsync();
            worker.Enqueue(cycle);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < 1000; index++) { worker.Enqueue(cycle); }
            worker.Enqueue(cycle with { Tasks = cycle.Tasks with { Data = cycle.Tasks.Data!.Select(row => row with { Healthy = true }).ToArray() } });
            release.TrySetResult();
            await Fixture.EventuallyAsync(() => worker.CompletedCycles == 2);
            Assert.Equal(new[] { "start:a" }, fixture.Executor.Events);
            Assert.Null(worker.LastError);
        }
        finally { stop.Cancel(); release.TrySetResult(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task ACancelledStopRpcStillCommitsItsFinalOperationReceipt()
    {
        await using var fixture = new Fixture(new Node("a"));
        await fixture.SeedAsync();
        var entered = fixture.Signal(); var release = fixture.Signal();
        fixture.Executor.StopHandler = async _ => { entered.TrySetResult(); await release.Task; return new(true, "fake-stop"); };
        var journal = new OperationJournal(fixture.Paths);
        var dispatcher = new AgentCommandDispatcher(fixture.Orchestrator, new HostStatusReader(), fixture.Workspace,
            operations: journal, tasks: fixture.Controller);
        var id = AgentProtocol.NewOperationId();
        using var request = new CancellationTokenSource();
        var pending = fixture.Own(dispatcher.DispatchAsync(new("stop-rpc", "stop", "a", OperationId: id), request.Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        request.Cancel(); release.TrySetResult();
        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Ok);
        var receipt = await journal.GetAsync(id);
        Assert.NotNull(receipt);
        Assert.Equal("completed", receipt.State.ToString().ToLowerInvariant());
        Assert.True(Assert.Single((await fixture.Store.ReadAsync()).Tasks).Paused);
    }

    [Fact]
    public async Task GroupStartupDoesNotResumePausedTasksOrRequireAutomaticRecoveryEnabled()
    {
        await using var fixture = new Fixture(new Node("a"), new Node("b", Tracked: false));
        await fixture.SeedAsync();
        await fixture.Controller.StopAsync("a");
        await fixture.PreferenceAsync(false);
        await fixture.Controller.StartMissingAsync(default);
        Assert.Equal(new[] { "stop:a", "start:b" }, fixture.Executor.Events);
        Assert.True((await fixture.Store.ReadAsync()).Tasks.Single(task => task.TaskName == "a").Paused);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ExplicitPerTaskChoicesAreIndependentOfLegacyGlobalAndTracking(bool starts, bool recovers, bool appLaunch)
    {
        await using var fixture = new Fixture(new Node("a", Tracked: false, StartOnAppLaunch: starts, AutoRecover: recovers));
        await fixture.SeedAsync();
        await fixture.PreferenceAsync(false);
        if (appLaunch) { await fixture.Controller.StartOnAppLaunchAsync(default); }
        else { await fixture.Controller.RecoverObservedAsync(await fixture.CycleAsync(), default); }
        Assert.Equal(appLaunch ? starts : recovers, fixture.Executor.Events.Count == 1);
        Assert.All(fixture.Executor.Events, item => Assert.Equal("start:a", item));
    }

    [Fact]
    public async Task GuiLaunchAndRecoveryKeepManualPauseAcrossANewController()
    {
        await using var fixture = new Fixture(new Node("a", StartOnAppLaunch: true, AutoRecover: true));
        await fixture.SeedAsync();
        await fixture.Controller.StopAsync("a");
        var reopened = new AgentTaskController(fixture.Workspace, fixture.Orchestrator, new(fixture.Paths, fixture.Clock), fixture.Clock);
        await reopened.InitializeRuntimeAsync();
        await reopened.StartOnAppLaunchAsync(default);
        await reopened.RecoverObservedAsync(await fixture.CycleAsync(), default);
        Assert.Equal(new[] { "stop:a" }, fixture.Executor.Events);
        Assert.True(Assert.Single((await fixture.Store.ReadAsync()).Tasks).Paused);
        Assert.True((await reopened.StartAsync("a")).Succeeded);
        Assert.False(Assert.Single((await fixture.Store.ReadAsync()).Tasks).Paused);
        Assert.Equal(new[] { "stop:a", "start:a" }, fixture.Executor.Events);
        await reopened.DrainAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthyUnmarkedDependencyIsUsedButNeverAutomaticallyStarted(bool present)
    {
        await using var fixture = new Fixture(new Node("parent", ["dep"], AutoRecover: true), new Node("dep", AutoRecover: false));
        await fixture.SeedAsync();
        fixture.Executor.Healthy["dep"] = present;
        var cycle = await fixture.CycleAsync();
        cycle = cycle with { Tasks = cycle.Tasks with { Data = cycle.Tasks.Data!.Select(row => row.Name == "dep"
            ? row with { Healthy = true, Status = "OK", Reason = "tcp-open" } : row).ToArray() } };
        await fixture.Controller.RecoverObservedAsync(cycle, default);
        Assert.Equal(present ? new[] { "start:parent" } : [], fixture.Executor.Events.ToArray());
    }

    [Fact]
    public async Task ConfirmedMissingTargetIsRecoveredEvenIfItsCustomHealthEndpointStillResponds()
    {
        await using var fixture = new Fixture(new Node("a", AutoRecover: true));
        await fixture.SeedAsync();
        var cycle = await fixture.CycleAsync();
        cycle = cycle with { Tasks = cycle.Tasks with { Data = [cycle.Tasks.Data![0] with { Healthy = true, Status = "OK", Reason = "tcp-open" }] } };
        await fixture.Controller.RecoverObservedAsync(cycle, default);
        Assert.Equal(new[] { "start:a" }, fixture.Executor.Events);
    }

    [Fact]
    public async Task GuiOpenCheckReportsMissingIntentStateWithoutInitializingIt()
    {
        await using var fixture = new Fixture(new Node("a", StartOnAppLaunch: true, AutoRecover: true));
        var result = Assert.Single(await fixture.Controller.StartOnAppLaunchAsync(default));
        Assert.False(result.Succeeded);
        Assert.Contains("持久控制状态缺失", result.Message);
        Assert.False((await fixture.Store.ReadAsync()).Initialized);
        Assert.Empty(fixture.Executor.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticPathsNeverStartAnExistingOrUnverifiableTarget(bool unknown)
    {
        await using var fixture = new Fixture(new Node("a", StartOnAppLaunch: true, AutoRecover: true));
        await fixture.SeedAsync();
        fixture.Probe.Presence = _ => unknown ? HealthResult.Unknown("identity-unreadable", "unknown")
            : HealthResult.Ok("process-running", "present despite a failing configured health check");
        await fixture.Controller.StartOnAppLaunchAsync(default);
        await fixture.Controller.RecoverObservedAsync(await fixture.CycleAsync(), default);
        Assert.Empty(fixture.Executor.Events);
        Assert.Equal(0, fixture.Probe.ActiveCalls);
    }

    private sealed record Node(string Name, string[]? Depends = null, bool Enabled = true, bool Tracked = true,
        string RunAs = "current_user", string Trigger = "logon", bool? StartOnAppLaunch = null, bool? AutoRecover = null);

    private sealed class Clock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan interval) => Interlocked.Add(ref _ticks, interval.Ticks);
    }

    private sealed class Executor : ITaskExecutor
    {
        public ConcurrentQueue<string> Events { get; } = new();
        public ConcurrentDictionary<string, bool> Healthy { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Func<AutorunTask, Task<CommandResult>>? StartHandler { get; set; }
        public Func<AutorunTask, Task<CommandResult>>? StopHandler { get; set; }
        public async Task<CommandResult> StartAsync(AutorunTask task, CancellationToken cancellationToken = default)
        {
            Events.Enqueue("start:" + task.Name);
            var result = StartHandler is null ? new(true, "fake-start") : await StartHandler(task);
            if (result.Succeeded) { Healthy[task.Name] = true; }
            return result;
        }
        public async Task<CommandResult> StopAsync(AutorunTask task, CancellationToken cancellationToken = default)
        {
            Events.Enqueue("stop:" + task.Name);
            var result = StopHandler is null ? new(true, "fake-stop") : await StopHandler(task);
            if (result.Succeeded) { Healthy[task.Name] = false; }
            return result;
        }
        public IReadOnlyList<int> FindProcessIds(AutorunTask task) => [];
    }
    private sealed class Probe(Executor executor) : IHealthProbe
    {
        private int _activeCalls;
        public int ActiveCalls => Volatile.Read(ref _activeCalls);
        public Func<AutorunTask, HealthResult>? Presence { get; set; }
        public Task<HealthResult> CheckAsync(AutorunTask task, bool quick, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _activeCalls); return CheckPassiveAsync(task, cancellationToken); }
        public Task<HealthResult> CheckPassiveAsync(AutorunTask task, CancellationToken cancellationToken = default) =>
            Task.FromResult(Presence?.Invoke(task) ?? (executor.Healthy.TryGetValue(task.Name, out var healthy) && healthy
                ? HealthResult.Ok("process-running", "fake") : HealthResult.Fail("process-not-found", "fake")));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("SystemAutorun-recovery-controller-tests-").FullName;
        public ProjectPaths Paths { get; }
        public ConfigurationWorkspace Workspace { get; }
        public RecoveryControlStore Store { get; }
        public Clock Clock { get; } = new();
        public Executor Executor { get; } = new();
        public Probe Probe { get; }
        public TaskOrchestrator Orchestrator { get; }
        public AgentTaskController Controller { get; }
        private readonly List<TaskCompletionSource> _signals = [];
        private readonly List<Task> _owned = [];
        public Fixture(params Node[] nodes)
        {
            try
            {
                var yaml = new StringBuilder("version: 1\nsettings:\n  default_startup_delay: 0\ntasks:\n");
                foreach (var node in nodes)
                {
                    yaml.Append($"  - name: {node.Name}\n    type: windows\n    exec: never-execute-recovery-fixture.exe\n    enabled: {node.Enabled.ToString().ToLowerInvariant()}\n    expected_running: {node.Tracked.ToString().ToLowerInvariant()}\n");
                    if (node.StartOnAppLaunch is { } starts) { yaml.Append($"    start_on_app_launch: {starts.ToString().ToLowerInvariant()}\n"); }
                    if (node.AutoRecover is { } recovers) { yaml.Append($"    auto_recover: {recovers.ToString().ToLowerInvariant()}\n"); }
                    yaml.Append($"    depends_on: [{string.Join(", ", node.Depends ?? [])}]\n    launch:\n      run_as: {node.RunAs}\n      trigger: {node.Trigger}\n    health:\n      type: process\n      pattern: never-execute-recovery-fixture\n      timeout: 1\n");
                }
                File.WriteAllText(Path.Combine(Root, "autorun.yaml"), yaml.ToString());
                File.WriteAllText(Path.Combine(Root, "autorun.gui.settings.json"), "{\"auto_recover_failed_tasks\":true}");
                Paths = ProjectPaths.Resolve(Root); Workspace = new(Paths); Store = new(Paths, Clock); Probe = new(Executor);
                Orchestrator = new(new ConfigurationCache(new ConfigurationLoader(), Paths), new OverrideStore(), Executor, Probe, new FileLog());
                Controller = new(Workspace, Orchestrator, Store, Clock);
            }
            catch { Cleanup(); throw; }
        }
        public async Task SeedAsync()
        { var state = await Store.ReadAsync(); await Store.InitializeEmptyAsync(state.Revision); }
        public async Task PreferenceAsync(bool enabled)
        { var state = await Workspace.ReadAsync(); await Workspace.PatchSettingsAsync(new(AutoRecoverFailedTasks: enabled), state.Revision); }
        public async Task<AgentMonitoringSnapshot> CycleAsync()
        {
            var configuration = await Workspace.ReadAsync(); var now = Clock.GetUtcNow();
            var observations = configuration.Configuration.Tasks.Select(task => TaskSnapshot.From(task, HealthResult.Fail("fake", "fake"))).ToArray();
            return new(1, AgentProtocol.ForRoot(Root).RootId, "fixture", 0, "", now, now, now, configuration.Revision, 10,
                new(true, now, now, observations, null), new(false, null, null, null, null), new(false, null, null, null, null), [], new(false, null, null, 0, 30));
        }
        public TaskCompletionSource Signal() { var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _signals.Add(signal); return signal; }
        public T Own<T>(T task) where T : Task { _owned.Add(task); return task; }
        public async Task WaitStateAsync(Func<RecoveryControlSnapshot, bool> ready)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!ready(await Store.ReadAsync(timeout.Token))) { await Task.Delay(5, timeout.Token); }
        }
        public static async Task EventuallyAsync(Func<bool> ready)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!ready()) { await Task.Delay(5, timeout.Token); }
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var signal in _signals) { signal.TrySetResult(); }
            try { await Task.WhenAll(_owned).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception) when (_owned.All(task => task.IsCompleted)) { }
            await Controller.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Cleanup();
        }
        private void Cleanup()
        {
            var resolved = Path.GetFullPath(Root);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(resolved));
            Assert.StartsWith("SystemAutorun-recovery-controller-tests-", Path.GetFileName(resolved));
            Directory.Delete(resolved, recursive: true);
        }
    }
}
