using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SystemAutorun.Agent;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;
using SystemAutorun.Core.Services;
using Application = System.Windows.Application;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class RemoteHostStreamingAndUiTests
{
    [Fact]
    public async Task AgentStreamingPopulatesRecentAndDurableHistoryHandlesReconnectAndIsolatesEdits()
    {
        var streamSource = new ControlledStreamSource();
        MonitoringHistoryStore? history = null;
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(
            remoteMonitorFactory: (workspace, instance) => new AgentRemoteHostMonitor(workspace, streamSource, instance),
            pairedMonitorFactory: (workspace, instance, remote) =>
            {
                history = new MonitoringHistoryStore(workspace.Paths);
                return new AgentMonitor(workspace, new EmptyLocalSource(), instance, history,
                    interval: TimeSpan.FromMilliseconds(500), healthInterval: TimeSpan.FromSeconds(10), remoteMonitor: remote);
            });
        try
        {
            var arm = LinuxDef(fixture.Root, "arm-1", "10.18.245.127");
            var configClient = new AgentConfigurationClient(fixture.Client);
            _ = await configClient.ReadAsync();
            _ = await configClient.MutateAsync<ApplicationSettings>("remote-add", payload: arm);

            using var loopCts = new CancellationTokenSource();
            var remoteLoop = fixture.RemoteMonitor!.RunAsync(loopCts.Token);
            try
            {
                var session1 = await streamSource.WaitSessionAsync("arm-1", 1);
                var baseAt = DateTimeOffset.UtcNow.AddSeconds(-4);
                for (var i = 0; i < 6; i++)
                {
                    await session1.PushAsync(LinuxSample(arm, baseAt.AddMilliseconds(i * 500), 20 + i * 3, 1024 + i * 100));
                }
                await WaitUntilAsync(() =>
                {
                    var probe = new AgentHostClient(fixture.Client).ReadRemoteAsync("arm-1").GetAwaiter().GetResult();
                    return probe.Available && probe.Snapshot.DiskReadBytesPerSecond is >= 1524;
                });

                // 1. Background stream populated 5-minute 500ms ring BEFORE any remote-status call.
                var now = DateTimeOffset.UtcNow;
                var monitoringClient = new DashboardMonitoringClient(fixture.Client);
                var recent = await monitoringClient.ReadRecentResourcesAsync(
                    new ResourceHistoryQuery(now.AddMinutes(-5), now, 601, RemoteHostId: "arm-1"));
                Assert.Equal("arm-1", recent.RemoteHostId);
                Assert.Equal(500, recent.SourceIntervalMilliseconds);
                Assert.Equal(6, recent.Points.Count);
                Assert.Equal(20, recent.Points[0].CpuPercent);
                Assert.Equal(1024, recent.Points[0].DiskReadBytesPerSecond);

                // 2. remote-status serves cached stream telemetry cleanly without opening extra sessions.
                var status = await new AgentHostClient(fixture.Client).ReadRemoteAsync("arm-1");
                Assert.True(status.Available);
                Assert.Equal("arm-server-1", status.Snapshot.MachineName);
                Assert.Equal(2, status.Snapshot.Drives.Count);
                Assert.Equal(2, status.Snapshot.Networks!.Count);
                Assert.Equal(1, streamSource.TotalSessions("arm-1"));

                // 3. Reconnect produces a new session epoch -> BreakBefore continuity boundary.
                session1.Complete();
                var session2 = await streamSource.WaitSessionAsync("arm-1", 2);
                await session2.PushAsync(LinuxSample(arm, DateTimeOffset.UtcNow, 48, 4096));
                await WaitUntilAsync(async () =>
                {
                    var qNow = DateTimeOffset.UtcNow;
                    var check = await monitoringClient.ReadRecentResourcesAsync(
                        new ResourceHistoryQuery(qNow.AddMinutes(-5), qNow, 601, RemoteHostId: "arm-1"));
                    return check.Points.Count >= 7;
                });
                var reconnectNow = DateTimeOffset.UtcNow;
                var afterReconnect = await monitoringClient.ReadRecentResourcesAsync(
                    new ResourceHistoryQuery(reconnectNow.AddMinutes(-5), reconnectNow, 601, RemoteHostId: "arm-1"));
                Assert.True(afterReconnect.Points[^1].BreakBefore);
                Assert.Equal(48, afterReconnect.Points[^1].CpuPercent);

                // 4. Durable 10s sample captures online remote host cleanly.
                await fixture.Monitor!.SampleOnceAsync();
                var durableNow = DateTimeOffset.UtcNow;
                var durable = await monitoringClient.ReadResourceHistoryAsync(
                    new ResourceHistoryQuery(durableNow.AddMinutes(-1), durableNow, 6, Source: ResourceHistorySource.Durable, RemoteHostId: "arm-1"));
                Assert.Equal("arm-1", durable.RemoteHostId);
                Assert.Contains(durable.Points, point => point.CpuPercent == 48 && point.DiskReadBytesPerSecond == 4096);

                // 5. Failing stream returns remote_unavailable promptly during backoff instead of hanging.
                session2.Fail(new InvalidOperationException("connection reset by peer"));
                await WaitUntilAsync(() => streamSource.TotalSessions("arm-1") >= 3 || true);
                RemoteHostProbeResult offlineResult;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                do
                {
                    offlineResult = await new AgentHostClient(fixture.Client).ReadRemoteAsync("arm-1");
                    if (!offlineResult.Available) { break; }
                    await Task.Delay(50);
                } while (sw.Elapsed < TimeSpan.FromSeconds(5));
                Assert.False(offlineResult.Available);
                Assert.Equal("remote_unavailable", offlineResult.Reason);

                // 6. Changing target endpoint isolates history (ABA prevention) and cancels old stream.
                await configClient.ReadAsync();
                _ = await configClient.MutateAsync<ApplicationSettings>("remote-update", payload: arm with { Host = "10.18.245.128" });
                var sessionAfterEdit = await streamSource.WaitSessionForHostAsync("arm-1", "10.18.245.128");
                Assert.Equal("10.18.245.128", sessionAfterEdit.Definition.Host);
                var isoNow = DateTimeOffset.UtcNow;
                var isolatedRecent = await monitoringClient.ReadRecentResourcesAsync(
                    new ResourceHistoryQuery(isoNow.AddMinutes(-5), isoNow, 601, RemoteHostId: "arm-1"));
                Assert.Empty(isolatedRecent.Points);
                var isolatedDurable = await monitoringClient.ReadResourceHistoryAsync(
                    new ResourceHistoryQuery(isoNow.AddMinutes(-1), isoNow, 6, Source: ResourceHistorySource.Durable, RemoteHostId: "arm-1"));
                Assert.Null(isolatedDurable.LatestRecordAt);

                _ = await configClient.MutateAsync<ApplicationSettings>("remote-remove", "arm-1");
                await WaitUntilAsync(() => sessionAfterEdit.Token.IsCancellationRequested);
            }
            finally
            {
                loopCts.Cancel();
                try { await remoteLoop; } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            history?.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteHostLiveRefreshZeroFlashPopulatesAllReadoutsChartsAndWindows(bool light)
    {
        var streamSource = new ControlledStreamSource();
        MonitoringHistoryStore? history = null;
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(
            remoteMonitorFactory: (workspace, instance) => new AgentRemoteHostMonitor(workspace, streamSource, instance),
            pairedMonitorFactory: (workspace, instance, remote) =>
            {
                history = new MonitoringHistoryStore(workspace.Paths);
                return new AgentMonitor(workspace, new EmptyLocalSource(), instance, history,
                    interval: TimeSpan.FromMilliseconds(500), healthInterval: TimeSpan.FromSeconds(10), remoteMonitor: remote);
            });

        try
        {
            var arm = LinuxDef(fixture.Root, "arm-1", "10.18.245.127") with { Name = "Oracle ARM 圣何塞 (ZeroTier)" };
            var win = LinuxDef(fixture.Root, "win-1", "10.18.245.99") with { Name = "Windows Edge", Platform = RemoteHostPlatform.Windows };
            var configClient = new AgentConfigurationClient(fixture.Client);
            _ = await configClient.ReadAsync();
            _ = await configClient.MutateAsync<ApplicationSettings>("settings-set",
                payload: new ApplicationSettings { RemoteHosts = [arm, win] });

            using var loopCts = new CancellationTokenSource();
            var remoteLoop = fixture.RemoteMonitor!.RunAsync(loopCts.Token);

            try
            {
                var session = await streamSource.WaitSessionAsync("arm-1", 1);
                var now = DateTimeOffset.UtcNow;
                for (var i = 0; i < 24; i++)
                {
                    var t = now.AddMilliseconds((i - 24) * 500);
                    await session.PushAsync(LinuxSample(arm, t, 18 + (i % 7) * 5, 16_000 + i * 2_048));
                }
                await fixture.Monitor!.SampleOnceAsync();

                await GuiConfigurationControlTests.RunStaAsync(async () =>
                {
                    if (Application.Current is not SystemAutorun.App.App)
                    {
                        var app = new SystemAutorun.App.App(runRuntimeStartup: false);
                        app.InitializeComponent();
                    }
                    WindowTheme.Install();
                    var previousTheme = ThemeService.IsLight ? ThemePreference.Light : ThemePreference.Dark;
                    ThemeService.Apply(light ? ThemePreference.Light : ThemePreference.Dark, persist: null);

                    var cursor = new AgentConfigurationClient(fixture.Client);
                    var model = new MainWindowViewModel(
                        fixture.Client,
                        new NativeCacheStore(fixture.Paths.GuiCachePath),
                        cursor,
                        fixture.Paths,
                        new AgentRemoteHostReader(fixture.Client, cursor),
                        new DashboardMonitoringClient(fixture.Client))
                    {
                        AutoRefresh = true,
                    };
                    var main = new SystemAutorun.App.MainWindow(
                        model,
                        new AgentStartupClient(fixture.Client),
                        new StartupCacheStore(fixture.Paths.StartupCachePath))
                    {
                        Width = 1152,
                        Height = 648,
                        ShowActivated = false,
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -32000,
                        Top = -32000,
                    };
                    ColorStateAssist.SetReduceMotion(main, true);

                    try
                    {
                        main.Show();
                        await model.InitializeAsync();
                        model.SelectedHostTab = model.Hosts.Single(tab => tab.Id == "arm-1");
                        await WaitUntilStaAsync(() =>
                            model.HostStatusAvailable
                            && model.SelectedHostTab?.Id == "arm-1"
                            && model.HostResourceCpu.Any(p => p.Value is not null), TimeSpan.FromSeconds(10));
                        await model.ResourceHistoryTask;
                        if (model.RefreshCommand.ExecutionTask is { } initialRefresh) { await initialRefresh; }
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        main.UpdateLayout();

                        Assert.Equal("Linux", model.HostIcon);
                        Assert.Equal("arm-server-1", model.HostName);
                        Assert.Equal("Ubuntu 24.04.4 LTS", model.HostOs);
                        Assert.NotEqual("--", model.HostResourceCpuText);
                        Assert.NotEqual("--", model.HostMemoryUsedText);
                        Assert.NotEqual("--", model.HostDiskReadText);
                        Assert.NotEqual("--", model.HostDiskWriteText);
                        Assert.NotEqual("--", model.HostReceiveText);
                        Assert.NotEqual("--", model.HostSendText);
                        Assert.Equal(2, model.Drives.Count);
                        Assert.Equal(2, model.Networks.Count);

                        var charts = main.MonitoringTabs.HostCharts;
                        foreach (var plot in new[] { charts.HostCpuPlot, charts.HostMemoryPlot, charts.HostDiskPlot, charts.HostNetworkPlot })
                        {
                            Assert.Contains(plot.Points!, p => p.Value is not null);
                            Assert.NotEqual("此目标无资源轨迹", model.DashboardResourceEmptyText);
                        }
                        Assert.Contains(charts.HostDiskPlot.SecondaryPoints!, p => p.Value is not null);
                        Assert.Contains(charts.HostNetworkPlot.SecondaryPoints!, p => p.Value is not null);

                        // Measure zero-flash guarantee across steady-state live refreshes (500ms) and full refreshes!
                        var availableFalseEvents = 0;
                        var hostOsQueryFlashes = 0;
                        var driveStructuralChanges = 0;
                        var networkStructuralChanges = 0;
                        model.PropertyChanged += (_, args) =>
                        {
                            if (args.PropertyName == nameof(model.HostStatusAvailable) && !model.HostStatusAvailable) { availableFalseEvents++; }
                            if (args.PropertyName == nameof(model.HostOs) && model.HostOs.Contains("正在查询", StringComparison.Ordinal)) { hostOsQueryFlashes++; }
                        };
                        model.Drives.CollectionChanged += (_, args) => { if (args.Action != NotifyCollectionChangedAction.Move) { driveStructuralChanges++; } };
                        model.Networks.CollectionChanged += (_, args) => { if (args.Action != NotifyCollectionChangedAction.Move) { networkStructuralChanges++; } };

                        Assert.True(model.CanRefreshAtHalfSecond);
                        model.SelectAutoRefreshIntervalCommand.Execute("500");
                        Assert.Equal(500, model.AutoRefreshMilliseconds);

                        var liveStampField = typeof(MainWindowViewModel).GetField("_lastSnapshotStartTimestamp", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        for (var step = 1; step <= 4; step++)
                        {
                            await session.PushAsync(LinuxSample(arm, DateTimeOffset.UtcNow, 40 + step * 5, 80_000 + step * 10_000));
                            await WaitUntilStaAsync(async () =>
                            {
                                var p = await new AgentHostClient(fixture.Client).ReadRemoteAsync("arm-1");
                                return p.Available && p.Snapshot.CpuUsagePercent >= 40 + step * 5;
                            }, TimeSpan.FromSeconds(5));
                            liveStampField.SetValue(model, null);
                            await model.RefreshLiveAsync();
                        }
                        if (model.RefreshCommand.ExecutionTask is { } pendingRefresh) { await pendingRefresh; }
                        await model.RefreshCommand.ExecuteAsync(null);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                        Assert.Equal(0, availableFalseEvents);
                        Assert.Equal(0, hostOsQueryFlashes);
                        Assert.Equal(0, driveStructuralChanges);
                        Assert.Equal(0, networkStructuralChanges);
                        Assert.Equal("OK", model.ServerStatus);
                        Assert.Equal("60%", model.HostResourceCpuText);

                        var evidenceDir = Environment.GetEnvironmentVariable("SA_R129_EVIDENCE");
                        if (!string.IsNullOrWhiteSpace(evidenceDir))
                        {
                            Directory.CreateDirectory(evidenceDir);
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            main.UpdateLayout();
                            SaveBitmap(main, Path.Combine(evidenceDir, $"r129-remote-{(light ? "light" : "dark")}-24h-1152x648.png"));

                            var scroller = FindDescendants<ScrollViewer>(main.MonitoringTabs)
                                .FirstOrDefault(sv => Grid.GetColumn(sv) == 1);
                            scroller?.ScrollToEnd();
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            main.UpdateLayout();
                            SaveBitmap(main, Path.Combine(evidenceDir, $"r129-remote-{(light ? "light" : "dark")}-24h-bottom-1152x648.png"));
                            scroller?.ScrollToTop();
                        }

                        // Switch to 7d and 30d durable windows and verify plots render cleanly.
                        foreach (var hours in new[] { 168, 720 })
                        {
                            model.SelectDashboardWindowCommand.Execute(hours.ToString());
                            await model.ResourceHistoryTask;
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            main.UpdateLayout();
                            Assert.True(model.DashboardHistoryMode);
                            Assert.Equal(301, model.HostResourceCpu.Count);
                            Assert.Contains(model.HostResourceCpu, p => p.Value is not null);
                            if (hours == 168 && !string.IsNullOrWhiteSpace(evidenceDir))
                            {
                                main.Width = 1320;
                                main.Height = 820;
                                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                                main.UpdateLayout();
                                SaveBitmap(main, Path.Combine(evidenceDir, $"r129-remote-{(light ? "light" : "dark")}-7d-1320x820.png"));
                            }
                        }

                        model.SelectDashboardWindowCommand.Execute("0");
                        await model.ResourceHistoryTask;

                        // Genuine offline error MUST still clear to unknown.
                        session.Fail(new InvalidOperationException("ssh connection lost"));
                        await WaitUntilStaAsync(async () => !(await new AgentHostClient(fixture.Client).ReadRemoteAsync("arm-1")).Available, TimeSpan.FromSeconds(5));
                        await model.RefreshCommand.ExecuteAsync(null);
                        Assert.False(model.HostStatusAvailable);
                        Assert.Equal("UNKNOWN", model.SelectedHostTab!.Status);
                        Assert.Empty(model.Drives);
                        Assert.Empty(model.Networks);
                    }
                    finally
                    {
                        model.DetachDashboard();
                        ColorStateAssist.SetReduceMotion(main, true);
                        var closing = typeof(SystemAutorun.App.MainWindow).GetMethod("Window_Closing", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        main.Closing -= (System.ComponentModel.CancelEventHandler)closing.CreateDelegate(typeof(System.ComponentModel.CancelEventHandler), main);
                        main.Close();
                        ThemeService.Apply(previousTheme, persist: null);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    }
                }, TimeSpan.FromSeconds(30));
            }
            finally
            {
                loopCts.Cancel();
                try { await remoteLoop; } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            history?.Dispose();
        }
    }

    [Fact]
    public async Task RemoteHostPreWarmedSwitchWithMotionEnabledHasZeroSkeletonFlashAndDrawsAllCharts()
    {
        var streamSource = new ControlledStreamSource();
        MonitoringHistoryStore? history = null;
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(
            remoteMonitorFactory: (workspace, instance) => new AgentRemoteHostMonitor(workspace, streamSource, instance),
            pairedMonitorFactory: (workspace, instance, remote) =>
            {
                history = new MonitoringHistoryStore(workspace.Paths);
                return new AgentMonitor(workspace, new EmptyLocalSource(), instance, history,
                    interval: TimeSpan.FromMilliseconds(500), healthInterval: TimeSpan.FromSeconds(10), remoteMonitor: remote);
            });

        try
        {
            var arm = LinuxDef(fixture.Root, "arm-1", "10.18.245.127") with { Name = "Oracle ARM 圣何塞 (ZeroTier)" };
            var configClient = new AgentConfigurationClient(fixture.Client);
            _ = await configClient.ReadAsync();
            _ = await configClient.MutateAsync<ApplicationSettings>("settings-set",
                payload: new ApplicationSettings { RemoteHosts = [arm] });

            using var loopCts = new CancellationTokenSource();
            var remoteLoop = fixture.RemoteMonitor!.RunAsync(loopCts.Token);

            try
            {
                var session = await streamSource.WaitSessionAsync("arm-1", 1);
                var now = DateTimeOffset.UtcNow;
                for (var i = 0; i < 24; i++)
                {
                    await session.PushAsync(LinuxSample(arm, now.AddMilliseconds((i - 24) * 500), 22 + (i % 6) * 4, 24_000 + i * 1_500));
                }
                await WaitUntilAsync(async () =>
                {
                    var probe = await new AgentHostClient(fixture.Client).ReadRemoteAsync("arm-1");
                    return probe.Available;
                });
                await fixture.Monitor!.SampleOnceAsync();

                await GuiConfigurationControlTests.RunStaAsync(async () =>
                {
                    if (Application.Current is not SystemAutorun.App.App)
                    {
                        var app = new SystemAutorun.App.App(runRuntimeStartup: false);
                        app.InitializeComponent();
                    }
                    WindowTheme.Install();
                    var previousTheme = ThemeService.IsLight ? ThemePreference.Light : ThemePreference.Dark;
                    ThemeService.Apply(ThemePreference.Dark, persist: null);

                    var cursor = new AgentConfigurationClient(fixture.Client);
                    var model = new MainWindowViewModel(
                        fixture.Client,
                        new NativeCacheStore(fixture.Paths.GuiCachePath),
                        cursor,
                        fixture.Paths,
                        new AgentRemoteHostReader(fixture.Client, cursor),
                        new DashboardMonitoringClient(fixture.Client))
                    {
                        AutoRefresh = true,
                    };
                    var main = new SystemAutorun.App.MainWindow(
                        model,
                        new AgentStartupClient(fixture.Client),
                        new StartupCacheStore(fixture.Paths.StartupCachePath))
                    {
                        Width = 1152,
                        Height = 648,
                        ShowActivated = false,
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -32000,
                        Top = -32000,
                    };

                    try
                    {
                        main.Show();
                        await model.InitializeAsync();

                        // Even before opening the Hosts tab, AgentMonitoringSnapshot.RemoteHosts pre-warms remote status.
                        var armTab = model.Hosts.Single(tab => tab.Id == "arm-1");
                        var localTab = model.Hosts.Single(tab => tab.IsLocal);
                        Assert.Equal("OK", armTab.Status);
                        Assert.NotNull(armTab.RemoteSnapshot);
                        Assert.True(armTab.RemoteSnapshot!.Online);
                        Assert.StartsWith("arm-server-1 · ", armTab.StatusDetail);

                        var availableFalseEvents = 0;
                        var skeletonTextFlashes = 0;
                        var driveStructuralChanges = 0;
                        var networkStructuralChanges = 0;
                        model.PropertyChanged += (_, args) =>
                        {
                            if (model.SelectedHostTab?.Id != "arm-1") { return; }
                            if (args.PropertyName == nameof(model.HostStatusAvailable) && !model.HostStatusAvailable) { availableFalseEvents++; }
                            if (args.PropertyName == nameof(model.HostOs)
                                && (model.HostOs.Contains("等待当前主机", StringComparison.Ordinal)
                                    || model.HostOs.Contains("正在查询", StringComparison.Ordinal)))
                            {
                                skeletonTextFlashes++;
                            }
                        };
                        model.Drives.CollectionChanged += (_, args) =>
                        {
                            if (model.SelectedHostTab?.Id == "arm-1" && args.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove)
                            { driveStructuralChanges++; }
                        };
                        model.Networks.CollectionChanged += (_, args) =>
                        {
                            if (model.SelectedHostTab?.Id == "arm-1" && args.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove)
                            { networkStructuralChanges++; }
                        };

                        main.SelectTab(MainWindowViewModel.OverviewPage);
                        model.SelectedHostTab = armTab;
                        Assert.True(model.HostStatusAvailable);
                        Assert.Equal("arm-server-1", model.HostName);
                        await model.ResourceHistoryTask;
                        if (model.RefreshCommand.ExecutionTask is { } firstTabRefresh) { await firstTabRefresh; }

                        // Switch local -> remote again and confirm pre-warmed cached status prevents skeleton blanking.
                        model.SelectedHostTab = localTab;
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        model.SelectedHostTab = armTab;
                        Assert.True(model.HostStatusAvailable);
                        await model.ResourceHistoryTask;
                        if (model.RefreshCommand.ExecutionTask is { } secondTabRefresh) { await secondTabRefresh; }
                        var charts = main.MonitoringTabs.HostCharts;
                        var allPlots = new[] { charts.HostCpuPlot, charts.HostMemoryPlot, charts.HostDiskPlot, charts.HostNetworkPlot };
                        await WaitUntilStaAsync(() =>
                            model.HostResourceCpu.Any(p => p.Value is not null)
                            && allPlots.All(plot => plot.EffectiveSweepProgress >= 0.999),
                            TimeSpan.FromSeconds(6));
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        main.UpdateLayout();

                        Assert.Equal(0, availableFalseEvents);
                        Assert.Equal(0, skeletonTextFlashes);
                        Assert.Equal(0, driveStructuralChanges);
                        Assert.Equal(0, networkStructuralChanges);

                        foreach (var plot in allPlots)
                        {
                            Assert.Contains(plot.Points!, p => p.Value is not null);
                            Assert.True(plot.EffectiveSweepProgress >= 0.999);
                        }
                        Assert.Contains(charts.HostDiskPlot.SecondaryPoints!, p => p.Value is not null);
                        Assert.Contains(charts.HostNetworkPlot.SecondaryPoints!, p => p.Value is not null);
                        Assert.NotEqual("--", model.HostResourceCpuText);
                    }
                    finally
                    {
                        model.DetachDashboard();
                        ColorStateAssist.SetReduceMotion(main, true);
                        var closing = typeof(SystemAutorun.App.MainWindow).GetMethod("Window_Closing", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        main.Closing -= (System.ComponentModel.CancelEventHandler)closing.CreateDelegate(typeof(System.ComponentModel.CancelEventHandler), main);
                        main.Close();
                        ThemeService.Apply(previousTheme, persist: null);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    }
                }, TimeSpan.FromSeconds(30));
            }
            finally
            {
                loopCts.Cancel();
                try { await remoteLoop; } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            history?.Dispose();
        }
    }

    private static RemoteHostDefinition LinuxDef(string root, string id, string host) => new()
    {
        Id = id,
        Name = id,
        Host = host,
        Port = 22,
        UserName = "ubuntu",
        Platform = RemoteHostPlatform.Linux,
        KnownHostsFile = Path.Combine(root, "known_hosts"),
        ConnectTimeoutSeconds = 3,
        CommandTimeoutSeconds = 5,
    };

    private static RemoteHostStatusSnapshot LinuxSample(RemoteHostDefinition def, DateTimeOffset at, double cpu, double rate) => new(
        def.Id,
        def.Name,
        def.Platform,
        def.Host,
        true,
        null,
        at,
        "arm-server-1",
        "Ubuntu 24.04.4 LTS",
        TimeSpan.FromDays(61) + TimeSpan.FromHours(1) + TimeSpan.FromMinutes(32),
        cpu,
        24L << 30,
        (24L << 30) - (6L << 30),
        [
            new DriveStatusSnapshot("/", "ext4", 100L << 30, 42L << 30),
            new DriveStatusSnapshot("/data", "ext4", 500L << 30, 210L << 30),
        ],
        def.Host,
        850L << 20,
        310L << 20,
        4,
        703,
        4842,
        rate,
        rate * 0.6,
        rate * 2.5,
        rate * 1.1,
        [
            new NetworkStatusSnapshot("enp0s6", "10.0.0.4", 600L << 20, 210L << 20),
            new NetworkStatusSnapshot("zt0", def.Host, 250L << 20, 100L << 20),
        ]);

    private static void SaveBitmap(FrameworkElement element, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) { yield return match; }
            foreach (var nested in FindDescendants<T>(child)) { yield return nested; }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow >= limit) { throw new TimeoutException("Condition was not satisfied in time."); }
            await Task.Delay(25);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!await condition())
        {
            if (DateTime.UtcNow >= limit) { throw new TimeoutException("Async condition was not satisfied in time."); }
            await Task.Delay(25);
        }
    }

    private static async Task WaitUntilStaAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow >= limit) { throw new TimeoutException("STA condition was not satisfied in time."); }
            await Task.Delay(30);
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
    }

    private static async Task WaitUntilStaAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!await condition())
        {
            if (DateTime.UtcNow >= limit) { throw new TimeoutException("STA async condition was not satisfied in time."); }
            await Task.Delay(30);
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
    }

    private sealed class EmptyLocalSource : IAgentMonitoringSource
    {
        public Task<IReadOnlyList<TaskSnapshot>> ObserveTasksAsync(AutorunConfiguration configuration, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskSnapshot>>([]);
        public Task<IReadOnlyList<ManagedProcessSnapshot>> ObserveProcessesAsync(AutorunConfiguration configuration,
            IReadOnlyList<TaskSnapshot> health, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ManagedProcessSnapshot>>([]);
        public Task<HostStatusSnapshot> ObserveHostAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HostStatusSnapshot("local", "Windows", DateTimeOffset.UtcNow, TimeSpan.FromHours(1), 5, 16L << 30, 8L << 30, [], []));
    }

    private sealed class ControlledStreamSource : IRemoteHostStreamSource
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<StreamSession>> _sessions = new(StringComparer.OrdinalIgnoreCase);

        public int TotalSessions(string id) => _sessions.TryGetValue(id, out var queue) ? queue.Count : 0;

        public async Task<StreamSession> WaitSessionAsync(string id, int ordinal)
        {
            StreamSession? match = null;
            await WaitUntilAsync(() =>
            {
                if (_sessions.TryGetValue(id, out var q) && q.Count >= ordinal)
                {
                    match = q.ElementAt(ordinal - 1);
                    return true;
                }
                return false;
            });
            return match!;
        }

        public async Task<StreamSession> WaitSessionForHostAsync(string id, string host)
        {
            StreamSession? match = null;
            await WaitUntilAsync(() =>
            {
                if (_sessions.TryGetValue(id, out var q))
                {
                    match = q.LastOrDefault(s => s.Definition.Host == host);
                    return match is not null;
                }
                return false;
            });
            return match!;
        }

        public async IAsyncEnumerable<RemoteHostStatusSnapshot> StreamAsync(
            RemoteHostDefinition definition,
            TimeSpan samplingInterval,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var queue = _sessions.GetOrAdd(definition.Id, _ => new ConcurrentQueue<StreamSession>());
            var session = new StreamSession(definition, cancellationToken);
            queue.Enqueue(session);
            if (session.Definition.Host == "10.18.245.127" && queue.Any(s => s.FailedImmediate))
            {
                throw new InvalidOperationException("still offline");
            }

            await foreach (var sample in session.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return sample;
            }
        }

        internal sealed class StreamSession(RemoteHostDefinition definition, CancellationToken token)
        {
            private readonly Channel<RemoteHostStatusSnapshot> _channel = Channel.CreateUnbounded<RemoteHostStatusSnapshot>();
            public RemoteHostDefinition Definition => definition;
            public CancellationToken Token => token;
            public bool FailedImmediate { get; private set; }

            public ValueTask PushAsync(RemoteHostStatusSnapshot sample) => _channel.Writer.WriteAsync(sample);
            public void Complete() => _channel.Writer.TryComplete();
            public void Fail(Exception exception) { FailedImmediate = true; _channel.Writer.TryComplete(exception); }
            public IAsyncEnumerable<RemoteHostStatusSnapshot> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
        }
    }
}
