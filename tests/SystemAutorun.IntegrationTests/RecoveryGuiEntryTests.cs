using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using SystemAutorun.App.Dialogs;
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
public sealed class RecoveryGuiEntryTests
{
    [Theory]
    [InlineData("more", false)] [InlineData("list", false)] [InlineData("more", true)]
    public async Task TaskPolicyEntriesReplaceStatusOnlyActionAndNeverResumeManualStop(string source, bool remote)
    {
        await using var host = new ConfigurationControlPlaneTests.ServerFixture();
        await File.WriteAllTextAsync(host.Paths.ConfigPath, RecoveryWorkflowIntegrationTests.Configuration);
        var store = new RecoveryControlStore(host.Paths);
        var paused = await store.InitializePausedAsync(["media"], (await store.ReadAsync()).Revision);
        var configurationClient = new AgentConfigurationClient(host.Client);
        var configuration = await configurationClient.ReadAsync();
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is not SystemAutorun.App.App)
            { var application = new SystemAutorun.App.App(runRuntimeStartup: false); application.InitializeComponent(); }
            WindowTheme.Install();
            var reader = new NeverRemote();
            var dashboard = new MainWindowViewModel(host.Client, new NativeCacheStore(host.Paths.GuiCachePath),
                configurationClient, host.Paths, reader) { AutoRefresh = false, SearchText = "med" };
            dashboard.ApplyMonitoringConfiguration(configuration);
            var row = new TaskRowViewModel(TaskSnapshot.From(configuration.Configuration.GetTask("media"),
                HealthResult.Unknown("isolated-fixture", "No workload observation or execution.")));
            var main = new SystemAutorun.App.MainWindow(dashboard, new AgentStartupClient(host.Client), new StartupCacheStore(host.Paths.StartupCachePath))
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, Width = 1320, Height = 820 };
            try
            {
                main.Show(); dashboard.Tasks.Add(row); dashboard.SelectedTask = row; await Idle(main);
                if (remote) { dashboard.SelectedHostTab = new HostTabViewModel(new RemoteHostDefinition
                    { Id = "unconnected-fixture", Name = "disabled fixture", Host = "127.0.0.1", Enabled = false }); }
                var menu = source == "more" ? main.ServiceMoreButton.ContextMenu : main.ServiceList.ContextMenu;
                menu.PlacementTarget = source == "more" ? main.ServiceMoreButton : main.ServiceList;
                await Idle(main); // Resolve real popup bindings without opening an offscreen popup on the user's desktop.
                Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "自动恢复状态…"));
                var start = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "打开应用时未运行则启动"));
                var recover = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "进程停止后自动恢复"));
                Assert.Same(dashboard.ToggleTaskAppLaunchCommand, start.Command);
                Assert.Same(dashboard.ToggleTaskAutoRecoverCommand, recover.Command);
                foreach (var entry in new[] { start, recover })
                {
                    Assert.True(entry.IsCheckable); Assert.Same(dashboard, entry.DataContext);
                    Assert.Equal(!remote, entry.IsEnabled); Assert.Null(entry.FocusVisualStyle);
                }
                if (!remote)
                {
                    dashboard.IsBusy = true; await Idle(main); Assert.False(start.IsEnabled); Assert.False(recover.IsEnabled);
                    dashboard.IsBusy = false; await Idle(main); Assert.True(start.IsEnabled); Assert.True(recover.IsEnabled);
                    var previous = dashboard.SelectedAutoRecover;
                    var peer = new MenuItemAutomationPeer(recover);
                    Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
                    await Idle(main); Assert.NotNull(dashboard.ToggleTaskAutoRecoverCommand.ExecutionTask);
                    await dashboard.ToggleTaskAutoRecoverCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(8)); await Idle(main);
                    Assert.True(dashboard.SelectedAutoRecover == !previous, $"Model policy did not persist: previous={previous}, current={dashboard.SelectedAutoRecover}, status={dashboard.StatusMessage}");
                    Assert.True(recover.IsChecked == !previous, $"Menu check is stale: previous={previous}, model={dashboard.SelectedAutoRecover}, checked={recover.IsChecked}, hasBinding={System.Windows.Data.BindingOperations.IsDataBound(recover, MenuItem.IsCheckedProperty)}");
                    var saved = await host.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
                    Assert.Equal(!previous, saved.Configuration.GetTask("media").AutoRecover);
                    Assert.Equal("med", dashboard.SearchText);
                }
                Assert.Empty(main.OwnedWindows.OfType<RecoveryWindow>()); Assert.Equal(0, reader.Calls);
                var after = await store.ReadAsync(); Assert.Equal(paused.Revision, after.Revision);
                Assert.True(Assert.Single(after.Tasks).Paused);
                Assert.Equal(RecoveryPendingState.Missing, (await new RecoveryPendingStore(host.Paths, "media").ReadAsync()).State);
            }
            finally
            {
                main.ServiceMoreButton.ContextMenu.IsOpen = false; main.ServiceList.ContextMenu.IsOpen = false;
                var closing = typeof(SystemAutorun.App.MainWindow).GetMethod("Window_Closing", BindingFlags.Instance | BindingFlags.NonPublic)!;
                main.Closing -= (System.ComponentModel.CancelEventHandler)closing.CreateDelegate(typeof(System.ComponentModel.CancelEventHandler), main);
                main.Close(); dashboard.DetachDashboard();
            }
        });
        // This configuration-only Agent deliberately has no sampler. The GUI may report
        // its three read RPCs unavailable, but must not fall back to probing or hide mutation errors.
        Assert.All(host.ObservedErrors, error =>
        {
            Assert.Contains(error.Command, new[] { "monitoring-snapshot", "monitoring-history", "resource-history" });
            Assert.Equal("monitoring_unavailable", Assert.IsType<AgentCommandException>(error.Error).Code);
        });
    }
    private static async Task Idle(Window window) { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private sealed class NeverRemote : IRemoteHostReader
    {
        internal int Calls;
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Task policy entry tests must never connect to remote hosts."); }
    }
}
