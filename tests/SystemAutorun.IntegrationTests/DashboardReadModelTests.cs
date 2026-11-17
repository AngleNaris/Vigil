using System.Globalization;
using System.IO;
using System.Reflection;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Services;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.IntegrationTests;

public sealed class DashboardReadModelTests
{
    [Theory]
    [InlineData(-1, "00:00:00")] [InlineData(0, "00:00:00")] [InlineData(59, "00:00:59")]
    [InlineData(60, "00:01:00")] [InlineData(86399, "23:59:59")] [InlineData(90061, "1天 01:01:01")]
    public void SharedUptimeFormatKeepsSecondsAcrossMinuteAndDayBoundaries(int seconds, string expected) =>
        Assert.Equal(expected, typeof(MainWindowViewModel).GetMethod("FormatUptime", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [TimeSpan.FromSeconds(seconds)]));

    [Fact]
    public async Task RefreshSelectsOnlyVisibleTasksAndEmptyFiltersHaveNoSelection()
    {
        await WithModel(model =>
        {
            Replace(model, [Task("alpha"), Task("beta")]);
            model.SearchText = "beta";
            Assert.Null(model.SelectedTask);
            Replace(model, [Task("alpha"), Task("beta")]);
            Assert.Equal("beta", model.SelectedTask?.Name);
            model.SearchText = "no-such-fixture";
            Replace(model, [Task("alpha"), Task("beta")]);
            Assert.True(model.IsEmpty);
            Assert.Null(model.SelectedTask);
            Assert.Equal("没有匹配结果", model.DashboardEmptyTitle);
            Assert.Equal("清除筛选", model.DashboardEmptyActionLabel);
            Assert.Same(model.ClearFiltersCommand, model.DashboardEmptyActionCommand);
            model.DashboardEmptyActionCommand.Execute(null);
            Assert.False(model.IsEmpty);
            Assert.Equal("", model.SearchText);
            model.FilterByStatusCommand.Execute("FAIL");
            Replace(model, [Task("alpha"), Task("beta")]);
            Assert.Null(model.SelectedTask);
            model.ClearFiltersCommand.Execute(null);
            Replace(model, [Task("alpha"), Task("beta")]);
            Assert.Equal("alpha", model.SelectedTask?.Name);
        });
    }

    [Fact]
    public async Task MetricsUseMatchedProcessAndLocalPhysicalMemoryNotHostCpu()
    {
        await WithModel(model =>
        {
            model.SelectedTask = new TaskRowViewModel(Task("alpha"));
            var now = model.DashboardNow;
            Host(model, now, totalMemory: 1000, hostCpu: 99);
            model.ManagedProcesses.Add(new ManagedProcessRowViewModel(Process("ALPHA", now, cpu: 12.5, workingSet: 100)));
            Assert.Equal($"{12.5:0.0}%", model.DashboardCpuText);
            Assert.Equal($"{10d:0.0}%", model.DashboardMemoryPercentText);
            Assert.Equal("100 B", model.DashboardWorkingSetText);
            Assert.Equal("2天 00:00:00", model.DashboardSelectedUptime);
            Assert.Contains("PID 4242", model.DashboardSourceText);
            model.SelectedTask = new TaskRowViewModel(Task("unmatched"));
            Assert.Equal("--", model.DashboardCpuText);
            Assert.Equal("--", model.DashboardMemoryPercentText);
            Assert.Equal("--", model.DashboardSelectedUptime);
        });
    }

    [Fact]
    public async Task StaleProcessOrUnknownMemoryDenominatorNeverShowsZero()
    {
        await WithModel(model =>
        {
            model.SelectedTask = new TaskRowViewModel(Task("alpha"));
            var now = model.DashboardNow;
            Host(model, now, totalMemory: 1000);
            model.ManagedProcesses.Add(new ManagedProcessRowViewModel(Process("alpha", now.AddSeconds(-31))));
            Assert.Equal("--", model.DashboardCpuText);
            Assert.Equal("--", model.DashboardMemoryPercentText);
            Assert.Equal("--", model.DashboardSelectedUptime);
            model.ManagedProcesses[0] = new ManagedProcessRowViewModel(Process("alpha", now));
            Host(model, now.AddSeconds(-31), totalMemory: 1000);
            Assert.NotEqual("--", model.DashboardCpuText);
            Assert.Equal("--", model.DashboardMemoryPercentText);
            Host(model, now, totalMemory: 0);
            Assert.Equal("--", model.DashboardMemoryPercentText);
            model.ManagedProcesses[0] = new ManagedProcessRowViewModel(Process("alpha", now.AddSeconds(6)));
            Assert.Equal("--", model.DashboardCpuText);
        });
    }

