using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Models;
using Button = System.Windows.Controls.Button;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class ResourceDashboardWindowTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task HeaderAndActualSelectorsStayOnOneRowAcrossDesktopSizes(bool light) => WithResourceWindow(light, async (model, main, _) =>
    {
        ColorStateAssist.SetReduceMotion(main, true);
        foreach (var (width, height) in new[] { (920, 580), (1152, 648), (1320, 820) })
        {
            main.Width = width; main.Height = height;
            await Task.Delay(650); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
            foreach (var control in new FrameworkElement[] { main.HealthWindowControl, main.ServiceActions })
            {
                Assert.Equal(32, control.ActualHeight);
                Assert.InRange(control.TranslatePoint(new Point(), main.ServiceHeader).Y, -.1, .1);
                Assert.True(control.TranslatePoint(new Point(control.ActualWidth, 0), main.ServiceHeader).X <= main.ServiceHeader.ActualWidth + .1);
            }
            Assert.True(main.ServiceTitle.ActualWidth > 30); Assert.Equal(32, main.ServiceUptime.ActualHeight);
            Assert.True(main.ServiceUptime.TranslatePoint(new Point(), main).Y >= main.ServiceHeader.TranslatePoint(new Point(0, main.ServiceHeader.ActualHeight), main).Y);
            Assert.True(main.ServiceUptime.TranslatePoint(new Point(), main).Y < main.ServiceHealthPlot.TranslatePoint(new Point(), main).Y);
            Assert.Equal(TimeSpan.FromMinutes(5), main.ServiceCpuPlot.WindowDuration); Assert.True(main.ServiceMemoryPlot.Bytes);
            Assert.Contains(main.ServiceCpuPlot.Points!, point => point.Value is > 0);
            StartupGuiWindowTests.CaptureStateEvidence(main, $"r121-{(light ? "light" : "dark")}-{width}x{height}.png");
        }
        Assert.Null(main.FindName("ResourceModeButton"));
        await Choose(main.HealthWindowButton, "168");
        Assert.True(model.DashboardHistoryMode); Assert.True(main.ServiceMemoryPlot.Bytes);
        Assert.Contains(main.ServiceMemoryPlot.Points!, point => point.Value is > 1_000_000);
        Assert.Equal(168, model.DashboardWindowHours); Assert.Equal(TimeSpan.FromDays(7), main.ServiceCpuPlot.WindowDuration);
        await Choose(main.HealthWindowButton, "0");
        Assert.False(model.DashboardHistoryMode); Assert.Equal(TimeSpan.FromMinutes(5), main.ServiceCpuPlot.WindowDuration);
        Assert.Equal(0, main.ServiceHealthPlot.WindowHours); Assert.Equal(TimeSpan.FromMinutes(5), model.DashboardHealthDuration);
        model.SelectedTask = new TaskRowViewModel(model.SelectedTask!.Snapshot with { Name = new string('长', 100) });
        main.Width = 920; main.Height = 580; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
        Assert.True(main.ServiceTitle.ActualWidth > 30); Assert.Equal(TextTrimming.CharacterEllipsis, main.ServiceTitle.TextTrimming);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task HostFourPlotsShareControlsAndKeepBothRateDirections(bool light) => WithResourceWindow(light, async (model, main, _) =>
    {
        ColorStateAssist.SetReduceMotion(main, true); model.CurrentPage = MainWindowViewModel.OverviewPage;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var charts = main.MonitoringTabs.HostCharts;
        foreach (var plot in new[] { charts.HostCpuPlot, charts.HostMemoryPlot, charts.HostDiskPlot, charts.HostNetworkPlot })
        { Assert.Contains(plot.Points!, point => point.Value is > 0); Assert.Equal(TimeSpan.FromMinutes(5), plot.WindowDuration); }
        Assert.True(charts.HostMemoryPlot.Bytes); Assert.True(charts.HostDiskPlot.Rate); Assert.True(charts.HostNetworkPlot.Rate);
        Assert.Contains(charts.HostDiskPlot.SecondaryPoints!, point => point.Value is > 0);
        Assert.Contains(charts.HostNetworkPlot.SecondaryPoints!, point => point.Value is > 0);
        Assert.Null(charts.FindName("HostModeButton"));
        foreach (var hours in new[] { 0, 24, 168, 720 })
        {
            await Choose(charts.HostWindowButton, hours.ToString());
            Assert.Equal(hours != 0, model.DashboardHistoryMode);
            foreach (var plot in new[] { charts.HostCpuPlot, charts.HostMemoryPlot, charts.HostDiskPlot, charts.HostNetworkPlot })
            {
                Assert.Equal(hours == 0 ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(hours), plot.WindowDuration);
                Assert.Equal(model.DashboardResourceEnd, plot.EndAt);
                if (hours != 0) { Assert.Equal(301, plot.Points!.Count); }
            }
            foreach (var (width, height) in new[] { (920, 580), (1152, 648), (1320, 820) })
            {
                main.Width = width; main.Height = height; charts.HostNetworkPlot.BringIntoView();
                await Task.Delay(650); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
                Assert.True(charts.HostNetworkPlot.ActualWidth >= 200); Assert.InRange(charts.HostNetworkPlot.ActualHeight, 117.5, 118.5);
                StartupGuiWindowTests.CaptureStateEvidence(main, $"r125-host-{hours}-{(light ? "light" : "dark")}-{width}x{height}.png");
            }
        }
        Assert.Single(model.Drives); Assert.Single(model.Networks);
        await Choose(charts.HostWindowButton, "0"); Assert.False(model.DashboardHistoryMode);
    });

    [Fact]
    public Task NativeResourceProbeIsExplicitAndUsesOnlyOwnedProcessOrSyntheticObservations()
    {
        var directory = Environment.GetEnvironmentVariable("SA_RESOURCE_PROBE");
        if (string.IsNullOrEmpty(directory)) { return Task.CompletedTask; }
        Assert.True(Directory.Exists(directory));
        return WithResourceWindow(Environment.GetEnvironmentVariable("SA_RESOURCE_PROBE_THEME") == "light", async (model, main, source) =>
        {
            var ownProcess = Environment.GetEnvironmentVariable("SA_RESOURCE_PROBE_SOURCE") == "own-process";
            var syntheticHalfSecond = !ownProcess && Environment.GetEnvironmentVariable("SA_RESOURCE_PROBE_HALF") == "1";
            if (syntheticHalfSecond) { source.HalfSecondCapability = true; source.Apply(model); }
            var reader = ownProcess ? new SystemAutorun.Windows.Health.ManagedProcessReader() : null;
            using var hostReader = ownProcess ? new SystemAutorun.Windows.Health.HostStatusReader() : null;
            var phase = 0;
            R121HealthMotionScene? healthScene = null;
            if (ownProcess)
            {
                model.CurrentPage = MainWindowViewModel.ExceptionLogsPage; source.Clear();
                await AddReal(); await Task.Delay(1100); await AddReal();
                model.CurrentPage = MainWindowViewModel.WorkloadsPage;
            }
            main.Title = Environment.GetEnvironmentVariable("SA_RESOURCE_PROBE_TITLE")
                ?? (ownProcess ? "SystemAutorun · R121 isolated own PID + OS host / synthetic health" : "SystemAutorun · R121 synthetic isolated UI");
            main.Left = 20; main.Top = 20; main.Width = 1152; main.Height = 648;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var limit = int.TryParse(Environment.GetEnvironmentVariable("SA_RESOURCE_PROBE_MAX_SECONDS"), out var seconds)
                && seconds is >= 30 and <= 300 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromMinutes(15);
            var deadline = DateTime.UtcNow + limit;
            await File.WriteAllTextAsync(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId, hwnd = new WindowInteropHelper(main).Handle.ToInt64(),
                dpi = VisualTreeHelper.GetDpi(main).PixelsPerInchX, main.Title, deadlineUtc = deadline,
                commandPath = Path.Combine(directory, "command.json"), statePath = Path.Combine(directory, "state.json"),
                finishPath = Path.Combine(directory, "finish.txt"), source = ownProcess ? "own-process-plus-host" : "synthetic-only",
                syntheticSamplingMilliseconds = syntheticHalfSecond ? 500 : 1000
            }));
            long searchKeyAt = 0; EventHandler? searchFrame = null;
            void SearchKeyDown(object sender, System.Windows.Input.KeyEventArgs args)
            {
                if (args.Key is (System.Windows.Input.Key.Back or System.Windows.Input.Key.Delete)
                    && main.SearchBox.Text.Length > 0
                    && (main.SearchBox.Text.Length == 1 || main.SearchBox.SelectionLength == main.SearchBox.Text.Length))
                { searchKeyAt = System.Diagnostics.Stopwatch.GetTimestamp(); }
            }
            void SearchTextChanged(object sender, TextChangedEventArgs args)
            {
                if (searchKeyAt == 0 || main.SearchBox.Text.Length != 0) { return; }
                var began = searchKeyAt; searchKeyAt = 0;
                if (searchFrame is not null) { CompositionTarget.Rendering -= searchFrame; }
                searchFrame = (_, _) =>
                {
                    var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                    var visible = main.ServiceList.Items.Cast<object>().Select(item => main.ServiceList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem)
                        .Count(row => row is not null && ServiceSearchPresentationLatencyTests.HasVisibleText(row, main.ServiceList));
                    var expected = model.TasksView.Cast<object>().Count();
                    if ((visible != expected || expected == 0) && elapsed < 5000) { return; }
                    CompositionTarget.Rendering -= searchFrame; searchFrame = null;
                    File.WriteAllText(Path.Combine(directory, "native-search-timing.json"), JsonSerializer.Serialize(new
                    { elapsedMs = elapsed, expected, visible, textEmpty = main.SearchBox.Text.Length == 0,
                        modelEmpty = string.IsNullOrEmpty(model.SearchText), sampleMethod = "isolated real keyboard to WPF rendering callback; not DWM display timing" }));
                };
                CompositionTarget.Rendering += searchFrame;
            }
            main.SearchBox.PreviewKeyDown += SearchKeyDown; main.SearchBox.TextChanged += SearchTextChanged;
            var sampling = false;
            var timer = new DispatcherTimer(syntheticHalfSecond ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) =>
            {
                if (sampling) { return; }
                sampling = true;
                try
                {
                    if (ownProcess) { await AddReal(); }
                    else { AddSynthetic(model, source, ++phase); }
                }
                finally { sampling = false; }
            }, main.Dispatcher);
            try
            {
                while (!File.Exists(Path.Combine(directory, "finish.txt")) && DateTime.UtcNow < deadline)
                {
                    if (File.Exists(Path.Combine(directory, "command.json")))
                    {
                        using var command = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "command.json")));
                        var value = command.RootElement;
                        if (value.TryGetProperty("light", out var light)) { ThemeService.Apply(light.GetBoolean() ? ThemePreference.Light : ThemePreference.Dark, persist: null); }
                        if (value.TryGetProperty("width", out var width)) { main.Width = width.GetInt32(); main.Height = value.GetProperty("height").GetInt32(); }
                        if (value.TryGetProperty("page", out var page)) { model.CurrentPage = page.GetString()!; }
                        if (value.TryGetProperty("health", out var health))
                        {
                            Assert.False(ownProcess, "Synthetic health commands must not mix with the OS/own-process probe.");
                            healthScene ??= new(model, main, source);
                            await healthScene.CommandAsync(health.GetString()!);
                        }
                        await model.DashboardHistoryTask; await model.ResourceHistoryTask;
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        var focused = System.Windows.Input.Keyboard.FocusedElement as FrameworkElement;
                        await File.WriteAllTextAsync(Path.Combine(directory, "state.json"), JsonSerializer.Serialize(new
                        {
                            id = value.TryGetProperty("id", out var id) ? id.GetString() : null, at = DateTimeOffset.UtcNow,
                            main.ActualWidth, main.ActualHeight, ThemeService.IsLight, model.CurrentPage,
                            model.DashboardWindowHours, model.DashboardHealthWindowLabel, model.DashboardHistoryMode,
                            serviceMenuOpen = main.HealthWindowButton.ContextMenu!.IsOpen,
                            hostMenuOpen = main.MonitoringTabs.HostCharts.HostWindowButton.ContextMenu!.IsOpen,
                            focus = focused is null ? null : new { type = focused.GetType().Name, focused.Name,
                                automationName = System.Windows.Automation.AutomationProperties.GetName(focused),
                                header = (focused as MenuItem)?.Header?.ToString() },
                            health = healthScene?.Snapshot(), interruption = healthScene?.LastInterruption
                        }));
                        // A receipt is complete before removing the request; consumers must match id.
                        File.Delete(Path.Combine(directory, "command.json"));
                    }
                    await Task.Delay(100);
                }
                Assert.True(File.Exists(Path.Combine(directory, "finish.txt")), "Explicit native resource probe did not finish.");
            }
            finally
            {
                timer.Stop();
                main.SearchBox.PreviewKeyDown -= SearchKeyDown; main.SearchBox.TextChanged -= SearchTextChanged;
                if (searchFrame is not null) { CompositionTarget.Rendering -= searchFrame; }
                while (sampling) { await Dispatcher.Yield(DispatcherPriority.Background); }
                healthScene?.Dispose();
            }

            async Task AddReal()
            {
                var definitions = source.Configuration.Configuration.Tasks.Where(task => task.Enabled).ToArray();
                var samples = await reader!.ReadAsync(definitions, new LiveResourceSamplingTests.OwnPidOnly(), new LiveResourceSamplingTests.PassiveFixtureHealth());
                Assert.All(samples, sample => Assert.Equal(Environment.ProcessId, Assert.Single(sample.Processes).ProcessId));
                var host = await hostReader!.ReadAsync(); var at = DateTimeOffset.UtcNow;
                source.Observe(at, samples, host, durable: phase++ % 10 == 0);
                if (model.AutoRefresh && model.IsPresentationActive) { source.Apply(model); }
                await File.AppendAllTextAsync(Path.Combine(directory, "own-process.ndjson"), JsonSerializer.Serialize(new { at, samples, host }) + Environment.NewLine);
            }
        }, TimeSpan.FromMinutes(16));
    }

    internal static Task WithResourceWindow(bool light, Func<MainWindowViewModel, SystemAutorun.App.MainWindow, ResourceWindowSource, Task> action, TimeSpan? timeout = null)
    {
        ResourceWindowSource? source = null;
        return FakeUiMotionTests.WithMain(light, async (model, main) =>
        {
            CompactSidebarTests.Seed(model, [CompactSidebarTests.Snapshot("R121 合成任务 A"), CompactSidebarTests.Snapshot("R121 合成任务 B")]);
            model.CurrentPage = MainWindowViewModel.WorkloadsPage;
            await source!.ConfigureAsync(model);
            var now = DateTimeOffset.UtcNow;
            for (var second = 0; second <= 270; second++)
            {
                if (second is >= 80 and <= 89) { continue; }
                var at = now.AddSeconds(second - 270);
                source.Observe(at, model.Tasks.Select(task => ResourceWindowSource.Synthetic(task, at, second)).ToArray(),
                    ResourceWindowSource.SyntheticHost(at, second), durable: second % 10 == 0);
            }
            source.Apply(model); model.AutoRefresh = true;
            AnimationRegressionTests.Logs(model);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await action(model, main, source);
        }, timeout, (fixture, configuration) => source = new ResourceWindowSource(fixture, configuration));
    }

    private static void AddSynthetic(MainWindowViewModel model, ResourceWindowSource source, int phase)
    {
        var at = DateTimeOffset.UtcNow;
        source.Observe(at, model.Tasks.Select(task => ResourceWindowSource.Synthetic(task, at, phase)).ToArray(),
            ResourceWindowSource.SyntheticHost(at, phase), durable: phase % 10 == 0);
        if (model.AutoRefresh && model.IsPresentationActive) { source.Apply(model); }
    }

    internal static async Task Choose(Button button, string parameter)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var menu = button.ContextMenu!;
        var item = menu.Items.OfType<MenuItem>().Single(row => (string?)row.CommandParameter == parameter);
        var peer = new System.Windows.Automation.Peers.MenuItemAutomationPeer(item);
        ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); menu.IsOpen = false;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
}
