using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;
using SystemAutorun.Core.Services;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Path = System.IO.Path;
using Point = System.Windows.Point;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class R131VigilPolishAndThemeTests
{
    [Fact]
    public Task LiveModeSwitchingServicesDoesNotFlashHealthPlotRed() =>
        ResourceDashboardWindowTests.WithResourceWindow(false, async (model, main, source) =>
        {
            ColorStateAssist.SetReduceMotion(main, false);
            model.CurrentPage = MainWindowViewModel.WorkloadsPage;
            model.SelectDashboardWindowCommand.Execute("0");
            await model.DashboardHistoryTask;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            var secondTask = model.Tasks[1];
            var delayedHistory = new TaskCompletionSource<MonitoringHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.HistoryReader = (_, _) => delayedHistory.Task;

            model.SelectedTask = secondTask;
            Assert.Empty(model.DashboardHealthHistory);
            Assert.Null(model.DashboardHealthDataAt);

            var end = DateTimeOffset.UtcNow;
            delayedHistory.SetResult(new MonitoringHistoryResult(
                1,
                secondTask.Name,
                MonitoringTaskIdentity.For(secondTask.Snapshot),
                end.AddMinutes(-5),
                end,
                end,
                end.AddMinutes(-5),
                300,
                0,
                0,
                100,
                100,
                [new(end.AddMinutes(-5), end, ObservationHealth.Healthy)],
                [],
                Truncated: false,
                Warning: null));

            await model.DashboardHistoryTask;
            await Dispatcher.Yield(DispatcherPriority.Render);
            main.UpdateLayout();

            Assert.False(main.ServiceHealthPlot.DataPulseActive);
            var (pixels, pw, ph) = CapturePixels(main.ServiceHealthPlot);
            var redHits = CountRedPixels(pixels, pw, ph, 0.02, 0.98, 0.10, 0.45);
            Assert.Equal(0, redHits);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FocusedAndSelectedListAndGridRowsNeverShowDarkBottomBorder(bool light) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, _) =>
        {
            ColorStateAssist.SetReduceMotion(main, true);
            model.CurrentPage = MainWindowViewModel.WorkloadsPage;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();

            var subtle = ((SolidColorBrush)main.FindResource("Semantic.Brush.BorderSubtle")).Color;
            var rowFocusBorder = ((SolidColorBrush)main.FindResource("Semantic.Brush.RowFocusBorder")).Color;
            Assert.Equal(subtle, rowFocusBorder);

            var serviceRow = Assert.IsType<ListBoxItem>(main.ServiceList.ItemContainerGenerator.ContainerFromIndex(0));
            serviceRow.Focus();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();

            var serviceSurface = Assert.IsType<Border>(serviceRow.Template.FindName("Row", serviceRow));
            Assert.Null(serviceRow.FocusVisualStyle);
            Assert.Equal(subtle, Assert.IsType<SolidColorBrush>(serviceSurface.BorderBrush).Color);

            model.CurrentPage = MainWindowViewModel.OverviewPage;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();

            var hostRow = Assert.IsType<ListBoxItem>(main.MonitoringTabs.HostList.ItemContainerGenerator.ContainerFromIndex(0));
            hostRow.Focus();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();

            var hostSurface = Assert.IsType<Border>(hostRow.Template.FindName("Row", hostRow));
            Assert.Null(hostRow.FocusVisualStyle);
            Assert.Equal(subtle, Assert.IsType<SolidColorBrush>(hostSurface.BorderBrush).Color);

            CaptureEvidenceIfRequested(main, light ? "service-light-selected-row-1152x648.png" : "service-dark-crimson-1152x648.png",
                restoreToServicePage: light, model: model);
        });

    [Fact]
    public Task WindowReturnFromBackgroundSuppressesSkeletonWipeAndPulse() =>
        ResourceDashboardWindowTests.WithResourceWindow(false, async (model, main, _) =>
        {
            ColorStateAssist.SetReduceMotion(main, false);
            model.CurrentPage = MainWindowViewModel.WorkloadsPage;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();

            Assert.True(model.AutoRefresh);
            Assert.Equal("OK", model.Tasks[0].Status);

            var snapshotField = typeof(MainWindowViewModel).GetField("_monitoringSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var currentSnapshot = Assert.IsType<AgentMonitoringSnapshot>(snapshotField.GetValue(model));
            var staleAt = DateTimeOffset.UtcNow.AddSeconds(-60);
            var agedTasks = currentSnapshot.Tasks with { ObservedAt = staleAt, AttemptedAt = staleAt };
            snapshotField.SetValue(model, currentSnapshot with { Tasks = agedTasks, ReturnedAt = DateTimeOffset.UtcNow });

            model.SetPresentationActive(false);
            Assert.False(model.InLocalResumeGrace);
            Assert.False(model.InRemoteResumeGrace);

            model.SetPresentationActive(true);
            Assert.True(model.InLocalResumeGrace);
            Assert.True(model.InRemoteResumeGrace);
            Assert.Equal("OK", model.Tasks[0].Status);

            DataChangePulse.SetSuppress(main, true);
            Assert.True(DataChangePulse.GetSuppress(main.ServiceCpuPlot));
            DataChangePulse.SetSuppress(main, false);
            Assert.False(DataChangePulse.GetSuppress(main.ServiceCpuPlot));

            var hiddenWithWindowField = typeof(DashboardPlot).GetField("_hiddenWithWindow", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var awaitingField = typeof(DashboardPlot).GetField("_awaitingSeriesDraw", BindingFlags.Instance | BindingFlags.NonPublic)!;
            awaitingField.SetValue(main.ServiceCpuPlot, false);
            main.Hide();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True((bool)hiddenWithWindowField.GetValue(main.ServiceCpuPlot)!);
            main.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False((bool)hiddenWithWindowField.GetValue(main.ServiceCpuPlot)!);
            Assert.False((bool)awaitingField.GetValue(main.ServiceCpuPlot)!);
        });

    [Fact]
    public async Task HostListSupportsSingleServerAddEditContextMenuDoubleClickAndDragReorder()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var root = fixture.Root;
        var hostA = new RemoteHostDefinition
        {
            Id = "host-a",
            Name = "节点 A",
            Enabled = false,
            Platform = RemoteHostPlatform.Linux,
            Host = "192.0.2.10",
            Port = 22,
            UserName = "ops",
            Authentication = RemoteAuthenticationMode.SshAgent,
            KnownHostsFile = Path.Combine(root, "known_hosts_a"),
        };
        var hostB = hostA with { Id = "host-b", Name = "节点 B", Host = "192.0.2.11" };
        await File.WriteAllTextAsync(hostA.KnownHostsFile, "# synthetic fixture\n");
        var cfg = new AgentConfigurationClient(fixture.Client);
        _ = await cfg.ReadAsync();
        _ = await cfg.MutateAsync<ApplicationSettings>("remote-add", payload: hostA);
        _ = await cfg.MutateAsync<ApplicationSettings>("remote-add", payload: hostB);

        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is not SystemAutorun.App.App)
            {
                var app = new SystemAutorun.App.App(runRuntimeStartup: false);
                app.InitializeComponent();
            }
            WindowTheme.Install();
            var model = new MainWindowViewModel(
                fixture.Client,
                new NativeCacheStore(fixture.Paths.GuiCachePath),
                cfg,
                fixture.Paths,
                new NeverRemoteReader(),
                new DashboardFixtureMonitoringClient(fixture))
            { AutoRefresh = false };

            var main = new SystemAutorun.App.MainWindow(
                model,
                new AgentStartupClient(fixture.Client),
                new StartupCacheStore(fixture.Paths.StartupCachePath))
            {
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            ColorStateAssist.SetReduceMotion(main, true);

            try
            {
                main.Show();
                await model.InitializeAsync();
                model.CurrentPage = MainWindowViewModel.OverviewPage;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                main.UpdateLayout();

                var plusButtons = Descendants(main.MonitoringTabs)
                    .OfType<Button>()
                    .Where(btn => Equals(btn.Command, model.ManageRemoteHostsCommand))
                    .ToArray();
                Assert.NotEmpty(plusButtons);
                Assert.All(plusButtons, btn => Assert.Equal("添加服务器", btn.ToolTip));

                var hostMenu = main.MonitoringTabs.HostList.ContextMenu!;
                Assert.Equal(["编辑服务器", "删除服务器…"], hostMenu.Items.OfType<MenuItem>().Select(item => (string)item.Header));

                Assert.Equal(3, model.Hosts.Count);
                Assert.True(model.Hosts[0].IsLocal);
                Assert.Equal("host-a", model.Hosts[1].Id);
                Assert.Equal("host-b", model.Hosts[2].Id);

                model.SelectedHostTab = model.Hosts[0];
                Assert.False(model.EditRemoteHostCommand.CanExecute(model.Hosts[0]));
                Assert.False(model.RemoveRemoteHostCommand.CanExecute(model.Hosts[0]));

                model.SelectedHostTab = model.Hosts[1];
                Assert.True(model.EditRemoteHostCommand.CanExecute(model.Hosts[1]));
                Assert.True(model.RemoveRemoteHostCommand.CanExecute(model.Hosts[1]));

                string? dialogHostId = "sentinel";
                model.RemoteHostDialogRequested += (_, id) => dialogHostId = id;
                model.ManageRemoteHostsCommand.Execute(null);
                Assert.Null(dialogHostId);
                model.EditRemoteHostCommand.Execute(model.Hosts[1]);
                Assert.Equal("host-a", dialogHostId);

                // Double-clicking a remote host row triggers EditRemoteHostCommand.
                var rowB = Assert.IsType<ListBoxItem>(main.MonitoringTabs.HostList.ItemContainerGenerator.ContainerFromIndex(2));
                var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = Control.MouseDoubleClickEvent,
                    Source = rowB,
                };
                main.MonitoringTabs.HostList.RaiseEvent(doubleClick);
                Assert.Equal("host-b", dialogHostId);

                // Dragging host-b to index 1 keeps "本机" fixed at index 0 and moves host-b above host-a.
                var dragHostField = typeof(MonitoringView).GetField("_dragHost", BindingFlags.Instance | BindingFlags.NonPublic)!;
                dragHostField.SetValue(main.MonitoringTabs, model.Hosts[2]);
                var moved = main.MonitoringTabs.UpdateDragOrder(model, 0d);
                Assert.True(moved);
                Assert.True(model.Hosts[0].IsLocal);
                Assert.Equal("host-b", model.Hosts[1].Id);
                Assert.Equal("host-a", model.Hosts[2].Id);
                dragHostField.SetValue(main.MonitoringTabs, null);

                await model.ReorderRemoteHostsAsync(["host-b", "host-a"]);
                var saved = await cfg.ReadAsync();
                Assert.Equal(["host-b", "host-a"], saved.Preferences.RemoteHosts.Select(item => item.Id));

                // Context menu opening guard: suppressed on local host, allowed on remote host.
                var rowLocal = Assert.IsType<ListBoxItem>(main.MonitoringTabs.HostList.ItemContainerGenerator.ContainerFromIndex(0));
                var openMethod = typeof(MonitoringView).GetMethod("HostList_ContextMenuOpening", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var localArgs = CreateContextMenuArgs(rowLocal);
                openMethod.Invoke(main.MonitoringTabs, [main.MonitoringTabs.HostList, localArgs]);
                Assert.True(localArgs.Handled);

                var rowRemote = Assert.IsType<ListBoxItem>(main.MonitoringTabs.HostList.ItemContainerGenerator.ContainerFromIndex(1));
                var remoteArgs = CreateContextMenuArgs(rowRemote);
                openMethod.Invoke(main.MonitoringTabs, [main.MonitoringTabs.HostList, remoteArgs]);
                Assert.False(remoteArgs.Handled);

                // Verify single-server Add and Edit dialogs open cleanly.
                var addDialog = new RemoteHostsWindow([hostA, hostB], _ => Task.CompletedTask, editing: null)
                {
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                };
                var editDialog = new RemoteHostsWindow([hostA, hostB], _ => Task.CompletedTask, editing: hostA)
                {
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                };
                try
                {
                    addDialog.Show();
                    await Dispatcher.Yield(DispatcherPriority.Loaded);
                    addDialog.UpdateLayout();
                    Assert.Equal("添加远程服务器", addDialog.Title);
                    Assert.Equal(string.Empty, addDialog.NameTextBox.Text);
                    SaveEvidenceDialog(addDialog, "dialog-remote-add-dark.png");

                    var prevTheme = ThemeService.Preference;
                    var prevAccent = ThemeService.AccentPreference;
                    try
                    {
                        ThemeService.Apply(ThemePreference.Light, ThemeService.DefaultAccentId, persist: null);
                        editDialog.Show();
                        await Dispatcher.Yield(DispatcherPriority.Loaded);
                        editDialog.UpdateLayout();
                        Assert.Equal("编辑远程服务器", editDialog.Title);
                        Assert.Equal(hostA.Name, editDialog.NameTextBox.Text);
                        SaveEvidenceDialog(editDialog, "dialog-remote-edit-light.png");
                    }
                    finally
                    {
                        ThemeService.Apply(prevTheme, prevAccent, persist: null);
                    }
                }
                finally
                {
                    addDialog.Close();
                    editDialog.Close();
                }
            }
            finally
            {
                var closing = typeof(SystemAutorun.App.MainWindow).GetMethod("Window_Closing", BindingFlags.Instance | BindingFlags.NonPublic)!;
                main.Closing -= (System.ComponentModel.CancelEventHandler)closing.CreateDelegate(typeof(System.ComponentModel.CancelEventHandler), main);
                main.Close();
                model.DetachDashboard();
            }
        });
    }

    [Fact]
    public Task LogViewerUiEntriesRemovedWhileKeepingLogFolderAndExceptionTab() =>
        ResourceDashboardWindowTests.WithResourceWindow(false, (model, main, source) =>
        {
            _ = model;
            _ = source;
            Assert.Null(main.FindName("ServiceLogsButton"));
            Assert.DoesNotContain(main.ServiceMoreButton.ContextMenu!.Items.OfType<MenuItem>(),
                item => item.Header?.ToString()?.Contains("日志", StringComparison.Ordinal) == true);

            var settingsItems = main.SettingsButton.ContextMenu!.Items.OfType<MenuItem>().ToArray();
            Assert.Contains(settingsItems, item => Equals(item.Header, "打开日志目录"));
            Assert.DoesNotContain(settingsItems, item =>
                item.Header?.ToString()?.Contains("日志工具", StringComparison.Ordinal) == true
                || item.Header?.ToString()?.Contains("日志查看", StringComparison.Ordinal) == true);
            Assert.Equal(Visibility.Visible, main.ExceptionLogsTab.Visibility);
            return Task.CompletedTask;
        });

    [Fact]
    public Task LoadingSweepFinishesSmoothlyToRightEdgeInsteadOfAbruptlyDisappearing() =>
        GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is not SystemAutorun.App.App)
            {
                var app = new SystemAutorun.App.App(runRuntimeStartup: false);
                app.InitializeComponent();
            }
            WindowTheme.Install();

            var end = DateTimeOffset.UtcNow;
            var plot = new DashboardPlot
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
                Content = new Border
                {
                    Background = (Brush)Application.Current.FindResource("Semantic.Brush.WindowBackground"),
                    Child = plot,
                },
                Width = 340,
                Height = 160,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            ColorStateAssist.SetReduceMotion(host, false);

            try
            {
                host.Show();
                await Dispatcher.Yield(DispatcherPriority.Render);
                host.UpdateLayout();

                if (!SystemParameters.ClientAreaAnimation)
                {
                    plot.Points = [new PlotPoint(end.AddMinutes(-2), 25), new PlotPoint(end, 45)];
                    plot.IsLoading = false;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.False(plot.IsLoadingSweepVisible);
                    return;
                }

                await PresenceMotionTests.Until(() => plot.LoadingPhase is > 0.08 and < 0.78);
                plot.Points = [new PlotPoint(end.AddMinutes(-2), 25), new PlotPoint(end, 45)];
                plot.IsLoading = false;

                // Must keep animating smoothly to the right edge rather than vanishing instantly.
                Assert.True(plot.IsLoadingSweepVisible);
                await PresenceMotionTests.Until(() => !plot.IsLoadingSweepVisible);
                Assert.False(plot.IsLoadingSweepVisible);
            }
            finally
            {
                host.Close();
            }
        });

    [Fact]
    public Task VigilBrandingAboutWindowAndUpdateCheckerStates() =>
        ResourceDashboardWindowTests.WithResourceWindow(false, async (model, main, source) =>
        {
            _ = model;
            _ = source;
            Assert.Equal("Vigil", main.Title);
            Assert.Contains(Descendants(main).OfType<TextBlock>(), tb => tb.Text == "Vigil" && tb.IsVisible);
            var productAttr = typeof(MainWindowViewModel).Assembly.GetCustomAttribute<AssemblyProductAttribute>();
            Assert.Equal("Vigil", productAttr?.Product);

            var settingsHeaders = main.SettingsButton.ContextMenu!.Items.OfType<MenuItem>()
                .Select(item => item.Header?.ToString() ?? string.Empty)
                .ToArray();
            Assert.Contains("检查更新…", settingsHeaders);
            Assert.Contains("关于", settingsHeaders);
            Assert.Contains("强调色", settingsHeaders);

            Assert.True(UpdateChecker.IsNewerVersion("1.2.0", "1.0.0"));
            Assert.False(UpdateChecker.IsNewerVersion("v1.0.0", "1.0.0"));

            var openedUrls = new List<string>();
            UpdateCheckResult nextResult = new(UpdateCheckState.NoPublishedRelease, "1.0.0", null,
                UpdateChecker.RepositoryUrl, "仓库尚未发布公开 Release，可前往项目主页查看源码。");

            var about = new AboutWindow(
                autoCheckOnLoad: false,
                customChecker: _ => Task.FromResult(nextResult),
                openUrl: url => openedUrls.Add(url))
            {
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            ColorStateAssist.SetReduceMotion(about, true);

            try
            {
                about.Show();
                await Dispatcher.Yield(DispatcherPriority.Loaded);
                about.UpdateLayout();

                Assert.Equal("繁星之子卡萨帝亚", about.AuthorValueText.Text);
                Assert.Contains("v", about.VersionValueText.Text);

                await about.RunCheckUpdateAsync();
                Assert.Contains("尚未发布", about.UpdateStatusText.Text);
                Assert.Equal("项目主页", about.OpenRepoLabel.Text);

                nextResult = new(UpdateCheckState.UpToDate, "1.0.0", "1.0.0",
                    UpdateChecker.RepositoryUrl, "当前已是最新版本 v1.0.0。");
                await about.RunCheckUpdateAsync();
                Assert.Contains("最新版本", about.UpdateStatusText.Text);
                Assert.Equal("项目主页", about.OpenRepoLabel.Text);

                nextResult = new(UpdateCheckState.UpdateAvailable, "1.0.0", "2.0.0",
                    "https://github.com/AngleNaris/Vigil/releases/tag/v2.0.0",
                    "发现新版本 v2.0.0（当前 v1.0.0），可前往发布页下载更新。");
                await about.RunCheckUpdateAsync();
                Assert.Contains("v2.0.0", about.UpdateStatusText.Text);
                Assert.Equal("打开发布页", about.OpenRepoLabel.Text);

                about.OpenRepoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(["https://github.com/AngleNaris/Vigil/releases/tag/v2.0.0"], openedUrls);

                SaveEvidenceDialog(about, "dialog-about-dark.png");
            }
            finally
            {
                about.Close();
            }
        });

    [Fact]
    public Task CustomAccentPresetsAndHexRecolorUiAndPlotsWhileKeepingFaultsRed() =>
        ResourceDashboardWindowTests.WithResourceWindow(false, async (model, main, source) =>
        {
            var prevPref = ThemeService.Preference;
            var prevAccent = ThemeService.AccentPreference;
            var tempJson = Path.Combine(Path.GetTempPath(), $"vigil-appearance-{Guid.NewGuid():N}.json");

            try
            {
                // Verify all 6 presets in both Dark & Light satisfy WCAG >= 4.5 contrast, plus custom extremes.
                foreach (var accentId in ThemeService.AccentPresets.Select(p => p.Id).Concat(["#FF6B2B", "#00FFCC", "#111111", "#FFFF80"]))
                {
                    foreach (var light in new[] { false, true })
                    {
                        var palette = ThemeService.ResolveAccentPalette(accentId, light);
                        var hover = ParseHex(palette.InteractionHover);
                        var activeHover = ParseHex(palette.InteractionActiveHover);
                        var onHover = ParseHex(palette.InteractionOnHover);
                        var onActive = ParseHex(palette.InteractionOnActive);
                        var activeBg = light ? Color.FromRgb(0xE4, 0xE4, 0xE8) : Color.FromRgb(0x24, 0x24, 0x2B);

                        Assert.True(ContrastRatio(onHover, hover) >= 4.5,
                            $"Accent={accentId} light={light} onHover/hover {ContrastRatio(onHover, hover):0.00}:1");
                        Assert.True(ContrastRatio(onHover, activeHover) >= 4.5,
                            $"Accent={accentId} light={light} onHover/activeHover {ContrastRatio(onHover, activeHover):0.00}:1");
                        Assert.True(ContrastRatio(onActive, activeBg) >= 4.5,
                            $"Accent={accentId} light={light} onActive/activeBg {ContrastRatio(onActive, activeBg):0.00}:1");
                    }
                }

                // Persistence check via isolated temp file (never mutates user appearance.json).
                ThemeService.Apply(ThemePreference.Light, "#3B82F6", tempJson);
                var json = await File.ReadAllTextAsync(tempJson);
                using var doc = JsonDocument.Parse(json);
                Assert.Equal("light", doc.RootElement.GetProperty("theme").GetString());
                Assert.Equal("cobalt", doc.RootElement.GetProperty("accent").GetString());

                // Switch to cobalt in Dark theme: accent/curves turn blue while faults remain crimson red.
                ThemeService.Apply(ThemePreference.Dark, "cobalt", persist: null);
                ColorStateAssist.SetReduceMotion(main, true);
                model.CurrentPage = MainWindowViewModel.WorkloadsPage;
                var task = model.Tasks[0];
                model.SelectedTask = task;
                task.Update(task.Snapshot with { Status = "FAIL", Healthy = false, HealthObserved = true, Reason = "process-missing" });
                source.HistoryReader = (query, _) => Task.FromResult(new MonitoringHistoryResult(
                    1,
                    query.TaskName,
                    query.TaskIdentity,
                    query.From,
                    query.To,
                    query.To,
                    query.From,
                    Math.Max(0, (query.To - query.From).TotalSeconds - 10),
                    10,
                    0,
                    98.0,
                    100,
                    [
                        new(query.From, query.To.AddSeconds(-10), ObservationHealth.Healthy),
                        new(query.To.AddSeconds(-10), query.To, ObservationHealth.Fault),
                    ],
                    [],
                    false,
                    null));
                model.SelectDashboardWindowCommand.Execute("0");
                await model.DashboardHistoryTask;
                model.TickDashboard();
                await Dispatcher.Yield(DispatcherPriority.Render);
                main.UpdateLayout();

                Assert.Equal(Color.FromRgb(0x3B, 0x82, 0xF6), ((SolidColorBrush)main.FindResource("Semantic.Brush.Accent")).Color);
                Assert.Equal(Color.FromRgb(0xE1, 0x1D, 0x48), ((SolidColorBrush)main.FindResource("Semantic.Brush.StatusFailed")).Color);
                Assert.Equal(Color.FromRgb(0xE1, 0x1D, 0x48), ((SolidColorBrush)main.FindResource("Semantic.Brush.StatusCritical")).Color);

                var (cpuPx, cw, ch) = CapturePixels(main.ServiceCpuPlot);
                Assert.True(CountBluePixels(cpuPx, cw, ch, 0.02, 0.98, 0.15, 0.85) > 20, "Expected blue curve/gradient under cobalt accent.");

                var (healthPx, hw, hh) = CapturePixels(main.ServiceHealthPlot);
                Assert.True(CountRedPixels(healthPx, hw, hh, 0.85, 0.99, 0.10, 0.45) > 5, "Expected trailing fault cell to stay red under cobalt accent.");

                main.Width = 1152;
                main.Height = 648;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                main.UpdateLayout();
                SaveEvidenceIfRequested(main, "service-dark-cobalt-fault-1152x648.png");

                // Wide Dark cobalt host view
                model.CurrentPage = MainWindowViewModel.OverviewPage;
                main.Width = 1320;
                main.Height = 820;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                main.UpdateLayout();
                SaveEvidenceIfRequested(main, "host-dark-cobalt-1320x820.png");

                // Small Light emerald host view
                ThemeService.Apply(ThemePreference.Light, "emerald", persist: null);
                main.Width = 920;
                main.Height = 580;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                main.UpdateLayout();
                Assert.Equal(Color.FromRgb(0x04, 0x78, 0x57), ((SolidColorBrush)main.FindResource("Semantic.Brush.Accent")).Color);
                Assert.Equal(Color.FromRgb(0xBE, 0x12, 0x3C), ((SolidColorBrush)main.FindResource("Semantic.Brush.StatusFailed")).Color);
                SaveEvidenceIfRequested(main, "host-light-emerald-920x580.png");

                // Custom #FF6B2B check + AccentColorWindow UI test
                ThemeService.Apply(ThemePreference.Dark, "#FF6B2B", persist: null);
                var customAccent = ((SolidColorBrush)main.FindResource("Semantic.Brush.Accent")).Color;
                Assert.True(customAccent.R > customAccent.B + 40);

                ThemeService.Apply(ThemePreference.Light, ThemeService.DefaultAccentId, persist: null);
                var picker = new AccentColorWindow
                {
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                };
                ColorStateAssist.SetReduceMotion(picker, true);
                try
                {
                    picker.Show();
                    await Dispatcher.Yield(DispatcherPriority.Loaded);
                    picker.UpdateLayout();

                    Assert.Equal(ThemeService.AccentPresets.Count, picker.PresetGrid.Children.Count);
                    picker.HexTextBox.Text = "not-a-hex";
                    Assert.False(picker.ApplyButton.IsEnabled);

                    picker.HexTextBox.Text = "#FF6B2B";
                    Assert.True(picker.ApplyButton.IsEnabled);
                    Assert.Contains("#FF6B2B", picker.PreviewTitleText.Text);
                    SaveEvidenceDialog(picker, "dialog-accent-light.png");

                    picker.ResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(ThemeService.DefaultAccentId, picker.SelectedAccent);
                }
                finally
                {
                    picker.Close();
                }
            }
            finally
            {
                ThemeService.Apply(prevPref, prevAccent, persist: null);
                if (File.Exists(tempJson)) { File.Delete(tempJson); }
            }
        });

    [Fact]
    public Task EmptyStateButtonTextIsVerticallyCentered() =>
        ResourceDashboardWindowTests.WithResourceWindow(false, async (model, main, _) =>
        {
            ColorStateAssist.SetReduceMotion(main, true);
            model.Tasks.Clear();
            model.SelectedTask = null;
            model.CurrentPage = MainWindowViewModel.WorkloadsPage;
            main.Width = 1024;
            main.Height = 548;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();

            var button = Descendants(main.ServicesPage).OfType<Button>().Single(b =>
                b.IsVisible && string.Equals(b.Content as string, model.DashboardEmptyActionLabel, StringComparison.Ordinal));
            var (pixels, w, h) = CapturePixels(button);
            var minTextY = h;
            var maxTextY = -1;
            for (var y = 2; y < h - 2; y++)
            {
                for (var x = 6; x < w - 6; x++)
                {
                    var i = (y * w + x) * 4;
                    var lum = (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3;
                    if (lum > 90)
                    {
                        if (y < minTextY) { minTextY = y; }
                        if (y > maxTextY) { maxTextY = y; }
                    }
                }
            }

            Assert.True(minTextY < maxTextY, "Expected visible text pixels inside empty-state button.");
            var topGap = minTextY;
            var bottomGap = (h - 1) - maxTextY;
            Assert.True(Math.Abs(topGap - bottomGap) <= 1,
                $"Button text is not vertically centered: height={h}, minTextY={minTextY}, maxTextY={maxTextY}, topGap={topGap}, bottomGap={bottomGap}");
            SaveEvidenceIfRequested(main, "empty-state-button.png");
        });

    [Fact]
    public Task LogonStartupMenuIsCheckableAndTrayRestoresOnSingleLeftClick() =>
        ResourceDashboardWindowTests.WithResourceWindow(false, async (_, main, _) =>
        {
            Assert.Equal("登录自动启动", main.SelfStartupMenuItem.Header);
            Assert.True(main.SelfStartupMenuItem.IsCheckable);
            var submenuMethod = typeof(SystemAutorun.App.MainWindow).GetMethod("WindowsStartupMenu_SubmenuOpened", BindingFlags.Instance | BindingFlags.NonPublic)!;
            submenuMethod.Invoke(main, [main.WindowsStartupMenu, new RoutedEventArgs()]);

            var shown = 0;
            using var tray = new TrayService(() => shown++, () => { }, () => { }, () => { }, () => { });
            var notifyField = typeof(TrayService).GetField("_notifyIcon", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var notifyIcon = Assert.IsType<System.Windows.Forms.NotifyIcon>(notifyField.GetValue(tray));
            var onMouseUp = typeof(System.Windows.Forms.NotifyIcon).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)!;
            onMouseUp.Invoke(notifyIcon, [new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, 0, 0, 0)]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, shown);
        });

    private static ContextMenuEventArgs CreateContextMenuArgs(DependencyObject originalSource)
    {
        var args = (ContextMenuEventArgs)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ContextMenuEventArgs));
        args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
        args.Source = originalSource;
        return args;
    }

    private static void CaptureEvidenceIfRequested(
        SystemAutorun.App.MainWindow main,
        string fileName,
        bool restoreToServicePage = false,
        MainWindowViewModel? model = null)
    {
        if (restoreToServicePage && model is not null)
        {
            model.CurrentPage = MainWindowViewModel.WorkloadsPage;
            main.UpdateLayout();
        }
        SaveEvidenceIfRequested(main, fileName);
    }

    private static void SaveEvidenceIfRequested(FrameworkElement visual, string fileName)
    {
        var dir = Environment.GetEnvironmentVariable("SA_R131_EVIDENCE");
        if (string.IsNullOrWhiteSpace(dir)) { return; }
        Directory.CreateDirectory(dir);
        SavePng(visual, Path.Combine(dir, fileName));
    }

    private static void SaveEvidenceDialog(Window dialog, string fileName)
    {
        var dir = Environment.GetEnvironmentVariable("SA_R131_EVIDENCE");
        if (string.IsNullOrWhiteSpace(dir)) { return; }
        Directory.CreateDirectory(dir);
        SavePng(dialog, Path.Combine(dir, fileName));
    }

    private static void SavePng(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = File.Create(path);
        encoder.Save(fs);
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

    private static int CountBluePixels(byte[] pixels, int width, int height, double x0, double x1, double y0, double y1)
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
                if (pixels[i] > pixels[i + 2] + 60 && pixels[i] > 120) { count++; }
            }
        }
        return count;
    }

    private static Color ParseHex(string hex) =>
        (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);

    private static double ContrastRatio(Color a, Color b)
    {
        static double Channel(byte value) { var c = value / 255d; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
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

    private sealed class NeverRemoteReader : IRemoteHostReader
    {
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Isolated R131 UI tests must never connect to real remote hosts.");
    }
}
