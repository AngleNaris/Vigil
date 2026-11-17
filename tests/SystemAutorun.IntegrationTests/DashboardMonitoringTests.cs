using System.IO;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;
using SystemAutorun.Core.Services;

namespace SystemAutorun.IntegrationTests;

public sealed partial class DashboardMonitoringTests
{
    [Fact]
    public async Task HiddenRefreshKeepsPassiveAlertsButSkipsHistoryLogsCacheAndVisualTicks()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var cursor = new AgentConfigurationClient(fixture.Client); _ = await cursor.ReadAsync();
        var executable = Path.Combine(fixture.Root, "never-run-fixture.exe"); await File.WriteAllTextAsync(executable, "not executable");
        _ = await cursor.MutateAsync<ConfigurationMutationResult>("task-add", payload: new AddTaskRequest("fixture", AutorunTaskType.Windows,
            Executable: executable, HealthType: HealthCheckType.Process, HealthPattern: "never-run-fixture.exe", ExpectedRunning: true));
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var adapter = new DashboardFixtureMonitoringClient(fixture);
            var model = new MainWindowViewModel(fixture.Client, new NativeCacheStore(fixture.Paths.GuiCachePath), cursor, fixture.Paths, new NeverRemoteReader(), adapter);
            model.InitializeDashboard();
            try
            {
                await model.InitializeAsync();
                var alerts = new List<string>(); model.AlertRequested += (_, message) => alerts.Add(message);
                var configuration = await cursor.ReadAsync();
                var snapshot = adapter.Snapshot(configuration);
                adapter.SnapshotReader = _ => Task.FromResult(snapshot with
                {
                    Tasks = snapshot.Tasks with { Data = [snapshot.Tasks.Data![0] with { Status = "FAIL", Healthy = false, HealthObserved = true, Reason = "fixture-fault" }] },
                });
                model.SetPresentationActive(false);
                var queries = adapter.Queries.Count; var reads = adapter.SnapshotReads; var revision = model.DashboardRevision;
                var cache = await File.ReadAllTextAsync(fixture.Paths.GuiCachePath);
                var logs = model.ExceptionLogs.Count;
                await File.WriteAllTextAsync(Path.Combine(fixture.Root, "hidden-fixture.log"), "Warning: hidden read fixture");
                await model.RefreshCommand.ExecuteAsync(null); model.TickDashboard();
                Assert.Equal(reads + 1, adapter.SnapshotReads); Assert.Equal(queries, adapter.Queries.Count);
                Assert.Equal(revision, model.DashboardRevision); Assert.Equal(logs, model.ExceptionLogs.Count);
                Assert.Equal(cache, await File.ReadAllTextAsync(fixture.Paths.GuiCachePath)); Assert.Single(alerts);
                Assert.False(model.IsBusy); Assert.False(model.IsRefreshing);
                model.SetPresentationActive(true); await model.RefreshCommand.ExecuteAsync(null);
                Assert.True(adapter.Queries.Count > queries); Assert.True(model.DashboardRevision > revision);
                Assert.Contains(model.ExceptionLogs, row => row.Message.Contains("hidden read fixture", StringComparison.Ordinal));
                Assert.Single(alerts); // Re-observing the same fault is not another tray alarm.
            }
            finally { model.DetachDashboard(); await model.DashboardHistoryTask; await model.ResourceHistoryTask; }
        });
    }

    [Fact]
    public async Task SlowPollingDoesNotDisableAddAndItsLateSnapshotCannotUndoTheCommittedTask()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var cursor = new AgentConfigurationClient(fixture.Client); var initial = await cursor.ReadAsync();
        var executable = Path.Combine(fixture.Root, "never-run-fixture.exe"); await File.WriteAllTextAsync(executable, "not executable");
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var adapter = new DashboardFixtureMonitoringClient(fixture);
            var model = new MainWindowViewModel(fixture.Client, new NativeCacheStore(fixture.Paths.GuiCachePath), cursor, fixture.Paths, new NeverRemoteReader(), adapter);
            model.InitializeDashboard();
            var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reply = new TaskCompletionSource<AgentMonitoringSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? pending = null;
            try
            {
                await model.InitializeAsync();
                adapter.SnapshotReader = token => { adapter.SnapshotReader = null; entered.TrySetResult(token); return reply.Task; };
                pending = model.RefreshCommand.ExecuteAsync(null);
                var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(model.IsRefreshing); Assert.False(model.IsBusy); Assert.True(model.AddProgramCommand.CanExecute(null));
                Assert.True(await model.AddTaskAsync(new AddTaskRequest("retained", AutorunTaskType.Windows,
                    Executable: executable, HealthType: HealthCheckType.Process, HealthPattern: "never-run-fixture.exe", ExpectedRunning: false)));
                Assert.True(token.IsCancellationRequested);
                reply.TrySetResult(adapter.Snapshot(initial)); await pending;
                Assert.Equal("retained", Assert.Single(model.Tasks).Name);
                Assert.False(model.IsBusy); Assert.False(model.IsRefreshing);
            }
            finally
            {
                reply.TrySetResult(adapter.Snapshot(initial)); if (pending is not null) { await pending; }
                model.DetachDashboard(); await model.DashboardHistoryTask;
            }
        });
    }

    [Fact]
    public async Task DashboardRefreshIsEnabledByDefaultForSharedClientUpdates()
    {
        await WithModel(async (model, _, _) =>
        {
            Assert.True(model.AutoRefresh);
            Assert.Equal("1s", model.DashboardRefreshLabel);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task FailedConfigurationPollingClearsCurrentDataWithoutModalOrLateHistoryResurrection()
    {
        await WithModel(async (model, adapter, configuration) =>
        {
            model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration));
            var errors = new List<string>();
            model.ErrorRequested += (_, error) => errors.Add(error);
            var reply = new TaskCompletionSource<MonitoringHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            MonitoringHistoryQuery? captured = null;
            adapter.HistoryReader = (query, _) => { captured = query; return reply.Task; };
            model.SelectDashboardWindowCommand.Execute("168");
            var pending = model.DashboardHistoryTask;
            await adapter.MakeConfigurationUnreadableAsync();
            try { await model.RefreshCommand.ExecuteAsync(null); }
            finally { reply.TrySetResult(adapter.History(captured!, ObservationHealth.Healthy)); }
            await pending;
            Assert.Empty(errors);
            Assert.False(model.IsBusy);
            Assert.False(model.HostStatusAvailable);
            Assert.Empty(model.ManagedProcesses);
            Assert.All(model.Tasks, row => Assert.Equal("UNKNOWN", row.Status));
            Assert.Equal("--", model.DashboardCpuText);
            Assert.Equal("--", model.DashboardSelectedUptime);
            Assert.Equal("--", model.DashboardAvailabilityText);
            Assert.Empty(model.DashboardHealthHistory);
            Assert.Contains("不可用", model.StatusMessage);
            Assert.Contains("配置不可用", model.DashboardHistoryHelp);
            var queryCount = adapter.Queries.Count;
            model.SelectedTask = model.Tasks.Last();
            await model.DashboardHistoryTask;
            Assert.Equal(queryCount, adapter.Queries.Count);
        });
    }

    [Fact]
    public async Task UnavailableGroupsNeverPromoteTheirCachedPayloadOrHeartbeat()
    {
        await WithModel(async (model, adapter, configuration) =>
        {
            var snapshot = adapter.Snapshot(configuration);
            model.ApplyMonitoringSnapshot(snapshot);
            Assert.NotEqual("--", model.DashboardCpuText);
            Assert.Equal("2天 00:00:00", model.DashboardSelectedUptime);
            model.ApplyMonitoringSnapshot(snapshot with
            {
                ReturnedAt = DateTimeOffset.UtcNow, HeartbeatAt = DateTimeOffset.UtcNow,
                Tasks = snapshot.Tasks with { Available = false, Error = "failed-probe" },
                Processes = snapshot.Processes with { Available = false }, Host = snapshot.Host with { Available = false },
            });
            Assert.All(model.Tasks, row => { Assert.Equal("UNKNOWN", row.Status); Assert.False(row.Snapshot.HealthObserved); });
            Assert.Empty(model.ManagedProcesses);
            Assert.False(model.HostStatusAvailable);
            Assert.Equal("--", model.HostUptime);
            Assert.Equal("--", model.DashboardCpuText);
            Assert.Equal("--", model.DashboardWorkingSetText);
            Assert.Equal("--", model.DashboardSelectedUptime);
            Assert.Equal("--:--:--", model.DashboardSampleClock);
            await Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(-31)]
    [InlineData(6)]
    public async Task StaleOrFutureGroupsRemainUnknownEvenWithFreshReturnTime(int offset)
    {
        await WithModel(async (model, adapter, configuration) =>
        {
            var snapshot = adapter.Snapshot(configuration, DateTimeOffset.UtcNow.AddSeconds(offset));
            model.ApplyMonitoringSnapshot(snapshot with { ReturnedAt = DateTimeOffset.UtcNow });
            Assert.Equal("UNKNOWN", model.SelectedTask?.Status);
            Assert.Equal("--", model.DashboardCpuText);
            Assert.False(model.HostStatusAvailable);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task HostAndProcessAvailabilityAreIndependent()
    {
        await WithModel(async (model, adapter, configuration) =>
        {
            var snapshot = adapter.Snapshot(configuration);
            model.ApplyMonitoringSnapshot(snapshot with { Host = snapshot.Host with { Available = false } });
            Assert.NotEqual("--", model.DashboardCpuText);
            Assert.NotEqual("--", model.DashboardWorkingSetText);
            Assert.Equal("--", model.DashboardMemoryPercentText);
            Assert.False(model.HostStatusAvailable);
            model.ApplyMonitoringSnapshot(snapshot with { ReturnedAt = DateTimeOffset.UtcNow,
                Processes = snapshot.Processes with { Available = false } });
            Assert.True(model.HostStatusAvailable);
            Assert.Equal("--", model.DashboardCpuText);
            Assert.Equal("--", model.DashboardSelectedUptime);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ReplacingATargetRejectsOldProcessAndHistoryIdentity()
    {
        await WithModel(async (model, adapter, configuration) =>
        {
            var snapshot = adapter.Snapshot(configuration);
            model.ApplyMonitoringSnapshot(snapshot);
            var previous = configuration.Configuration.Tasks[0];
            var replacement = previous with { Executable = "isolated-replacement.exe" };
            var changed = configuration with { Configuration = configuration.Configuration with
                { Tasks = [replacement, configuration.Configuration.Tasks[1]] } };
            model.ApplyMonitoringConfiguration(changed);
            model.ApplyMonitoringSnapshot(snapshot with { ReturnedAt = DateTimeOffset.UtcNow });
            Assert.Equal("UNKNOWN", model.SelectedTask?.Status);
            Assert.Equal("--", model.DashboardCpuText);
            Assert.Equal("--", model.DashboardSelectedUptime);
            model.SelectedTask = model.Tasks.Last();
            model.SelectedTask = model.Tasks.First();
            await model.DashboardHistoryTask;
            Assert.Equal(MonitoringTaskIdentity.For(replacement), adapter.Queries.Last().TaskIdentity);
            Assert.NotEqual(MonitoringTaskIdentity.For(previous), adapter.Queries.Last().TaskIdentity);
        });
    }

    [Fact]
    public async Task ServicesUseExplicitContinuousHealthNotTheServiceHostsStartTime()
    {
        await WithModel(async (model, adapter, configuration) =>
        {
            var service = configuration.Configuration.Tasks[0] with { Type = AutorunTaskType.WindowsService,
                Executable = null, Service = "isolated-nonexistent-service", Launch = LaunchOptions.LegacyDefault(AutorunTaskType.WindowsService) };
            var changed = configuration with { Configuration = configuration.Configuration with { Tasks = [service] } };
            model.ApplyMonitoringConfiguration(changed);
            var snapshot = adapter.Snapshot(changed);
            var at = snapshot.Tasks.ObservedAt!.Value;
            var identity = MonitoringTaskIdentity.For(service);
            snapshot = snapshot with { Runtime = [new(service.Name, identity, at, null, "unavailable", "shared-host",
                at.AddMinutes(-7), "consecutive-passive-health-observations")] };
            model.ApplyMonitoringSnapshot(snapshot);
            Assert.Equal("连续健康 00:07:00", model.DashboardSelectedUptime);
            Assert.Contains("不是服务自身 uptime", model.DashboardUptimeHelp);
            model.ApplyMonitoringSnapshot(snapshot with { ReturnedAt = DateTimeOffset.UtcNow,
                Runtime = [snapshot.Runtime[0] with { ConsecutiveHealthySince = null, ConsecutiveHealthSource = "unavailable" }] });
            Assert.Equal("--", model.DashboardSelectedUptime);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task EmptyHistoryIsZeroCoverageNotInventedAvailabilityAndTheWindowStaysAnchored()
    {
        await WithModel(async (model, adapter, configuration) =>
        {
            model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration));
            foreach (var hours in new[] { 168, 720, 24 })
            {
                model.SelectDashboardWindowCommand.Execute(hours.ToString());
                await model.DashboardHistoryTask;
                Assert.Equal(TimeSpan.FromHours(hours), adapter.Queries.Last().To - adapter.Queries.Last().From);
                Assert.Equal("--", model.DashboardAvailabilityText);
                Assert.Equal($"{0d:0.0}%", model.DashboardCoverageText);
                Assert.Empty(model.DashboardHealthHistory);
                Assert.All(model.DashboardCpuTrend, point => Assert.Null(point.Value));
                Assert.Contains("没有历史记录", model.DashboardHistoryHelp);
                var end = model.DashboardHistoryEnd;
                model.TickDashboard();
                Assert.Equal(end, model.DashboardHistoryEnd);
            }
        });
    }

    [Fact]
    public async Task LateHistoryReplyCannotOverwriteAnotherSelectionOrWindow()
    {
        await WithModel(async (model, adapter, configuration) =>
        {
            model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration));
            var pending = new List<(MonitoringHistoryQuery Query, TaskCompletionSource<MonitoringHistoryResult> Reply)>();
            adapter.HistoryReader = (query, _) =>
            {
                var reply = new TaskCompletionSource<MonitoringHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Add((query, reply));
                return reply.Task; // Deliberately ignores cancellation to exercise the generation check.
            };
            model.SelectedTask = null;
            model.SelectedTask = model.Tasks.First();
            var old = model.DashboardHistoryTask;
            model.SelectedTask = model.Tasks.Last();
            var middle = model.DashboardHistoryTask;
            model.SelectDashboardWindowCommand.Execute("168");
            var current = model.DashboardHistoryTask;
            Assert.Equal(3, pending.Count);
            pending[2].Reply.SetResult(adapter.History(pending[2].Query, ObservationHealth.Healthy));
            await current;
            Assert.Equal($"{100d:0.0}%", model.DashboardAvailabilityText);
            Assert.Equal("beta", model.SelectedTask?.Name);
            Assert.Equal("healthy", Assert.Single(model.DashboardHealthHistory).State);
            foreach (var item in pending.Take(2)) { item.Reply.SetResult(adapter.History(item.Query, ObservationHealth.Fault)); }
            await Task.WhenAll(old, middle);
            Assert.Equal($"{100d:0.0}%", model.DashboardAvailabilityText);
            Assert.Equal("healthy", Assert.Single(model.DashboardHealthHistory).State);
            Assert.Equal(168, model.DashboardWindowHours);
        });
    }

    [Fact]
    public async Task HistoryFailureOrWrongIdentityClearsPreviouslySuccessfulData()
    {
        await WithModel(async (model, adapter, configuration) =>
        {
            model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration));
            adapter.HistoryReader = (query, _) => Task.FromResult(adapter.History(query, ObservationHealth.Fault));
            model.SelectDashboardWindowCommand.Execute("168");
            await model.DashboardHistoryTask;
            Assert.Equal($"{0d:0.0}%", model.DashboardAvailabilityText);
            Assert.Equal("failed", Assert.Single(model.DashboardHealthHistory).State);
            adapter.HistoryReader = (query, _) => Task.FromResult(adapter.History(query, ObservationHealth.Healthy) with { TaskIdentity = "foreign" });
            model.SelectDashboardWindowCommand.Execute("720");
            await model.DashboardHistoryTask;
            Assert.Equal("--", model.DashboardAvailabilityText);
            Assert.Equal("--", model.DashboardCoverageText);
            Assert.Empty(model.DashboardHealthHistory);
            Assert.Empty(model.DashboardCpuTrend);
            Assert.Contains("身份", model.DashboardHistoryHelp);
        });
    }

    [Fact]
    public async Task ClosingTheDashboardRejectsAnUncooperativeLateReply()
    {
        await WithModel(async (model, adapter, configuration) =>
        {
            model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration));
            var reply = new TaskCompletionSource<MonitoringHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            MonitoringHistoryQuery? captured = null;
            adapter.HistoryReader = (query, _) => { captured = query; return reply.Task; };
            model.SelectDashboardWindowCommand.Execute("168");
            var pending = model.DashboardHistoryTask;
            model.DetachDashboard();
            reply.SetResult(adapter.History(captured!, ObservationHealth.Healthy));
            await pending;
            Assert.Equal("--", model.DashboardAvailabilityText);
            Assert.Empty(model.DashboardHealthHistory);
        });
    }

    [Fact]
    public async Task SnapshotBodyMustMatchRootSchemaAndMonotonicReturnOrder()
    {
        await WithModel(async (model, adapter, configuration) =>
        {
            var snapshot = adapter.Snapshot(configuration);
            Assert.Throws<InvalidDataException>(() => model.ApplyMonitoringSnapshot(snapshot with { RootId = "foreign" }));
            Assert.Throws<InvalidDataException>(() => model.ApplyMonitoringSnapshot(snapshot with { SchemaVersion = 99 }));
            model.ApplyMonitoringSnapshot(snapshot);
            Assert.Throws<InvalidDataException>(() => model.ApplyMonitoringSnapshot(snapshot with { ReturnedAt = snapshot.ReturnedAt.AddSeconds(-1) }));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PassiveReadAdapterDoesNotFallBackToWorkloadProbesWhenMonitorIsUnavailable()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var cursor = new AgentConfigurationClient(fixture.Client);
        _ = await cursor.ReadAsync();
        _ = await cursor.MutateAsync<ConfigurationMutationResult>("task-add", payload: new AddTaskRequest("isolated-wsl",
            AutorunTaskType.WslBoot, Distro: "never-execute-fixture", HealthType: HealthCheckType.WslRunning, ExpectedRunning: true));
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var model = new MainWindowViewModel(fixture.Client, new NativeCacheStore(fixture.Paths.GuiCachePath), cursor, fixture.Paths, new NeverRemoteReader());
            model.InitializeDashboard();
            try
            {
                var errors = new List<string>();
                model.ErrorRequested += (_, error) => errors.Add(error);
                await model.InitializeAsync();
                Assert.Equal("UNKNOWN", Assert.Single(model.Tasks).Status);
                Assert.False(model.Tasks[0].Snapshot.HealthObserved);
                Assert.False(model.HostStatusAvailable);
                Assert.Contains("不可用", model.StatusMessage);
                Assert.Empty(errors);
                // ServerFixture's health probe/executor throw on any workload probe/run.
            }
            finally { model.DetachDashboard(); }
        });
    }

    [Fact]
    public async Task LiveReadAppliesResourcesWithoutBusyStateOrResettingTheServiceList()
    {
        await WithModel(async (model, adapter, configuration) =>
        {
            var observed = DateTimeOffset.UtcNow;
            model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration, observed));
            var row = model.Tasks[0];
            var resets = 0;
            ((System.Collections.Specialized.INotifyCollectionChanged)model.TasksView).CollectionChanged += (_, args) =>
            { if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) { resets++; } };
            var refreshing = false;
            model.PropertyChanged += (_, args) => refreshing |= args.PropertyName is nameof(model.IsRefreshing) or nameof(model.IsBusy);
            var live = adapter.Snapshot(configuration, observed);
            var at = observed.AddSeconds(1);
            adapter.SnapshotReader = _ => Task.FromResult(live with
            {
                ReturnedAt = at,
                Host = live.Host with { Data = live.Host.Data! with { CapturedAt = at, CpuUsagePercent = 41 } },
            });
            await model.RefreshLiveAsync();
            Assert.Equal("41%", model.CpuUsageText);
            Assert.Equal(0, resets);
            Assert.Same(row, model.Tasks[0]);
            Assert.False(refreshing);

            // A newer health observation does reconcile the list.
            var next = observed.AddSeconds(2);
            adapter.SnapshotReader = _ => Task.FromResult(adapter.Snapshot(configuration, next));
            await model.RefreshLiveAsync(); Assert.Equal(0, resets); // Same cadence slot must not read twice.
            await Task.Delay(1050); await model.RefreshLiveAsync();
            Assert.Equal(1, resets);
        });
    }

    [Fact]
    public async Task LiveReadIsSkippedForHiddenPausedOrBusyViewsAndNeverReportsFailure()
    {
        await WithModel(async (model, adapter, configuration) =>
        {
            model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration));
            var before = adapter.SnapshotReads;
            model.SetPresentationActive(false);
            await model.RefreshLiveAsync();
            model.SetPresentationActive(true);
            model.AutoRefresh = false;
            await model.RefreshLiveAsync();
            model.AutoRefresh = true;
            Assert.Equal(before, adapter.SnapshotReads);

            var status = model.StatusMessage;
            var cpu = model.CpuUsageText;
            adapter.SnapshotReader = _ => throw new IOException("Fixture agent unavailable.");
            await model.RefreshLiveAsync();
            Assert.Equal(before + 1, adapter.SnapshotReads);
            Assert.Equal(status, model.StatusMessage);
            Assert.Equal(cpu, model.CpuUsageText);
        });
    }

    private static async Task WithModel(Func<MainWindowViewModel, DashboardFixtureMonitoringClient, ConfigurationWorkspaceSnapshot, Task> action)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var cursor = new AgentConfigurationClient(fixture.Client);
        var loaded = await cursor.ReadAsync();
        var launch = new LaunchOptions(RunAsMode.CurrentUser, LaunchTrigger.Manual, WindowMode.Hidden, ProcessLifecycle.Tracked);
        AutorunTask Definition(string name) => new(name, AutorunTaskType.Windows, true, "isolated model fixture", $"{name}-never-executed.exe", [], null,
            null, null, null, [], 0, new(HealthCheckType.Process, $"{name}-never-executed.exe", null, null, 1, null, null), launch, true);
        var configuration = loaded with { Configuration = loaded.Configuration with { Tasks = [Definition("alpha"), Definition("beta")] } };
        var adapter = new DashboardFixtureMonitoringClient(fixture);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var model = new MainWindowViewModel(fixture.Client, new NativeCacheStore(fixture.Paths.GuiCachePath), cursor, fixture.Paths, new NeverRemoteReader(), adapter);
            model.InitializeDashboard();
            model.ApplyMonitoringConfiguration(configuration);
            try { await action(model, adapter, configuration); }
            finally { model.DetachDashboard(); await model.DashboardHistoryTask; await model.ResourceHistoryTask; }
        });
    }

    private sealed class NeverRemoteReader : IRemoteHostReader
    {
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Monitoring model fixtures must not connect to remote hosts.");
    }
}

