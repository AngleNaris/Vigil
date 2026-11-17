using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class ResourceHealthLifecycleWindowTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task BoundHealthUpdatesStayLocalAndHideReduceAndSelectionReleaseTheirLayers(bool light) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, source) =>
        {
            using var scene = new R121HealthMotionScene(model, main, source);
            await scene.CommandAsync("baseline");
            var plot = main.ServiceHealthPlot;
            Assert.False(plot.DataPulseActive);
            await scene.CommandAsync("update");
            if (SystemParameters.ClientAreaAnimation)
            {
                Assert.True(plot.DataPulseActive);
                Assert.True(plot.PulseLayer.Bounds.Left >= plot.ActualWidth * .69);
                Assert.True(plot.PulseLayer.Bounds.Right <= plot.ActualWidth * .91);
                var builds = plot.PulseGeometryBuilds;
                await Task.Delay(180); Assert.Equal(builds, plot.PulseGeometryBuilds);
            }
            await Task.Delay(720); Assert.False(plot.DataPulseActive); Assert.Empty(plot.PulseLayer.Children);
            var settledBuilds = plot.PulseGeometryBuilds;
            model.TickDashboard(); await R121HealthMotionScene.Idle(main);
            Assert.False(plot.DataPulseActive); Assert.Equal(settledBuilds, plot.PulseGeometryBuilds);
            await scene.CommandAsync("hide"); Assert.False(main.IsVisible); Assert.Equal(0, DataChangePulse.ActiveClocks);
            await scene.CommandAsync("show"); Assert.True(main.IsVisible); Assert.False(plot.DataPulseActive);
            await scene.CommandAsync("reduce"); Assert.True(ColorStateAssist.GetReduceMotion(main)); Assert.Equal(0, DataChangePulse.ActiveClocks);
            await scene.CommandAsync("update"); Assert.False(plot.DataPulseActive);
            await scene.CommandAsync("baseline");
            model.SelectedTask = model.Tasks.Last(); await model.DashboardHistoryTask; await model.ResourceHistoryTask;
            await R121HealthMotionScene.Idle(main); Assert.False(plot.DataPulseActive);
            model.SelectedTask = model.Tasks.First(); await model.DashboardHistoryTask; await model.ResourceHistoryTask;
            await R121HealthMotionScene.Idle(main); Assert.False(plot.DataPulseActive);
            Assert.Equal(0, DataChangePulse.ActiveClocks);
        });
}

// Synthetic health only; never a sampler, durable history or production Agent proof.
internal sealed class R121HealthMotionScene : IDisposable
{
    private readonly MainWindowViewModel _model;
    private readonly SystemAutorun.App.MainWindow _main;
    private readonly ResourceWindowSource _source;
    private readonly Func<MonitoringHistoryQuery, CancellationToken, Task<MonitoringHistoryResult>>? _previous;
    private readonly bool _autoRefresh, _reduced;
    private DateTimeOffset _anchor = DateTimeOffset.UtcNow;
    private DateTimeOffset _record = DateTimeOffset.UtcNow.AddSeconds(-5);
    private bool _changed;
    internal object? LastInterruption { get; private set; }

