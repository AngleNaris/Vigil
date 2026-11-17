using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Models;
using Button = System.Windows.Controls.Button;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class WindowMotionPerformanceTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task MeasureRealWindowPageAndToolPlaybackWithoutReducingItsStages(bool light) => FakeUiMotionTests.WithMain(light, async (model, main) =>
    {
        AnimationRegressionTests.Seed(model); AnimationRegressionTests.Logs(model);
        await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(main.MainScene));
        var samples = new List<object>();
        for (var round = 0; round < 3; round++)
        {
            await Measure("host", round, async () =>
            {
                main.HostsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await PresenceMotionTests.Until(() => main.MonitoringTabs.HostPage.IsHitTestVisible && !LineMotion.IsPlaying(main.MonitoringTabs.HostPage));
            });
            await Measure("services", round, async () =>
            {
                main.ServicesTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await PresenceMotionTests.Until(() => main.ServicesPage.IsHitTestVisible && !LineMotion.IsPlaying(main.ServicesPage));
            });
            await Measure("tool-open-close", round, async () =>
            {
                var tool = new DoctorWindow(new DoctorReport([new("Synthetic performance fixture", true, "No real diagnosis or workload action.")]))
                { Owner = main, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
                try
                {
                    tool.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    await PresenceMotionTests.Until(() => !LineMotion.IsPlaying((FrameworkElement)tool.Content) && !tool.HasAnimatedProperties);
                    tool.Close(); await tool.ClosureTask;
                    await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(main.ServicesPage));
                }
                finally { if (!tool.ClosureTask.IsCompleted) { ColorStateAssist.SetReduceMotion(tool, true); tool.Close(); } }
            });
        }
        if (Environment.GetEnvironmentVariable("SA_WINDOW_PERFORMANCE_EVIDENCE") is { Length: > 0 } directory)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, $"window-performance-{(light ? "light" : "dark")}.json"), JsonSerializer.Serialize(new
            {
                mode = "Real offscreen HWND/dispatcher playback with synthetic data; rendering callbacks are not presented desktop FPS or GPU cost",
                animationsEnabled = SystemParameters.ClientAreaAnimation, cacheLimit = LineMotion.MaximumCachedFrameSets, samples,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        async Task Measure(string operation, int round, Func<Task> action)
        {
            var intervals = new List<double>();
            var watch = Stopwatch.StartNew(); double last = 0;
            void Rendered(object? sender, EventArgs args) { var at = watch.Elapsed.TotalMilliseconds; if (last > 0) { intervals.Add(at - last); } last = at; }
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime; var allocated = GC.GetTotalAllocatedBytes();
            CompositionTarget.Rendering += Rendered;
            try { await action(); }
            finally { CompositionTarget.Rendering -= Rendered; }
            process.Refresh();
            samples.Add(new { operation, round, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                processCpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                allocatedBytes = GC.GetTotalAllocatedBytes() - allocated, workingSetBytes = process.WorkingSet64,
                cachedFrameSets = LineMotion.CachedFrameSetCount, renderingIntervalsMilliseconds = intervals });
        }
    }, TimeSpan.FromMinutes(2));
}
