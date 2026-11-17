using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Services;
using Color = System.Windows.Media.Color;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class DataChangePulseTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task ReadoutsAndOnlyChangedTrendSegmentsFadeAndReleaseWithoutReprojection(bool light) => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        EnsureApp(); var baseline = DataChangePulse.ActiveWatchers;
        var now = DateTimeOffset.UtcNow;
        var plot = Plot(now, light);
        var text = new TextBlock { Text = "20.0%", FontSize = 24, Height = 40 };
        var rest = new SolidColorBrush(light ? Colors.Black : Colors.White);
        text.Resources["PulseText"] = rest; text.SetResourceReference(TextBlock.ForegroundProperty, "PulseText");
        DataChangePulse.SetValue(text, "20.0%");
        var stack = new StackPanel(); stack.Children.Add(text); stack.Children.Add(plot); DataChangePulse.SetContext(stack, "A|live");
        var window = Window(stack);
        try
        {
            window.Show(); await Settle(); Assert.False(plot.DataPulseActive); Assert.Equal(0, DataChangePulse.ActiveClocks);
            plot.Points = [.. plot.Points!, new(now, 10)]; plot.DataAt = now; await Settle();
            Assert.False(plot.DataPulseActive); Assert.Equal(0, plot.PulseGeometryBuilds);
            plot.EndAt = now.AddSeconds(1); plot.DataAt = plot.EndAt; plot.Points = [.. plot.Points!, new(plot.EndAt, 20)];
            SurfaceMotion.SetRollingText(text, "21.0%"); DataChangePulse.SetValue(text, "21.0%");
            await Settle(); await Task.Delay(55);
            Assert.True(plot.DataPulseActive); Assert.Equal(2, DataChangePulse.ActiveClocks);
            var early = Capture(plot, $"trend-{(light ? "light" : "dark")}-early");
            Assert.NotEmpty(RedPixels(early));
            Assert.Equal(1, plot.PulseGeometryBuilds);
            await Task.Delay(200); Assert.InRange(plot.PulseLayer.Opacity, .02, .9); Assert.Equal(1, plot.PulseGeometryBuilds);
            Capture(plot, $"trend-{(light ? "light" : "dark")}-middle");
            await Task.Delay(500); Assert.False(plot.DataPulseActive); Assert.Empty(plot.PulseLayer.Children); Assert.Equal(0, DataChangePulse.ActiveClocks);
            Assert.Equal(1, plot.PulseGeometryBuilds); Assert.Same(rest, text.Foreground);
            var settled = Capture(plot, $"trend-{(light ? "light" : "dark")}-settled");
            Assert.NotEmpty(RedPixels(settled));
            var pulseDiff = DiffPixels(early, settled);
            Assert.NotEmpty(pulseDiff);
            Assert.All(pulseDiff, pixel => Assert.True(pixel.X > 350, $"Nonlocal pulse diff at {pixel.X},{pixel.Y}"));
            var replacement = new SolidColorBrush(Colors.Gray); text.Resources["PulseText"] = replacement;
            Assert.Same(replacement, text.Foreground); // The DynamicResource expression survived temporary ownership.
        }
        finally { window.Close(); }
        await Settle(); Assert.Equal(baseline, DataChangePulse.ActiveWatchers); Assert.Equal(0, DataChangePulse.ActiveClocks);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task HealthHighlightsOnlyChangedCellsRetainsGapsAndDoesNotReplayClockExtension(bool light) => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        EnsureApp(); var now = DateTimeOffset.UtcNow;
        var plot = Plot(now, light); plot.Mode = "Health"; plot.WindowHours = 1; plot.Height = 90;
        plot.Intervals = [new(now.AddHours(-1), now.AddMinutes(-30), "healthy")]; plot.DataAt = now.AddSeconds(-10);
        var window = Window(plot);
        try
        {
            window.Show(); await Settle(); Assert.False(plot.DataPulseActive);
            plot.Intervals = [new(now.AddHours(-1), now.AddMinutes(-30), "healthy"), new(now.AddMinutes(-15), now, "healthy")];
            plot.DataAt = now; await Settle(); await Task.Delay(55);
            Assert.True(plot.DataPulseActive);
            var image = Capture(plot, $"health-{(light ? "light" : "dark")}-early");
            var red = RedPixels(image); Assert.NotEmpty(red); Assert.All(red, pixel => Assert.True(pixel.X >= 314 && pixel.Y < 60));
            // Every existing cell gap remains transparent even inside the temporary red section.
            var stride = plot.ActualWidth / Math.Max(1, (int)(plot.ActualWidth / 9));
            foreach (var pixel in red)
            { var center = pixel.X + .5; Assert.True(center - Math.Floor(center / stride) * stride < stride - 2, $"Red at gap center {pixel.X},{pixel.Y}"); }
            await Task.Delay(680); Assert.False(plot.DataPulseActive);
            Assert.Empty(RedPixels(Capture(plot, $"health-{(light ? "light" : "dark")}-settled")));
            var builds = plot.PulseGeometryBuilds;
            plot.EndAt = now.AddSeconds(1); await Settle(); Assert.False(plot.DataPulseActive); Assert.Equal(builds, plot.PulseGeometryBuilds);
        }
        finally { window.Close(); }
        await Settle(); Assert.Equal(0, DataChangePulse.ActiveClocks);
    });

    [Fact]
    public Task SwitchHideReduceSuspendThemeAndUnloadImmediatelyReleaseAndRebaseline() => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        EnsureApp(); var watchers = DataChangePulse.ActiveWatchers; var preference = ThemeService.Preference;
        var now = DateTimeOffset.UtcNow; var plot = Plot(now, false); var value = 10d;
        var text = new TextBlock { Text = "10%", Foreground = Brushes.White }; DataChangePulse.SetValue(text, "10%");
        var root = new StackPanel(); root.Children.Add(text); root.Children.Add(plot); DataChangePulse.SetContext(root, "A");
        var window = Window(root);
        async Task Change()
        {
            plot.EndAt = plot.EndAt.AddSeconds(1); plot.DataAt = plot.EndAt; plot.Points = [.. plot.Points!, new(plot.EndAt, ++value)];
            DataChangePulse.SetValue(text, $"{value}%"); await Settle(); Assert.True(plot.DataPulseActive); Assert.Equal(2, DataChangePulse.ActiveClocks);
        }
        try
        {
            window.Show(); await Settle(); await Change();
            DataChangePulse.SetContext(root, "B"); plot.ScaleKey = "B";
            Assert.False(plot.DataPulseActive); Assert.Equal(0, DataChangePulse.ActiveClocks);
            // Values arriving in either binding order during a switch form one non-flashing baseline.
            DataChangePulse.SetValue(text, "40%"); plot.Points = [new(plot.EndAt, 40)]; value = 40;
            await Settle(); Assert.False(plot.DataPulseActive); Assert.Equal(0, DataChangePulse.ActiveClocks);
            await Change(); window.Hide(); Assert.False(plot.DataPulseActive); Assert.Equal(0, DataChangePulse.ActiveClocks);
            window.Show(); await Settle(); Assert.False(plot.DataPulseActive); await Change();
            ColorStateAssist.SetReduceMotion(root, true); Assert.Equal(0, DataChangePulse.ActiveClocks);
            ColorStateAssist.SetReduceMotion(root, false); await Settle(); await Change();
            LineMotion.SetIsSuspended(root, true); Assert.Equal(0, DataChangePulse.ActiveClocks);
            LineMotion.SetIsSuspended(root, false); await Settle(); await Change();
            ThemeService.Apply(preference == ThemePreference.Light ? ThemePreference.Dark : ThemePreference.Light, persist: null);
            Assert.Equal(0, DataChangePulse.ActiveClocks); await Settle(); await Change();
            for (var index = 0; index < 12; index++)
            {
                plot.ScaleKey = $"switch-{index}"; DataChangePulse.SetContext(root, plot.ScaleKey);
                plot.Points = [new(plot.EndAt, value + index)]; DataChangePulse.SetValue(text, $"{value + index}%");
            }
            await Settle(); Assert.False(plot.DataPulseActive); Assert.Equal(0, DataChangePulse.ActiveClocks);
            DataChangePulse.SetContext(root, null); await Settle(); DataChangePulse.SetValue(text, "99%"); await Settle();
            Assert.Equal(0, DataChangePulse.ActiveClocks); // Unavailable/remote scope has no eligible observation context.
            DataChangePulse.SetContext(root, "A-again"); await Settle();
            await Change(); window.Content = null; await Settle(); Assert.Equal(0, DataChangePulse.ActiveClocks);
            Assert.Equal(watchers, DataChangePulse.ActiveWatchers);
            using var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes(); var stoppedAt = Stopwatch.GetTimestamp();
            await Task.Delay(1100); Assert.Equal(0, DataChangePulse.ActiveClocks); Assert.Equal(watchers, DataChangePulse.ActiveWatchers);
            if (Environment.GetEnvironmentVariable("SA_DATA_PULSE_EVIDENCE") is { Length: > 0 } evidence)
            {
                process.Refresh(); Directory.CreateDirectory(evidence);
                await File.WriteAllTextAsync(Path.Combine(evidence, "cleanup.json"), JsonSerializer.Serialize(new
                {
                    scope = "Own test-host PID after the two helper owners unload; not production GUI/Agent, sampler or selection performance",
                    afterLastDetachMs = Stopwatch.GetElapsedTime(stoppedAt).TotalMilliseconds,
                    cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                    activeClocks = DataChangePulse.ActiveClocks, activeWatchers = DataChangePulse.ActiveWatchers, baselineWatchers = watchers,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally { window.Close(); ThemeService.Apply(preference, persist: null); }
    });

    private static DashboardPlot Plot(DateTimeOffset now, bool light)
    {
        var plot = new DashboardPlot { Width = 420, Height = 180, EndAt = now, WindowDuration = ResourceTrendBuffer.Window,
            ScaleKey = "A", DataAt = now.AddSeconds(-1), Points = Enumerable.Range(-5, 5).Select(second => new PlotPoint(now.AddSeconds(second), 10)).ToArray() };
        plot.Resources["Semantic.Brush.SurfacePrimary"] = new SolidColorBrush(light ? Color.FromRgb(250, 250, 250) : Color.FromRgb(19, 19, 21));
        plot.Resources["Semantic.Brush.StatusFailed"] = new SolidColorBrush(Color.FromRgb(225, 29, 72));
        plot.Resources["Semantic.Brush.TextPrimary"] = new SolidColorBrush(light ? Color.FromRgb(30, 30, 35) : Color.FromRgb(228, 228, 231));
        plot.Resources["Semantic.Brush.HistoryHealthy"] = new SolidColorBrush(light ? Color.FromRgb(82, 82, 91) : Color.FromRgb(48, 48, 54));
        return plot;
    }
    private static Window Window(UIElement content) => new() { Content = content, Width = 450, Height = 340, ShowActivated = false, Left = -32000, Top = -32000 };
    private static void EnsureApp()
    { if (Application.Current is null) { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); } }
    private static async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static RenderTargetBitmap Capture(DashboardPlot plot, string name)
    {
        var image = new RenderTargetBitmap((int)plot.ActualWidth, (int)plot.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, plot.ActualWidth, plot.ActualHeight);
            drawing.DrawRectangle((System.Windows.Media.Brush)plot.FindResource("Semantic.Brush.SurfacePrimary"), null, bounds);
            drawing.DrawRectangle(new VisualBrush(plot) { Stretch = Stretch.Fill }, null, bounds);
        }
        image.Render(visual); // Element-local bounds, not the child's translated position inside its owner.
        if (Environment.GetEnvironmentVariable("SA_DATA_PULSE_EVIDENCE") is { Length: > 0 } root)
        {
            Directory.CreateDirectory(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(Path.Combine(root, name + ".png")); encoder.Save(stream);
        }
        return image;
    }
    private static IReadOnlyList<(int X, int Y)> DiffPixels(RenderTargetBitmap first, RenderTargetBitmap second)
    {
        var stride = first.PixelWidth * 4;
        var a = new byte[stride * first.PixelHeight];
        var b = new byte[stride * second.PixelHeight];
        first.CopyPixels(a, stride, 0);
        second.CopyPixels(b, stride, 0);
        var result = new List<(int, int)>();
        for (var y = 0; y < first.PixelHeight; y++) for (var x = 0; x < first.PixelWidth; x++)
        {
            var i = y * stride + x * 4;
            if (Math.Abs(a[i] - b[i]) > 6 || Math.Abs(a[i + 1] - b[i + 1]) > 6 || Math.Abs(a[i + 2] - b[i + 2]) > 6)
            { result.Add((x, y)); }
        }
        return result;
    }
    private static IReadOnlyList<(int X, int Y)> RedPixels(RenderTargetBitmap image)
    {
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        var result = new List<(int, int)>();
        for (var y = 0; y < image.PixelHeight; y++) for (var x = 0; x < image.PixelWidth; x++)
        { var index = (y * image.PixelWidth + x) * 4; if (pixels[index + 2] > 100 && pixels[index + 2] > pixels[index + 1] * 2) result.Add((x, y)); }
        return result;
    }
}
