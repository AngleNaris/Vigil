using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using Path = System.IO.Path;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class R126DashboardAndChromeTests
{
    [Fact]
    public Task LeftToRightPlotSweepUses620MsClipRevealWithoutBottomScanline() => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        Assert.Equal(620d, DashboardPlot.SweepDurationMilliseconds);
        EnsureAppInitialized();
        var previous = ThemeService.IsLight ? ThemePreference.Light : ThemePreference.Dark;
        ThemeService.Apply(ThemePreference.Dark, persist: null);

        var end = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.FromHours(8));
        var blankPoints = Enumerable.Range(0, 61).Select(i => new PlotPoint(end.AddSeconds(-300 + i * 5), null)).ToArray();
        var wavePoints = Enumerable.Range(0, 61).Select(i => new PlotPoint(end.AddSeconds(-300 + i * 5), i % 2 == 0 ? 78d : 22d)).ToArray();

        var trendPlot = new DashboardPlot
        {
            Mode = "Trend",
            Width = 460,
            Height = 160,
            WindowDuration = TimeSpan.FromMinutes(5),
            EndAt = end,
            Points = blankPoints,
            ScaleKey = "r126-trend",
        };
        var healthPlot = new DashboardPlot
        {
            Mode = "Health",
            Width = 460,
            Height = 36,
            WindowHours = 24,
            EndAt = end,
            Intervals = [new(end.AddHours(-24), end, "unknown")],
            ScaleKey = "r126-health",
        };

        var root = new StackPanel { Background = (Brush)Application.Current.FindResource("Semantic.Brush.WindowBackground") };
        root.Children.Add(trendPlot);
        root.Children.Add(healthPlot);
        var window = new Window
        {
            Content = root,
            Width = 480,
            Height = 240,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
        };
        ColorStateAssist.SetReduceMotion(window, false);
        try
        {
            window.Show();
            await PresenceMotionTests.Until(() => !IsSweeping(trendPlot) && !IsSweeping(healthPlot));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var (healthBlank, wH, hH) = CapturePixels(healthPlot);

            trendPlot.Points = wavePoints;
            trendPlot.ScaleKey = "r126-trend-data";
            healthPlot.Intervals = [new(end.AddHours(-24), end.AddHours(-12), "healthy"), new(end.AddHours(-12), end, "failed")];
            healthPlot.ScaleKey = "r126-health-data";

            await PresenceMotionTests.Until(() => !IsSweeping(trendPlot) && !IsSweeping(healthPlot));
            Assert.Null(trendPlot.Clip);
            Assert.Null(healthPlot.Clip);
            Assert.Equal(1d, trendPlot.SweepProgress);
            Assert.Equal(1d, healthPlot.SweepProgress);

            if (!SystemParameters.ClientAreaAnimation) { return; }

            var bounds = trendPlot.PlotBounds;
            var leftStartFrac = (bounds.Left + 4) / trendPlot.ActualWidth;
            var leftEndFrac = (bounds.Left + bounds.Width * 0.40) / trendPlot.ActualWidth;
            var rightStartFrac = (bounds.Left + bounds.Width * 0.54) / trendPlot.ActualWidth;
            var rightEndFrac = (bounds.Right - 3) / trendPlot.ActualWidth;
            var topFrac = (bounds.Top + 3) / trendPlot.ActualHeight;
            var bottomFrac = (bounds.Bottom - 3) / trendPlot.ActualHeight;
            var scanTopFrac = Math.Max(0, (trendPlot.ActualHeight - 28) / trendPlot.ActualHeight);

            trendPlot.SweepProgress = 0d;
            healthPlot.SweepProgress = 0d;
            trendPlot.InvalidateVisual();
            healthPlot.InvalidateVisual();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var (trendZero, wT, hT) = CapturePixels(trendPlot);
            var (healthZero, _, _) = CapturePixels(healthPlot);
            Assert.Equal(0, CountBrightPixels(trendZero, wT, hT, leftStartFrac, rightEndFrac, topFrac, bottomFrac));
            Assert.True(CountDiffPixels(healthBlank, healthZero, wH, hH, 0.08, 0.92, 0.05, 0.30) > 20,
                "Expected SweepProgress=0 to hide health blocks compared to settled blank hatch.");

            trendPlot.SweepProgress = 0.45d;
            healthPlot.SweepProgress = 0.45d;
            trendPlot.InvalidateVisual();
            healthPlot.InvalidateVisual();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var (trendMid, _, _) = CapturePixels(trendPlot);
            var (healthMid, _, _) = CapturePixels(healthPlot);

            var leftTrendDiff = CountDiffPixels(trendZero, trendMid, wT, hT, leftStartFrac, leftEndFrac, topFrac, bottomFrac);
            var rightTrendDiff = CountDiffPixels(trendZero, trendMid, wT, hT, rightStartFrac, rightEndFrac, topFrac, bottomFrac);
            var rightBottomRed = CountRedPixels(trendMid, wT, hT, rightStartFrac, rightEndFrac, scanTopFrac, 1.0);
            Assert.True(leftTrendDiff > 15, $"Expected left trend waveform drawn at 45%, got {leftTrendDiff} diff pixels.");
            Assert.Equal(0, rightTrendDiff);
            Assert.Equal(0, rightBottomRed);

            var leftHealthDiff = CountDiffPixels(healthZero, healthMid, wH, hH, 0.08, 0.38, 0.05, 0.30);
            var rightHealthDiff = CountDiffPixels(healthZero, healthMid, wH, hH, 0.54, 0.92, 0.05, 0.30);
            Assert.True(leftHealthDiff > 20, $"Expected left health blocks drawn at 45%, got {leftHealthDiff} diff pixels.");
            Assert.Equal(0, rightHealthDiff);

            trendPlot.SweepProgress = 1d;
            healthPlot.SweepProgress = 1d;
            trendPlot.InvalidateVisual();
            healthPlot.InvalidateVisual();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var (trendFull, _, _) = CapturePixels(trendPlot);
            var (healthFull, _, _) = CapturePixels(healthPlot);
            Assert.True(CountDiffPixels(trendZero, trendFull, wT, hT, rightStartFrac, rightEndFrac, topFrac, bottomFrac) > 15);
            Assert.True(CountDiffPixels(healthZero, healthFull, wH, hH, 0.60, 0.92, 0.05, 0.30) > 20);
            Assert.Null(trendPlot.Clip);
            Assert.Null(healthPlot.Clip);
        }
        finally
        {
            ThemeService.Apply(previous, persist: null);
            window.Close();
        }
    });

    [Fact]
    public Task PageSwitchingAsyncPointsAndRapidReversalReplaySweepWhilePollingDoesNot() =>
        ResourceDashboardWindowTests.WithResourceWindow(false, async (model, main, source) =>
        {
            ColorStateAssist.SetReduceMotion(main, false);
            var charts = main.MonitoringTabs.HostCharts;
            await AwaitSettledAsync(main.ServiceHealthPlot, main.ServiceCpuPlot, main.ServiceMemoryPlot);

            // Switch Services -> Hosts: all 4 Host charts animate cleanly.
            var sawHost = TrackSweeps(charts.HostCpuPlot, charts.HostMemoryPlot, charts.HostDiskPlot, charts.HostNetworkPlot);
            main.SelectTab(MainWindowViewModel.OverviewPage);
            await AwaitTrackedSweepsAsync(sawHost, TimeSpan.FromSeconds(5));

            // Switch Hosts -> Exceptions -> Services: all 3 Service charts animate cleanly.
            main.SelectTab(MainWindowViewModel.ExceptionLogsPage);
            await Task.Delay(180);
            var sawService = TrackSweeps(main.ServiceHealthPlot, main.ServiceCpuPlot, main.ServiceMemoryPlot);
            main.SelectTab(MainWindowViewModel.WorkloadsPage);
            await AwaitTrackedSweepsAsync(sawService, TimeSpan.FromSeconds(5));

            // Rapid mid-fade reversal Services -> Hosts -> Services: Service charts still animate cleanly.
            main.SelectTab(MainWindowViewModel.OverviewPage);
            await Task.Delay(45);
            var sawRapid = TrackSweeps(main.ServiceHealthPlot, main.ServiceCpuPlot, main.ServiceMemoryPlot);
            main.SelectTab(MainWindowViewModel.WorkloadsPage);
            await AwaitTrackedSweepsAsync(sawRapid, TimeSpan.FromSeconds(5));

            // Delayed async arrival: awaiting entrance does not draw empty, starts sweep once points arrive.
            var savedCpu = main.ServiceCpuPlot.Points;
            main.ServiceCpuPlot.Points = [];
            main.ServiceCpuPlot.RequestEntranceSweep(0);
            await Task.Delay(80);
            Assert.False(IsSweeping(main.ServiceCpuPlot));
            var sawLate = TrackSweeps(main.ServiceCpuPlot);
            main.ServiceCpuPlot.Points = savedCpu;
            await AwaitTrackedSweepsAsync(sawLate, TimeSpan.FromSeconds(5));

            // Regular 0.5s/1s polling under the same ScaleKey must not replay the sweep animation.
            await AwaitSettledAsync(main.ServiceHealthPlot, main.ServiceCpuPlot, main.ServiceMemoryPlot);
            var polledDuringSweep = false;
            void WatchSweep(object? sender, EventArgs _) =>
                polledDuringSweep |= IsSweeping(main.ServiceHealthPlot)
                    || IsSweeping(main.ServiceCpuPlot)
                    || IsSweeping(main.ServiceMemoryPlot)
                    || main.ServiceHealthPlot.SweepProgress < 0.999
                    || main.ServiceCpuPlot.SweepProgress < 0.999
                    || main.ServiceMemoryPlot.SweepProgress < 0.999;
            CompositionTarget.Rendering += WatchSweep;
            try
            {
                for (var tick = 0; tick < 2; tick++)
                {
                    var at = DateTimeOffset.UtcNow;
                    source.Observe(at, model.Tasks.Select(task => ResourceWindowSource.Synthetic(task, at, 300 + tick)).ToArray(),
                        ResourceWindowSource.SyntheticHost(at, 300 + tick), durable: false);
                    source.Apply(model);
                    await Task.Delay(45);
                }
            }
            finally { CompositionTarget.Rendering -= WatchSweep; }
            Assert.False(polledDuringSweep, "Routine polling updates must not replay left-to-right entrance sweep.");
        });

    [Fact]
    public async Task ExtraneousTruncationAndTenSecondSourceCopyRemovedWhileRealErrorsStay()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var cursor = new AgentConfigurationClient(fixture.Client);
        _ = await cursor.ReadAsync();
        var exe = Path.Combine(fixture.Root, "never-run-r126-copy.exe");
        await File.WriteAllTextAsync(exe, "not executable");
        _ = await cursor.MutateAsync<ConfigurationMutationResult>("task-add", payload: new AddTaskRequest("r126-copy",
            AutorunTaskType.Windows, Executable: exe, HealthType: HealthCheckType.Process, HealthPattern: "never-run-r126-copy.exe", ExpectedRunning: true));

        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var client = new DashboardFixtureMonitoringClient(fixture)
            {
                HistoryReader = (query, _) =>
                {
                    var to = query.To;
                    var from = query.From;
                    return Task.FromResult(new MonitoringHistoryResult(1, query.TaskName, query.TaskIdentity, from, to, to,
                        to.AddMinutes(-30), 60, 0, Math.Max(0, (to - from).TotalSeconds - 60), 100, 10,
                        [new HealthHistoryInterval(from, to, ObservationHealth.Healthy)], [], Truncated: true, Warning: null));
                },
                ResourceReader = (query, _) => Task.FromResult(new ResourceHistoryResult(1, query.Source, query.TaskName,
                    query.TaskIdentity, query.From, query.To, 10, 10,
                    [new ResourceHistoryPoint(query.To.AddSeconds(-10), "r126", CpuPercent: 12.5, MemoryBytes: 42L << 20)],
                    query.To.AddMinutes(-30), query.To, Truncated: true, Warning: null))
            };
            var model = new MainWindowViewModel(fixture.Client, new NativeCacheStore(fixture.Paths.GuiCachePath),
                cursor, fixture.Paths, new NeverRemote(), client);
            model.InitializeDashboard();
            try
            {
                await model.InitializeAsync();
                model.SelectDashboardWindowCommand.Execute("168");
                await model.DashboardHistoryTask;
                await model.ResourceHistoryTask;

                Assert.DoesNotContain("截断", model.DashboardHistoryNotice, StringComparison.Ordinal);
                Assert.DoesNotContain("不完整", model.DashboardHistoryNotice, StringComparison.Ordinal);
                Assert.DoesNotContain("截断", model.DashboardResourceCaption, StringComparison.Ordinal);
                Assert.DoesNotContain("不完整", model.DashboardResourceCaption, StringComparison.Ordinal);
                Assert.DoesNotContain("10秒源", model.DashboardResourceCaption, StringComparison.Ordinal);
                Assert.DoesNotContain("有效样本均值", model.DashboardResourceCaption, StringComparison.Ordinal);

                client.HistoryReader = (_, _) => throw new InvalidOperationException("history failure");
                client.ResourceReader = (_, _) => throw new InvalidOperationException("resource failure");
                model.SelectDashboardWindowCommand.Execute("720");
                await model.DashboardHistoryTask;
                await model.ResourceHistoryTask;

                Assert.Equal("历史读取失败", model.DashboardHistoryNotice);
                Assert.Equal("资源轨迹不可用", model.DashboardResourceCaption);
            }
            finally { model.DetachDashboard(); await model.DashboardHistoryTask; await model.ResourceHistoryTask; }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ChromeAlignmentLegendCenterTextCenteringAndRedWhiteColors(bool light) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, _) =>
        {
            ColorStateAssist.SetReduceMotion(main, true);
            main.Width = 1152;
            main.Height = 648;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();

            // 1. Top bar Brand icon, sidebar toggle icon, and sidebar row text restore 18 DIP left alignment.
            var brandIcon = Descendants(main).OfType<LineIcon>().First(icon => icon.Kind == "Brand");
            var toggleButton = Assert.IsType<Button>(main.FindName("ToggleServicesSidebarButton"));
            var toggleIcon = Descendants(toggleButton).OfType<LineIcon>().First();
            var firstServiceRow = Descendants(main.ServiceList).OfType<ListBoxItem>().First();
            var serviceRowText = Descendants(firstServiceRow).OfType<TextBlock>().First();
            var brandLeft = brandIcon.TranslatePoint(new Point(), main).X;
            var toggleIconLeft = toggleIcon.TranslatePoint(new Point(), main).X;
            var rowLeft = serviceRowText.TranslatePoint(new Point(), main).X;
            Assert.InRange(brandLeft, 18.0, 20.0);
            Assert.InRange(Math.Abs(brandLeft - toggleIconLeft), 0, 1.0);
            Assert.InRange(Math.Abs(brandLeft - rowLeft), 0, 1.0);

            // 2. Service detail left (6 DIP from divider) & right edge (6 DIP from window right, aligned with close chrome) in 2x2 chart layout.
            var activityIcon = Descendants(main).OfType<LineIcon>().First(icon => icon.Kind == "Activity");
            var cpuIcon = Descendants(main).OfType<LineIcon>().First(icon => icon.Kind == "Cpu");
            var memoryIcon = Descendants(main).OfType<LineIcon>().First(icon => icon.Kind == "Memory");
            var diskIcon = Descendants(main).OfType<LineIcon>().First(icon => icon.Kind == "Disk");
            var networkIcon = Descendants(main).OfType<LineIcon>().First(icon => icon.Kind == "Network");
            var cpuIconBorder = Ancestors(cpuIcon).OfType<Border>().First();
            var diskIconBorder = Ancestors(diskIcon).OfType<Border>().First();
            var cpuBorder = Assert.IsType<Border>(main.FindName("ServiceCpuCell"));
            var memoryBorder = Assert.IsType<Border>(main.FindName("ServiceMemoryCell"));
            var diskBorder = Assert.IsType<Border>(main.FindName("ServiceDiskCell"));
            var networkBorder = Assert.IsType<Border>(main.FindName("ServiceNetworkCell"));
            var networkPlot = Assert.IsType<DashboardPlot>(main.FindName("ServiceNetworkPlot"));
            var healthFrame = Assert.IsType<Border>(main.ServiceHealthPlot.Parent);
            var headerLeft = main.ServiceHeader.TranslatePoint(new Point(), main).X;
            var sidebarDividerRight = 1 + main.ServicesPage.ColumnDefinitions[0].ActualWidth;
            Assert.InRange(Math.Abs(headerLeft - (sidebarDividerRight + 6)), 0, 1.0);
            Assert.InRange(Math.Abs(headerLeft - activityIcon.TranslatePoint(new Point(), main).X), 0, 1.0);
            Assert.InRange(Math.Abs(headerLeft - healthFrame.TranslatePoint(new Point(), main).X), 0, 1.0);
            Assert.InRange(Math.Abs(headerLeft - cpuIconBorder.TranslatePoint(new Point(), main).X), 0, 1.0);
            Assert.InRange(Math.Abs(headerLeft - diskIconBorder.TranslatePoint(new Point(), main).X), 0, 1.0);
            Assert.InRange(Math.Abs(cpuBorder.TranslatePoint(new Point(), main).Y - memoryBorder.TranslatePoint(new Point(), main).Y), 0, 1.0);
            Assert.InRange(Math.Abs(diskBorder.TranslatePoint(new Point(), main).Y - networkBorder.TranslatePoint(new Point(), main).Y), 0, 1.0);
            Assert.True(memoryIcon.TranslatePoint(new Point(), main).X > cpuIcon.TranslatePoint(new Point(), main).X);
            Assert.True(networkIcon.TranslatePoint(new Point(), main).X > diskIcon.TranslatePoint(new Point(), main).X);
            Assert.Equal(0.5d, main.ServiceCpuPlot.PlotBounds.Left, 1.0);
            Assert.Equal(0.5d, main.ServiceCpuPlot.ActualWidth - main.ServiceCpuPlot.PlotBounds.Right, 1.0);

            var closeIcon = Descendants(main).OfType<LineIcon>().First(icon => icon.Kind == "Close");
            var closeBorder = Ancestors(closeIcon).OfType<Border>().ElementAt(1);
            var topChromeRight = closeBorder.TranslatePoint(new Point(closeBorder.ActualWidth, 0), main).X;
            var serviceActionsRight = main.ServiceActions.TranslatePoint(new Point(main.ServiceActions.ActualWidth, 0), main).X;
            var healthFrameRight = healthFrame.TranslatePoint(new Point(healthFrame.ActualWidth, 0), main).X;
            var cpuPlotLeft = main.ServiceCpuPlot.TranslatePoint(new Point(main.ServiceCpuPlot.PlotBounds.Left, 0), main).X;
            var memPlotRight = main.ServiceMemoryPlot.TranslatePoint(new Point(main.ServiceMemoryPlot.PlotBounds.Right, 0), main).X;
            var networkPlotRight = networkPlot.TranslatePoint(new Point(networkPlot.PlotBounds.Right, 0), main).X;
            Assert.InRange(Math.Abs(topChromeRight - serviceActionsRight), 0, 1.0);
            Assert.InRange(Math.Abs(topChromeRight - healthFrameRight), 0, 1.0);
            Assert.InRange(Math.Abs(headerLeft - cpuPlotLeft), 0, 1.5);
            Assert.InRange(Math.Abs(topChromeRight - memPlotRight), 0, 1.5);
            Assert.InRange(Math.Abs(topChromeRight - networkPlotRight), 0, 1.5);

            // 3. Health '?' legend optical vertical center relative to '可用率' label.
            var healthLegend = Descendants(main).OfType<PlotLegendHelp>().First(item => item.Legend == "Health");
            var healthLabel = Descendants(main).OfType<TextBlock>().First(tb => tb.Text == "可用率" && tb.IsVisible);
            Assert.InRange(Math.Abs(CenterY(healthLegend.HelpButton, main) - CenterY(healthLabel, main)), 0, 1.5);

            // 4. Top SearchBox text host vertically centered inside SearchSurface.
            var searchSurface = Assert.IsType<Border>(main.FindName("SearchSurface"));
            AssertVerticalCenter(Assert.IsAssignableFrom<FrameworkElement>(main.SearchBox.Template.FindName("PART_ContentHost", main.SearchBox)), searchSurface, 1.5);

            // 5. Hosts page Disk/Network '?' legend vertical centers.
            main.SelectTab(MainWindowViewModel.OverviewPage);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();
            foreach (var (legendKey, labelText) in new[] { ("Disk", "磁盘IO"), ("Network", "网络") })
            {
                var help = Descendants(main.MonitoringTabs.HostCharts).OfType<PlotLegendHelp>().First(item => item.Legend == legendKey);
                var label = Descendants(main.MonitoringTabs.HostCharts).OfType<TextBlock>().First(tb => tb.Text == labelText && tb.IsVisible);
                Assert.InRange(Math.Abs(CenterY(help.HelpButton, main) - CenterY(label, main)), 0, 1.5);
            }

            // 6. Exceptions page SearchBox & ComboBox vertical centering and active row red/white.
            main.SelectTab(MainWindowViewModel.ExceptionLogsPage);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();
            var excSearchSurface = Assert.IsType<Border>(main.MonitoringTabs.FindName("ExceptionSearchSurface"));
            var excSearchBox = Assert.IsType<TextBox>(main.MonitoringTabs.FindName("ExceptionLogSearchBox"));
            AssertVerticalCenter(Assert.IsAssignableFrom<FrameworkElement>(excSearchBox.Template.FindName("PART_ContentHost", excSearchBox)), excSearchSurface, 1.5);
            var levelCombo = Descendants(main.MonitoringTabs).OfType<ComboBox>()
                .Single(box => AutomationProperties.GetName(box) == "异常级别筛选");
            var comboContent = Descendants(levelCombo).OfType<ContentPresenter>().First();
            AssertVerticalCenter(comboContent, levelCombo, 1.5);

            // 7. Red + soft gray-white (Dark) / pure white (Light) hover & active brushes, softened Dark background & no pure #FFF in Dark.
            var hover = ((SolidColorBrush)main.FindResource("Semantic.Brush.InteractionHover")).Color;
            var active = ((SolidColorBrush)main.FindResource("Semantic.Brush.InteractionActive")).Color;
            var activeHover = ((SolidColorBrush)main.FindResource("Semantic.Brush.InteractionActiveHover")).Color;
            var onHover = ((SolidColorBrush)main.FindResource("Semantic.Brush.InteractionOnHover")).Color;
            var onActive = ((SolidColorBrush)main.FindResource("Semantic.Brush.InteractionOnActive")).Color;
            var selection = ((SolidColorBrush)main.FindResource("Semantic.Brush.Selection")).Color;
            var windowBg = ((SolidColorBrush)main.FindResource("Semantic.Brush.WindowBackground")).Color;
            var textPrimary = ((SolidColorBrush)main.FindResource("Semantic.Brush.TextPrimary")).Color;
            var interactionFocus = ((SolidColorBrush)main.FindResource("Semantic.Brush.InteractionFocus")).Color;
            AssertIsRed(hover);
            Assert.True(Math.Abs(active.R - active.G) <= 12 && Math.Abs(active.R - active.B) <= 12,
                $"Active surface should be neutral grey, got #{active.R:X2}{active.G:X2}{active.B:X2}");
            AssertIsRed(activeHover);
            AssertIsRed(onActive);
            Assert.True(ContrastRatio(onActive, active) >= 4.5, $"Active ink/surface contrast too low: {ContrastRatio(onActive, active):0.00}:1");
            Assert.True(ContrastRatio(onHover, hover) >= 4.5, $"Hover ink/surface contrast too low: {ContrastRatio(onHover, hover):0.00}:1");
            Assert.True(ContrastRatio(onHover, activeHover) >= 4.5, $"ActiveHover ink/surface contrast too low: {ContrastRatio(onHover, activeHover):0.00}:1");
            if (light)
            {
                Assert.Equal(Color.FromRgb(255, 255, 255), onHover);
            }
            else
            {
                Assert.Equal(Color.FromRgb(0x14, 0x14, 0x17), windowBg);
                Assert.True(textPrimary.R < 245 && textPrimary.G < 245 && textPrimary.B < 245, $"Dark TextPrimary should be soft off-white, got #{textPrimary.R:X2}{textPrimary.G:X2}{textPrimary.B:X2}");
                Assert.True(interactionFocus.R < 245 && interactionFocus.G < 245 && interactionFocus.B < 245, $"Dark InteractionFocus should be soft off-white, got #{interactionFocus.R:X2}{interactionFocus.G:X2}{interactionFocus.B:X2}");
                Assert.True(onHover.R >= 215 && onHover.R <= 242 && onHover.B <= 242, $"Dark onHover should be soft gray-white, got #{onHover.R:X2}{onHover.G:X2}{onHover.B:X2}");
                Assert.NotEqual(onHover, onActive);
                Assert.NotEqual(Color.FromRgb(255, 255, 255), onHover);
            }
            Assert.True(Math.Abs(selection.R - selection.G) <= 20 && Math.Abs(selection.R - selection.B) <= 20, "Selection brush must stay neutral for unknown health hatch.");

            var logsTab = Assert.IsType<Button>(main.FindName("ExceptionLogsTab"));
            Assert.Equal(onActive, ((SolidColorBrush)logsTab.Foreground).Color);
            Assert.Equal(active, ((SolidColorBrush)Assert.IsType<Border>(logsTab.Template.FindName("Surface", logsTab)).Background).Color);

            // 8. Expanded disclosure header keeps readable foreground contrast in both Light and Dark themes.
            var addWindow = new AddProgramWindow(StartupGuiControlPlaneTests.Entry, _ => Task.FromResult(true))
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000
            };
            ColorStateAssist.SetReduceMotion(addWindow, true);
            try
            {
                addWindow.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                addWindow.UpdateLayout();
                var headerSite = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(
                    addWindow.AdvancedExpander.Template.FindName("HeaderSite", addWindow.AdvancedExpander));
                var headerFg = ((SolidColorBrush)headerSite.Foreground).Color;
                var bg = ((SolidColorBrush)addWindow.FindResource("Semantic.Brush.WindowBackground")).Color;
                Assert.True(ContrastRatio(headerFg, bg) >= 4.5,
                    $"Expanded disclosure header contrast too low: fg=#{headerFg.R:X2}{headerFg.G:X2}{headerFg.B:X2} on bg=#{bg.R:X2}{bg.G:X2}{bg.B:X2}");
            }
            finally { addWindow.Close(); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RedTrendLinesOsIconsTightHeaderGapServiceHeadingsAndTabSwitchDelayWork(bool light) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, _) =>
        {
            // 1. Red trend stroke lines for primary and secondary series in both themes.
            var end = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.FromHours(8));
            var flatPrimary = Enumerable.Range(0, 61).Select(i => new PlotPoint(end.AddSeconds(-300 + i * 5), 60d)).ToArray();
            var flatSecondary = Enumerable.Range(0, 61).Select(i => new PlotPoint(end.AddSeconds(-300 + i * 5), 25d)).ToArray();
            var trendPlot = new DashboardPlot
            {
                Mode = "Trend",
                Width = 320,
                Height = 140,
                WindowDuration = TimeSpan.FromMinutes(5),
                EndAt = end,
                Points = flatPrimary,
                SecondaryPoints = flatSecondary,
                ScaleKey = "r128-red-lines",
            };
            var host = new Border { Background = (Brush)main.FindResource("Semantic.Brush.WindowBackground"), Child = trendPlot };
            var probe = new Window
            {
                Content = host, Width = 340, Height = 160,
                ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000
            };
            ColorStateAssist.SetReduceMotion(probe, true);
            try
            {
                probe.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                probe.UpdateLayout();
                var (pixels, pw, ph) = CapturePixels(trendPlot);
                var primaryRedHits = CountRedPixels(pixels, pw, ph, 0.20, 0.80, 0.25, 0.50);
                Assert.True(primaryRedHits > 15, $"Expected red primary trend stroke pixels, got {primaryRedHits}.");
            }
            finally { probe.Close(); }

            // 2. OS icon mapping and tight HostHeader gap.
            Assert.Equal("Windows", MainWindowViewModel.ResolveHostIcon("Microsoft Windows 10.0.28000"));
            Assert.Equal("Linux", MainWindowViewModel.ResolveHostIcon("Ubuntu 24.04.1 LTS (GNU/Linux)"));
            Assert.Equal("MacOs", MainWindowViewModel.ResolveHostIcon("Darwin Kernel Version 24.1.0 / macOS 15"));
            Assert.Equal("Host", MainWindowViewModel.ResolveHostIcon("Unknown Custom Node"));

            main.SelectTab(MainWindowViewModel.OverviewPage);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();
            var hostOsIcon = Assert.IsType<LineIcon>(main.MonitoringTabs.FindName("HostOsIcon"));
            Assert.Equal("Windows", hostOsIcon.Kind);

            var nameTb = Descendants(main.MonitoringTabs).OfType<TextBlock>()
                .First(tb => System.Windows.Data.BindingOperations.GetBinding(tb, TextBlock.TextProperty)?.Path?.Path == nameof(model.HostName));
            var osTb = Descendants(main.MonitoringTabs).OfType<TextBlock>()
                .First(tb => System.Windows.Data.BindingOperations.GetBinding(tb, TextBlock.TextProperty)?.Path?.Path == nameof(model.HostOs));
            var header = Assert.IsType<Border>(main.MonitoringTabs.FindName("HostHeader"));
            var nameBottom = nameTb.TranslatePoint(new Point(0, nameTb.ActualHeight), header).Y;
            var osTop = osTb.TranslatePoint(new Point(), header).Y;
            Assert.InRange(osTop - nameBottom, 0, 5.0);

            var prevOs = model.HostOs;
            try
            {
                model.HostOs = "Ubuntu 24.04.1 LTS (GNU/Linux 6.8.0-48-generic x86_64)";
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal("Linux", hostOsIcon.Kind);
            }
            finally { model.HostOs = prevOs; }

            // 3. Redesigned Services CPU/Memory compact heading + balanced 2x2 column plots.
            main.SelectTab(MainWindowViewModel.WorkloadsPage);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();
            var cpuHeading = Assert.IsType<MetricHeadingPanel>(main.FindName("ServiceCpuHeading"));
            var memHeading = Assert.IsType<MetricHeadingPanel>(main.FindName("ServiceMemoryHeading"));
            var cpuCell = Assert.IsType<Border>(main.FindName("ServiceCpuCell"));
            var memCell = Assert.IsType<Border>(main.FindName("ServiceMemoryCell"));
            Assert.True(cpuHeading.IsVisible);
            Assert.True(memHeading.IsVisible);
            Assert.InRange(Math.Abs(main.ServiceCpuPlot.ActualWidth - main.ServiceMemoryPlot.ActualWidth), 0, 2.0);
            Assert.True(cpuCell.ActualWidth + memCell.ActualWidth >= main.ServiceHealthPlot.ActualWidth,
                $"Expected 2x2 chart row ({cpuCell.ActualWidth + memCell.ActualWidth}) spanning health width ({main.ServiceHealthPlot.ActualWidth}).");

            // 4. Tab-switch delayed sweep works across Overview <-> Workloads (including scrolled off-fold host plots) after TickDashboard().
            if (!light && SystemParameters.ClientAreaAnimation)
            {
                ColorStateAssist.SetReduceMotion(main, false);
                await AwaitSettledAsync(main.ServiceHealthPlot, main.ServiceCpuPlot, main.ServiceMemoryPlot);
                main.SelectTab(MainWindowViewModel.OverviewPage);
                var hc = main.MonitoringTabs.HostCharts;
                Assert.Equal(0d, hc.HostCpuPlot.EffectiveSweepProgress, 3);
                Assert.True(hc.HostCpuPlot.CurrentRemainingEntranceDelayMs() >= 250,
                    $"HostCpuPlot delay was wiped: {hc.HostCpuPlot.CurrentRemainingEntranceDelayMs()} ms");
                Assert.True(hc.HostDiskPlot.CurrentRemainingEntranceDelayMs() >= 250,
                    $"Off-viewport HostDiskPlot missed delayed sweep: {hc.HostDiskPlot.CurrentRemainingEntranceDelayMs()} ms");
                await AwaitSettledAsync(hc.HostCpuPlot, hc.HostMemoryPlot, hc.HostDiskPlot, hc.HostNetworkPlot);

                main.SelectTab(MainWindowViewModel.WorkloadsPage);
                Assert.Equal(0d, main.ServiceHealthPlot.EffectiveSweepProgress, 3);
                Assert.Equal(0d, main.ServiceCpuPlot.EffectiveSweepProgress, 3);
                Assert.True(main.ServiceCpuPlot.CurrentRemainingEntranceDelayMs() >= 250,
                    $"ServiceCpuPlot delay was wiped on tab return: {main.ServiceCpuPlot.CurrentRemainingEntranceDelayMs()} ms");
                Assert.True(main.ServiceMemoryPlot.CurrentRemainingEntranceDelayMs() >= 250,
                    $"ServiceMemoryPlot delay was wiped on tab return: {main.ServiceMemoryPlot.CurrentRemainingEntranceDelayMs()} ms");
                await AwaitSettledAsync(main.ServiceHealthPlot, main.ServiceCpuPlot, main.ServiceMemoryPlot);
            }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RedChartProjectionPostEntranceDelayBackdropAndTextBoxHoverWork(bool light) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, _) =>
        {
            // 1. Red chart projection area gradient under a steady upper-band trend series.
            var end = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.FromHours(8));
            var areaPoints = Enumerable.Range(0, 151)
                .Select(i => new PlotPoint(end.AddSeconds(-300 + i * 2), i == 0 ? 0d : 80d))
                .ToArray();
            var gradientPlot = new DashboardPlot
            {
                Mode = "Trend",
                Width = 360,
                Height = 140,
                WindowDuration = TimeSpan.FromMinutes(5),
                EndAt = end,
                Points = areaPoints,
                ScaleKey = "r127-area",
            };
            var baselinePlot = new DashboardPlot
            {
                Mode = "Trend",
                Width = 360,
                Height = 140,
                WindowDuration = TimeSpan.FromMinutes(5),
                EndAt = end,
                Points = [new PlotPoint(end.AddSeconds(-300), 0d), new PlotPoint(end.AddSeconds(-298), 80d)],
                ScaleKey = "r127-area-base",
            };
            var hostStack = new StackPanel { Background = (Brush)main.FindResource("Semantic.Brush.WindowBackground") };
            hostStack.Children.Add(gradientPlot);
            hostStack.Children.Add(baselinePlot);
            var probe = new Window
            {
                Content = hostStack,
                Width = 380,
                Height = 300,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            ColorStateAssist.SetReduceMotion(probe, true);
            try
            {
                probe.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                probe.UpdateLayout();
                // Sample between the 80% stroke (~y=0.20*Height) and 50% dashed midline (~y=0.50*Height) at x=50%.
                var (filledPixels, wG, hG) = CapturePixels(gradientPlot);
                var (emptyPixels, _, _) = CapturePixels(baselinePlot);
                var sx = (int)(wG * 0.50);
                var sy = (int)((gradientPlot.PlotBounds.Top + gradientPlot.PlotBounds.Height * 0.35) * (hG / gradientPlot.ActualHeight));
                var idx = (sy * wG + sx) * 4;
                var dB = filledPixels[idx] - emptyPixels[idx];
                var dG = filledPixels[idx + 1] - emptyPixels[idx + 1];
                var dR = filledPixels[idx + 2] - emptyPixels[idx + 2];
                // Blending red over Dark background increases R more than G; blending red over Light background lowers G more than R.
                Assert.True(light ? (dG < dR - 6) : (dR > dG + 6 && dR > dB + 3),
                    $"Expected red area gradient delta at ({sx},{sy}) in {(light ? "Light" : "Dark")}, got dR={dR}, dG={dG}, dB={dB}.");
            }
            finally { probe.Close(); }

            // 2. Post-entrance delayed chart sweep: LineMotion keeps charts hidden at t~0 and reveals after entrance settles.
            Assert.True(LineMotion.PlotEntranceDelayMilliseconds >= 400);
            if (!light && SystemParameters.ClientAreaAnimation)
            {
                ColorStateAssist.SetReduceMotion(main, false);
                main.SelectTab(MainWindowViewModel.WorkloadsPage);
                await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(main));
                Assert.True(LineMotion.Play(main.ServicesPage));
                // Right after Play starts (t < 80ms), plots hold progress = 0 while borders/text animate first.
                Assert.Equal(0d, main.ServiceCpuPlot.EffectiveSweepProgress, 3);
                Assert.Equal(0d, main.ServiceHealthPlot.EffectiveSweepProgress, 3);
                await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(main.ServicesPage));
                await AwaitSettledAsync(main.ServiceHealthPlot, main.ServiceCpuPlot, main.ServiceMemoryPlot);
                Assert.Equal(1d, main.ServiceCpuPlot.EffectiveSweepProgress, 3);
            }

            // 3. DialogBackdrop uses red diagonal lines at 2x speed (7s) and non-trivial opacity.
            Assert.Equal(TimeSpan.FromSeconds(7), DialogBackdrop.DriftDuration);
            Assert.True(main.DialogScrim.TextureVisual.Opacity >= 0.12);
            main.DialogScrim.IsPresented = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();
            var tileBrush = Assert.IsType<DrawingBrush>(main.DialogScrim.TextureVisual.Fill);
            var linePen = DescendantDrawings(tileBrush.Drawing).OfType<GeometryDrawing>().Select(d => d.Pen).First(p => p is not null);
            AssertIsRed(Assert.IsType<SolidColorBrush>(linePen!.Brush).Color);
            main.DialogScrim.IsPresented = false;
            await PresenceMotionTests.Until(() => !main.DialogScrim.IsVisible);

            // 4. TextBox uses custom ControlTemplate without MaterialDesign black underline overrides and turns red on focus/hover.
            var addWindow = new AddProgramWindow(StartupGuiControlPlaneTests.Entry, _ => Task.FromResult(true))
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            ColorStateAssist.SetReduceMotion(addWindow, true);
            try
            {
                addWindow.Show();
                addWindow.AdvancedExpander.IsExpanded = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                addWindow.UpdateLayout();

                Assert.DoesNotContain(Descendants(addWindow.ArgumentsTextBox),
                    part => part.GetType().Namespace?.StartsWith("MaterialDesignThemes", StringComparison.Ordinal) == true);
                var outerBorder = Assert.IsType<Border>(addWindow.ArgumentsTextBox.Template.FindName("OuterBorder", addWindow.ArgumentsTextBox));
                Assert.Equal(new Thickness(0, 0, 0, 1), outerBorder.BorderThickness);

                var expectedRed = ((SolidColorBrush)addWindow.FindResource("Semantic.Brush.InteractionHover")).Color;
                ColorStateAssist.SetFrameHovered(addWindow.ArgumentsTextBox, true);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(expectedRed, Assert.IsType<SolidColorBrush>(outerBorder.BorderBrush).Color);
                ColorStateAssist.SetFrameHovered(addWindow.ArgumentsTextBox, false);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.NotEqual(expectedRed, Assert.IsType<SolidColorBrush>(outerBorder.BorderBrush).Color);
            }
            finally { addWindow.Close(); }
        });

    private static IEnumerable<Drawing> DescendantDrawings(Drawing root)
    {
        yield return root;
        if (root is DrawingGroup group)
        {
            foreach (var child in group.Children)
            {
                foreach (var nested in DescendantDrawings(child))
                {
                    yield return nested;
                }
            }
        }
    }

    [Fact]
    public async Task CaptureR126VisualEvidenceWhenRequested()
    {
        var dir = Environment.GetEnvironmentVariable("SA_R127_EVIDENCE") ?? Environment.GetEnvironmentVariable("SA_R126_EVIDENCE");
        if (string.IsNullOrEmpty(dir)) { return; }
        Directory.CreateDirectory(dir);

        foreach (var light in new[] { false, true })
        {
            var tag = light ? "light" : "dark";
            await ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, _) =>
            {
                ColorStateAssist.SetReduceMotion(main, true);

                // Services across desktop widths
                main.SelectTab(MainWindowViewModel.WorkloadsPage);
                foreach (var (w, h) in new[] { (920, 580), (1152, 648), (1320, 820) })
                {
                    main.Width = w;
                    main.Height = h;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    main.UpdateLayout();
                    SavePng(main, Path.Combine(dir, $"svc-{tag}-{w}x{h}.png"));
                }

                // Scrim (DialogBackdrop) over MainWindow at 1152x648
                main.Width = 1152;
                main.Height = 648;
                main.DialogScrim.IsPresented = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                main.UpdateLayout();
                SavePng(main, Path.Combine(dir, $"scrim-{tag}-1152x648.png"));
                main.DialogScrim.IsPresented = false;
                await PresenceMotionTests.Until(() => !main.DialogScrim.IsVisible);

                // Hosts 1152x648 top + scrolled bottom, Linux OS icon preview, and 920x580
                main.SelectTab(MainWindowViewModel.OverviewPage);
                main.Width = 1152;
                main.Height = 648;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                main.UpdateLayout();
                SavePng(main, Path.Combine(dir, $"host-top-{tag}-1152x648.png"));

                var savedOs = model.HostOs;
                model.HostOs = "Ubuntu 24.04.1 LTS (GNU/Linux 6.8.0-48-generic x86_64)";
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                main.UpdateLayout();
                SavePng(main, Path.Combine(dir, $"host-linux-{tag}-1152x648.png"));
                model.HostOs = savedOs;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                var hostScroll = Ancestors(main.MonitoringTabs.HostCharts).OfType<ScrollViewer>().First();
                hostScroll.ScrollToBottom();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                main.UpdateLayout();
                SavePng(main, Path.Combine(dir, $"host-bottom-{tag}-1152x648.png"));

                hostScroll.ScrollToTop();
                main.Width = 920;
                main.Height = 580;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                main.UpdateLayout();
                SavePng(main, Path.Combine(dir, $"host-top-{tag}-920x580.png"));

                // Exceptions 1152x648 with selected row
                main.SelectTab(MainWindowViewModel.ExceptionLogsPage);
                main.Width = 1152;
                main.Height = 648;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                main.UpdateLayout();
                SavePng(main, Path.Combine(dir, $"exc-{tag}-1152x648.png"));

                // Mid-sweep 45% capture in Dark mode on both Services and Hosts
                if (!light && SystemParameters.ClientAreaAnimation)
                {
                    ColorStateAssist.SetReduceMotion(main, false);
                    main.SelectTab(MainWindowViewModel.WorkloadsPage);
                    await AwaitSettledAsync(main.ServiceHealthPlot, main.ServiceCpuPlot, main.ServiceMemoryPlot);
                    main.ServiceHealthPlot.SweepProgress = 0.45d;
                    main.ServiceCpuPlot.SweepProgress = 0.45d;
                    main.ServiceMemoryPlot.SweepProgress = 0.45d;
                    main.ServiceHealthPlot.InvalidateVisual();
                    main.ServiceCpuPlot.InvalidateVisual();
                    main.ServiceMemoryPlot.InvalidateVisual();
                    await Dispatcher.Yield(DispatcherPriority.Render);
                    main.UpdateLayout();
                    SavePng(main, Path.Combine(dir, "svc-sweep45-dark-1152x648.png"));

                    main.SelectTab(MainWindowViewModel.OverviewPage);
                    var hc = main.MonitoringTabs.HostCharts;
                    Ancestors(hc).OfType<ScrollViewer>().First().ScrollToTop();
                    await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(main));
                    await AwaitSettledAsync(hc.HostCpuPlot, hc.HostMemoryPlot, hc.HostDiskPlot, hc.HostNetworkPlot);
                    hc.HostCpuPlot.SweepProgress = 0.45d;
                    hc.HostMemoryPlot.SweepProgress = 0.45d;
                    hc.HostCpuPlot.InvalidateVisual();
                    hc.HostMemoryPlot.InvalidateVisual();
                    await Dispatcher.Yield(DispatcherPriority.Render);
                    main.UpdateLayout();
                    SavePng(main, Path.Combine(dir, "host-sweep45-dark-1152x648.png"));
                }
            });

            // Dialogs: AddProgramWindow & StartupManagerWindow
            var reader = new StartupGuiControlPlaneTests.Reader { Entries = StartupGuiWindowTests.SampleEntries() };
            await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: reader);
            await GuiConfigurationControlTests.RunStaAsync(async () =>
            {
                EnsureAppInitialized();
                var prev = ThemeService.IsLight ? ThemePreference.Light : ThemePreference.Dark;
                ThemeService.Apply(light ? ThemePreference.Light : ThemePreference.Dark, persist: null);
                try
                {
                    var add = new AddProgramWindow(StartupGuiControlPlaneTests.Entry, _ => Task.FromResult(true))
                    {
                        ShowActivated = false,
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -32000,
                        Top = -32000
                    };
                    ColorStateAssist.SetReduceMotion(add, true);
                    try
                    {
                        add.Show();
                        add.AdvancedExpander.IsExpanded = true;
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        add.UpdateLayout();
                        ColorStateAssist.SetFrameHovered(add.ArgumentsTextBox, true);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        add.UpdateLayout();
                        SavePng(add, Path.Combine(dir, $"dlg-add-{tag}.png"));
                    }
                    finally { add.Close(); }

                    using var model = new StartupManagerViewModel(new(server.Client), new(server.Paths.StartupCachePath));
                    var startup = new StartupManagerWindow(model)
                    {
                        ShowActivated = false,
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -32000,
                        Top = -32000
                    };
                    ColorStateAssist.SetReduceMotion(startup, true);
                    try
                    {
                        startup.Show();
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        await startup.InitializationTask;
                        model.SelectedEntry = model.Entries.First();
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        startup.UpdateLayout();
                        SavePng(startup, Path.Combine(dir, $"dlg-startup-{tag}.png"));
                    }
                    finally { startup.Close(); }
                }
                finally { ThemeService.Apply(prev, persist: null); }
            });
        }
    }

    private static void EnsureAppInitialized()
    {
        if (Application.Current is not SystemAutorun.App.App)
        {
            var app = new SystemAutorun.App.App(runRuntimeStartup: false);
            app.InitializeComponent();
        }
        WindowTheme.Install();
    }

    private static bool IsSweeping(DashboardPlot plot) =>
        DependencyPropertyHelper.GetValueSource(plot, DashboardPlot.SweepProgressProperty).IsAnimated;

    private static async Task AwaitSettledAsync(params DashboardPlot[] plots)
    {
        await PresenceMotionTests.Until(() => plots.All(p => !IsSweeping(p) && p.SweepProgress >= 0.999));
        await Dispatcher.Yield(DispatcherPriority.Render);
    }

    private static Dictionary<DashboardPlot, bool> TrackSweeps(params DashboardPlot[] plots) =>
        plots.ToDictionary(p => p, _ => false);

    private static async Task AwaitTrackedSweepsAsync(Dictionary<DashboardPlot, bool> tracked, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var plot in tracked.Keys.ToArray())
            {
                if (IsSweeping(plot) || plot.SweepProgress < 0.999)
                {
                    tracked[plot] = true;
                }
            }
            if (tracked.Values.All(v => v) && tracked.Keys.All(p => !IsSweeping(p) && p.SweepProgress >= 0.999))
            {
                break;
            }
            await Dispatcher.Yield(DispatcherPriority.Render);
            await Task.Delay(12);
        }
        if (SystemParameters.ClientAreaAnimation)
        {
            foreach (var (plot, saw) in tracked)
            {
                Assert.True(saw, $"Expected {plot.Name} ({plot.Mode}) to replay SweepProgress animation.");
            }
        }
        await PresenceMotionTests.Until(() => tracked.Keys.All(p => !IsSweeping(p) && p.SweepProgress >= 0.999));
        foreach (var plot in tracked.Keys)
        {
            Assert.Equal(1d, plot.SweepProgress);
            Assert.Null(plot.Clip);
        }
    }

    private static double CenterY(FrameworkElement element, UIElement root)
    {
        var topLeft = element.TranslatePoint(new Point(), root);
        return topLeft.Y + element.ActualHeight / 2d;
    }

    private static void AssertVerticalCenter(FrameworkElement inner, FrameworkElement outer, double maxDelta)
    {
        var innerTop = inner.TranslatePoint(new Point(), outer).Y;
        var topGap = innerTop;
        var bottomGap = outer.ActualHeight - (innerTop + inner.ActualHeight);
        Assert.InRange(Math.Abs(topGap - bottomGap), 0, maxDelta);
    }

    private static void AssertIsRed(Color color)
    {
        Assert.True(color.R > 150 && color.R > color.G * 2 && color.R > color.B * 2,
            $"Expected red interaction color, got #{color.R:X2}{color.G:X2}{color.B:X2}.");
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

    private static int CountDiffPixels(byte[] a, byte[] b, int width, int height, double x0, double x1, double y0, double y1)
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
                if (Math.Abs(a[i] - b[i]) > 6 || Math.Abs(a[i + 1] - b[i + 1]) > 6 || Math.Abs(a[i + 2] - b[i + 2]) > 6)
                {
                    count++;
                }
            }
        }
        return count;
    }

    private static int CountBrightPixels(byte[] pixels, int width, int height, double x0, double x1, double y0, double y1)
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
                if (pixels[i + 2] > 170 && pixels[i + 1] > 170 && pixels[i] > 170)
                {
                    count++;
                }
            }
        }
        return count;
    }

    private static double ContrastRatio(Color a, Color b)
    {
        static double Luminance(Color c)
        {
            static double Chan(byte v) { var s = v / 255d; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Chan(c.R) + 0.7152 * Chan(c.G) + 0.0722 * Chan(c.B);
        }
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
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
                var blue = pixels[i];
                var green = pixels[i + 1];
                var red = pixels[i + 2];
                if (red > 150 && red > green * 2 && blue < 100)
                {
                    count++;
                }
            }
        }
        return count;
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject node)
    {
        while (VisualTreeHelper.GetParent(node) is { } parent)
        {
            yield return parent;
            node = parent;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class NeverRemote : IRemoteHostReader
    {
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Remote hosts are disabled in R126 offscreen tests.");
    }
}
