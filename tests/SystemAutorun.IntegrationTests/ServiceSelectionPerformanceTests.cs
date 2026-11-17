using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed partial class ServiceSelectionPerformanceTests
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed record Trace(string Phase, string Target, double AtMs, double DurationMs);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task MeasureSelectionThroughNonemptyHistoryAndPostCompletionTicks(bool light) => WithSource(light, async (model, main, source) =>
    {
        await Seed(model, source);
        await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(main.MainScene));
        await Task.Delay(100);
        var directory = Environment.GetEnvironmentVariable("SA_SELECTION_EVIDENCE");
        var rounds = string.IsNullOrEmpty(directory) ? 1 : 3;
        var samples = new List<object>();
        var detail = (FrameworkElement)main.ServiceCpuPlot.Parent;
        while (detail.Parent is FrameworkElement parent && SurfaceMotion.GetTransitionKey(detail) is null) { detail = parent; }
        Assert.NotNull(SurfaceMotion.GetTransitionKey(detail));
        foreach (var intervals in new[] { 1, 120, MonitoringHistoryProjection.MaximumIntervals })
        foreach (var delay in new[] { 650, 850 })
        for (var round = 0; round < rounds; round++)
        {
            ConfigureHistoryReader(source, intervals, delay);
            var traces = new List<Trace>();
            var watch = Stopwatch.StartNew();
            var operations = new Dictionary<DispatcherOperation, long>();
            var layoutPasses = 0;
            double tailMs = 0, tailCpuMs = 0, tailAllocated = 0, settleWaitMs = 0; int tailLayouts = 0, clocksAfter700 = 0;
            TimeSpan lastRendering = TimeSpan.MinValue;
            void Timed(string phase, string target, double milliseconds) => traces.Add(new(phase, target, watch.Elapsed.TotalMilliseconds, milliseconds));
            void Started(object? sender, DispatcherHookEventArgs args)
            {
                if (args.Operation.Priority is DispatcherPriority.Render or DispatcherPriority.DataBind)
                { operations[args.Operation] = Stopwatch.GetTimestamp(); }
            }
            void Completed(object? sender, DispatcherHookEventArgs args)
            {
                if (operations.Remove(args.Operation, out var start))
                { Timed($"dispatcher.{args.Operation.Priority}", "", Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
            }
            void Rendered(object? sender, EventArgs args)
            {
                var rendering = ((RenderingEventArgs)args).RenderingTime;
                if (lastRendering == rendering) { return; }
                lastRendering = rendering;
                Timed("rendering.callback", "", 0);
            }
            void Layout(object? sender, EventArgs args) => layoutPasses++;
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes();
            var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            var hooks = main.Dispatcher.Hooks;
            MotionTiming.Observer = Timed;
            hooks.OperationStarted += Started; hooks.OperationCompleted += Completed;
            CompositionTarget.Rendering += Rendered; detail.LayoutUpdated += Layout;
            try
            {
                // Exercise ListBox selection/two-way binding, not a top-level tab or manual Play call.
                main.ServiceList.SelectedIndex = main.ServiceList.SelectedIndex == 0 ? 1 : 0;
                Assert.Same(main.ServiceList.SelectedItem, model.SelectedTask);
                SetCurrent(model, source);
                async Task Sample()
                { await Task.Delay(1000); SetCurrent(model, source); }
                await Task.WhenAll(model.DashboardHistoryTask, Sample(), Task.Delay(1850));
                await model.DashboardHistoryTask; await model.ResourceHistoryTask;
                // The synthetic reply now travels through the real history adapter/VM path.
                // Stop only this fixture's periodic GUI refresh; the explicit next-second sample
                // is already delivered. Measure the final quiet second independently of dispatch callbacks.
                model.AutoRefresh = false;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(700);
                clocksAfter700 = DataChangePulse.ActiveClocks; var settleStart = Stopwatch.GetTimestamp();
                // A fixed wall delay does not prove an offscreen WPF clock completed. Wait for
                // actual release without stopping it, then retain the independent quiet-second assertion.
                await PresenceMotionTests.Until(() => DataChangePulse.ActiveClocks == 0 && !LineMotion.IsPlaying(detail));
                settleWaitMs = Stopwatch.GetElapsedTime(settleStart).TotalMilliseconds;
                Assert.Equal(0, DataChangePulse.ActiveClocks); Assert.False(LineMotion.IsPlaying(detail));
                var tailStart = watch.Elapsed.TotalMilliseconds; var tailCpu = process.TotalProcessorTime;
                var tailBytes = GC.GetTotalAllocatedBytes(); var tailLayoutStart = layoutPasses;
                await Task.Delay(1100);
                tailMs = watch.Elapsed.TotalMilliseconds - tailStart;
                tailCpuMs = (process.TotalProcessorTime - tailCpu).TotalMilliseconds;
                tailAllocated = GC.GetTotalAllocatedBytes() - tailBytes; tailLayouts = layoutPasses - tailLayoutStart;
                Assert.True(tailMs >= 1000); Assert.Equal(0, DataChangePulse.ActiveClocks);
                Assert.False(LineMotion.IsPlaying(detail));
                Assert.Equal(intervals, main.ServiceHealthPlot.Intervals!.Count);
                Assert.True(main.ServiceCpuPlot.Points?.Count > 0,
                    $"fixture selected={model.SelectedTask?.Name}; local={model.IsLocalHostSelected}; active={model.IsPresentationActive}; modelPoints={model.DashboardResourceCpu.Count}; key={model.DashboardResourceScaleKey}; status={model.ManagedProcessStatusText}; rows={string.Join(';', model.ManagedProcesses.Select(row => row.Snapshot.TaskName + ':' + row.Snapshot.TaskIdentity))}; expected={MonitoringTaskIdentity.For(model.SelectedTask!.Snapshot)}");
                Assert.All(main.ServiceCpuPlot.Points!.Where(point => point.Value is not null), point => Assert.Equal(.04, point.Value));
                Assert.Contains(main.ServiceCpuPlot.Points!, point => point.BreakBefore);
                if (SystemParameters.ClientAreaAnimation)
                { Assert.Contains(traces, item => item.Phase == "entrance.resume"); Assert.Contains(traces, item => item.Phase == "entrance.dispose"); }
            }
            finally
            {
                MotionTiming.Observer = null;
                hooks.OperationStarted -= Started; hooks.OperationCompleted -= Completed;
                CompositionTarget.Rendering -= Rendered; detail.LayoutUpdated -= Layout;
            }
            process.Refresh();
            var stoppedAt = traces.Where(item => item.Phase == "entrance.dispose").Select(item => item.AtMs).DefaultIfEmpty(0).Max();
            samples.Add(new { round, intervals, historyBuckets = 120, replyDelayMs = delay, layoutPasses,
                tailMs, tailCpuMs, tailAllocatedBytes = tailAllocated, tailLayouts, settleWaitMs, clocksAfter700,
                observedMs = watch.Elapsed.TotalMilliseconds, lastEntranceDisposeMs = stoppedAt,
                afterLastEntranceDisposeMs = watch.Elapsed.TotalMilliseconds - stoppedAt, activePulseClocks = DataChangePulse.ActiveClocks,
                cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                collections = Enumerable.Range(0, 3).Select(index => GC.CollectionCount(index) - collections[index]).ToArray(),
                liveCpuPoints = main.ServiceCpuPlot.Points!.Count, liveMemoryPoints = main.ServiceMemoryPlot.Points!.Count,
                firstCpu = main.ServiceCpuPlot.Points![0].Value, lastCpu = main.ServiceCpuPlot.Points![^1].Value, traces });
        }
        if (!string.IsNullOrEmpty(directory))
        {
            Assert.True(Directory.Exists(directory));
            await File.WriteAllTextAsync(Path.Combine(directory, $"selection-{(light ? "light" : "dark")}.json"), JsonSerializer.Serialize(new
            {
                mode = "Offscreen real HWND/dispatcher; synthetic constant live points and explicit nonempty history; UI callbacks are not presented FPS",
                animationsEnabled = SystemParameters.ClientAreaAnimation, width = main.ActualWidth, height = main.ActualHeight,
                dpi = VisualTreeHelper.GetDpi(main).PixelsPerDip, samples,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }, TimeSpan.FromMinutes(4));

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task DenseSettledTailDistinguishesRenderingObserverCost(bool light) => WithSource(light, async (model, main, source) =>
    {
        var output = Environment.GetEnvironmentVariable("SA_SELECTION_OBSERVER_EVIDENCE");
        if (string.IsNullOrEmpty(output)) { return; }
        Assert.True(Directory.Exists(output)); await Seed(model, source);
        await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(main.MainScene));
        await SetHistory(model, source, MonitoringHistoryProjection.MaximumIntervals);
        model.AutoRefresh = false; await Task.Delay(800);
        await PresenceMotionTests.Until(() => DataChangePulse.ActiveClocks == 0);
        await Task.Delay(200); Assert.Equal(0, DataChangePulse.ActiveClocks);
        var samples = new List<object>(); using var process = Process.GetCurrentProcess();
        foreach (var observed in new[] { true, false, true, false })
        {
            var callbacks = 0;
            void Rendered(object? sender, EventArgs args) => callbacks++;
            if (observed) { CompositionTarget.Rendering += Rendered; }
            try
            {
                await Task.Delay(150);
                var cpu = process.TotalProcessorTime; var bytes = GC.GetTotalAllocatedBytes(); var watch = Stopwatch.StartNew();
                await Task.Delay(1100);
                samples.Add(new { observed, milliseconds = watch.Elapsed.TotalMilliseconds,
                    cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                    allocatedBytes = GC.GetTotalAllocatedBytes() - bytes, callbacks, activePulseClocks = DataChangePulse.ActiveClocks });
            }
            finally { if (observed) { CompositionTarget.Rendering -= Rendered; } }
        }
        await File.WriteAllTextAsync(Path.Combine(output, $"observer-{(light ? "light" : "dark")}.json"), JsonSerializer.Serialize(new
        {
            scope = "Offscreen synthetic 8192-interval settled main window; A/B observer diagnostic, no frame-rate claim",
            renderTier = RenderCapability.Tier >> 16, renderMode = RenderOptions.ProcessRenderMode.ToString(), samples,
        }, new JsonSerializerOptions { WriteIndented = true }));
    });

    [Fact]
    public Task NativeSelectionProbeIsExplicitAndNeverConnectsToProduction() => WithSource(false, async (model, main, source) =>
    {
        var directory = Environment.GetEnvironmentVariable("SA_SELECTION_PROBE");
        if (string.IsNullOrEmpty(directory)) { return; }
        Assert.True(Directory.Exists(directory));
        if (Environment.GetEnvironmentVariable("SA_SELECTION_NATIVE_TAIL_PROBE") == "1")
        { await MeasureNativeDenseTail(model, main, source, directory); return; }
        await Seed(model, source); await SetHistory(model, source, 8192);
        main.Title = "SystemAutorun · R121 isolated synthetic selection";
        main.Left = 30; main.Top = 30; main.Width = 1152; main.Height = 648;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var finish = Path.Combine(directory, "finish.txt");
        var command = Path.Combine(directory, "command.json");
        var until = DateTimeOffset.UtcNow.AddMinutes(14);
        await File.WriteAllTextAsync(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(new
        { pid = Environment.ProcessId, hwnd = new WindowInteropHelper(main).Handle.ToInt64(), dpi = VisualTreeHelper.GetDpi(main).PixelsPerDip,
            finish, command, until }));
        string? previous = null;
        var lastSelection = model.SelectedTask;
        var selectedAt = DateTimeOffset.UtcNow;
        var sampledAt = selectedAt;
        var published = true;
        while (!File.Exists(finish) && DateTimeOffset.UtcNow < until)
        {
            if (!ReferenceEquals(lastSelection, model.SelectedTask))
            { lastSelection = model.SelectedTask; selectedAt = DateTimeOffset.UtcNow; published = false; SetCurrent(model, source); }
            if (!published && DateTimeOffset.UtcNow - selectedAt >= TimeSpan.FromMilliseconds(650))
            { await SetHistory(model, source, 8192); published = true; }
            if (DateTimeOffset.UtcNow - sampledAt >= TimeSpan.FromSeconds(1))
            { SetCurrent(model, source); sampledAt = DateTimeOffset.UtcNow; }
            if (File.Exists(command))
            {
                var content = await File.ReadAllTextAsync(command);
                if (content != previous)
                {
                    previous = content;
                    using var document = JsonDocument.Parse(content);
                    var root = document.RootElement;
                    if (root.TryGetProperty("light", out var light)) { ThemeService.Apply(light.GetBoolean() ? ThemePreference.Light : ThemePreference.Dark, persist: null); }
                    if (root.TryGetProperty("width", out var width)) { main.Width = width.GetDouble(); }
                    if (root.TryGetProperty("height", out var height)) { main.Height = height.GetDouble(); }
                }
            }
            await Task.Delay(30);
        }
        Assert.True(File.Exists(finish), "Explicit native probe was not handed its finish file.");
    }, TimeSpan.FromMinutes(16));

    private static Task WithSource(bool light, Func<MainWindowViewModel, SystemAutorun.App.MainWindow, ResourceWindowSource, Task> action, TimeSpan? timeout = null)
    {
        ResourceWindowSource? source = null;
        return FakeUiMotionTests.WithMain(light, (model, main) => action(model, main, source!), timeout,
            (fixture, configuration) => source = new ResourceWindowSource(fixture, configuration));
    }

    private static async Task Seed(MainWindowViewModel model, ResourceWindowSource source)
    {
        CompactSidebarTests.Seed(model,
            [CompactSidebarTests.Snapshot("R121 合成服务 A"), CompactSidebarTests.Snapshot("R121 合成服务 B")]);
        Assert.All(model.Tasks, task => Assert.True(task.Enabled));
        model.CurrentPage = MainWindowViewModel.WorkloadsPage;
        await source.ConfigureAsync(model);
        var now = DateTimeOffset.UtcNow;
        for (var second = -300; second <= 0; second++)
        {
            if (second is >= -220 and <= -211) { continue; }
            var at = now.AddSeconds(second);
            source.Observe(at, model.Tasks.Select(task => Sample(task, at)).ToArray(), ResourceWindowSource.SyntheticHost(at, 0));
        }
        SetCurrent(model, source); await model.ResourceHistoryTask;
        await SetHistory(model, source, 120);
    }

    private static ManagedProcessSnapshot Sample(TaskRowViewModel task, DateTimeOffset at) => new(task.Name, "R121 synthetic constant observations", task.Type,
        true, true, true, "OK", "fixture", "No process or workload is started", at,
        [new(987654, "never-started-r120", null, DateTimeOffset.UnixEpoch.AddDays(1), .04, 80L << 20, 80L << 20, 2, true)],
        TaskIdentity: MonitoringTaskIdentity.For(task.Snapshot));

    private static void SetCurrent(MainWindowViewModel model, ResourceWindowSource source)
    {
        model.AutoRefresh = false;
        var at = DateTimeOffset.UtcNow;
        source.Observe(at, model.Tasks.Select(task => Sample(task, at)).ToArray(), ResourceWindowSource.SyntheticHost(at, 0));
        source.Apply(model); model.AutoRefresh = true;
    }

    private static Task SetHistory(MainWindowViewModel model, ResourceWindowSource source, int count)
    {
        ConfigureHistoryReader(source, count, 0);
        return (Task)typeof(MainWindowViewModel).GetMethod("QueueDashboardHistoryAsync", Private)!.Invoke(model, [false])!;
    }

    private static void ConfigureHistoryReader(ResourceWindowSource source, int count, int delay) => source.HistoryReader = async (query, cancellation) =>
    {
        if (delay > 0) { await Task.Delay(delay, cancellation); }
        return History(query, count);
    };

    private static MonitoringHistoryResult History(MonitoringHistoryQuery query, int count)
    {
        var end = query.To; var from = query.From;
        var step = (end - from).Ticks / count;
        var gap = count == 1 ? 0 : Math.Min(TimeSpan.FromMilliseconds(300).Ticks, step / 4);
        var intervals = Enumerable.Range(0, count).Select(index => new HealthHistoryInterval(from.AddTicks(index * step),
            from.AddTicks((index + 1) * step - gap), ObservationHealth.Healthy)).ToArray();
        var bucketTicks = (end - from).Ticks / 120;
        var buckets = Enumerable.Range(0, 120).Select(index => new MonitoringHistoryBucket(from.AddTicks(index * bucketTicks), from.AddTicks((index + 1) * bucketTicks),
            700, 0, 20, 100, 97, .04, .25)).ToArray();
        return new MonitoringHistoryResult(1, query.TaskName, query.TaskIdentity, from, end, end, from,
            84000, 0, 2400, 100, 97, intervals, buckets, false, "R121 synthetic replay; not a production history response");
    }
}
