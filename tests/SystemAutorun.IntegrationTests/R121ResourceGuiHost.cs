using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.IntegrationTests;

internal sealed record R121GuiObservation(int Pid, DateTime StartedUtc, string Image, long Hwnd, DateTimeOffset At,
    string Selected, string[] SelectedNames, bool Light, int AgentPid, string AgentInstanceId,
    int[] SelectedProcessIds, ResourceHistoryResult Resource, PlotPoint[] Cpu, PlotPoint[] Memory, string WorkingSetText);

// Test-assembly process only: real App/MainWindow/VM/Agent adapters, no alternate production entry point.
internal static class R121ResourceGuiHost
{
    internal static string Image(string root, string session)
    {
        _ = R121PolicyProcessFixture.RequireRoot(root);
        if (session is not ("first-dark" or "second-dark" or "first-light" or "second-light"))
        { throw new ArgumentException("Unknown owned GUI session."); }
        return Path.Combine(AppContext.BaseDirectory, $"SAFixture_R121_GUI_{Path.GetFileName(root)[^32..]}_{session}.exe");
    }
    internal static string FileName(string root, string session, string phase) => Path.Combine(root, $"gui-{session}-{phase}.json");

    internal static async Task<int> RunAsync(string root, string session)
    {
        root = R121PolicyProcessFixture.RequireRoot(root);
        Assert.Equal(Image(root, session), Environment.ProcessPath);
        var paths = new ProjectPaths(root); var client = new AgentClient(root);
        var configuration = await client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        Assert.Empty(configuration.Preferences.RemoteHosts);
        Assert.Equal(new[] { "owned-launch", "owned-recovery" }, configuration.Configuration.Tasks.Select(task => task.Name).Order());
        Assert.All(configuration.Configuration.Tasks, task =>
        {
            var kind = task.Name == "owned-launch" ? "launch" : "recovery";
            Assert.Equal(R121PolicyProcessFixture.WorkerImage(root, kind), task.Executable);
            Assert.Equal(new[] { "--r121-passive-worker", root, kind }, task.Arguments);
            Assert.Equal(root, task.WorkingDirectory); Assert.True(task.Enabled); Assert.True(task.ExpectedRunning);
            Assert.Equal(RunAsMode.CurrentUser, task.Launch.RunAs); Assert.Equal(LaunchTrigger.Logon, task.Launch.Trigger);
            Assert.False(task.StartOnAppLaunch); Assert.False(task.AutoRecover);
        });
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var light = session.EndsWith("light", StringComparison.Ordinal);
                ThemeService.Apply(light ? ThemePreference.Light : ThemePreference.Dark, persist: null);
                var model = new MainWindowViewModel(client, new NativeCacheStore(paths.GuiCachePath),
                    new AgentConfigurationClient(client), paths, new NeverRemote()) { AutoRefresh = false };
                var main = new SystemAutorun.App.MainWindow(model, new AgentStartupClient(client), new StartupCacheStore(paths.StartupCachePath))
                {
                    ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = Environment.GetEnvironmentVariable("SA_AGENT_GUI_RESOURCE_VISIBLE") == "1" ? 20 : -32000,
                    Top = Environment.GetEnvironmentVariable("SA_AGENT_GUI_RESOURCE_VISIBLE") == "1" ? 20 : -32000,
                    Width = 1152, Height = 648, Title = $"SystemAutorun · R121 real Agent GUI / {session}"
                };
                var starterCalls = 0;
                var manager = new AgentProcessManager(client, paths, _ =>
                { starterCalls++; throw new InvalidOperationException("This GUI must only attach to its already running owned Agent."); });
                void SetField(string name, object value) => typeof(SystemAutorun.App.App)
                    .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, value);
                SetField("_mainWindow", main); SetField("_mainViewModel", model);
                SetField("_paths", paths); SetField("_agentManager", manager);
                var selected = new List<string>(); var closed = 0; var closeAttempts = 0; var cancelledUserClose = false;
                var requestedExit = false; Exception? failure = null;
                model.PropertyChanged += (_, change) =>
                {
                    if (change.PropertyName == nameof(MainWindowViewModel.SelectedTask) && model.SelectedTask is { } task
                        && selected.LastOrDefault() != task.Name) { selected.Add(task.Name); }
                };
                main.Closing += (_, change) => { closeAttempts++; if (!app.IsExiting) { cancelledUserClose = change.Cancel; } };
                main.Closed += (_, _) => { Assert.True(app.IsExiting); closed++; };
                app.Exit += (_, _) => Publish(root, session, "exit", new
                { pid = Environment.ProcessId, app.IsExiting, closed, closeAttempts, cancelledUserClose, requestedExit, starterCalls, selectedNames = selected.ToArray(), at = DateTimeOffset.UtcNow });
                app.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        await manager.EnsureRunningAsync(); Assert.Equal(0, starterCalls);
                        await model.InitializeAsync(); await Idle(main);
                        Assert.Equal(light, ThemeService.IsLight);
                        main.Close(); Assert.True(cancelledUserClose); Assert.Equal(0, closed); Assert.False(main.IsVisible);
                        main.Show(); await Idle(main);
                        Assert.Equal("owned-launch", model.SelectedTask!.Name);
                        await Observe("a");
                        var watch = Stopwatch.StartNew(); var selectedB = false;
                        while (!File.Exists(FileName(root, session, "exit-request")) && watch.Elapsed < TimeSpan.FromSeconds(60))
                        {
                            if (session.StartsWith("second", StringComparison.Ordinal) && !selectedB
                                && File.Exists(FileName(root, session, "select-b")))
                            {
                                Assert.DoesNotContain("owned-recovery", selected);
                                var row = model.Tasks.Single(task => task.Name == "owned-recovery");
                                var container = Assert.IsType<ListBoxItem>(main.ServiceList.ItemContainerGenerator.ContainerFromItem(row));
                                var peer = new ListBoxItemAutomationPeer(row, new ListBoxAutomationPeer(main.ServiceList));
                                Assert.IsAssignableFrom<ISelectionItemProvider>(peer.GetPattern(PatternInterface.SelectionItem)).Select();
                                await Idle(main); await Observe("b-live");
                                await Mode("history"); await Observe("b-history");
                                await Mode("live"); await Observe("b-final-live");
                                selectedB = true;
                            }
                            await Task.Delay(100);
                        }
                        Assert.True(File.Exists(FileName(root, session, "exit-request")), "GUI did not receive explicit normal exit request.");
                        if (session.StartsWith("second", StringComparison.Ordinal)) { Assert.True(selectedB); }
                        Assert.Equal(0, starterCalls);
                    }
                    catch (Exception error)
                    {
                        failure = error; File.WriteAllText(Path.Combine(root, $"gui-{session}.error.txt"), error.ToString());
                    }
                    finally
                    {
                        // Production exit path: IsExiting -> actual Closing/Closed -> manager Dispose -> Application.Shutdown.
                        // Never detach Window_Closing or invoke dispatcher shutdown in place of application exit.
                        requestedExit = true; app.RequestExit();
                    }
                }));
                var code = app.Run(main);
                completion.SetResult(failure is null && closed == 1 && requestedExit && app.IsExiting && starterCalls == 0 ? code : 1);

                async Task Observe(string phase)
                {
                    await model.DashboardHistoryTask.WaitAsync(TimeSpan.FromSeconds(8));
                    ResourceHistoryResult? resource = null;
                    var deadline = Stopwatch.StartNew();
                    while (deadline.Elapsed < TimeSpan.FromSeconds(8))
                    {
                        await model.ResourceHistoryTask.WaitAsync(TimeSpan.FromSeconds(8)); await Idle(main);
                        resource = typeof(MainWindowViewModel).GetField("_resourceHistory", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .GetValue(model) as ResourceHistoryResult;
                        if (resource?.TaskName == model.SelectedTask!.Name
                            && resource.Source == (model.DashboardHistoryMode ? ResourceHistorySource.Durable : ResourceHistorySource.RecentSeconds)
                            && resource.Points.Any(point => point.MemoryBytes > 0)) { break; }
                        await Task.Delay(100);
                    }
                    Assert.NotNull(resource);
                    Assert.Equal(model.SelectedTask!.Name, resource.TaskName);
                    Assert.Equal(model.DashboardHistoryMode ? ResourceHistorySource.Durable : ResourceHistorySource.RecentSeconds, resource.Source);
                    Assert.Contains(resource.Points, point => point.MemoryBytes > 0);
                    var expected = ResourcePlotSeries.From(resource);
                    var actualCpu = Assert.IsAssignableFrom<IReadOnlyList<PlotPoint>>(main.ServiceCpuPlot.Points);
                    var actualMemory = Assert.IsAssignableFrom<IReadOnlyList<PlotPoint>>(main.ServiceMemoryPlot.Points);
                    Assert.Equal(expected.Cpu.ToArray(), actualCpu.ToArray());
                    Assert.Equal(expected.Memory.ToArray(), actualMemory.ToArray());
                    Assert.True(main.ServiceMemoryPlot.Bytes); Assert.NotEqual("--", model.DashboardWorkingSetText);
                    Assert.False(model.SelectedStartOnAppLaunch); Assert.False(model.SelectedAutoRecover);
                    var monitoring = await client.SendAsync<AgentMonitoringSnapshot>("monitoring-snapshot");
                    using var process = Process.GetCurrentProcess();
                    Publish(root, session, phase, new R121GuiObservation(process.Id, process.StartTime.ToUniversalTime(),
                        process.MainModule!.FileName, new WindowInteropHelper(main).Handle.ToInt64(), DateTimeOffset.UtcNow,
                        model.SelectedTask.Name, selected.ToArray(), light, monitoring.ProcessId, monitoring.InstanceId,
                        monitoring.Processes.Data!.Single(task => task.TaskName == model.SelectedTask.Name).Processes.Select(item => item.ProcessId).ToArray(),
                        resource, actualCpu.ToArray(), actualMemory.ToArray(), model.DashboardWorkingSetText));
                }
                async Task Mode(string mode)
                {
                    main.HealthWindowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(main);
                    var hours = mode == "history" ? "168" : "0";
                    var item = main.HealthWindowButton.ContextMenu.Items.OfType<MenuItem>().Single(row => Equals(row.CommandParameter, hours));
                    Assert.IsAssignableFrom<IInvokeProvider>(new MenuItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke();
                    await Idle(main); Assert.Equal(mode == "history", model.DashboardHistoryMode);
                    main.HealthWindowButton.ContextMenu.IsOpen = false;
                }
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(root, $"gui-{session}.error.txt"), error.ToString()); completion.TrySetResult(1);
            }
        }) { IsBackground = false, Name = "R121 owned process WPF dispatcher" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return await completion.Task;
    }
    private static void Publish(string root, string session, string phase, object value)
    {
        var path = FileName(R121PolicyProcessFixture.RequireRoot(root), session, phase);
        if (File.Exists(path)) { throw new IOException("Owned GUI observation already exists."); }
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, AgentProtocol.JsonOptions));
        File.Move(path + ".tmp", path);
    }
    private static async Task Idle(Window main)
    { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout(); }
    private sealed class NeverRemote : IRemoteHostReader
    {
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Owned resource GUI must never contact remote hosts.");
    }
}
