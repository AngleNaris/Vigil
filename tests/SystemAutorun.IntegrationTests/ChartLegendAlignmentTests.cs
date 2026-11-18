using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using Point = System.Windows.Point;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class ChartLegendAlignmentTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task HostActualPlotEdgesMatchIconAndReadoutAcrossDesktopWidths(bool light) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, _) =>
        {
            ColorStateAssist.SetReduceMotion(main, true); model.CurrentPage = MainWindowViewModel.OverviewPage;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var charts = main.MonitoringTabs.HostCharts;
            var items = new[]
            {
                (Icon: charts.HostCpuIcon, Readout: charts.HostCpuReadout, Plot: charts.HostCpuPlot),
                (Icon: charts.HostMemoryIcon, Readout: charts.HostMemoryReadout, Plot: charts.HostMemoryPlot),
                (Icon: charts.HostDiskIcon, Readout: charts.HostDiskReadout, Plot: charts.HostDiskPlot),
                (Icon: charts.HostNetworkIcon, Readout: charts.HostNetworkReadout, Plot: charts.HostNetworkPlot),
            };
            foreach (var (width, height) in new[] { (920, 580), (1152, 648), (1320, 820) })
            {
                main.Width = width; main.Height = height;
                foreach (var item in items)
                {
                    item.Plot.BringIntoView(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
                    Assert.False(item.Plot.PlotBounds.IsEmpty);
                    var left = item.Plot.TranslatePoint(new Point(item.Plot.PlotBounds.Left, 0), charts).X;
                    var right = item.Plot.TranslatePoint(new Point(item.Plot.PlotBounds.Right, 0), charts).X;
                    var icon = item.Icon.TranslatePoint(new Point(), charts).X;
                    var readout = item.Readout.TranslatePoint(new Point(item.Readout.ActualWidth, 0), charts).X;
                    Assert.InRange(Math.Abs(left - icon), 0, .75);
                    Assert.InRange(Math.Abs(right - readout), 0, .75);
                }
                StartupGuiWindowTests.CaptureStateEvidence(main, $"r124-host-bottom-{(light ? "light" : "dark")}-{width}.png");
                if (width == 1152)
                {
                    var plot = charts.HostNetworkPlot;
                    plot.UpdateHover(new Point(plot.PlotBounds.Left + plot.PlotBounds.Width * .75, 60));
                    Assert.Contains("B/s", plot.CurrentHoverText);
                    await Dispatcher.Yield(DispatcherPriority.Render);
                    StartupGuiWindowTests.CaptureStateEvidence(main, $"r124-host-hover-{(light ? "light" : "dark")}.png");
                    plot.ClearHover();
                }
                charts.HostCpuPlot.BringIntoView(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                StartupGuiWindowTests.CaptureStateEvidence(main, $"r124-host-top-{(light ? "light" : "dark")}-{width}.png");
            }
            var helps = Descendants(charts).OfType<PlotLegendHelp>().ToArray();
            Assert.Equal(2, helps.Length);
            var disk = Assert.Single(helps, help => help.Legend == "Disk");
            var network = Assert.Single(helps, help => help.Legend == "Network");
            Assert.Equal("读取", disk.PrimaryName); Assert.Equal("写入", disk.SecondaryName);
            Assert.Equal("接收", network.PrimaryName); Assert.Equal("发送", network.SecondaryName);
            Assert.False(disk.Popup.StaysOpen); Assert.False(network.Popup.StaysOpen);
            Assert.Equal("Inbound", Assert.Single(Descendants(charts.HostDiskReadout.Parent as DependencyObject ?? charts)
                .OfType<LineIcon>(), icon => icon.Kind == "Inbound").Kind);
        });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task ServiceHealthAndResourceHelpPreserveContentAndHover(bool light) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, _) =>
        {
            ColorStateAssist.SetReduceMotion(main, true);
            Assert.Equal(MainWindowViewModel.WorkloadsPage, model.CurrentPage);
            foreach (var (width, height) in new[] { (920, 580), (1152, 648), (1320, 820) })
            {
                main.Width = width; main.Height = height;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
                var helps = Descendants(main.ServicesPage).OfType<PlotLegendHelp>().Where(help => help.IsVisible).ToArray();
                Assert.Single(helps, help => help.Legend == "Health");
                Assert.True(main.ServiceHealthPlot.ActualHeight > 20);
                Assert.Contains(main.ServiceCpuPlot.Points!, point => point.Value is > 0);
                StartupGuiWindowTests.CaptureStateEvidence(main, $"r124-service-{(light ? "light" : "dark")}-{width}.png");
                if (width == 1152)
                {
                    var plot = main.ServiceCpuPlot;
                    plot.UpdateHover(new Point(plot.PlotBounds.Left + plot.PlotBounds.Width * .75, 60));
                    Assert.Contains("%", plot.CurrentHoverText);
                    await Dispatcher.Yield(DispatcherPriority.Render);
                    StartupGuiWindowTests.CaptureStateEvidence(main, $"r124-service-hover-{(light ? "light" : "dark")}.png");
                    plot.ClearHover();
                    main.ServiceHealthPlot.UpdateHover(new Point(main.ServiceHealthPlot.ActualWidth * .5, 12));
                    Assert.Contains("未知", main.ServiceHealthPlot.CurrentHoverText);
                    await Dispatcher.Yield(DispatcherPriority.Render);
                    StartupGuiWindowTests.CaptureStateEvidence(main, $"r124-service-health-hover-{(light ? "light" : "dark")}.png");
                    main.ServiceHealthPlot.ClearHover();
                }
            }
        });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task ServiceFourPlotsKeepBothRateDirectionsLegendsAndEdgeAlignmentAcrossDesktopWidths(bool light) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, _) =>
        {
            ColorStateAssist.SetReduceMotion(main, true);
            Assert.Equal(MainWindowViewModel.WorkloadsPage, model.CurrentPage);
            var scroll = Assert.IsType<ScrollViewer>(main.FindName("ServiceMetricsScroll"));
            var diskPlot = Assert.IsType<DashboardPlot>(main.FindName("ServiceDiskPlot"));
            var networkPlot = Assert.IsType<DashboardPlot>(main.FindName("ServiceNetworkPlot"));
            var diskIcon = Assert.Single(Descendants(main.ServicesPage).OfType<LineIcon>(), icon => icon.Kind == "Disk");
            var networkIcon = Assert.Single(Descendants(main.ServicesPage).OfType<LineIcon>(), icon => icon.Kind == "Network");
            var diskReadout = Assert.IsType<TextBlock>(main.FindName("ServiceDiskReadout"));
            var diskWriteReadout = Assert.IsType<TextBlock>(main.FindName("ServiceDiskWriteReadout"));
            var netReadout = Assert.IsType<TextBlock>(main.FindName("ServiceNetworkReadout"));
            var netSendReadout = Assert.IsType<TextBlock>(main.FindName("ServiceNetworkSendReadout"));

            foreach (var plot in new[] { main.ServiceCpuPlot, main.ServiceMemoryPlot, diskPlot, networkPlot })
            {
                Assert.Contains(plot.Points!, point => point.Value is > 0);
                Assert.Equal(TimeSpan.FromMinutes(5), plot.WindowDuration);
            }
            Assert.True(diskPlot.Rate);
            Assert.True(networkPlot.Rate);
            Assert.Contains(diskPlot.SecondaryPoints!, point => point.Value is > 0);
            Assert.Contains(networkPlot.SecondaryPoints!, point => point.Value is > 0);
            Assert.NotEqual("--", diskReadout.Text);
            Assert.NotEqual("--", diskWriteReadout.Text);
            Assert.NotEqual("--", netReadout.Text);
            Assert.NotEqual("--", netSendReadout.Text);

            var helps = Descendants(main.ServicesPage).OfType<PlotLegendHelp>().ToArray();
            var diskHelp = Assert.Single(helps, help => help.Legend == "Disk");
            var networkHelp = Assert.Single(helps, help => help.Legend == "Network");
            Assert.Equal("读取", diskHelp.PrimaryName); Assert.Equal("写入", diskHelp.SecondaryName);
            Assert.Equal("接收", networkHelp.PrimaryName); Assert.Equal("发送", networkHelp.SecondaryName);

            var detailLeft = main.ServiceHeader.TranslatePoint(new Point(), main).X;
            var detailRight = main.ServiceActions.TranslatePoint(new Point(main.ServiceActions.ActualWidth, 0), main).X;
            foreach (var (width, height) in new[] { (920, 580), (1152, 648), (1320, 820) })
            {
                main.Width = width; main.Height = height;
                scroll.ScrollToTop();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
                detailLeft = main.ServiceHeader.TranslatePoint(new Point(), main).X;
                detailRight = main.ServiceActions.TranslatePoint(new Point(main.ServiceActions.ActualWidth, 0), main).X;
                var cpuTop = main.ServiceCpuPlot.TranslatePoint(new Point(), main).Y;
                var memTop = main.ServiceMemoryPlot.TranslatePoint(new Point(), main).Y;
                Assert.InRange(Math.Abs(cpuTop - memTop), 0, 1.0);
                StartupGuiWindowTests.CaptureStateEvidence(main, $"workloads-top-{(light ? "light" : "dark")}-{width}x{height}.png");

                scroll.ScrollToBottom();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
                detailLeft = main.ServiceHeader.TranslatePoint(new Point(), main).X;
                detailRight = main.ServiceActions.TranslatePoint(new Point(main.ServiceActions.ActualWidth, 0), main).X;
                foreach (var plot in new[] { main.ServiceCpuPlot, main.ServiceMemoryPlot, diskPlot, networkPlot })
                {
                    Assert.False(plot.PlotBounds.IsEmpty);
                    Assert.InRange(plot.ActualHeight, 117.5, 118.5);
                }
                Assert.InRange(Math.Abs(main.ServiceCpuPlot.TranslatePoint(new Point(main.ServiceCpuPlot.PlotBounds.Left, 0), main).X - detailLeft), 0, 1.5);
                Assert.InRange(Math.Abs(diskPlot.TranslatePoint(new Point(diskPlot.PlotBounds.Left, 0), main).X - detailLeft), 0, 1.5);
                Assert.InRange(Math.Abs(main.ServiceMemoryPlot.TranslatePoint(new Point(main.ServiceMemoryPlot.PlotBounds.Right, 0), main).X - detailRight), 0, 1.5);
                Assert.InRange(Math.Abs(networkPlot.TranslatePoint(new Point(networkPlot.PlotBounds.Right, 0), main).X - detailRight), 0, 1.5);
                Assert.InRange(Math.Abs(diskPlot.TranslatePoint(new Point(), main).Y - networkPlot.TranslatePoint(new Point(), main).Y), 0, 1.0);
                Assert.True(diskPlot.TranslatePoint(new Point(), main).Y > main.ServiceCpuPlot.TranslatePoint(new Point(), main).Y);
                Assert.True(diskIcon.TranslatePoint(new Point(), main).X >= detailLeft);
                Assert.True(networkIcon.TranslatePoint(new Point(), main).X > diskIcon.TranslatePoint(new Point(), main).X);
                StartupGuiWindowTests.CaptureStateEvidence(main, $"workloads-bottom-{(light ? "light" : "dark")}-{width}x{height}.png");
            }
            scroll.ScrollToTop();
        });

    [Theory]
    [InlineData("Health", "未知", "正常", "故障")]
    [InlineData("Disk", "读取", "写入", null)]
    [InlineData("Network", "接收", "发送", null)]
    public Task LegendContentBindsInInactiveFixtureWithoutOpeningPopup(string legend, string first, string second, string? third) =>
        GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is null) { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
            var control = new PlotLegendHelp { Legend = legend, AccessibleName = "合成图例" };
            Assert.False(control.Popup.StaysOpen); Assert.False(control.IsOpen);
            // Render the content in an owned offscreen window: a real Popup can capture the mouse
            // or reposition itself onto the user's desktop even when its owner is offscreen.
            var child = Assert.IsAssignableFrom<UIElement>(control.Popup.Child); control.Popup.Child = null;
            var panel = new StackPanel(); panel.Children.Add(control); panel.Children.Add(child);
            var window = new Window { Content = panel, ShowActivated = false, Left = -32000, Top = -32000, Width = 400, Height = 250 };
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.False(window.IsActive); Assert.False(control.IsOpen);
                Assert.Same(control, control.ContentRoot.DataContext);
                var text = Descendants(control.ContentRoot).OfType<TextBlock>().Where(item => item.IsVisible).Select(item => item.Text).ToArray();
                Assert.Contains(first, text); Assert.Contains(second, text);
                if (third is not null) { Assert.Contains(third, text); }
                Assert.Equal(third is null ? 2 : 3, text.Length);
                Assert.InRange(control.ContentRoot.ActualWidth, 1, 160); // Legend stays content-sized even inside a stretching parent.
                Assert.Equal("LegendHelp", Assert.Single(Descendants(control.Trigger).OfType<LineIcon>()).Kind);
                StartupGuiWindowTests.CaptureStateEvidence(window, $"r125-legend-{legend.ToLowerInvariant()}.png");
            }
            finally { panel.Children.Remove(child); control.Popup.Child = child; window.Close(); }
        });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }
}