// Explicit presentation fake: real Agent RPC tests and native UI evidence are separate.
internal sealed class DashboardFixtureMonitoringClient(ConfigurationControlPlaneTests.ServerFixture fixture) : IDashboardMonitoringClient
{
    private readonly string _instance = Guid.NewGuid().ToString("N");
    public List<MonitoringHistoryQuery> Queries { get; } = [];
    public Func<MonitoringHistoryQuery, CancellationToken, Task<MonitoringHistoryResult>>? HistoryReader { get; set; }
    public List<ResourceHistoryQuery> ResourceQueries { get; } = [];
    public Func<ResourceHistoryQuery, CancellationToken, Task<ResourceHistoryResult>>? ResourceReader { get; set; }
    public List<ResourceHistoryQuery> RecentResourceQueries { get; } = [];
    public Func<ResourceHistoryQuery, CancellationToken, Task<RecentResourceHistoryResult>>? RecentResourceReader { get; set; }

    public Task<RecentResourceHistoryResult> ReadRecentResourcesAsync(ResourceHistoryQuery query, CancellationToken cancellationToken = default)
    {
        RecentResourceQueries.Add(query);
        return RecentResourceReader?.Invoke(query, cancellationToken)
            ?? Task.FromResult(ResourceHistoryProjection.ProjectRecentPrecise([], query, DateTimeOffset.UtcNow, 500, _instance));
    }

