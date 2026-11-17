using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Recovery;
using SystemAutorun.Core.Services;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class TaskPolicyGuiPersistenceTests
{
    [Theory]
    [InlineData("more", false)] [InlineData("list", false)]
    [InlineData("more", true)] [InlineData("list", true)]
    public Task EligibleMenusPersistBothIndependentPoliciesWithoutResumingManualPause(string source, bool light) =>
        WithEligibleWindow(light, async session =>
        {
            var (start, recover) = await Entries(session, source);
            session.Model.IsBusy = true; await Idle(session.Main);
            Assert.False(start.IsEnabled); Assert.False(recover.IsEnabled);
            session.Model.IsBusy = false; await Idle(session.Main);
            foreach (var (entry, app, recovery) in new[]
                { (start, true, false), (recover, true, true), (start, false, true), (recover, false, false) })
            {
                Assert.True(entry.IsEnabled);
                var peer = new MenuItemAutomationPeer(entry);
                Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
                await SettleCommands(session);
                await session.VerifyAsync(app, recovery);
                Assert.Equal(app, start.IsChecked); Assert.Equal(recovery, recover.IsChecked);
            }
            session.Main.ServiceList.SelectedIndex = 1; await Idle(session.Main);
            Assert.Equal("policy-b", session.Model.SelectedTask!.Name);
            Assert.False(session.Model.SelectedStartOnAppLaunch); Assert.False(session.Model.SelectedAutoRecover);
            Assert.False(start.IsChecked); Assert.False(recover.IsChecked);
            Assert.True(start.IsEnabled); Assert.True(recover.IsEnabled);
        });

    [Fact]
    public Task NativeEligiblePolicyProbeRequiresObservedWritesAndExplicitFinish()
    {
        var directory = Environment.GetEnvironmentVariable("SA_POLICY_GUI_PROBE");
        if (string.IsNullOrEmpty(directory)) { return Task.CompletedTask; }
        var full = Path.GetFullPath(directory);
        Assert.Equal(full, directory);
        Assert.Equal("r121", Path.GetFileName(Path.GetDirectoryName(full)));
        Assert.StartsWith("native-policy-", Path.GetFileName(full));
        Assert.Equal(FileAttributes.Directory, File.GetAttributes(full) & (FileAttributes.Directory | FileAttributes.ReparsePoint));
        var light = Environment.GetEnvironmentVariable("SA_POLICY_GUI_THEME") == "light";
        return WithEligibleWindow(light, async session =>
        {
            session.Main.Title = $"SystemAutorun · R121 eligible isolated policies / {(light ? "light" : "dark")} / no sampler";
            session.Main.Left = 20; session.Main.Top = 20;
            await Idle(session.Main);
            var stages = new List<string>();
            async Task Publish()
            {
                await SettleCommands(session);
                var app = session.Model.SelectedStartOnAppLaunch;
                var recovery = session.Model.SelectedAutoRecover;
                await session.VerifyAsync(app, recovery);
                var stage = $"{(app ? 1 : 0)}{(recovery ? 1 : 0)}";
                if (stages.LastOrDefault() == stage) { return; }
                stages.Add(stage);
                var json = JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.UtcNow, selected = session.Model.SelectedTask?.Name,
                    startOnAppLaunch = app, autoRecover = recovery, light, stages,
                    evidence = "Real WPF model, RPC read and independent ConfigurationLoader disk reread agree; other task, globals and paused bytes unchanged. No sampler/executor allowed."
                }, AgentProtocol.JsonOptions);
                await File.WriteAllTextAsync(Path.Combine(full, "state.new"), json);
                File.Move(Path.Combine(full, "state.new"), Path.Combine(full, "state.json"), overwrite: true);
                await File.AppendAllTextAsync(Path.Combine(full, "observations.ndjson"), json + Environment.NewLine);
            }
            await Publish();
            await File.WriteAllTextAsync(Path.Combine(full, "ready.json"), JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId, hwnd = new WindowInteropHelper(session.Main).Handle.ToInt64(),
                dpi = VisualTreeHelper.GetDpi(session.Main).PixelsPerInchX, session.Main.Title,
                root = session.Host.Root, expectedStages = new[] { "00", "10", "11", "01", "00" },
                source = "Two explicit enabled CurrentUser+Logon tasks with invalid owned non-PE files; config-only server rejects all workload probing/start/stop."
            }, AgentProtocol.JsonOptions));
            var deadline = DateTime.UtcNow.AddMinutes(8);
            while (!File.Exists(Path.Combine(full, "finish.txt")) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
                if (!session.Model.IsBusy) { await Publish(); }
            }
            await Publish();
            Assert.True(File.Exists(Path.Combine(full, "finish.txt")), "Explicit native policy probe did not finish.");
            Assert.Equal(new[] { "00", "10", "11", "01", "00" }, stages);
            await File.WriteAllTextAsync(Path.Combine(full, "result.json"), JsonSerializer.Serialize(new
            { stages, independentDiskReread = true, pausedBytesUnchanged = true, otherTaskUnchanged = true, globalPreferencesUnchanged = true, workloadOperations = 0 }, AgentProtocol.JsonOptions));
        }, TimeSpan.FromMinutes(9), visible: true);
    }

    private static async Task WithEligibleWindow(bool light, Func<PolicySession, Task> action, TimeSpan? timeout = null, bool visible = false)
    {
        await using var host = new ConfigurationControlPlaneTests.ServerFixture();
        var client = new AgentConfigurationClient(host.Client);
        await client.ReadAsync();
        foreach (var name in new[] { "policy-a", "policy-b" })
        {
            var image = Path.Combine(host.Root, name + ".exe");
            await File.WriteAllTextAsync(image, "Deliberately not a PE image; must never execute.");
            await client.MutateAsync<ConfigurationMutationResult>("task-add", payload: new AddTaskRequest(name,
                AutorunTaskType.Windows, Executable: image, StartupDelay: 0, HealthType: HealthCheckType.Process,
                HealthPattern: Path.GetFileName(image),
                Launch: new(RunAsMode.CurrentUser, LaunchTrigger.Logon, WindowMode.Hidden, ProcessLifecycle.Tracked),
                ExpectedRunning: true, StartOnAppLaunch: false, AutoRecover: false));
        }
        var configuration = await client.ReadAsync();
        Assert.Equal(new[] { "policy-a", "policy-b" }, configuration.Configuration.Tasks.Select(task => task.Name).Order());
        Assert.Empty(configuration.Preferences.RemoteHosts);
        Assert.All(configuration.Configuration.Tasks, task =>
        {
            Assert.True(task.Enabled); Assert.True(task.ExpectedRunning);
            Assert.Equal(RunAsMode.CurrentUser, task.Launch.RunAs); Assert.Equal(LaunchTrigger.Logon, task.Launch.Trigger);
            Assert.False(task.StartOnAppLaunch); Assert.False(task.AutoRecover);
            Assert.Equal(Path.Combine(host.Root, task.Name + ".exe"), task.Executable);
        });
        var store = new RecoveryControlStore(host.Paths);
        var paused = await store.InitializePausedAsync(["policy-a", "policy-b"], (await store.ReadAsync()).Revision);
        var pausedBytes = await File.ReadAllBytesAsync(store.FilePath);
        var selectedDefinition = JsonSerializer.Serialize(configuration.Configuration.GetTask("policy-a"), AgentProtocol.JsonOptions);
        var settingsBytes = File.Exists(host.Paths.GuiSettingsPath) ? await File.ReadAllBytesAsync(host.Paths.GuiSettingsPath) : null;
        var otherTask = JsonSerializer.Serialize(configuration.Configuration.GetTask("policy-b"), AgentProtocol.JsonOptions);
        var preferences = JsonSerializer.Serialize(configuration.Preferences, AgentProtocol.JsonOptions);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is not SystemAutorun.App.App)
            { var application = new SystemAutorun.App.App(runRuntimeStartup: false); application.InitializeComponent(); }
            WindowTheme.Install();
            var previousLight = ThemeService.IsLight;
            ThemeService.Apply(light ? ThemePreference.Light : ThemePreference.Dark, persist: null);
            var remote = new NeverRemote();
            var model = new MainWindowViewModel(host.Client, new NativeCacheStore(host.Paths.GuiCachePath), client, host.Paths, remote)
            { AutoRefresh = false };
            model.ApplyMonitoringConfiguration(configuration);
            var main = new SystemAutorun.App.MainWindow(model, new AgentStartupClient(host.Client), new StartupCacheStore(host.Paths.StartupCachePath))
            {
                ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = visible ? 20 : -32000, Top = visible ? 20 : -32000, Width = 1152, Height = 648
            };
            async Task Verify(bool app, bool recovery)
            {
                var rpc = await host.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
                var disk = await new ConfigurationLoader().LoadAsync(host.Root);
                foreach (var saved in new[] { rpc.Configuration, disk })
                {
                    Assert.Equal(app, saved.GetTask("policy-a").StartOnAppLaunch);
                    Assert.Equal(recovery, saved.GetTask("policy-a").AutoRecover);
                    Assert.Equal(selectedDefinition, JsonSerializer.Serialize(saved.GetTask("policy-a") with
                        { StartOnAppLaunch = false, AutoRecover = false }, AgentProtocol.JsonOptions));
                    Assert.Equal(otherTask, JsonSerializer.Serialize(saved.GetTask("policy-b"), AgentProtocol.JsonOptions));
                }
                Assert.Equal(preferences, JsonSerializer.Serialize(rpc.Preferences, AgentProtocol.JsonOptions));
                Assert.Equal(paused.Revision, (await store.ReadAsync()).Revision);
                Assert.Equal(pausedBytes, await File.ReadAllBytesAsync(store.FilePath));
                Assert.Equal(settingsBytes, File.Exists(host.Paths.GuiSettingsPath) ? await File.ReadAllBytesAsync(host.Paths.GuiSettingsPath) : null);
                Assert.Equal(app, model.SelectedStartOnAppLaunch); Assert.Equal(recovery, model.SelectedAutoRecover);
                Assert.Equal("policy-a", model.SelectedTask!.Name); Assert.Equal(0, remote.Calls);
                foreach (var name in new[] { "policy-a", "policy-b" })
                { Assert.Equal(RecoveryPendingState.Missing, (await new RecoveryPendingStore(host.Paths, name).ReadAsync()).State); }
                Assert.All(host.ObservedErrors, error =>
                {
                    Assert.Contains(error.Command, new[] { "monitoring-snapshot", "monitoring-history", "resource-history" });
                    Assert.Equal("monitoring_unavailable", Assert.IsType<AgentCommandException>(error.Error).Code);
                });
            }
            try
            {
                main.Show();
                foreach (var task in configuration.Configuration.Tasks)
                { model.Tasks.Add(new TaskRowViewModel(TaskSnapshot.From(task, HealthResult.Unknown("isolated-fixture", "No workload observation or execution.")))); }
                main.ServiceList.SelectedIndex = 0;
                await Idle(main);
                var session = new PolicySession(host, model, main, Verify);
                await Verify(false, false);
                await action(session);
            }
            finally
            {
                main.ServiceMoreButton.ContextMenu.IsOpen = false; main.ServiceList.ContextMenu.IsOpen = false;
                var closing = typeof(SystemAutorun.App.MainWindow).GetMethod("Window_Closing", BindingFlags.Instance | BindingFlags.NonPublic)!;
                main.Closing -= (System.ComponentModel.CancelEventHandler)closing.CreateDelegate(typeof(System.ComponentModel.CancelEventHandler), main);
                main.Close(); model.DetachDashboard();
                await model.DashboardHistoryTask.WaitAsync(TimeSpan.FromSeconds(8));
                await model.ResourceHistoryTask.WaitAsync(TimeSpan.FromSeconds(8));
                ThemeService.Apply(previousLight ? ThemePreference.Light : ThemePreference.Dark, persist: null);
            }
        }, timeout);
    }

    private static async Task<(MenuItem Start, MenuItem Recover)> Entries(PolicySession session, string source)
    {
        var menu = source == "more" ? session.Main.ServiceMoreButton.ContextMenu : session.Main.ServiceList.ContextMenu;
        menu.PlacementTarget = source == "more" ? session.Main.ServiceMoreButton : session.Main.ServiceList;
        await Idle(session.Main);
        var start = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "打开应用时未运行则启动"));
        var recover = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "进程停止后自动恢复"));
        foreach (var item in new[] { start, recover })
        { Assert.True(item.IsCheckable); Assert.Null(item.FocusVisualStyle); Assert.Same(session.Model, item.DataContext); }
        return (start, recover);
    }

    private static async Task SettleCommands(PolicySession session)
    {
        await Idle(session.Main);
        if (session.Model.ToggleTaskAppLaunchCommand.ExecutionTask is { } start) { await start.WaitAsync(TimeSpan.FromSeconds(8)); }
        if (session.Model.ToggleTaskAutoRecoverCommand.ExecutionTask is { } recover) { await recover.WaitAsync(TimeSpan.FromSeconds(8)); }
        Assert.False(session.Model.IsBusy); await Idle(session.Main);
    }
    private static async Task Idle(Window window)
    { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private sealed record PolicySession(ConfigurationControlPlaneTests.ServerFixture Host, MainWindowViewModel Model,
        SystemAutorun.App.MainWindow Main, Func<bool, bool, Task> VerifyAsync);
    private sealed class NeverRemote : IRemoteHostReader
    {
        internal int Calls;
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Policy tests must never contact remote hosts."); }
    }
}
