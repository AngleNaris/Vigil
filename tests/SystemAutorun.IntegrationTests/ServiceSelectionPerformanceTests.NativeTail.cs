using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.IntegrationTests;

public sealed partial class ServiceSelectionPerformanceTests
{
    private sealed record NativeTailTrace(string Phase, string Target, long AtTicks, double DurationMs);
    private sealed record NativeThreadCpu(int Id, long? StartUtcTicks, double? CpuMs, string? Error);
    private sealed record NativeCpuSample(long ReadStarted, long ReadEnded, double CpuMs, uint ReaderThread, NativeThreadCpu[] Threads);

    private static async Task MeasureNativeDenseTail(MainWindowViewModel model, SystemAutorun.App.MainWindow main,
        ResourceWindowSource source, string directory)
    {
        await Seed(model, source);
        main.Title = "SystemAutorun · R121 isolated dense tail";
        main.Left = 30; main.Top = 30; main.Width = 1152; main.Height = 648;
        var detail = (FrameworkElement)main.ServiceCpuPlot.Parent;
        while (detail.Parent is FrameworkElement parent && SurfaceMotion.GetTransitionKey(detail) is null) { detail = parent; }
        Assert.NotNull(SurfaceMotion.GetTransitionKey(detail));
        detail.Name = "R121NativeSelectedDetail"; // Test-only target identity; no product name/style change.
        var hwnd = new WindowInteropHelper(main).Handle; var uiThread = GetCurrentThreadId();
        using var process = Process.GetCurrentProcess();
        var identity = new { pid = process.Id, processStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            hwnd = hwnd.ToInt64(), title = main.Title, uiThread, frequency = Stopwatch.Frequency };
        foreach (var light in new[] { false, true })
        {
            ThemeService.Apply(light ? ThemePreference.Light : ThemePreference.Dark, persist: null);
            await SetHistory(model, source, MonitoringHistoryProjection.MaximumIntervals);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(1800);
            foreach (var replyDelay in new[] { 650, 850 })
            foreach (var pauseInput in new[] { false, true })
            {
                Assert.True(SystemParameters.ClientAreaAnimation, "Native Stop evidence requires enabled client animations.");
                Assert.True(NativeTailVisible(hwnd)); Assert.False(LineMotion.IsPlaying(detail));
                await PresenceMotionTests.Until(() => DataChangePulse.ActiveClocks == 0);
                ConfigureHistoryReader(source, MonitoringHistoryProjection.MaximumIntervals, replyDelay);
                var priorObserver = MotionTiming.Observer; Assert.Null(priorObserver);
                var traces = new List<NativeTailTrace>(); var updates = new List<long>();
                var cpu = new List<NativeCpuSample> { await Task.Run(ReadNativeOwnCpu) };
                var started = Stopwatch.GetTimestamp(); var foregroundAtStart = NativeTailForeground().ToInt64();
                long ended = 0; long? pausedAt = null;
                var oldIndex = main.ServiceList.SelectedIndex; Assert.InRange(oldIndex, 0, 1);
                var mode = pauseInput ? "paused-input" : "updating"; var theme = light ? "light" : "dark";
                var id = $"{theme}-{replyDelay}-{mode}";
                Action<string, string, double> observer = (phase, target, milliseconds) =>
                { if (target == detail.Name) { traces.Add(new(phase, target, Stopwatch.GetTimestamp(), milliseconds)); } };
                // Independent cancellation also releases the observer if an STA continuation cannot resume.
                using var stopSampling = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var observerGuard = stopSampling.Token.Register(() =>
                { if (ReferenceEquals(MotionTiming.Observer, observer)) { MotionTiming.Observer = priorObserver; } });
                var sampling = Task.CompletedTask;
                try
                {
                    Assert.False(stopSampling.IsCancellationRequested);
                    MotionTiming.Observer = observer;
                    sampling = Task.Run(async () =>
                    {
                        while (true)
                        {
                            try { await Task.Delay(100, stopSampling.Token); }
                            catch (OperationCanceledException) when (stopSampling.IsCancellationRequested) { break; }
                            cpu.Add(ReadNativeOwnCpu());
                        }
                    });
                    main.ServiceList.SelectedIndex = oldIndex == 0 ? 1 : 0;
                    Assert.Same(main.ServiceList.SelectedItem, model.SelectedTask);
                    SetCurrent(model, source); updates.Add(Stopwatch.GetTimestamp());
                    var lastSample = updates[^1]; var explicitSamples = 0;
                    while (true)
                    {
                        if ((!pauseInput || explicitSamples == 0) && Stopwatch.GetElapsedTime(lastSample).TotalMilliseconds >= 1000)
                        {
                            SetCurrent(model, source); updates.Add(Stopwatch.GetTimestamp());
                            explicitSamples++; lastSample = updates[^1];
                            if (pauseInput) { model.AutoRefresh = false; pausedAt = Stopwatch.GetTimestamp(); }
                        }
                        var now = Stopwatch.GetTimestamp();
                        Assert.True(Stopwatch.GetElapsedTime(started, now).TotalSeconds < 12, "Native tail exceeded its bounded observation.");
                        var lastStop = traces.Where(item => item.Phase == "entrance.dispose").Select(item => item.AtTicks).DefaultIfEmpty(started).Max();
                        var edge = Math.Max(lastStop, pausedAt ?? started);
                        if (Stopwatch.GetElapsedTime(started, now).TotalMilliseconds >= 4500 && updates.Count >= (pauseInput ? 2 : 5)
                            && model.DashboardHistoryTask.IsCompleted && model.ResourceHistoryTask.IsCompleted
                            && Stopwatch.GetElapsedTime(edge, now).TotalMilliseconds >= 1500) { break; }
                        await Task.Delay(30);
                    }
                    await model.DashboardHistoryTask; await model.ResourceHistoryTask;
                    ended = Stopwatch.GetTimestamp();
                }
                finally
                {
                    MotionTiming.Observer = priorObserver;
                    stopSampling.Cancel(); await sampling;
                    cpu.Add(await Task.Run(ReadNativeOwnCpu));
                    // Keep raw failure evidence before the post-capture validity assertions below.
                    await WriteNativeTailJson(Path.Combine(directory, id + ".json"), new
                    {
                        scope = "Own native HWND; synthetic adapters and programmatic ListBox binding; includes observer/sampler and SetCurrent false/true refresh-toggle costs, no production Agent/workload, stack/presentation/FPS or root-cause claim",
                        identity, theme, replyDelayMs = replyDelay, mode, started, ended, pausedAt, updates,
                        renderingObserverInstalled = false, autoRefresh = model.AutoRefresh,
                        dashboardClockStillRunning = ((DispatcherTimer)typeof(SystemAutorun.App.MainWindow).GetField("_dashboardClock", Private)!.GetValue(main)!).IsEnabled,
                        selectedBefore = oldIndex, selectedAfter = main.ServiceList.SelectedIndex, selectedTask = model.SelectedTask!.Name,
                        intervals = main.ServiceHealthPlot.Intervals!.Count, cpuPoints = main.ServiceCpuPlot.Points!.Count,
                        detailTarget = detail.Name, detailPlaying = LineMotion.IsPlaying(detail), activeClocks = DataChangePulse.ActiveClocks,
                        width = main.ActualWidth, height = main.ActualHeight, dpi = VisualTreeHelper.GetDpi(main).PixelsPerDip,
                        foregroundAtStart, foregroundAtEnd = NativeTailForeground().ToInt64(), nativeVisible = NativeTailVisible(hwnd),
                        threadLimits = "All enumerated threads retained including failures. Observed births/deaths only; between-snapshot short-lived threads and unreadable counters remain unaccounted. No render-thread naming; each counter lies within its batch read interval.",
                        traces, cpu
                    });
                }
                var resumed = traces.Last(item => item.Phase == "entrance.resume").AtTicks;
                var disposed = traces.Last(item => item.Phase == "entrance.dispose").AtTicks;
                Assert.True(disposed >= resumed); Assert.False(LineMotion.IsPlaying(detail));
                Assert.Equal(MonitoringHistoryProjection.MaximumIntervals, main.ServiceHealthPlot.Intervals!.Count);
                Assert.NotEmpty(main.ServiceCpuPlot.Points!); Assert.True(NativeTailVisible(hwnd));
                Assert.Equal(!pauseInput, model.AutoRefresh); Assert.Equal(pauseInput, pausedAt.HasValue);
                var tailEdge = Math.Max(disposed, pausedAt ?? disposed);
                var usable = cpu.Where(sample => sample.ReadStarted >= tailEdge && sample.ReadEnded <= ended).ToArray();
                Assert.True(usable.Length >= 2);
                var first = usable[0]; var last = usable[^1];
                var minimumSpan = Stopwatch.GetElapsedTime(first.ReadEnded, last.ReadStarted).TotalMilliseconds;
                Assert.True(minimumSpan >= 1000, "Conservative post-dispose/post-pause CPU sample span must cover a full second.");
                var firstUi = Assert.Single(first.Threads, row => row.Id == uiThread && row.StartUtcTicks is not null && row.CpuMs is not null);
                var lastUi = Assert.Single(last.Threads, row => row.Id == uiThread && row.StartUtcTicks == firstUi.StartUtcTicks && row.CpuMs is not null);
                Assert.True(lastUi.CpuMs >= firstUi.CpuMs);
                await WriteNativeTailJson(Path.Combine(directory, id + "-validated.json"), new
                { resumed, disposed, tailEdge, firstReadEnded = first.ReadEnded, lastReadStarted = last.ReadStarted,
                    minimumCpuSpanMs = minimumSpan, matchedUiThread = firstUi.Id, matchedUiStart = firstUi.StartUtcTicks,
                    uiCpuMs = lastUi.CpuMs - firstUi.CpuMs, afterDisposeMs = Stopwatch.GetElapsedTime(disposed, ended).TotalMilliseconds });
                model.AutoRefresh = false; await Task.Delay(900);
            }
            var suffix = light ? "light" : "dark"; var captured = Path.Combine(directory, $"captured-{suffix}.txt");
            Assert.False(File.Exists(captured));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(100);
            await WriteNativeTailJson(Path.Combine(directory, $"ready-{suffix}.json"), new
            { identity, captured, deadline, theme = suffix, intervals = main.ServiceHealthPlot.Intervals!.Count,
                width = main.ActualWidth, height = main.ActualHeight, dpi = VisualTreeHelper.GetDpi(main).PixelsPerDip,
                nativeVisible = NativeTailVisible(hwnd), timingAlreadyEnded = true });
            while (!File.Exists(captured) && DateTimeOffset.UtcNow < deadline) { await Task.Delay(100); }
            Assert.True(File.Exists(captured), "Native pixels were not acknowledged before the bounded capture deadline.");
        }
    }

    private static NativeCpuSample ReadNativeOwnCpu()
    {
        var began = Stopwatch.GetTimestamp(); var reader = GetCurrentThreadId();
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var rows = new List<NativeThreadCpu>();
        foreach (ProcessThread thread in process.Threads)
        {
            using (thread)
            {
                var id = thread.Id;
                try { rows.Add(new(id, thread.StartTime.ToUniversalTime().Ticks, thread.TotalProcessorTime.TotalMilliseconds, null)); }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                { rows.Add(new(id, null, null, exception.GetType().Name)); }
            }
        }
        var total = process.TotalProcessorTime.TotalMilliseconds;
        return new(began, Stopwatch.GetTimestamp(), total, reader, rows.ToArray());
    }

    private static async Task WriteNativeTailJson(string path, object value)
    {
        var staged = path + ".pending";
        Assert.False(File.Exists(path)); Assert.False(File.Exists(staged));
        await File.WriteAllTextAsync(staged, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(staged, path);
    }

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeTailVisible(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint NativeTailForeground();
}