    public Task<ResourceHistoryResult> ReadResourceHistoryAsync(ResourceHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ResourceQueries.Add(query);
        return ResourceReader?.Invoke(query, cancellationToken) ?? Task.FromResult(query.Source == ResourceHistorySource.RecentSeconds
            ? ResourceHistoryProjection.ProjectRecent([], query, DateTimeOffset.UtcNow)
            : ResourceHistoryProjection.ProjectDurable([], query, DateTimeOffset.UtcNow));
    }

    internal Task MakeConfigurationUnreadableAsync() =>
        File.WriteAllTextAsync(fixture.Paths.ConfigPath, "version: [invalid-yaml");

    public int SnapshotReads { get; private set; }
    public Func<CancellationToken, Task<AgentMonitoringSnapshot>>? SnapshotReader { get; set; }

    public async Task<AgentMonitoringSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        SnapshotReads++;
        return SnapshotReader is { } read ? await read(cancellationToken)
            : Snapshot(await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration", cancellationToken: cancellationToken));
    }

    public Task<MonitoringHistoryResult> ReadHistoryAsync(MonitoringHistoryQuery query, CancellationToken cancellationToken = default)
    {
        Queries.Add(query);
        return HistoryReader?.Invoke(query, cancellationToken) ?? Task.FromResult(History(query));
    }

