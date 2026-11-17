using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Services;
using Point = System.Windows.Point;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class DashboardPlotInteractionTests
{
    [Fact]
    public Task EmptyChartRegionsRemainPointerTargetsForUnknownHover() => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        if (Application.Current is null) { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
        var now = DateTimeOffset.UtcNow;
        var plot = new DashboardPlot { Width = 400, Height = 180, EndAt = now, WindowDuration = ResourceTrendBuffer.Window,
            Points = [new(now.AddSeconds(-2), .04), new(now.AddSeconds(-1), .04)] };
        var window = new Window { Width = 400, Height = 180, Content = plot, ShowActivated = false, Left = -32000, Top = -32000 };
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(plot, VisualTreeHelper.HitTest(plot, new Point(200, 50))?.VisualHit);
            Assert.False(plot.Focusable);
            Assert.Null(plot.ToolTip);
            plot.UpdateHover(new Point(200, 50));
            Assert.Contains("未采样", plot.CurrentHoverText);
            Assert.NotEmpty(plot.HoverLine.Children);
            plot.ClearHover();
            Assert.Null(plot.CurrentHoverText);
            Assert.Empty(plot.HoverLine.Children); Assert.Empty(plot.HoverLabels.Children);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task HoverOverlayUsesActualValuesAndDropsStaleData() => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        if (Application.Current is null) { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
        var now = DateTimeOffset.UtcNow;
        var plot = new DashboardPlot { Width = 400, Height = 180, EndAt = now, WindowDuration = TimeSpan.FromSeconds(10), Rate = true,
            Points = [new(now.AddSeconds(-5), 1536)], SecondaryPoints = [new(now.AddSeconds(-5), 2560)] };
        var window = new Window { Width = 400, Height = 180, Content = plot, ShowActivated = false, Left = -32000, Top = -32000 };
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            plot.UpdateHover(new Point(plot.PlotBounds.Left + plot.PlotBounds.Width / 2, 50));
            Assert.Contains("1.5 KB/s", plot.CurrentHoverText);
            Assert.Contains("2.5 KB/s", plot.CurrentHoverText);
            Assert.Equal(now.AddSeconds(-5), plot.CurrentHoverSample?.At);
            var guide = Assert.IsType<GeometryDrawing>(plot.HoverLine.Children[0]);
            var actual = Assert.IsType<SolidColorBrush>(guide.Pen!.Brush).Color;
            Assert.Equal(Assert.IsType<SolidColorBrush>(plot.FindResource("Semantic.Brush.StatusFailed")).Color, actual);
            plot.Points = []; plot.SecondaryPoints = [];
            await Dispatcher.Yield(DispatcherPriority.Render); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Contains("未采样", plot.CurrentHoverText);
            Assert.Null(plot.CurrentHoverSample);
            plot.Mode = "Health";
            Assert.Null(plot.CurrentHoverText); Assert.Empty(plot.HoverLine.Children); Assert.Empty(plot.HoverLabels.Children);
            plot.WindowHours = 1;
            plot.Intervals = [new(now.AddMinutes(-31), now.AddMinutes(-29), "failed")];
            await Dispatcher.Yield(DispatcherPriority.Render);
            plot.UpdateHover(new Point(200, 10));
            Assert.Contains("故障", plot.CurrentHoverText);
            plot.ClearHover(); Assert.Empty(plot.HoverLine.Children); Assert.Empty(plot.HoverLabels.Children);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task HoverGuideReversesAndRespectsMotionLifetime() => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        if (Application.Current is null) { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
        var now = DateTimeOffset.UtcNow;
        var plot = new DashboardPlot { Width = 400, Height = 180, EndAt = now, WindowDuration = TimeSpan.FromSeconds(10),
            Points = [new(now.AddSeconds(-5), 12.3)] };
        var window = new Window { Width = 400, Height = 180, Content = plot, ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            ColorStateAssist.SetReduceMotion(plot, false);
            plot.UpdateHover(new Point(plot.PlotBounds.Left + plot.PlotBounds.Width / 2, 50));
            Assert.Contains(now.AddSeconds(-5).ToLocalTime().ToString("ss.fff"), plot.CurrentHoverText);
            if (SystemParameters.ClientAreaAnimation)
            {
                Assert.True(plot.HoverAnimationActive);
                await Task.Delay(55);
                var growing = plot.HoverLineLength;
                Assert.InRange(growing, .01, .99);
                plot.UpdateHover(new Point(plot.PlotBounds.Left + plot.PlotBounds.Width / 2 + 1, 50));
                Assert.True(plot.HoverLineLength >= growing - .02); // Movement does not restart entry.
                plot.ExitHover(); Assert.Null(plot.CurrentHoverText); Assert.Empty(plot.HoverLabels.Children);
                Assert.True(plot.HoverAnimationActive); Assert.NotEmpty(plot.HoverLine.Children);
                var exitStart = plot.HoverLineLength;
                await Task.Delay(25); var shrinking = plot.HoverLineLength; Assert.True(shrinking < exitStart);
                plot.UpdateHover(new Point(plot.PlotBounds.Left + plot.PlotBounds.Width / 2, 50));
                Assert.True(plot.HoverAnimationActive); await PresenceMotionTests.Until(() => !plot.HoverAnimationActive);
                Assert.InRange(plot.HoverLineLength, .999, 1); Assert.False(plot.HoverAnimationActive);
                plot.ExitHover(); await PresenceMotionTests.Until(() => !plot.HoverAnimationActive);
                Assert.Empty(plot.HoverLine.Children); Assert.False(plot.HoverAnimationActive);
            }
            ColorStateAssist.SetReduceMotion(plot, true);
            plot.UpdateHover(new Point(200, 50));
            Assert.Equal(1, plot.HoverLineLength); Assert.False(plot.HoverAnimationActive);
            plot.ExitHover(); Assert.Empty(plot.HoverLine.Children); Assert.False(plot.HoverAnimationActive);
            ColorStateAssist.SetReduceMotion(plot, false); plot.UpdateHover(new Point(200, 50));
            plot.Visibility = Visibility.Collapsed;
            Assert.Empty(plot.HoverLine.Children); Assert.False(plot.HoverAnimationActive);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task BothSeriesAndTheirChangePulseRemainSolidAndDistinct() => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        if (Application.Current is null) { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
        var now = DateTimeOffset.UtcNow;
        var plot = new DashboardPlot { Width = 400, Height = 180, EndAt = now, WindowDuration = TimeSpan.FromSeconds(10), Rate = true,
            DataAt = now.AddSeconds(-1), Points = [new(now.AddSeconds(-2), 10), new(now.AddSeconds(-1), 20)],
            SecondaryPoints = [new(now.AddSeconds(-2), 30), new(now.AddSeconds(-1), 40)] };
        ColorStateAssist.SetReduceMotion(plot, false);
        var window = new Window { Width = 400, Height = 180, Content = plot, ShowActivated = false, Left = -32000, Top = -32000 };
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(100);
            var drawings = Drawings(VisualTreeHelper.GetDrawing(plot)).Where(item => item.Geometry is StreamGeometry && item.Pen is not null).ToArray();
            Assert.Equal(2, drawings.Length);
            Assert.All(drawings, item => Assert.True(item.Pen!.DashStyle is null || item.Pen.DashStyle.Dashes.Count == 0));
            Assert.NotEqual(((SolidColorBrush)drawings[0].Pen!.Brush).Color, ((SolidColorBrush)drawings[1].Pen!.Brush).Color);
            plot.EndAt = now.AddSeconds(1); plot.Points = [.. plot.Points!, new(now, 25)];
            plot.SecondaryPoints = [.. plot.SecondaryPoints!, new(now, 45)]; plot.DataAt = now;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(50);
            Assert.True(plot.DataPulseActive); Assert.True(plot.PulseGeometryBuilds > 0);
            var pulse = Drawings(plot.PulseLayer).Where(item => item.Pen is not null).ToArray();
            Assert.Equal(2, pulse.Length);
            Assert.All(pulse, item => Assert.True(item.Pen!.DashStyle is null || item.Pen.DashStyle.Dashes.Count == 0));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task HoverRefreshesColorsAcrossThemesAndUsesRightEdgeBucket() => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        if (Application.Current is null) { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
        var previous = ThemeService.Preference; var now = DateTimeOffset.UtcNow;
        ThemeService.Apply(ThemePreference.Dark, persist: null);
        var plot = new DashboardPlot { Width = 400, Height = 180, EndAt = now, WindowDuration = TimeSpan.FromMinutes(1),
            Points = [new(now.AddSeconds(-30), 12.34, now.AddMinutes(-1), now)] };
        var window = new Window { Width = 400, Height = 180, Content = plot, ShowActivated = false, Left = -32000, Top = -32000 };
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            plot.UpdateHover(new Point(plot.PlotBounds.Right, 50));
            Assert.NotNull(plot.CurrentHoverSample); Assert.Contains("12.3%", plot.CurrentHoverText);
            var dark = ((SolidColorBrush)Assert.IsType<GeometryDrawing>(plot.HoverLabels.Children[0]).Brush!).Color;
            ThemeService.Apply(ThemePreference.Light, persist: null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var light = ((SolidColorBrush)Assert.IsType<GeometryDrawing>(plot.HoverLabels.Children[0]).Brush!).Color;
            Assert.NotEqual(dark, light); Assert.Contains("12.3%", plot.CurrentHoverText);
        }
        finally { window.Close(); ThemeService.Apply(previous, persist: null); }
    });

    private static IEnumerable<GeometryDrawing> Drawings(Drawing? drawing)
    {
        if (drawing is GeometryDrawing geometry) { yield return geometry; }
        if (drawing is DrawingGroup group)
        { foreach (var child in group.Children) { foreach (var result in Drawings(child)) { yield return result; } } }
    }
}
