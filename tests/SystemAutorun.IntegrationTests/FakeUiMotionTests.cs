using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class FakeUiMotionTests
{
    [Fact]
    public Task CompositeBordersTraceTheirOwnEdgesAndKeepTheBrushMappingFixed() => GuiConfigurationControlTests.RunStaAsync(() =>
    {
        var ink = new SolidColorBrush(Colors.Gray) { Opacity = .7 };
        var size = new Size(100, 60); var thickness = new Thickness(1);
        DrawingGroup At(double progress) => Assert.IsType<DrawingGroup>(Assert.IsType<DrawingBrush>(
            LineMotion.DrawLines(ink, Colors.White, size, thickness, progress)).Drawing);
        Assert.Single(At(0).Children); // Transparent full-size mapping anchor, no premature line.
        var quarter = At(.25);
        Assert.Equal(new Rect(size), quarter.Bounds);
        Assert.Equal(new Rect(0, 0, 80, 1), Assert.IsType<GeometryDrawing>(quarter.Children[1]).Geometry.Bounds);
        Assert.Equal(2, quarter.Children.Count);
        var brush = Assert.IsType<SolidColorBrush>(Assert.IsType<GeometryDrawing>(quarter.Children[1]).Brush);
        Assert.Equal(Colors.White, brush.Color); // Solid ink across the whole traced path, no spatial gradient.
        var threeQuarters = At(.75);
        Assert.Equal(new Rect(20, 59, 80, 1), Assert.IsType<GeometryDrawing>(threeQuarters.Children[3]).Geometry.Bounds);
        Assert.Equal(new Rect(99, 0, 1, 60), Assert.IsType<GeometryDrawing>(threeQuarters.Children[2]).Geometry.Bounds);
        Assert.Same(ink, LineMotion.DrawLines(ink, Colors.White, size, thickness, 1));
        Assert.Equal(.7, ink.Opacity); Assert.False(ink.IsFrozen);
        // Mixed right/bottom edges do not become a diagonal wipe.
        var mixed = Assert.IsType<DrawingGroup>(Assert.IsType<DrawingBrush>(LineMotion.DrawLines(ink, Colors.White,
            size, new Thickness(0, 0, 1, 1), .25)).Drawing);
        Assert.Equal(new Rect(99, 0, 1, 40), Assert.IsType<GeometryDrawing>(mixed.Children[1]).Geometry.Bounds);
        Assert.Equal(2, mixed.Children.Count);
        return Task.CompletedTask;
    });

    [Fact]
    public Task GlyphsRevealMonotonicallyWithoutAnyBrightnessPulse() => GuiConfigurationControlTests.RunStaAsync(() =>
    {
        var timeline = LineMotion.Reveal(.8, 130);
        Assert.IsType<LinearDoubleKeyFrame>(timeline.KeyFrames[^1]);
        var clock = timeline.CreateClock(); clock.Controller!.Begin(); clock.Controller.Pause();
        var previous = 0d;
        for (var time = 0; time <= 230; time++)
        {
            clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(time), TimeSeekOrigin.BeginTime);
            var value = (double)clock.GetCurrentValue(.8, .8);
            Assert.InRange(value, previous - 1e-8, .8);
            if (time <= 130) { Assert.Equal(0, value); }
            previous = value;
        }
        Assert.Equal(.8, previous, 8);
        clock.Controller.Remove();
        Assert.InRange(LineMotion.MaximumDelayMilliseconds + 4 * 45, 800, 900);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task MainFirstShowPageAndToolEntrancesKeepTheirCanvasFixed(bool light) => WithMain(light, async (model, main) =>
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(main.FindName("MainScene"));
        if (SystemParameters.ClientAreaAnimation) { Assert.True(LineMotion.IsPlaying(root)); }
        await Samples(main, $"first-{Theme(light)}");
        await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(root));
        main.HostsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var page = main.MonitoringTabs.HostPage;
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        if (SystemParameters.ClientAreaAnimation) { Assert.True(LineMotion.IsPlaying(page), $"First page entrance: loaded={page.IsLoaded}, visible={page.IsVisible}, presented={page.IsPresented}, size={page.RenderSize}, reduced={ColorStateAssist.GetReduceMotion(page)}"); }
        Assert.Equal(1, page.Opacity); Assert.False(page.HasAnimatedProperties);
        Assert.Equal(0, Assert.IsType<TranslateTransform>(page.RenderTransform).Y);
        await Samples(main, $"host-{Theme(light)}");
        await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(page));
        // Same owner can switch quickly without cross-root ownership or resurrecting an outgoing page.
        main.ServicesTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        main.HostsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        main.ExceptionLogsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ColorStateAssist.SetReduceMotion(main, true);
        Assert.False(LineMotion.IsPlaying(root)); Assert.False(LineMotion.IsPlaying(page));
        Assert.False(LineMotion.IsPlaying(main.ServicesPage)); Assert.False(LineMotion.IsPlaying(main.MonitoringTabs.ExceptionsPage));
        Assert.True(main.MonitoringTabs.ExceptionsPage.IsHitTestVisible); Assert.False(main.ServicesPage.IsHitTestVisible);
        ColorStateAssist.SetReduceMotion(main, false);
        var tool = main.CreateAddProgramWindow();
        tool.ShowActivated = false; tool.WindowStartupLocation = WindowStartupLocation.Manual; tool.Left = -32000; tool.Top = -32000;
        var surface = Assert.IsType<PresencePanel>(tool.Content);
        var title = Descendants(surface).OfType<TextBlock>().Single(text => text.Text == "添加工作负载");
        var titleParticipated = false;
        var clipDescriptor = DependencyPropertyDescriptor.FromProperty(UIElement.ClipProperty, title.GetType());
        void ObserveTitle(object? sender, EventArgs args) => titleParticipated |= DependencyPropertyHelper.GetValueSource(title, UIElement.ClipProperty).IsAnimated;
        clipDescriptor.AddValueChanged(title, ObserveTitle);
        try
        {
            tool.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            Assert.Equal(1, surface.Opacity); Assert.False(surface.HasAnimatedProperties);
            Assert.Equal(0, Assert.IsType<TranslateTransform>(surface.RenderTransform).Y);
            if (SystemParameters.ClientAreaAnimation)
            {
                await PresenceMotionTests.Until(() => LineMotion.IsPlaying(surface));
                Assert.True(titleParticipated, "Tool title must participate through the bounded reveal; early content may already have settled when the remaining lines are observed.");
            }
            await Samples(tool, $"add-{Theme(light)}");
            await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(surface));
            tool.Close(); await tool.ClosureTask;
            Assert.False(surface.HasAnimatedProperties);
        }
        finally { clipDescriptor.RemoveValueChanged(title, ObserveTitle); ColorStateAssist.SetReduceMotion(tool, true); tool.Close(); }
        // Main-window root also owns cleanup (not just the PresencePanel page wrapper).
        Assert.Equal(SystemParameters.ClientAreaAnimation, LineMotion.Play(root));
        main.Hide(); Assert.False(LineMotion.IsPlaying(root));
        Assert.All(Descendants(root).OfType<FrameworkElement>().Where(element => element is Border or TextBlock),
            element => Assert.False(DependencyPropertyHelper.GetValueSource(element, UIElement.OpacityProperty).IsAnimated));
        main.Show(); Assert.False(LineMotion.IsPlaying(root)); // Tray restore is not first load.
        model.TickDashboard(); Assert.False(LineMotion.IsPlaying(root));
        if (SystemParameters.ClientAreaAnimation)
        {
            Assert.True(LineMotion.Play(root)); main.WindowState = WindowState.Minimized;
            Assert.False(LineMotion.IsPlaying(root)); Assert.False(LineMotion.Play(root));
            main.WindowState = WindowState.Normal; Assert.False(LineMotion.IsPlaying(root));
            Assert.True(LineMotion.Play(root)); ColorStateAssist.SetReduceMotion(main, true);
            Assert.False(LineMotion.IsPlaying(root));
        }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task SharedTracksGuttersMetersAndHeadersStayAlignedAcrossDesktopWindows(bool light) => WithMain(light, async (_, main) =>
    {
        ColorStateAssist.SetReduceMotion(main, true);
        foreach (var (width, height) in new[] { (920d, 580d), (960d, 620d), (1320d, 820d), (1620d, 651d) })
        {
            main.Width = width; main.Height = height;
            main.ServicesTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); main.UpdateLayout();
            var serviceTrack = main.ServicesPage.ColumnDefinitions[0].ActualWidth;
            // R112 deliberately removed both bottom status bars rather than preserving empty 28-DIP rows.
            Assert.DoesNotContain(Descendants(main.ServicesPage).OfType<Border>(),
                border => border.BorderThickness == new Thickness(0, 1, 0, 0));
            main.HostsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); main.UpdateLayout();
            var view = main.MonitoringTabs;
            Near(serviceTrack, view.HostPage.ColumnDefinitions[0].ActualWidth);
            var cells = Descendants(view.HostPage).OfType<Border>().Where(border => AutomationProperties.GetName(border) is "主机CPU" or "主机内存用量" or "整机磁盘IO" or "网络收发速率").ToArray();
            Assert.Equal(4, cells.Length);
            Assert.All(cells, cell => { Assert.Equal(20, cell.Padding.Left); Assert.Equal(20, cell.Padding.Right); });
            var plots = cells.Select(cell => Descendants(cell).OfType<DashboardPlot>().Single()).ToArray();
            Assert.All(plots, plot => Assert.Equal(118, plot.Height));
            Near(plots[0].TranslatePoint(new Point(), main).Y, plots[1].TranslatePoint(new Point(), main).Y);
            Near(plots[2].TranslatePoint(new Point(), main).Y, plots[3].TranslatePoint(new Point(), main).Y);
            StartupGuiWindowTests.CaptureStateEvidence(main, $"r110-host-{Theme(light)}-{width}x{height}.png");
            main.ExceptionLogsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); main.UpdateLayout();
            Assert.Equal(44, view.ExceptionsPage.RowDefinitions[0].ActualHeight);
            var grid = Descendants(view.ExceptionsPage).OfType<DataGrid>().Single();
            Assert.Equal(44, grid.ColumnHeaderHeight);
            var details = (Border)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(view.ExceptionDetailsCaption));
            Assert.Equal(44, details.ActualHeight);
            StartupGuiWindowTests.CaptureStateEvidence(main, $"r110-exceptions-{Theme(light)}-{width}x{height}.png");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        static void Near(double a, double b) => Assert.InRange(Math.Abs(a - b), 0, .75);
    });

    [Fact]
    public Task NativeFakeUiProbeUsesOnlyAnIsolatedFixture() => WithMain(Environment.GetEnvironmentVariable("SA_FAKEUI_PROBE_THEME") == "light", async (model, main) =>
    {
        var probe = Environment.GetEnvironmentVariable("SA_FAKEUI_PROBE");
        if (string.IsNullOrEmpty(probe)) { return; }
        Assert.True(System.IO.Directory.Exists(probe));
        ColorStateAssist.SetReduceMotion(main, true);
        AnimationRegressionTests.Seed(model);
        AnimationRegressionTests.Logs(model);
        main.Title = "SystemAutorun · R117 isolated UI"; main.Left = 12; main.Top = 12;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        ColorStateAssist.SetReduceMotion(main, false);
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(probe, "ready.json"), System.Text.Json.JsonSerializer.Serialize(new
        { pid = Environment.ProcessId, hwnd = new WindowInteropHelper(main).Handle.ToInt64(), dpi = VisualTreeHelper.GetDpi(main).PixelsPerInchX }));
        var poll = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => AnimationRegressionTests.Seed(model), main.Dispatcher);
        try
        {
            var deadline = DateTime.UtcNow.AddMinutes(12);
            while (!System.IO.File.Exists(System.IO.Path.Combine(probe, "finish.txt")) && DateTime.UtcNow < deadline) { await Task.Delay(100); }
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(probe, "finish.txt")), "Native UI probe was not finished.");
        }
        finally { poll.Stop(); }
    }, TimeSpan.FromMinutes(13));

    private static async Task Samples(Window window, string prefix)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SA_STATE_GUI_EVIDENCE"))) { return; }
        var watch = Stopwatch.StartNew();
        var samples = new List<string> { "requested_ms,elapsed_ms" };
        foreach (var at in new[] { 0, 80, 160, 240, 320, 400, 480, 560, 640 })
        {
            var delay = at - (int)watch.ElapsedMilliseconds; if (delay > 0) { await Task.Delay(delay); }
            samples.Add($"{at},{watch.ElapsedMilliseconds}");
            StartupGuiWindowTests.CaptureStateEvidence(window, $"r110-{prefix}-{at:D3}.png");
        }
        await System.IO.File.WriteAllLinesAsync(System.IO.Path.Combine(Environment.GetEnvironmentVariable("SA_STATE_GUI_EVIDENCE")!, $"r110-{prefix}-timings.csv"), samples);
    }

    private static string Theme(bool light) => light ? "light" : "dark";

    internal static async Task WithMain(bool light, Func<MainWindowViewModel, SystemAutorun.App.MainWindow, Task> action, TimeSpan? timeout = null,
        Func<ConfigurationControlPlaneTests.ServerFixture, ConfigurationWorkspaceSnapshot, IDashboardMonitoringClient>? monitoringFactory = null)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is not SystemAutorun.App.App)
            { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
            WindowTheme.Install();
            var previous = ThemeService.IsLight ? ThemePreference.Light : ThemePreference.Dark;
            ThemeService.Apply(light ? ThemePreference.Light : ThemePreference.Dark, persist: null);
            var cursor = new AgentConfigurationClient(fixture.Client);
            var monitoring = monitoringFactory is null ? null : monitoringFactory(fixture, await cursor.ReadAsync(CancellationToken.None));
            var model = new MainWindowViewModel(fixture.Client, new NativeCacheStore(fixture.Paths.GuiCachePath),
                cursor, fixture.Paths, new NeverRemoteReader(), monitoring: monitoring) { AutoRefresh = false };
            var main = new SystemAutorun.App.MainWindow(model, new AgentStartupClient(fixture.Client), new StartupCacheStore(fixture.Paths.StartupCachePath))
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            typeof(MainWindowViewModel).GetMethod("ApplyHostStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model,
                [new HostStatusSnapshot("合成主机", "合成 Windows 系统版本", DateTimeOffset.UtcNow, TimeSpan.FromHours(54), 37, 32L << 30, 12L << 30,
                    [new DriveStatusSnapshot(@"C:\", "合成系统盘", 512L << 30, 128L << 30)],
                    [new NetworkStatusSnapshot("合成以太网", "192.0.2.24", 9L << 30, 2L << 30)], LogicalProcessorCount: 16, ProcessCount: 214, ThreadCount: 2890)]);
            ColorStateAssist.SetReduceMotion(main, false);
            try { main.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded); await action(model, main); }
            finally
            {
                ColorStateAssist.SetReduceMotion(main, true);
                var closing = typeof(SystemAutorun.App.MainWindow).GetMethod("Window_Closing", BindingFlags.Instance | BindingFlags.NonPublic)!;
                main.Closing -= (System.ComponentModel.CancelEventHandler)closing.CreateDelegate(typeof(System.ComponentModel.CancelEventHandler), main);
                main.Close(); ThemeService.Apply(previous, persist: null);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
        }, timeout);
    }

    private sealed class NeverRemoteReader : IRemoteHostReader
    {
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("FakeUI fixtures must never connect to real remote hosts.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        { var child = VisualTreeHelper.GetChild(root, index); yield return child; foreach (var item in Descendants(child)) { yield return item; } }
    }
}