    [Fact]
    public async Task CpuNeedsADeltaButARealMeasuredZeroIsNotUnknown()
    {
        await WithModel(model =>
        {
            model.SelectedTask = new TaskRowViewModel(Task("alpha"));
            var sample = Process("alpha", model.DashboardNow, cpuAvailable: false);
            model.ManagedProcesses.Add(new ManagedProcessRowViewModel(sample));
            Assert.Equal("--", model.DashboardCpuText);
            Assert.Equal("--", model.ManagedProcesses[0].CpuText);
            Assert.NotEqual("--", model.DashboardWorkingSetText);
            model.ManagedProcesses[0] = new ManagedProcessRowViewModel(Process("alpha", model.DashboardNow, cpu: 0));
            Assert.Equal($"{0:0.0}%", model.DashboardCpuText);
            Assert.Equal($"{0:0.0}%", model.ManagedProcesses[0].CpuText);
            model.SelectedTask = new TaskRowViewModel(Task("alpha", "wsl_command"));
            Assert.Equal("--", model.DashboardCpuText);
            Assert.Equal("--", model.DashboardWorkingSetText);
            model.SelectedTask = new TaskRowViewModel(Task("alpha", "windows_service"));
            Assert.Equal("--", model.DashboardWorkingSetText);
        });
    }

    [Fact]
    public async Task ServiceAndWslUptimeDoesNotBorrowHostProcessStartTime()
    {
        await WithModel(model =>
        {
            var now = model.DashboardNow;
            model.ManagedProcesses.Add(new ManagedProcessRowViewModel(Process("alpha", now)));
            var converter = new DashboardUptimeConverter();
            foreach (var type in new[] { "windows_service", "wsl_boot", "wsl_systemd", "wsl_command" })
            {
                var task = new TaskRowViewModel(Task("alpha", type));
                model.SelectedTask = task;
                Assert.Equal("--", model.DashboardSelectedUptime);
                Assert.Equal("--", converter.Convert([task, 0, model], typeof(string), null!, CultureInfo.InvariantCulture));
            }
        });
    }

    [Fact]
    public async Task AllHistoryWindowsRemainTruthfullyUnknownUntilHistoryIsConnected()
    {
        await WithModel(model =>
        {
            foreach (var hours in new[] { 24, 168, 720 })
            {
                model.SelectDashboardWindowCommand.Execute(hours.ToString(CultureInfo.InvariantCulture));
                Assert.Equal(hours, model.DashboardWindowHours);
                Assert.Empty(model.DashboardHealthHistory);
                Assert.Empty(model.DashboardAvailabilityTrend);
                Assert.Empty(model.DashboardCoverageTrend);
                Assert.Empty(model.DashboardCpuTrend);
                Assert.Empty(model.DashboardMemoryTrend);
                Assert.Equal("--", model.DashboardAvailabilityText);
                Assert.Equal("--", model.DashboardCoverageText);
                Assert.Contains("未知", model.DashboardHistoryHelp);
            }
            model.SelectDashboardWindowCommand.Execute("9999");
            Assert.Equal(720, model.DashboardWindowHours);
            model.SelectDashboardTypeCommand.Execute("windows_service");
            Assert.Equal("windows_service", model.SelectedTypeFilter.Value);
            model.SelectDashboardTypeCommand.Execute("invalid-type");
            Assert.Equal("windows_service", model.SelectedTypeFilter.Value);
        });
    }

