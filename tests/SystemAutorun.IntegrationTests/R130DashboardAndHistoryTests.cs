using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SystemAutorun.Agent;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Path = System.IO.Path;
using Point = System.Windows.Point;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class R130DashboardAndHistoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task HostHeaderOmitsSshConnectionLineAndShowsMemoryPercentReadout(bool light) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, _) =>
        {
            ColorStateAssist.SetReduceMotion(main, true);
            model.CurrentPage = MainWindowViewModel.OverviewPage;
            var remoteDef = new RemoteHostDefinition
            {
                Id = "r130-tx",
                Name = "腾讯云",
                Enabled = true,
                Platform = RemoteHostPlatform.Linux,
                Host = "192.0.2.55",
                Port = 22,
                UserName = "root",
                Authentication = RemoteAuthenticationMode.SshAgent,
                KnownHostsFile = Path.Combine(Path.GetTempPath(), "r130-known-hosts"),
            };
            var remoteTab = new HostTabViewModel(remoteDef);
            var now = DateTimeOffset.UtcNow;
            var totalBytes = 3_543_348_019L;
            var usedBytes = 2_576_980_378L;
            var remoteSnapshot = new RemoteHostStatusSnapshot(
                remoteDef.Id,
                remoteDef.Name,
                remoteDef.Platform,
                remoteDef.Host,
                true,
                null,
                now,
                "VM-12-15-ubuntu",
                "Ubuntu 22.04.4 LTS",
                TimeSpan.FromDays(157).Add(TimeSpan.FromHours(17)),
                2.0,
                totalBytes,
                totalBytes - usedBytes,
                [],
                "10.0.0.12",
                123_456,
                654_321,
                2,
                394,
                1911);
            remoteTab.ApplyRemoteStatus(remoteSnapshot);
            model.Hosts.Add(remoteTab);
            model.SelectedHostTab = remoteTab;
            typeof(MainWindowViewModel).GetMethod("ApplyRemoteHostStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(model, [remoteSnapshot]);

            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();

            var header = Assert.IsType<Border>(main.MonitoringTabs.FindName("HostHeader"));
            var headerTexts = Descendants(header).OfType<TextBlock>().ToArray();
            Assert.DoesNotContain(headerTexts, tb => tb.Text.Contains("SSH", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(headerTexts, tb =>
                BindingOperations.GetBinding(tb, TextBlock.TextProperty)?.Path?.Path == nameof(model.HostConnectionText));

            var charts = main.MonitoringTabs.HostCharts;
            Assert.Equal("73%", model.HostMemoryPercentText);
            Assert.Equal("73%", charts.HostMemoryReadout.Text);
            Assert.Contains("/", model.MemoryUsageText, StringComparison.Ordinal);
            Assert.NotEqual(model.HostMemoryUsedText, model.HostMemoryPercentText);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FailedServiceImmediatelyRendersRedFaultBlockAndFlipsPowerActionToStart(bool light) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, source) =>
        {
            ColorStateAssist.SetReduceMotion(main, true);
            model.AutoRefresh = false;

            var targetTask = model.Tasks[0];
            model.SelectedTask = targetTask;
            targetTask.Update(targetTask.Snapshot with
            {
                Status = "FAIL",
                Healthy = false,
                HealthObserved = true,
                Reason = "process-missing",
                Detail = "进程未运行",
            });

            source.HistoryReader = (query, _) => Task.FromResult(new MonitoringHistoryResult(
                1,
                query.TaskName,
                query.TaskIdentity,
                query.From,
                query.To,
                query.To,
                query.From,
                Math.Max(0, (query.To - query.From).TotalSeconds - 5),
                5,
                0,
                99.9,
                100,
                [
                    new(query.From, query.To.AddSeconds(-5), ObservationHealth.Healthy),
                    new(query.To.AddSeconds(-5), query.To, ObservationHealth.Fault),
                ],
                [],
                Truncated: false,
                Warning: null));

            foreach (var hours in new[] { 24, 0 })
            {
                model.SelectDashboardWindowCommand.Execute(hours.ToString(CultureInfo.InvariantCulture));
                await model.DashboardHistoryTask;
                model.TickDashboard();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                main.UpdateLayout();

                Assert.False(model.IsSelectedTaskRunning);
                Assert.Same(model.StartCommand, main.ServicePowerButton.Command);
                Assert.Equal("启动服务", main.ServicePowerButton.ToolTip);
                Assert.Equal("启动服务", AutomationProperties.GetName(main.ServicePowerButton));
                var powerIcon = Descendants(main.ServicePowerButton).OfType<LineIcon>().Single();
                Assert.Equal("Play", powerIcon.Kind);

                var (pixels, pw, ph) = CapturePixels(main.ServiceHealthPlot);
                var count = Math.Max(1, (int)(main.ServiceHealthPlot.ActualWidth / 9));
                var stride = main.ServiceHealthPlot.ActualWidth / count;
                var lastCellCenterFracX = ((count - 1) * stride + Math.Max(1, stride - 3) * 0.5) / main.ServiceHealthPlot.ActualWidth;
                var redHits = CountRedPixels(pixels, pw, ph, Math.Max(0, lastCellCenterFracX - 0.015), Math.Min(1, lastCellCenterFracX + 0.015), 0.10, 0.45);
                Assert.True(redHits > 5, $"Expected red fault block in last cell for window={hours}h, got {redHits} red pixels.");

                var lastCellPoint = new Point((count - 1) * stride + Math.Max(1, stride - 3) * 0.5, 16);
                main.ServiceHealthPlot.UpdateHover(lastCellPoint);
                Assert.Contains("故障", main.ServiceHealthPlot.CurrentHoverText);
                main.ServiceHealthPlot.ClearHover();
            }

            targetTask.Update(targetTask.Snapshot with
            {
                Status = "OK",
                Healthy = true,
                HealthObserved = true,
                Reason = "process-running",
                Detail = "进程正常运行",
            });
            model.TickDashboard();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(model.IsSelectedTaskRunning);
            Assert.Same(model.StopCommand, main.ServicePowerButton.Command);
            Assert.Equal("Stop", Descendants(main.ServicePowerButton).OfType<LineIcon>().Single().Kind);
            Assert.Equal("停止服务", main.ServicePowerButton.ToolTip);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ServicePageTypographyAndColumnsAlignAndTrendBottomRightTimesAreRemoved(bool light) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, _) =>
        {
            ColorStateAssist.SetReduceMotion(main, true);
            model.CurrentPage = MainWindowViewModel.WorkloadsPage;
            main.Width = 1152;
            main.Height = 648;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();

            var titleX = main.ServiceTitle.TranslatePoint(new Point(), main).X;
            var availabilityLabel = Descendants(main.ServicesPage).OfType<TextBlock>().First(tb => tb.Text == "可用率" && tb.IsVisible);
            var cpuLabel = Assert.IsType<TextBlock>(main.FindName("ServiceCpuLabel"));
            var memoryLabel = Assert.IsType<TextBlock>(main.FindName("ServiceMemoryLabel"));
            var availX = availabilityLabel.TranslatePoint(new Point(), main).X;
            var cpuX = cpuLabel.TranslatePoint(new Point(), main).X;
            var memX = memoryLabel.TranslatePoint(new Point(), main).X;
            Assert.InRange(Math.Abs(titleX - availX), 0, 1.0);
            Assert.InRange(Math.Abs(titleX - cpuX), 0, 1.0);
            Assert.InRange(Math.Abs(titleX - memX), 0, 1.0);

            var availValue = Assert.IsType<TextBlock>(main.FindName("ServiceAvailabilityText"));
            var uptimeValue = Assert.IsType<TextBlock>(main.FindName("ServiceUptimeText"));
            Assert.Same(availabilityLabel.FontFamily, availValue.FontFamily);
            Assert.Same(availabilityLabel.FontFamily, uptimeValue.FontFamily);
            Assert.Equal(availabilityLabel.FontSize, availValue.FontSize);
            Assert.InRange(Math.Abs(CenterY(availabilityLabel, main) - CenterY(availValue, main)), 0, 1.0);
            Assert.InRange(Math.Abs(CenterY(availabilityLabel, main) - CenterY(uptimeValue, main)), 0, 1.5);

            var cpuMeta = Assert.IsType<TextBlock>(main.FindName("ServiceCpuMetaText"));
            var memMeta = Assert.IsType<TextBlock>(main.FindName("ServiceMemoryMetaText"));
            Assert.Same(cpuLabel.FontFamily, cpuMeta.FontFamily);
            Assert.Same(memoryLabel.FontFamily, memMeta.FontFamily);
            Assert.Equal(string.Empty, main.ServiceCpuPlot.Caption);
            Assert.Equal(string.Empty, main.ServiceMemoryPlot.Caption);

            // Trend bottom-right time range must no longer render on trend plots.
            var (plotPixels, pw, ph) = CapturePixels(main.ServiceCpuPlot);
            var inkCount = CountInkPixels(plotPixels, pw, ph, 0.78, 0.99, 0.83, 0.99);
            Assert.Equal(0, inkCount);
        });

    [Fact]
    public Task FourItemWindowMenuAndPlotLoadingAnimationWorkAcrossServiceAndHost() =>
        ResourceDashboardWindowTests.WithResourceWindow(false, async (model, main, source) =>
        {
            ColorStateAssist.SetReduceMotion(main, false);
            var serviceItems = main.HealthWindowButton.ContextMenu!.Items.OfType<MenuItem>().ToArray();
            var hostItems = main.MonitoringTabs.HostCharts.HostWindowButton.ContextMenu!.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(["实时", "最近1天", "最近1周", "最近1月"], serviceItems.Select(i => (string)i.Header));
            Assert.Equal(["0", "24", "168", "720"], serviceItems.Select(i => (string)i.CommandParameter!));
            Assert.Equal(["实时", "最近1天", "最近1周", "最近1月"], hostItems.Select(i => (string)i.Header));
            Assert.Equal(["0", "24", "168", "720"], hostItems.Select(i => (string)i.CommandParameter!));
            main.HealthWindowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.HealthWindowButton.ContextMenu!.IsOpen = false;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(0, model.DashboardWindowHours);
            Assert.True(serviceItems[0].IsChecked);
            Assert.Equal(0, main.ServiceHealthPlot.WindowHours);
            Assert.Equal(TimeSpan.FromMinutes(5), model.DashboardHealthDuration);
            Assert.Equal(TimeSpan.FromMinutes(5), main.ServiceCpuPlot.WindowDuration);

            var historyDelay = new TaskCompletionSource<MonitoringHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.HistoryReader = (_, _) => historyDelay.Task;

            // Exercise Model-driven loading state via QueueDashboardHistoryAsync with delayed reader.
            model.SelectDashboardWindowCommand.Execute("24");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(model.IsHistoryLoading);
            Assert.True(main.ServiceHealthPlot.IsLoading);
            Assert.True(serviceItems[1].IsChecked);
            Assert.False(serviceItems[0].IsChecked);

            var end = DateTimeOffset.UtcNow;
            historyDelay.SetResult(new MonitoringHistoryResult(1, model.SelectedTask!.Name,
                MonitoringTaskIdentity.For(model.SelectedTask.Snapshot),
                end.AddHours(-24), end, end, end.AddHours(-24), 86400, 0, 0, 100, 100,
                [new(end.AddHours(-24), end, ObservationHealth.Healthy)], [], false, null));
            await model.DashboardHistoryTask;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(model.IsHistoryLoading);
            Assert.False(main.ServiceHealthPlot.IsLoading);

            // Also verify DashboardPlot renders "正在载入…" and the crimson sweep bar when IsLoading=true and empty points.
            var loadingPlot = new DashboardPlot
            {
                Mode = "Trend",
                Width = 320,
                Height = 140,
                EndAt = end,
                WindowDuration = TimeSpan.FromMinutes(5),
                Points = [],
                IsLoading = true,
            };
            var host = new Window
            {
                Content = new Border { Background = (Brush)main.FindResource("Semantic.Brush.WindowBackground"), Child = loadingPlot },
                Width = 340, Height = 160, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            };
            ColorStateAssist.SetReduceMotion(host, false);
            try
            {
                host.Show();
                await Dispatcher.Yield(DispatcherPriority.Render);
                host.UpdateLayout();
                var (px, w, h) = CapturePixels(loadingPlot);
                var bottomRed = CountRedPixels(px, w, h, 0.01, 0.99, 0.74, 0.84);
                Assert.True(bottomRed > 15, $"Expected crimson loading sweep bar when IsLoading=true, got {bottomRed} red pixels.");
            }
            finally { host.Close(); }
        });

    [Fact]
    public async Task HistoryStoreSegmentCacheAcceleratesRepeatedQueriesAndDetectsSameSizeRewrites()
    {
        var root = Directory.CreateTempSubdirectory("SystemAutorun-r130-cache-tests-").FullName;
        try
        {
            var anchor = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
            var clock = new FixedClock(anchor.AddHours(48));
            var paths = new ProjectPaths(root);
            var rootId = AgentProtocol.ForRoot(root).RootId;
            var instanceId = Guid.NewGuid().ToString("N");
            var taskIdentity = new string('c', 64);

            using var store = new MonitoringHistoryStore(paths, clock: clock);
            for (var hour = 0; hour < 30; hour++)
            {
                for (var step = 0; step < 30; step++)
                {
                    var ts = anchor.AddHours(hour).AddMinutes(step);
                    await store.AppendAsync(new MonitoringHistoryFrame(
                        1, rootId, instanceId, ts,
                        [new TaskHistorySample("svc-cache", taskIdentity, ObservationHealth.Healthy, 5.2, 128L << 20, 4L << 30)],
                        Host: new HostHistorySample(12.0, 2L << 30, 4L << 30, 1000, 500, 2000, 1000)));
                }
            }

            var query = new MonitoringHistoryQuery("svc-cache", taskIdentity, anchor, anchor.AddHours(30), 120);
            var resQuery = new ResourceHistoryQuery(anchor, anchor.AddHours(30), 301, Source: ResourceHistorySource.Durable);

            var cold = await store.QueryAsync(query, instanceId);
            var cacheField = typeof(MonitoringHistoryStore).GetField("_segmentCache", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var cache = Assert.IsAssignableFrom<System.Collections.IDictionary>(cacheField.GetValue(store));
            Assert.Equal(30, cache.Count);

            var sw = Stopwatch.StartNew();
            var warmHealth = await store.QueryAsync(query, instanceId);
            var warmRes = await store.QueryResourcesAsync(resQuery, instanceId);
            sw.Stop();
            Assert.Equal(cold.HealthySeconds, warmHealth.HealthySeconds);
            Assert.Equal(301, warmRes.Points.Count);
            Assert.True(sw.ElapsedMilliseconds < 1500, $"Expected cached dual query under 1500ms, got {sw.ElapsedMilliseconds}ms.");

            // Same-length, same-mtime rewrite must bust the SHA-256 content-verified segment cache.
            var firstFile = Directory.GetFiles(store.DirectoryPath, "history-v1-*.jsonl").Order(StringComparer.Ordinal).First();
            var mtime = File.GetLastWriteTimeUtc(firstFile);
            var beforeBytes = await File.ReadAllTextAsync(firstFile);
            var len = new FileInfo(firstFile).Length;
            var rewritten = beforeBytes.Replace("\"healthy\"", "\"unknown\"", StringComparison.Ordinal);
            Assert.Equal(beforeBytes.Length, rewritten.Length);
            await File.WriteAllTextAsync(firstFile, rewritten);
            File.SetLastWriteTimeUtc(firstFile, mtime);
            Assert.Equal(len, new FileInfo(firstFile).Length);

            var invalidated = await store.QueryAsync(query, instanceId);
            Assert.True(invalidated.HealthySeconds < cold.HealthySeconds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AgentCommandAndProcessExitImmediatelyScheduleHealthWithoutWaitingTenSeconds()
    {
        var source = new ImmediateHealthSource();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(monitorFactory: (workspace, instance) =>
            new AgentMonitor(workspace, source, instance, null, "isolated-no-store",
                interval: TimeSpan.FromMilliseconds(80), healthInterval: TimeSpan.FromSeconds(10)));
        await File.WriteAllTextAsync(fixture.Paths.ConfigPath,
            "version: 1\nsettings:\n  default_startup_delay: 0\ntasks:\n  - name: fixture\n    type: windows\n    exec: fixture-never-execute.exe\n    expected_running: false\n    health:\n      type: process\n      pattern: fixture-never-execute\n");
        using var stop = new CancellationTokenSource();
        var run = Task.Run(() => fixture.Monitor!.RunAsync(stop.Token));
        try
        {
            await source.FirstHealth.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await source.ProcessLoopRunning.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, source.HealthCalls);

            // Simulate sudden process exit (ProcessCount 1 -> 0) while healthInterval is 10s away.
            source.ProcessAlive = false;
            await source.SecondHealth.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(source.HealthCalls >= 2);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(8));
        }
    }

    [Fact]
    public async Task CaptureR130VisualEvidenceWhenRequested()
    {
        var dir = Environment.GetEnvironmentVariable("SA_R130_EVIDENCE");
        if (string.IsNullOrEmpty(dir)) { return; }
        Directory.CreateDirectory(dir);

        foreach (var light in new[] { false, true })
        {
            var tag = light ? "light" : "dark";
            await ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, source) =>
            {
                ColorStateAssist.SetReduceMotion(main, true);
                main.Width = 1152;
                main.Height = 648;
                var now = DateTimeOffset.UtcNow;

                // 1. Service page with failed process + Play icon + immediate red fault block (Realtime & 1d)
                var targetTask = model.Tasks[0];
                model.SelectedTask = targetTask;
                targetTask.Update(targetTask.Snapshot with
                {
                    Status = "FAIL",
                    Healthy = false,
                    HealthObserved = true,
                    Reason = "process-missing",
                    Detail = "进程未运行",
                });
                source.HistoryReader = (query, _) => Task.FromResult(new MonitoringHistoryResult(
                    1, query.TaskName, query.TaskIdentity, query.From, query.To, query.To, query.From,
                    Math.Max(0, (query.To - query.From).TotalSeconds - 5), 5, 0, 99.9, 100,
                    [
                        new(query.From, query.To.AddSeconds(-5), ObservationHealth.Healthy),
                        new(query.To.AddSeconds(-5), query.To, ObservationHealth.Fault),
                    ],
                    [], false, null));

                model.SelectDashboardWindowCommand.Execute("24");
                await model.DashboardHistoryTask;
                model.TickDashboard();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                main.UpdateLayout();
                SavePng(main, Path.Combine(dir, $"r130-service-fail-24h-{tag}-1152x648.png"));

                model.SelectDashboardWindowCommand.Execute("0");
                await model.DashboardHistoryTask;
                model.TickDashboard();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                main.UpdateLayout();
                SavePng(main, Path.Combine(dir, $"r130-service-fail-realtime-{tag}-1152x648.png"));

                // 2. Service page with running process + Stop icon
                source.HistoryReader = (query, _) => Task.FromResult(new MonitoringHistoryResult(
                    1, query.TaskName, query.TaskIdentity, query.From, query.To, query.To, query.From,
                    Math.Max(0, (query.To - query.From).TotalSeconds), 0, 0, 100.0, 100,
                    [new(query.From, query.To, ObservationHealth.Healthy)],
                    [], false, null));
                targetTask.Update(targetTask.Snapshot with
                {
                    Status = "OK",
                    Healthy = true,
                    HealthObserved = true,
                    Reason = "process-running",
                    Detail = "进程正常运行",
                });
                model.SelectDashboardWindowCommand.Execute("24");
                await model.DashboardHistoryTask;
                model.SelectDashboardWindowCommand.Execute("0");
                await model.DashboardHistoryTask;
                model.TickDashboard();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                main.UpdateLayout();
                SavePng(main, Path.Combine(dir, $"r130-service-ok-{tag}-1152x648.png"));

                // 3. Loading state screenshot in Dark mode
                if (!light)
                {
                    main.ServiceCpuPlot.Points = [];
                    main.ServiceCpuPlot.IsLoading = true;
                    main.ServiceMemoryPlot.Points = [];
                    main.ServiceMemoryPlot.IsLoading = true;
                    main.ServiceHealthPlot.Intervals = [];
                    main.ServiceHealthPlot.IsLoading = true;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    main.UpdateLayout();
                    SavePng(main, Path.Combine(dir, "r130-service-loading-dark-1152x648.png"));
                    main.ServiceCpuPlot.IsLoading = false;
                    main.ServiceMemoryPlot.IsLoading = false;
                    main.ServiceHealthPlot.IsLoading = false;
                }

                // 3. Host page with remote Linux host (no SSH line, 73% big memory, no bottom-right times)
                model.CurrentPage = MainWindowViewModel.OverviewPage;
                var remoteDef = new RemoteHostDefinition
                {
                    Id = "r130-vis-tx", Name = "腾讯云", Enabled = true, Platform = RemoteHostPlatform.Linux,
                    Host = "192.0.2.55", Port = 22, UserName = "root",
                    Authentication = RemoteAuthenticationMode.SshAgent,
                    KnownHostsFile = Path.Combine(Path.GetTempPath(), "r130-known-hosts"),
                };
                var remoteTab = new HostTabViewModel(remoteDef);
                var totalBytes = 3_543_348_019L;
                var usedBytes = 2_576_980_378L;
                var remoteSnapshot = new RemoteHostStatusSnapshot(
                    remoteDef.Id, remoteDef.Name, remoteDef.Platform, remoteDef.Host, true, null, now,
                    "VM-12-15-ubuntu", "Ubuntu 22.04.4 LTS",
                    TimeSpan.FromDays(157).Add(TimeSpan.FromHours(17)),
                    2.0, totalBytes, totalBytes - usedBytes,
                    [], "10.0.0.12", 123_456, 654_321, 2, 394, 1911,
                    DiskReadBytesPerSecond: 0, DiskWriteBytesPerSecond: 8_192,
                    NetworkReceiveBytesPerSecond: 2_990, NetworkSendBytesPerSecond: 9_540);
                remoteTab.ApplyRemoteStatus(remoteSnapshot);
                model.Hosts.Add(remoteTab);
                model.SelectedHostTab = remoteTab;
                typeof(MainWindowViewModel).GetMethod("ApplyRemoteHostStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(model, [remoteSnapshot]);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                main.UpdateLayout();
                SavePng(main, Path.Combine(dir, $"r130-host-remote-{tag}-1152x648.png"));
            });
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ImmediateHealthSource : IAgentMonitoringSource
    {
        private int _healthCalls;
        private int _processCalls;
        public volatile bool ProcessAlive = true;
        public int HealthCalls => Volatile.Read(ref _healthCalls);
        public TaskCompletionSource FirstHealth { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondHealth { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ProcessLoopRunning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<TaskSnapshot>> ObserveTasksAsync(AutorunConfiguration configuration, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _healthCalls);
            if (count == 1) { FirstHealth.TrySetResult(); }
            else if (count >= 2) { SecondHealth.TrySetResult(); }
            IReadOnlyList<TaskSnapshot> rows = configuration.Tasks.Select(t =>
                TaskSnapshot.From(t, ProcessAlive ? HealthResult.Ok(t.Name, "alive") : HealthResult.Fail(t.Name, "dead"))).ToArray();
            return Task.FromResult(rows);
        }

        public Task<IReadOnlyList<ManagedProcessSnapshot>> ObserveProcessesAsync(AutorunConfiguration configuration,
            IReadOnlyList<TaskSnapshot> tasks, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _processCalls) >= 2) { ProcessLoopRunning.TrySetResult(); }
            IReadOnlyList<ManagedProcessSnapshot> rows = configuration.Tasks.Select(t =>
            {
                var snap = TaskSnapshot.From(t, HealthResult.Ok(t.Name, "alive"));
                return new ManagedProcessSnapshot(
                    t.Name,
                    "immediate",
                    snap.Type,
                    true,
                    true,
                    true,
                    ProcessAlive ? "OK" : "FAIL",
                    "immediate",
                    "immediate",
                    DateTimeOffset.UtcNow,
                    ProcessAlive ? [new(1234, "proc", null, DateTimeOffset.UtcNow.AddMinutes(-5), 1.0, 1024, 1024, 1, true)] : [],
                    TaskIdentity: MonitoringTaskIdentity.For(snap));
            }).ToArray();
            return Task.FromResult(rows);
        }

        public Task<HostStatusSnapshot> ObserveHostAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HostStatusSnapshot("imm", "win", DateTimeOffset.UtcNow, TimeSpan.FromHours(1), 5, 1024, 512, [], []));
    }

    private static double CenterY(FrameworkElement element, UIElement root)
    {
        var topLeft = element.TranslatePoint(new Point(), root);
        return topLeft.Y + element.ActualHeight / 2d;
    }

    private static (byte[] Pixels, int Width, int Height) CapturePixels(FrameworkElement element)
    {
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        var host = new DrawingVisual();
        using (var dc = host.RenderOpen())
        {
            dc.DrawRectangle(
                new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null,
                new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        bitmap.Render(host);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return (pixels, width, height);
    }

    private static void SavePng(FrameworkElement window, string path)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static int CountRedPixels(byte[] pixels, int width, int height, double x0, double x1, double y0, double y1)
    {
        var minX = Math.Clamp((int)Math.Floor(width * x0), 0, width - 1);
        var maxX = Math.Clamp((int)Math.Ceiling(width * x1), 0, width);
        var minY = Math.Clamp((int)Math.Floor(height * y0), 0, height - 1);
        var maxY = Math.Clamp((int)Math.Ceiling(height * y1), 0, height);
        var count = 0;
        for (var y = minY; y < maxY; y++)
        {
            var rowOffset = y * width * 4;
            for (var x = minX; x < maxX; x++)
            {
                var i = rowOffset + x * 4;
                if (pixels[i + 2] > 150 && pixels[i + 1] < 80 && pixels[i] < 110) { count++; }
            }
        }
        return count;
    }

    private static int CountInkPixels(byte[] pixels, int width, int height, double x0, double x1, double y0, double y1)
    {
        var minX = Math.Clamp((int)Math.Floor(width * x0), 0, width - 1);
        var maxX = Math.Clamp((int)Math.Ceiling(width * x1), 0, width);
        var minY = Math.Clamp((int)Math.Floor(height * y0), 0, height - 1);
        var maxY = Math.Clamp((int)Math.Ceiling(height * y1), 0, height);
        var count = 0;
        for (var y = minY; y < maxY; y++)
        {
            var rowOffset = y * width * 4;
            for (var x = minX; x < maxX; x++)
            {
                var i = rowOffset + x * 4;
                if (pixels[i + 3] > 20)
                {
                    count++;
                }
            }
        }
        return count;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }
}