    public AgentMonitoringSnapshot Snapshot(ConfigurationWorkspaceSnapshot configuration, DateTimeOffset? observed = null)
    {
        var at = observed ?? DateTimeOffset.UtcNow;
        var tasks = configuration.Configuration.Tasks.Select(task => TaskSnapshot.From(task, HealthResult.Ok("fixture", "synthetic observation"))).ToArray();
        var processes = tasks.Select(task => new ManagedProcessSnapshot(task.Name, "fixture", task.Type, true, true, true, "OK", "fixture", "fixture", at,
            [new(4242, "fixture", null, at.AddDays(-2), 12.5, 100, 50, 1, true)], TaskIdentity: MonitoringTaskIdentity.For(task))).ToArray();
        return new(1, fixture.Endpoint.RootId, _instance, Environment.ProcessId, "fixture", at.AddHours(-1), at, at,
            configuration.Revision, 10, new(true, at, at, tasks, null), new(true, at, at, processes, null),
            new(true, at, at, new(Environment.MachineName, "synthetic host", at, TimeSpan.FromDays(900), 99, 1000, 0, [], []), null), [],
            new(true, at, null, 1024, 30));
    }

    public MonitoringHistoryResult History(MonitoringHistoryQuery query, ObservationHealth? health = null)
    {
        MonitoringHistoryFrame[] frames = health is null ? [] : [new(1, fixture.Endpoint.RootId, _instance, query.To.AddSeconds(-10),
            [new(query.TaskName, query.TaskIdentity, health.Value, 12.5, 100, 1000)])];
        return MonitoringHistoryProjection.Project(frames, query, DateTimeOffset.UtcNow, _instance);
    }
}