    [Fact]
    public async Task ReturningToLocalDuringRemoteRefreshObtainsFreshLocalSnapshots()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var configuration = new AgentConfigurationClient(fixture.Client);
        var knownHosts = Path.Combine(fixture.Root, "known_hosts");
        await File.WriteAllTextAsync(knownHosts, "# fake reader only, no SSH connections\n");
        var remote = new RemoteHostDefinition
        {
            Id = "fixture-remote", Name = "isolated remote fixture", Enabled = true,
            Platform = RemoteHostPlatform.Linux, Host = "192.0.2.25", UserName = "fixture",
            Authentication = RemoteAuthenticationMode.SshAgent, KnownHostsFile = knownHosts,
        };
        _ = await configuration.ReadAsync();
        _ = await configuration.MutateAsync<ApplicationSettings>("remote-upsert", payload: remote);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var reader = new DeferredRemoteReader();
            var model = new MainWindowViewModel(fixture.Client, new NativeCacheStore(fixture.Paths.GuiCachePath),
                configuration, fixture.Paths, reader, new DashboardFixtureMonitoringClient(fixture));
            await model.InitializeAsync();
            var local = model.Hosts.Single(host => host.IsLocal);
            var localField = typeof(MainWindowViewModel).GetField("_lastLocalHostSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var before = localField.GetValue(model);
            Assert.NotNull(before);
            var borrowedRemoteSnapshot = false;
            model.PropertyChanged += (_, change) =>
            {
                if (model.IsLocalHostSelected && change.PropertyName == nameof(model.HostName)
                    && model.HostName == remote.Name) { borrowedRemoteSnapshot = true; }
            };
            try
            {
                model.SelectedHostTab = model.Hosts.Single(host => !host.IsLocal);
                await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(model.IsBusy); Assert.True(model.IsRefreshing);
                Assert.False(model.AddProgramCommand.CanExecute(null)); // Remote-host restrictions remain independent of read busy state.
                var refresh = Assert.IsAssignableFrom<Task>(model.RefreshCommand.ExecutionTask);
                model.SelectedHostTab = local;
                model.ManagedProcessStatusText = "unrefreshed local process fixture";
                reader.Completion.SetResult(RemoteHostStatusSnapshot.Offline(remote, "fake completed probe")
                    with { MachineName = remote.Name });
                await refresh.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(model.IsLocalHostSelected);
                Assert.False(borrowedRemoteSnapshot);
                Assert.NotSame(before, localField.GetValue(model));
                Assert.Equal(Environment.MachineName, model.HostName);
                Assert.Equal(string.Empty, model.HostConnectionText);
                Assert.True(model.HostStatusAvailable);
                Assert.Equal("尚未指定应持续运行的工作负载。", model.ManagedProcessStatusText);
                Assert.Equal("本机家庭服务器状态已刷新。", model.StatusMessage);
            }
            finally
            {
                reader.Completion.TrySetResult(RemoteHostStatusSnapshot.Offline(remote, "fixture cleanup"));
                if (model.RefreshCommand.ExecutionTask is { } pending)
                {
                    await pending.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
        });
    }