    internal R121HealthMotionScene(MainWindowViewModel model, SystemAutorun.App.MainWindow main, ResourceWindowSource source)
    {
        _model = model; _main = main; _source = source; _previous = source.HistoryReader;
        _autoRefresh = model.AutoRefresh; _reduced = ColorStateAssist.GetReduceMotion(main);
        source.HistoryReader = ReadAsync;
    }
    internal async Task CommandAsync(string action)
    {
        switch (action)
        {
            case "baseline":
                _anchor = DateTimeOffset.UtcNow;
                _model.AutoRefresh = false; ColorStateAssist.SetReduceMotion(_main, true);
                await SetAsync(false); await _model.ResourceHistoryTask; await Idle(_main);
                ColorStateAssist.SetReduceMotion(_main, false); await Idle(_main);
                LastInterruption = null;
                break;
            case "update": await SetAsync(true); break;
            case "hide": case "reduce":
                await CommandAsync("baseline"); await SetAsync(true); await Task.Delay(65);
                var before = Snapshot();
                if (SystemParameters.ClientAreaAnimation) { Assert.True(_main.ServiceHealthPlot.DataPulseActive); }
                if (action == "hide") { _main.Hide(); }
                else { ColorStateAssist.SetReduceMotion(_main, true); }
                Assert.False(_main.ServiceHealthPlot.DataPulseActive); Assert.Equal(0, DataChangePulse.ActiveClocks);
                LastInterruption = new { action, before, after = Snapshot() };
                break;
            case "show":
                _main.Show(); await Idle(_main); Assert.False(_main.ServiceHealthPlot.DataPulseActive);
                break;
            case "resume":
                ColorStateAssist.SetReduceMotion(_main, false); await Idle(_main);
                break;
            case "clock":
                _model.TickDashboard(); await Idle(_main); Assert.False(_main.ServiceHealthPlot.DataPulseActive);
                break;
            default: throw new ArgumentException("Unknown owned synthetic health action.", nameof(action));
        }
    }
    private async Task SetAsync(bool changed)
    {
        _changed = changed; _record = DateTimeOffset.UtcNow;
        var read = Assert.IsAssignableFrom<Task>(typeof(MainWindowViewModel)
            .GetMethod("QueueDashboardHistoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_model, [false]));
        await read; await Idle(_main);
    }
    private Task<MonitoringHistoryResult> ReadAsync(MonitoringHistoryQuery query, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var duration = query.To - query.From;
        DateTimeOffset At(double fraction) => _anchor - duration * fraction;
        List<HealthHistoryInterval> spans = [new(At(.90), At(.55), ObservationHealth.Healthy), new(At(.50), At(.40), ObservationHealth.Fault)];
        if (_changed) { spans.Add(new(At(.30), At(.10), ObservationHealth.Healthy)); }
        var healthy = spans.Where(span => span.Health == ObservationHealth.Healthy).Sum(span => (span.To - span.From).TotalSeconds);
        var fault = spans.Where(span => span.Health == ObservationHealth.Fault).Sum(span => (span.To - span.From).TotalSeconds);
        return Task.FromResult(new MonitoringHistoryResult(1, query.TaskName, query.TaskIdentity, query.From, query.To, _record,
            spans[0].From, healthy, fault, duration.TotalSeconds - healthy - fault, healthy / (healthy + fault) * 100,
            (healthy + fault) / duration.TotalSeconds * 100, spans, [], false, "R121 explicit synthetic health scene; not Agent records"));
    }
    internal object Snapshot()
    {
        var plot = _main.ServiceHealthPlot; var bounds = plot.PulseLayer.Bounds;
        var position = plot.IsLoaded ? plot.TranslatePoint(new System.Windows.Point(), _main) : new System.Windows.Point();
        return new
        {
            at = DateTimeOffset.UtcNow, _changed, _record, clientAreaAnimation = SystemParameters.ClientAreaAnimation,
            visible = _main.IsVisible, reduced = ColorStateAssist.GetReduceMotion(_main), _model.IsPresentationActive,
            activeClocks = DataChangePulse.ActiveClocks, activeWatchers = DataChangePulse.ActiveWatchers,
            healthActive = plot.DataPulseActive, healthLayerOpacity = plot.PulseLayer.Opacity, healthGeometryBuilds = plot.PulseGeometryBuilds,
            pulseBounds = bounds.IsEmpty ? null : new { bounds.X, bounds.Y, bounds.Width, bounds.Height },
            plot = new { position.X, position.Y, plot.ActualWidth, plot.ActualHeight, plot.WindowHours, plot.DataAt, plot.EndAt },
            intervals = plot.Intervals
        };
    }
    internal static async Task Idle(Window window)
    { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    public void Dispose()
    {
        _source.HistoryReader = _previous; ColorStateAssist.SetReduceMotion(_main, _reduced); _model.AutoRefresh = _autoRefresh;
    }
}
