using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SystemAutorun.App.Services;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class ReadingMotionTests
{
    private static Task WithHost(Func<StackPanel, Task> action) => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        if (System.Windows.Application.Current is not SystemAutorun.App.App) { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
        var host = new StackPanel();
        var window = new Window { Content = host, Width = 300, Height = 200, ShowActivated = false, Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await action(host);
        }
        finally { window.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    });

    private static async Task<TElement> Add<TElement>(StackPanel host, TElement element) where TElement : FrameworkElement
    {
        host.Children.Add(element);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        return element;
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!done() && DateTime.UtcNow < deadline) { await Task.Delay(20); }
        Assert.True(done());
    }

    [Fact]
    public Task RollingTextCountsWithinTheSameShapeAndEndsExact() => WithHost(async host =>
    {
        if (!SystemParameters.ClientAreaAnimation) { return; } // the machine setting snaps by design
        var text = await Add(host, new TextBlock());
        SurfaceMotion.SetRollingText(text, "10%");
        Assert.Equal("10%", text.Text); // nothing to count from
        SurfaceMotion.SetRollingText(text, "90%");
        Assert.True(text.HasAnimatedProperties);
        await WaitUntil(() => text.Text == "90%" && !text.HasAnimatedProperties);
    });

    [Theory]
    [InlineData("12 MB", "1 GB")]
    [InlineData("--", "37%")]
    [InlineData("37%", "--")]
    public Task RollingTextSwitchesImmediatelyWhenTheShapeChanges(string from, string to) => WithHost(async host =>
    {
        var text = await Add(host, new TextBlock());
        SurfaceMotion.SetRollingText(text, from);
        SurfaceMotion.SetRollingText(text, to);
        Assert.Equal(to, text.Text);
        Assert.False(text.HasAnimatedProperties);
    });

    [Fact]
    public Task ReducedMotionSnapsReadingsAndMeters() => WithHost(async host =>
    {
        ColorStateAssist.SetReduceMotion(host, true);
        var text = await Add(host, new TextBlock());
        var meter = await Add(host, new ProgressBar { Maximum = 100 });
        SurfaceMotion.SetRollingText(text, "10%");
        SurfaceMotion.SetRollingText(text, "90%");
        SurfaceMotion.SetMeterValue(meter, 80);
        Assert.Equal("90%", text.Text);
        Assert.Equal(80, meter.Value);
        Assert.False(text.HasAnimatedProperties);
        Assert.False(meter.HasAnimatedProperties);
    });

    [Fact]
    public Task MeterEasesFromTheDisplayedValueAndSettlesOnTheReading() => WithHost(async host =>
    {
        if (!SystemParameters.ClientAreaAnimation) { return; } // the machine setting snaps by design
        var meter = await Add(host, new ProgressBar { Maximum = 100 });
        SurfaceMotion.SetMeterValue(meter, 80);
        Assert.True(meter.HasAnimatedProperties);
        await Task.Delay(60);
        var shown = meter.Value;
        Assert.InRange(shown, 0.01, 79.99);
        SurfaceMotion.SetMeterValue(meter, 20); // reverses mid-way, without jumping back to zero
        Assert.True(meter.Value > 0);
        await WaitUntil(() => !meter.HasAnimatedProperties);
        Assert.Equal(20, meter.Value);
    });

    [Fact]
    public Task HiddenMeterTakesTheReadingDirectly() => WithHost(async host =>
    {
        var meter = await Add(host, new ProgressBar { Maximum = 100, Visibility = Visibility.Collapsed });
        SurfaceMotion.SetMeterValue(meter, 55);
        Assert.Equal(55, meter.Value);
        Assert.False(meter.HasAnimatedProperties);
    });

    [Fact]
    public Task CheckBoxGlyphFadesAndReverses() => WithHost(async host =>
    {
        if (!SystemParameters.ClientAreaAnimation) { return; } // the machine setting snaps by design
        var box = new CheckBox();
        box.Style = (Style)System.Windows.Application.Current.FindResource(typeof(CheckBox));
        await Add(host, box);
        box.ApplyTemplate();
        var glyph = Assert.IsType<System.Windows.Shapes.Path>(box.Template.FindName("CheckedGlyph", box));
        Assert.Equal(0, glyph.Opacity);
        box.IsChecked = true;
        Assert.True(glyph.HasAnimatedProperties);
        await WaitUntil(() => !glyph.HasAnimatedProperties);
        Assert.Equal(1, glyph.Opacity);
        box.IsChecked = false;
        await WaitUntil(() => !glyph.HasAnimatedProperties);
        Assert.Equal(0, glyph.Opacity);
    });
}