    [Fact]
    public async Task PeriodicRefreshUpdatesRowsInPlaceWithoutResettingTheList()
    {
        await WithModel(model =>
        {
            // R106: the 10 s refresh used to Clear() and rebuild every row, which blanked the list.
            Replace(model, [Task("alpha"), Task("beta")]);
            model.SelectedTask = model.Tasks.Single(row => row.Name == "beta");
            var rows = model.Tasks.ToArray(); var selected = model.SelectedTask;
            var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
            model.Tasks.CollectionChanged += (_, args) => changes.Add(args.Action);
            var renamed = new List<string?>();
            selected!.PropertyChanged += (_, args) => renamed.Add(args.PropertyName);
            Replace(model, [Task("alpha") with { Detail = "refreshed" }, Task("beta") with { Detail = "refreshed" }]);
            Assert.Empty(changes);
            Assert.Equal(rows, model.Tasks.ToArray());
            Assert.Same(selected, model.SelectedTask);
            Assert.Equal("refreshed", model.SelectedTask!.Detail);
            Assert.Contains(string.Empty, renamed);
            // A new task is inserted in name order and a removed one leaves; others keep their objects.
            Replace(model, [Task("beta"), Task("gamma")]);
            Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Remove, System.Collections.Specialized.NotifyCollectionChangedAction.Add], changes);
            Assert.Same(rows[1], model.Tasks[0]); Assert.Equal("gamma", model.Tasks[1].Name);
            Assert.Same(selected, model.SelectedTask);
        });
    }

    [Fact]
    public async Task LocalSnapshotsCannotFabricateResourcesWhenAgentHistoryIsUnavailable()
    {
        await WithModel(model =>
        {
            model.SelectedTask = new TaskRowViewModel(Task("alpha"));
            var first = Process("alpha", model.DashboardNow.AddSeconds(-2), cpu: .04, workingSet: 80L << 20);
            model.ManagedProcesses.Add(new ManagedProcessRowViewModel(first));
            model.TickDashboard(); Assert.Empty(model.DashboardResourceCpu); Assert.Empty(model.DashboardResourceMemory);
            model.SelectedTask = new TaskRowViewModel(Task("beta")); Assert.Empty(model.DashboardResourceCpu);
            model.SelectedTask = new TaskRowViewModel(Task("alpha")); Assert.Empty(model.DashboardResourceCpu);
            model.SetPresentationActive(false);
            model.ManagedProcesses.Clear(); model.ManagedProcesses.Add(new ManagedProcessRowViewModel(first with { CapturedAt = DateTimeOffset.UtcNow }));
            model.SetPresentationActive(true); Assert.Empty(model.DashboardResourceCpu);
            model.SelectDashboardWindowCommand.Execute("168");
            Assert.True(model.DashboardHistoryMode); Assert.True(model.DashboardResourceMemoryBytes);
            Assert.Empty(model.DashboardResourceCpu); Assert.Equal("最近1周", model.DashboardHealthWindowLabel);
            Assert.Equal(TimeSpan.FromDays(7), model.DashboardResourceDuration);
            model.SelectDashboardWindowCommand.Execute("0"); Assert.False(model.DashboardHistoryMode);
            Assert.Empty(model.DashboardResourceCpu); Assert.Equal("实时", model.DashboardHealthWindowLabel);
            Assert.Equal(TimeSpan.FromMinutes(5), model.DashboardResourceDuration);
        });
    }

    private static async Task WithModel(Action<MainWindowViewModel> action)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        await GuiConfigurationControlTests.RunStaAsync(() =>
        {
            var model = new MainWindowViewModel(fixture.Client, new NativeCacheStore(fixture.Paths.GuiCachePath),
                new AgentConfigurationClient(fixture.Client), fixture.Paths, new NeverRemoteReader());
            model.InitializeDashboard();
            try { action(model); }
            finally { model.DetachDashboard(); }
            return System.Threading.Tasks.Task.CompletedTask;
        });
    }

    private static void Replace(MainWindowViewModel model, TaskSnapshot[] tasks) =>
        typeof(MainWindowViewModel).GetMethod("ReplaceTasks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [tasks, false]);

    private static void Host(MainWindowViewModel model, DateTimeOffset at, long totalMemory, double hostCpu = 0)
    {
        typeof(MainWindowViewModel).GetField("_lastLocalHostSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model, new HostStatusSnapshot("fixture", "fixture", at, TimeSpan.FromDays(900), hostCpu,
                totalMemory, 0, [], []));
        model.HostStatusAvailable = true;
    }

    private static TaskSnapshot Task(string name, string type = "windows") => new(
        "OK", true, true, name, type, "isolated model fixture", "fixture", "fixture", 0,
        null, [], null, null, null, [],
        new LaunchOptions(RunAsMode.CurrentUser, LaunchTrigger.Manual, WindowMode.Hidden, ProcessLifecycle.Tracked),
        ExpectedRunning: true);

    private static ManagedProcessSnapshot Process(string taskName, DateTimeOffset at, double cpu = 0,
        long workingSet = 100, bool cpuAvailable = true) =>
        new(taskName, "fixture", "windows", true, true, true, "OK", "fixture", "fixture", at,
            [new(4242, "fixture", null, at.AddDays(-2), cpu, workingSet, 50, 1, CpuUsageAvailable: cpuAvailable)],
            TaskIdentity: MonitoringTaskIdentity.For(Task(taskName)));

    private sealed class DeferredRemoteReader : IRemoteHostReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<RemoteHostStatusSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return Completion.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class NeverRemoteReader : IRemoteHostReader
    {
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Dashboard model tests must never connect to remote hosts.");
    }
}
