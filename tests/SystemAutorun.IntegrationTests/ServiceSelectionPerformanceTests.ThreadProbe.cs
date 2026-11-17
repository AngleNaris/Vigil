using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.IntegrationTests;

public sealed partial class ServiceSelectionPerformanceTests
{
    [Fact]
    public Task ExplicitDenseSettledThreadProbeUsesOnlyItsOwnProcess() => WithSource(false, async (model, main, source) =>
    {
        var output = Environment.GetEnvironmentVariable("SA_SELECTION_THREAD_EVIDENCE");
        if (string.IsNullOrEmpty(output)) { return; }
        Assert.True(Directory.Exists(output)); await Seed(model, source);
        await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(main.MainScene));
        await SetHistory(model, source, MonitoringHistoryProjection.MaximumIntervals);
        model.AutoRefresh = false; await Task.Delay(800);
        await PresenceMotionTests.Until(() => DataChangePulse.ActiveClocks == 0); await Task.Delay(200);
        var uiThread = GetCurrentThreadId(); var samples = new List<object>();
        using var process = Process.GetCurrentProcess();
        var clock = (DispatcherTimer)typeof(SystemAutorun.App.MainWindow).GetField("_dashboardClock", Private)!.GetValue(main)!;
        foreach (var phase in new[] { "dense", "dense", "dense", "cache-health", "uncache-health", "clock-paused", "clock-resumed", "clock-paused", "clock-resumed", "hidden" })
        {
            if (phase == "cache-health") { main.ServiceHealthPlot.CacheMode = new System.Windows.Media.BitmapCache(); }
            if (phase == "uncache-health") { main.ServiceHealthPlot.CacheMode = null; }
            if (phase == "clock-paused") { clock.Stop(); }
            if (phase == "clock-resumed") { clock.Start(); }
            if (phase == "hidden") { main.Hide(); }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(250);
            var traces = new List<(string Phase, double Milliseconds)>();
            MotionTiming.Observer = (name, _, milliseconds) => traces.Add((name, milliseconds));
            var before = ThreadTimes(process); var cpu = process.TotalProcessorTime; var watch = Stopwatch.StartNew();
            try
            {
                await Task.Delay(2200);
                var after = ThreadTimes(process);
                samples.Add(new { phase, milliseconds = watch.Elapsed.TotalMilliseconds,
                    cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                    threads = after.Where(pair => before.ContainsKey(pair.Key)).Select(pair => new
                    { id = pair.Key, ui = pair.Key == uiThread, cpuMs = pair.Value.Cpu - before[pair.Key].Cpu, startAddress = pair.Value.StartAddress })
                        .OrderByDescending(row => row.cpuMs).Take(8).ToArray(),
                    traces = traces.GroupBy(trace => trace.Phase).Select(group => new
                    { phase = group.Key, calls = group.Count(), ms = group.Sum(trace => trace.Milliseconds) }).ToArray(),
                    activePulseClocks = DataChangePulse.ActiveClocks });
            }
            finally { MotionTiming.Observer = null; }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "threads.json"), JsonSerializer.Serialize(new
        { scope = "Own offscreen synthetic WPF window, no native production UI or workloads; temporary test-only timer/visibility isolation", uiThread, samples },
            new JsonSerializerOptions { WriteIndented = true }));
    }, TimeSpan.FromMinutes(2));

    private static Dictionary<int, (double Cpu, string StartAddress)> ThreadTimes(Process process)
    {
        process.Refresh(); var result = new Dictionary<int, (double, string)>();
        foreach (ProcessThread thread in process.Threads)
        {
            using (thread)
            {
                try { result[thread.Id] = (thread.TotalProcessorTime.TotalMilliseconds, $"0x{thread.StartAddress:X}"); }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { /* A completed worker has no live counters. */ }
            }
        }
        return result;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
