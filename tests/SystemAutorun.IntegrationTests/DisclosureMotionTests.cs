using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class DisclosureMotionTests
{
    [Fact]
    public Task ReducedMotionReachesBothEndpointsWithoutClocksOrHiddenInputs() => WithWindow(async (window, expander, body) =>
    {
        ColorStateAssist.SetReduceMotion(window, true);
        Assert.Equal(0, body.Progress); Assert.Equal(Visibility.Hidden, body.Child.Visibility);
        expander.IsExpanded = true; window.UpdateLayout();
        Assert.Equal(1, body.Progress); Assert.Equal(1, body.Opacity); Assert.Equal(180, body.ChevronAngle);
        Assert.False(Animated(body)); Assert.True(body.IsHitTestVisible); Assert.True(body.ActualHeight > 0);
        Assert.Equal(Visibility.Visible, body.Child.Visibility);
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        var chevron = Assert.IsType<LineIcon>(expander.Template.FindName("DisclosureChevron", expander));
        Assert.Equal(180, Assert.IsType<System.Windows.Media.RotateTransform>(chevron.RenderTransform).Angle);
        expander.IsExpanded = false; window.UpdateLayout();
        Assert.Equal(0, body.Progress); Assert.Equal(0, body.ActualHeight); Assert.Equal(0, body.ChevronAngle);
        Assert.False(Animated(body)); Assert.False(body.IsHitTestVisible);
        Assert.Equal(Visibility.Hidden, body.Child.Visibility);
        Assert.Equal(KeyboardNavigationMode.None, System.Windows.Input.KeyboardNavigation.GetTabNavigation(body));
        var header = Assert.IsType<ToggleButton>(expander.Template.FindName("HeaderSite", expander));
        Assert.Null(header.FocusVisualStyle); Assert.Equal(new Thickness(0), header.BorderThickness);
        Assert.Equal(ColorFocusRole.Foreground, ColorStateAssist.GetRole(header));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    });

    [Fact]
    public Task InterruptedDisclosureReversesFromTheDisplayedProgressAndReleasesClocks() => WithWindow(async (window, expander, body) =>
    {
        ColorStateAssist.SetReduceMotion(window, false);
        expander.IsExpanded = true;
        if (!SystemParameters.ClientAreaAnimation)
        { Assert.Equal(1, body.Progress); Assert.False(Animated(body)); return; }
        Assert.True(Animated(body), $"loaded={body.IsLoaded}, visible={body.IsVisible}, reduced={ColorStateAssist.GetReduceMotion(body)}, expanded={body.IsExpanded}, progress={body.Progress}");
        await Task.Delay(45); await Dispatcher.Yield(DispatcherPriority.Render);
        var entering = body.Progress; Assert.InRange(entering, 0, 1);
        expander.IsExpanded = false;
        Assert.InRange(Math.Abs(body.Progress - entering), 0, .02);
        Assert.False(body.IsHitTestVisible);
        expander.IsExpanded = true;
        await Settle(body);
        Assert.Equal(1, body.Progress); Assert.Equal(1, body.Opacity); Assert.True(body.IsHitTestVisible);
        expander.IsExpanded = false; await Settle(body);
        Assert.Equal(0, body.Progress); Assert.Equal(0, body.Opacity); Assert.Equal(Visibility.Hidden, body.Child.Visibility);
        // A late completion from an older direction must not revive the body.
        await Task.Delay(200); Assert.Equal(0, body.Progress); Assert.False(Animated(body));
    });

    [Fact]
    public Task ChangingReducedMotionMidFlightSettlesImmediatelyAndClosingReleasesClocks() => WithWindow(async (window, expander, body) =>
    {
        ColorStateAssist.SetReduceMotion(window, false); expander.IsExpanded = true;
        ColorStateAssist.SetReduceMotion(window, true);
        Assert.Equal(1, body.Progress); Assert.False(Animated(body));
        ColorStateAssist.SetReduceMotion(window, false); expander.IsExpanded = false;
        window.Close(); await window.ClosureTask.WaitAsync(TimeSpan.FromSeconds(5));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(body.IsLoaded); Assert.Equal(0, body.Progress); Assert.False(Animated(body));
    });

    [Fact]
    public Task ExpandedBodyRemeasuresWhenWidthOrContentChanges() => WithWindow(async (window, expander, body) =>
    {
        var content = new TextBlock { Text = new string('测', 80), TextWrapping = TextWrapping.Wrap };
        expander.Content = content;
        ColorStateAssist.SetReduceMotion(window, true); expander.IsExpanded = true; window.UpdateLayout();
        var before = body.ActualHeight;
        content.Text = new string('测', 800);
        window.Width = 480; window.UpdateLayout();
        Assert.Equal(1, body.Progress); Assert.True(body.ActualHeight > before);
        Assert.Equal(content.DesiredSize.Height, body.ActualHeight);
        Assert.False(Animated(body));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task DisclosureBodyUsesSemanticTextInBothThemes(bool light) => WithWindow(async (window, expander, body) =>
    {
        var previous = ThemeService.IsLight ? ThemePreference.Light : ThemePreference.Dark;
        try
        {
            ThemeService.Apply(light ? ThemePreference.Light : ThemePreference.Dark, persist: null);
            expander.IsExpanded = true; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var expected = Assert.IsType<System.Windows.Media.SolidColorBrush>(window.FindResource("Semantic.Brush.TextPrimary")).Color;
            var caption = Descendants(body).OfType<TextBlock>().Single(text => text.Text == "健康检查");
            Assert.Equal(expected, Assert.IsType<System.Windows.Media.SolidColorBrush>(caption.Foreground).Color);
            Assert.Equal(expected, Assert.IsType<System.Windows.Media.SolidColorBrush>(expander.Foreground).Color);
            var scroll = Descendants(window).OfType<ScrollViewer>().First(view => view.Content is StackPanel);
            scroll.ScrollToBottom(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            StartupGuiWindowTests.CaptureStateEvidence(window, $"disclosure-{(light ? "light" : "dark")}-scrolled.png");
        }
        finally { ThemeService.Apply(previous, persist: null); }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var nested in Descendants(child)) { yield return nested; }
        }
    }
    private static bool Animated(DisclosurePresenter body) => body.HasAnimatedProperties;
    private static async Task Settle(DisclosurePresenter body)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (Animated(body) && DateTime.UtcNow < until) { await Task.Delay(20); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
        Assert.False(Animated(body));
    }
    private static Task WithWindow(Func<AddProgramWindow, Expander, DisclosurePresenter, Task> action) => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        if (Application.Current is not SystemAutorun.App.App)
        { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
        WindowTheme.Install();
        var window = new AddProgramWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        ColorStateAssist.SetReduceMotion(window, true);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var body = Assert.IsType<DisclosurePresenter>(window.AdvancedExpander.Template.FindName("DisclosureBody", window.AdvancedExpander));
            await action(window, window.AdvancedExpander, body);
            var probe = Environment.GetEnvironmentVariable("SA_DISCLOSURE_PROBE");
            if (probe is not null)
            {
                var directory = new DirectoryInfo(Path.GetFullPath(probe));
                Assert.True(directory.Exists && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint));
                ColorStateAssist.SetReduceMotion(window, false); window.AdvancedExpander.IsExpanded = false;
                window.Title = "R107 disclosure · isolated"; window.Left = 100; window.Top = 100;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var samples = new List<string> { "milliseconds,expanded,progress,opacity,clock" };
                var property = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(DisclosurePresenter.ProgressProperty, typeof(DisclosurePresenter));
                EventHandler record = (_, _) => { if (samples.Count < 4096) { samples.Add(FormattableString.Invariant($"{clock.ElapsedMilliseconds},{body.IsExpanded},{body.Progress:R},{body.Opacity:R},{body.HasAnimatedProperties}")); } };
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult(); property.AddValueChanged(body, record);
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(probe, "ready.txt"), $"PID={Environment.ProcessId}\nHWND={new System.Windows.Interop.WindowInteropHelper(window).Handle}\nDPI={System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX}\nNo Agent, configuration, executor or native startup adapter. Close with caption X (modeless probe).\n");
                    await closed.Task.WaitAsync(TimeSpan.FromMinutes(5));
                }
                finally { property.RemoveValueChanged(body, record); await File.WriteAllLinesAsync(Path.Combine(probe, "frames.csv"), samples); }
            }
        }
        finally
        {
            ColorStateAssist.SetReduceMotion(window, true); window.Close();
            await window.ClosureTask.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }, Environment.GetEnvironmentVariable("SA_DISCLOSURE_PROBE") is null ? null : TimeSpan.FromMinutes(6));
}
