using System.Windows;
using System.Windows.Threading;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class ResourceDataPulseWindowTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task BoundServiceAndHostPlotsPulseOnlyTheirChangedVisibleData(bool light) => ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, source) =>
    {
        main.Width = 1152; main.Height = 648;
        await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(main.MainScene));
        await Task.Delay(1100); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(main.ServiceCpuPlot.DataPulseActive);
        Publish(0); await model.ResourceHistoryTask; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(main.ServiceCpuPlot.DataPulseActive); Assert.True(main.ServiceMemoryPlot.DataPulseActive);
        Assert.False(main.MonitoringTabs.HostCharts.HostCpuPlot.DataPulseActive);
        Capture("service", "early");
        await Task.Delay(700); Assert.False(main.ServiceCpuPlot.DataPulseActive); Assert.False(main.ServiceMemoryPlot.DataPulseActive);
        Capture("service", "settled");
        model.CurrentPage = MainWindowViewModel.OverviewPage;
        await model.ResourceHistoryTask; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(1200);
        var charts = main.MonitoringTabs.HostCharts;
        Assert.False(charts.HostCpuPlot.DataPulseActive); Assert.False(charts.HostDiskPlot.DataPulseActive);
        Publish(200); await model.ResourceHistoryTask; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(charts.HostCpuPlot.DataPulseActive); Assert.True(charts.HostMemoryPlot.DataPulseActive);
        Assert.True(charts.HostDiskPlot.DataPulseActive); Assert.True(charts.HostNetworkPlot.DataPulseActive);
        Assert.False(main.ServiceCpuPlot.DataPulseActive); Capture("host", "early");
        await Task.Delay(700); Capture("host", "settled"); Assert.Equal(0, DataChangePulse.ActiveClocks);
        await ResourceDashboardWindowTests.Choose(charts.HostWindowButton, "168");
        Assert.False(charts.HostCpuPlot.DataPulseActive); Assert.False(charts.HostDiskPlot.DataPulseActive); Assert.Equal(0, DataChangePulse.ActiveClocks);
        main.Hide(); Assert.Equal(0, DataChangePulse.ActiveClocks);

        void Publish(int phase)
        {
            // Deliver one explicit fixture observation and its readonly query together, independent
            // of the real window timer's 800ms resource throttle. This is not native input evidence.
            model.AutoRefresh = false;
            var at = DateTimeOffset.UtcNow;
            source.Observe(at, model.Tasks.Select(task => ResourceWindowSource.Synthetic(task, at, phase)).ToArray(), ResourceWindowSource.SyntheticHost(at, phase));
            source.Apply(model); model.AutoRefresh = true;
        }
        void Capture(string page, string phase) => StartupGuiWindowTests.CaptureStateEvidence(main,
            $"r121-pulse-{page}-{(light ? "light" : "dark")}-{phase}.png");
    });
}
