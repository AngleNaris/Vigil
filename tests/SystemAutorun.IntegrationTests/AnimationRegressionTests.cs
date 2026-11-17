using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class AnimationRegressionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task TracedEdgesUseOneSolidColorIncludingCornersAndUniformSettling(bool light) => GuiConfigurationControlTests.RunStaAsync(() =>
    {
        var color = light ? Color.FromRgb(225, 225, 229) : Color.FromRgb(39, 39, 42);
        var rest = new SolidColorBrush(color) { Opacity = .7 };
        foreach (var progress in new[] { .25, .5, .75, 1 })
        foreach (var strength in new[] { 1d, .75, .5, .25 })
        {
            var brush = Assert.IsType<DrawingBrush>(LineMotion.DrawLines(rest, Colors.Red,
                new Size(100, 60), new Thickness(1), progress, strength));
            Assert.True(brush.IsFrozen);
            var drawing = Assert.IsType<DrawingGroup>(brush.Drawing);
            var edges = drawing.Children.Cast<GeometryDrawing>().Skip(1).ToArray();
            var ink = Assert.IsType<SolidColorBrush>(edges[0].Brush);
            Assert.All(edges, edge => Assert.Same(ink, edge.Brush)); // Never a gradient, even across orthogonal corners.
            Assert.Equal(color.ScR + (Colors.Red.ScR - color.ScR) * strength, ink.Color.ScR, 5);
            Assert.Equal(color.ScG + (Colors.Red.ScG - color.ScG) * strength, ink.Color.ScG, 5);
            Assert.Equal(color.ScB + (Colors.Red.ScB - color.ScB) * strength, ink.Color.ScB, 5);
            Assert.Equal(.7, drawing.Opacity, 7);
        }
        Assert.Equal(color, rest.Color); Assert.Equal(.7, rest.Opacity); Assert.False(rest.IsFrozen);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task FrozenLineStatesSettleAtTheRealBrushBeforeAnimationRemoval(bool light) => GuiConfigurationControlTests.RunStaAsync(() =>
    {
        var color = light ? Color.FromArgb(180, 225, 225, 229) : Color.FromArgb(180, 39, 39, 42);
        var rest = new SolidColorBrush(color) { Opacity = .7 };
        var animation = LineMotion.MakeLines(rest, Colors.Red, new Size(400, 1), new Thickness(0, 0, 0, 1), 40);
        Assert.InRange(animation.KeyFrames.Count, 1, 17);
        Assert.Equal(16, animation.KeyFrames.Cast<ObjectKeyFrame>().Select(frame => frame.Value).Distinct().Count());
        Assert.All(animation.KeyFrames.Cast<ObjectKeyFrame>(), frame => Assert.True(Assert.IsAssignableFrom<Brush>(frame.Value).IsFrozen));
        Assert.True(animation.KeyFrames[^1].KeyTime.TimeSpan < animation.Duration.TimeSpan);
        var settled = Assert.IsType<SolidColorBrush>(animation.KeyFrames[^1].Value);
        Assert.Equal(color, settled.Color); Assert.Equal(rest.Opacity, settled.Opacity);
        var clock = animation.CreateClock(); clock.Controller!.Begin(); clock.Controller.Pause();
        var end = 40 + LineMotion.GrowMilliseconds;
        float previous = float.PositiveInfinity;
        foreach (var offset in new[] { 0d, 20d, 40d, 60d })
        {
            clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(end + offset), TimeSeekOrigin.BeginTime);
            var drawing = Assert.IsType<DrawingGroup>(Assert.IsType<DrawingBrush>(clock.GetCurrentValue(rest, rest)).Drawing);
            var ink = Assert.IsType<SolidColorBrush>(Assert.IsType<GeometryDrawing>(drawing.Children[1]).Brush).Color;
            var distance = Math.Abs(ink.ScR - color.ScR) + Math.Abs(ink.ScG - color.ScG) + Math.Abs(ink.ScB - color.ScB);
            Assert.True(distance < previous); previous = distance;
            Assert.Equal(rest.Opacity * color.ScA, drawing.Opacity, 7);
        }
        clock.Controller.SeekAlignedToLastTick(animation.Duration.TimeSpan - TimeSpan.FromMilliseconds(1), TimeSeekOrigin.BeginTime);
        var visible = Assert.IsType<SolidColorBrush>(clock.GetCurrentValue(rest, rest));
        Assert.True(visible.IsFrozen); // WPF copies the timeline; compare the final visual value, not brush identity.
        Assert.Equal(settled.Color, visible.Color); Assert.Equal(settled.Opacity, visible.Opacity);
        clock.Controller.Remove();
        Assert.Equal(color, rest.Color); Assert.False(rest.IsFrozen);
        return Task.CompletedTask;
    });

    [Fact]
    public Task SemanticRegionsRelayAndLocalElementsStaggerWithinABoundedEntrance() => GuiConfigurationControlTests.RunStaAsync(() =>
    {
        foreach (var viewport in new[] { new Size(900, 600), new Size(1600, 900) })
        {
            var schedule = new LineMotion.EntranceSchedule(viewport);
            for (var region = 0; region <= 4; region++)
            {
                var parent = new StackPanel(); LineMotion.SetRegion(parent, region);
                var text = new TextBlock { Text = "有限文本分段，不按字符测量。", Width = 400 };
                parent.Children.Add(text);
                var bounds = new Rect(0, 0, 400, 30); // Same position: only semantic order distinguishes regions.
                Assert.Equal(region, LineMotion.GetRegion(text));
                var icon = schedule.Next(text, bounds, EntranceStage.Label);
                var label = schedule.Next(text, bounds, EntranceStage.Label);
                var readout = schedule.Next(text, bounds, EntranceStage.Readout);
                var plot = schedule.Next(text, bounds, EntranceStage.Plot);
                Assert.Equal(region * 100 + 100, icon);
                Assert.Equal(45, label - icon);
                Assert.True(label < readout && readout < plot);
                Assert.Equal(100, plot - readout);
                text.Measure(new Size(400, 100)); text.Arrange(bounds);
                var reveal = GlyphReveal.Create(text, label)!.Value;
                var timeline = Assert.IsType<ObjectAnimationUsingKeyFrames>(reveal.Animation);
                Assert.InRange(timeline.KeyFrames.Count, 3, 6);
                Assert.True(timeline.Duration.TimeSpan.TotalMilliseconds <= 900);
                for (var slot = 0; slot < 150; slot++)
                { Assert.True(schedule.Next(text, bounds, EntranceStage.Plot) + 180 <= 900); }
            }
        }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task HostMetersEnterAfterReadoutsWithoutReplayingTheirTemplateLines(bool light) => FakeUiMotionTests.WithMain(light, async (_, main) =>
    {
        ColorStateAssist.SetReduceMotion(main, true);
        main.HostsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var page = main.MonitoringTabs.HostPage;
        var meters = Descendants(page).OfType<ProgressBar>().ToArray();
        Assert.Single(meters); // R121 keeps only the fixture's storage-volume meter; resources use four plots.
        var values = meters.Select(meter => meter.Value).ToArray();
        Assert.Equal(new[] { 4 }, meters.Select(LineMotion.GetRegion));
        var plots = Descendants(page).OfType<DashboardPlot>().ToArray();
        Assert.Equal(4, plots.Length);
        Assert.Equal(new[] { 1, 2, 3, 4 }, plots.Select(LineMotion.GetRegion));
        // The four taller resource plots place the retained volume below the initial viewport.
        meters[0].BringIntoView();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
        Assert.True(meters[0].TransformToAncestor(page).TransformBounds(new Rect(meters[0].RenderSize)).IntersectsWith(new Rect(page.RenderSize)));
        ColorStateAssist.SetReduceMotion(main, false);
        if (SystemParameters.ClientAreaAnimation)
        {
            Assert.True(LineMotion.Play(page));
            foreach (var meter in meters)
            {
                Assert.True(DependencyPropertyHelper.GetValueSource(meter, UIElement.OpacityProperty).IsAnimated);
                foreach (var name in new[] { "PART_Track", "PART_Indicator" })
                {
                    var line = Assert.IsType<Border>(meter.Template.FindName(name, meter));
                    Assert.False(DependencyPropertyHelper.GetValueSource(line, Border.BackgroundProperty).IsAnimated);
                    Assert.False(line.Background is DrawingBrush);
                }
            }
            await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(page));
        }
        Assert.Equal(values, meters.Select(meter => meter.Value));
        Assert.All(meters, meter => Assert.Equal(1, meter.Opacity));
        static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index); yield return child;
                foreach (var item in Descendants(child)) { yield return item; }
            }
        }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task ResourcePollingAndTransientSelectionResetNeverReplayTheDetailEntrance(bool light) => FakeUiMotionTests.WithMain(light, async (model, main) =>
    {
        ColorStateAssist.SetReduceMotion(main, true);
        Seed(model);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        ColorStateAssist.SetReduceMotion(main, false);
        var detail = main.ServicesPage.Children.OfType<Grid>().Single(grid => Grid.GetColumn(grid) == 1);
        var selected = model.SelectedTask!;
        var root = Assert.IsAssignableFrom<FrameworkElement>(main.FindName("MainScene"));
        for (var tick = 0; tick < 5; tick++)
        {
            // A fresh IPC deserialization has equal names with different string identities.
            Seed(model);
            Assert.Same(selected, model.SelectedTask);
            if (tick == 2) { model.SelectedTask = null; model.SelectedTask = selected; }
            model.TickDashboard();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, detail.Opacity);
            Assert.False(LineMotion.IsPlaying(detail), $"same identity re-entered on resource tick {tick}");
            Assert.False(LineMotion.IsPlaying(root));
            Assert.False(LineMotion.IsPlaying(main.ServicesPage));
            Assert.False(DependencyPropertyHelper.GetValueSource(detail, UIElement.OpacityProperty).IsAnimated);
            await Task.Delay(220);
        }
        // A real selection still owns one entrance, including after rapid A → B → A.
        model.SelectedTask = model.Tasks[1];
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (SystemParameters.ClientAreaAnimation) { Assert.True(LineMotion.IsPlaying(detail)); }
        await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(detail));
        model.SelectedTask = selected;
        model.SelectedTask = model.Tasks[1];
        model.SelectedTask = selected;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (SystemParameters.ClientAreaAnimation) { Assert.True(LineMotion.IsPlaying(detail)); }
        await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(detail));
        Assert.Same(selected, main.ServiceList.SelectedItem);
        Assert.Equal(1, detail.Opacity);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task LogSelectionAnimatesTheHeaderAndTheNativeEditorFrameAndRestoresItsBrush(bool light) => FakeUiMotionTests.WithMain(light, async (model, main) =>
    {
        ColorStateAssist.SetReduceMotion(main, true);
        Logs(model);
        main.ExceptionLogsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        ColorStateAssist.SetReduceMotion(main, false);
        var editor = main.MonitoringTabs.ExceptionDetailsText;
        var panel = Assert.IsType<Grid>(VisualTreeHelper.GetParent(editor));
        var header = panel.Children.OfType<Border>().Single();
        var original = Assert.IsType<SolidColorBrush>(editor.BorderBrush).Color;
        var originalSource = DependencyPropertyHelper.GetValueSource(editor, Control.BorderBrushProperty).BaseValueSource;
        for (var index = 1; index >= 0; index--)
        {
            model.SelectedExceptionLog = model.ExceptionLogs[index];
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(model.SelectedExceptionLog.Message, editor.Text);
            if (SystemParameters.ClientAreaAnimation)
            {
                Assert.True(LineMotion.IsPlaying(panel));
                Assert.Equal(1, editor.Opacity); // The frame must be visible during drawing, not hidden until drawing has finished.
                var content = Assert.IsAssignableFrom<FrameworkElement>(editor.Template.FindName("PART_ContentHost", editor));
                Assert.True(DependencyPropertyHelper.GetValueSource(content, UIElement.OpacityProperty).IsAnimated);
                Assert.True(DependencyPropertyHelper.GetValueSource(header, Border.BorderBrushProperty).IsAnimated);
                Assert.True(DependencyPropertyHelper.GetValueSource(editor, Control.BorderBrushProperty).IsAnimated,
                    "Native editor was previously an early-return surface with an instant, unanimated frame.");
            }
            await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(panel));
            Assert.Equal(original, Assert.IsType<SolidColorBrush>(editor.BorderBrush).Color);
            Assert.Equal(originalSource, DependencyPropertyHelper.GetValueSource(editor, Control.BorderBrushProperty).BaseValueSource);
            Assert.False(DependencyPropertyHelper.GetValueSource(editor, Control.BorderBrushProperty).IsAnimated);
            Assert.Equal(new Thickness(1), editor.BorderThickness);
            Assert.True(editor.IsReadOnly);
            Assert.Null(editor.FocusVisualStyle);
        }
        model.SelectedExceptionLog = model.ExceptionLogs[1];
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        ColorStateAssist.SetReduceMotion(main, true);
        Assert.False(LineMotion.IsPlaying(panel));
        Assert.Equal(original, Assert.IsType<SolidColorBrush>(editor.BorderBrush).Color);
        Assert.Equal(originalSource, DependencyPropertyHelper.GetValueSource(editor, Control.BorderBrushProperty).BaseValueSource);
    });

    [Fact]
    public Task TemplateChildLoadsDoNotRestartAnAlreadyVisiblePopupFade() => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        if (Application.Current is not SystemAutorun.App.App)
        { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
        var stack = new StackPanel();
        var surface = new Border { Child = stack };
        SurfaceMotion.SetFadeOnShow(surface, true);
        var window = new Window { Content = surface, Width = 300, Height = 200, ShowActivated = false,
            Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await PresenceMotionTests.Until(() => !surface.HasAnimatedProperties);
            for (var index = 0; index < 5; index++)
            {
                stack.Children.Add(new TextBlock { Text = "Child load, not a new popup" });
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.False(surface.HasAnimatedProperties);
                Assert.Equal(1, surface.Opacity);
            }
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task SharedHoverRecipesOnlyChangeColorAndNeverStartEntranceOrOpacityPulses(bool light) => FakeUiMotionTests.WithMain(light, async (_, main) =>
    {
        ColorStateAssist.SetReduceMotion(main, true);
        var inspected = 0;
        foreach (var styleName in new[] { "DashboardIconButton", "DashboardTabButton", "DashboardRangeButton", "DashboardSequenceItem", "PrimaryButtonStyle", "DangerButtonStyle" })
        {
            var style = Assert.IsType<Style>(main.FindResource(styleName));
            var control = style.TargetType == typeof(ListBoxItem) ? (Control)new ListBoxItem() : new Button();
            control.Style = style;
            var template = control.Template;
            Assert.NotNull(template);
            foreach (var triggers in new[] { style.Triggers, template.Triggers })
            foreach (var trigger in triggers.OfType<Trigger>().Where(trigger => trigger.Property == UIElement.IsMouseOverProperty))
            {
                inspected++;
                Assert.Empty(trigger.EnterActions); Assert.Empty(trigger.ExitActions);
                Assert.All(trigger.Setters.OfType<Setter>(), setter => Assert.DoesNotContain(setter.Property,
                    new[] { UIElement.OpacityProperty, UIElement.ClipProperty, UIElement.OpacityMaskProperty, SurfaceMotion.TransitionKeyProperty }));
            }
        }
        Assert.True(inspected >= 6);
        var surface = new Border { Width = 40, Height = 30 };
        main.ServicesPage.Children.Add(surface);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        SurfaceMotion.SetFill(surface, Brushes.Transparent);
        ColorStateAssist.SetReduceMotion(main, false);
        var target = (SolidColorBrush)main.FindResource("Semantic.Brush.SurfaceHover");
        SurfaceMotion.SetFill(surface, target);
        Assert.False(DependencyPropertyHelper.GetValueSource(surface, UIElement.OpacityProperty).IsAnimated);
        Assert.False(DependencyPropertyHelper.GetValueSource(surface, UIElement.ClipProperty).IsAnimated);
        Assert.False(LineMotion.IsPlaying(main.ServicesPage));
        await PresenceMotionTests.Until(() => ReferenceEquals(surface.Background, target));
        Assert.Equal(1, surface.Opacity);
        main.ServicesPage.Children.Remove(surface);
    });

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public Task TransparentHoverInterpolationCannotOvershootItsVisibleEndpoints(bool light, bool entering) => GuiConfigurationControlTests.RunStaAsync(() =>
    {
        var backdrop = light ? Color.FromRgb(244, 244, 246) : Color.FromRgb(8, 8, 10);
        var hover = light ? Color.FromRgb(228, 228, 232) : Color.FromRgb(24, 24, 28);
        var from = entering ? Colors.Transparent : hover;
        var to = entering ? hover : Colors.Transparent;
        var animation = SurfaceMotion.FillAnimation(from, to);
        var clock = animation.CreateClock(); clock.Controller!.Begin(); clock.Controller.Pause();
        static double[] Composite(Color color, Color behind) => new[]
        {
            color.R * color.A / 255d + behind.R * (1 - color.A / 255d),
            color.G * color.A / 255d + behind.G * (1 - color.A / 255d),
            color.B * color.A / 255d + behind.B * (1 - color.A / 255d),
        };
        var initial = Composite(from, backdrop); var final = Composite(to, backdrop); var previous = initial;
        for (var time = 0; time <= animation.Duration.TimeSpan.TotalMilliseconds; time++)
        {
            clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(time), TimeSeekOrigin.BeginTime);
            var displayed = Composite((Color)clock.GetCurrentValue(from, to), backdrop);
            for (var channel = 0; channel < 3; channel++)
            {
                Assert.InRange(displayed[channel], Math.Min(initial[channel], final[channel]) - .01, Math.Max(initial[channel], final[channel]) + .01);
                if (final[channel] >= initial[channel]) { Assert.True(displayed[channel] >= previous[channel] - .01); }
                else { Assert.True(displayed[channel] <= previous[channel] + .01); }
            }
            previous = displayed;
        }
        clock.Controller.Remove();
        return Task.CompletedTask;
    });

    [Fact]
    public Task ClosingBackdropOwnerUnsubscribesEvenBeforeDispatcherTeardown() => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        if (Application.Current is not SystemAutorun.App.App)
        { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
        var backdrop = new DialogBackdrop { IsPresented = true };
        var window = new Window { Content = backdrop, Width = 300, Height = 200, ShowActivated = false,
            Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual };
        var subscribed = typeof(DialogBackdrop).GetField("_subscribed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True((bool)subscribed.GetValue(backdrop)!);
            window.Close();
            Assert.False((bool)subscribed.GetValue(backdrop)!);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task HoveringInteractiveFrameOrSwitchingDuringPageEntrancePreservesLineTransitionAndRestingBrush() =>
        FakeUiMotionTests.WithMain(false, async (model, main) =>
    {
        Seed(model);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        FrameworkElement? detail = null;
        Border? plotFrame = null;
        for (DependencyObject? cur = main.ServiceHealthPlot; cur is not null; cur = VisualTreeHelper.GetParent(cur))
        {
            if (plotFrame is null && cur is Border b && SurfaceMotion.GetStroke(b) is not null) { plotFrame = b; }
            if (cur is FrameworkElement fe && SurfaceMotion.GetTransitionKey(fe) is not null) { detail = fe; break; }
        }
        Assert.NotNull(detail);
        Assert.NotNull(plotFrame);
        var restColor = ((SolidColorBrush)main.FindResource("Semantic.Brush.BorderSubtle")).Color;
        var hoverBrush = (SolidColorBrush)main.FindResource("Semantic.Brush.InteractionHover");

        // Simulate hovering an interactive frame right as a selection switch occurs during page entrance.
        Assert.True(LineMotion.Play(main.ServicesPage));
        Assert.True(LineMotion.IsPlaying(main.ServicesPage));
        SurfaceMotion.SetStroke(plotFrame!, hoverBrush);
        SurfaceMotion.SetStroke(plotFrame!, (Brush)main.FindResource("Semantic.Brush.BorderSubtle"));

        model.SelectedTask = model.Tasks[1];
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (SystemParameters.ClientAreaAnimation)
        {
            Assert.True(LineMotion.IsPlaying(detail!));
            Assert.True(DependencyPropertyHelper.GetValueSource(plotFrame!, Border.BorderBrushProperty).IsAnimated);
        }
        await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(detail!));
        Assert.Equal(restColor, Assert.IsType<SolidColorBrush>(plotFrame!.BorderBrush).Color);
    });

    [Fact]
    public Task TextBoxHoverAndChartRangeSwitchUseRedInteractionBorderAndScanlineSweep() =>
        FakeUiMotionTests.WithMain(false, async (model, main) =>
    {
        Seed(model);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var hoverColor = ((SolidColorBrush)main.FindResource("Semantic.Brush.InteractionHover")).Color;
        var subtleColor = ((SolidColorBrush)main.FindResource("Semantic.Brush.BorderSubtle")).Color;

        // Simulate hovering the search surface (where MaterialDesignTextBox template trigger would otherwise set #A1A1AA).
        var searchBox = main.SearchBox;
        ColorStateAssist.SetReduceMotion(main, true);
        ColorStateAssist.SetFrameHovered(searchBox, true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(hoverColor, Assert.IsType<SolidColorBrush>(searchBox.BorderBrush).Color);

        // Even if a template trigger writes a light brush to BorderBrush while hovered, MouseEnter/IsMouseOver re-asserts InteractionHover.
        searchBox.SetCurrentValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(161, 161, 170)));
        searchBox.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
        Assert.Equal(hoverColor, Assert.IsType<SolidColorBrush>(searchBox.BorderBrush).Color);

        ColorStateAssist.SetFrameHovered(searchBox, false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(subtleColor, Assert.IsType<SolidColorBrush>(searchBox.BorderBrush).Color);

        // Switching chart range triggers the scanline sweep and settles cleanly with Clip == null.
        ColorStateAssist.SetReduceMotion(main, false);
        var plot = main.ServiceHealthPlot;
        plot.WindowHours = 168;
        if (SystemParameters.ClientAreaAnimation)
        {
            var vs = DependencyPropertyHelper.GetValueSource(plot, DashboardPlot.SweepProgressProperty);
            Assert.True(vs.IsAnimated, $"IsLoaded={plot.IsLoaded}, IsVisible={plot.IsVisible}, WindowHours={plot.WindowHours}, Mode={plot.Mode}, ReduceMotion={ColorStateAssist.GetReduceMotion(plot)}, BaseValueSource={vs.BaseValueSource}, SweepProgress={plot.SweepProgress}");
        }
        await PresenceMotionTests.Until(() => !DependencyPropertyHelper.GetValueSource(main.ServiceHealthPlot, DashboardPlot.SweepProgressProperty).IsAnimated);
        Assert.Null(main.ServiceHealthPlot.Clip);
        Assert.Equal(1d, (double)main.ServiceHealthPlot.GetValue(DashboardPlot.SweepProgressProperty));
    });

    internal static void Seed(MainWindowViewModel model) => CompactSidebarTests.Seed(model,
        [CompactSidebarTests.Snapshot(new string("合成健康服务".ToCharArray())), CompactSidebarTests.Snapshot(new string("合成失败服务".ToCharArray()), "FAIL")]);

    internal static void Logs(MainWindowViewModel model)
    {
        model.ExceptionLogs.Clear();
        foreach (var (source, message, level) in new[]
        {
            ("R115 合成来源A", "隔离测试事件A：只用于检查真实详情框线、换行与复制。\n第二行保持完整正文。", ExceptionLogLevel.Warning),
            ("R115 合成来源B", "隔离测试事件B：切换时标题和正文框线一起绘制，结束恢复主题原色。", ExceptionLogLevel.Error),
        })
        { model.ExceptionLogs.Add(new ExceptionLogRowViewModel(new ExceptionLogEntry(DateTimeOffset.Now, level, source, message, "synthetic-r115.log"))); }
        model.SelectedExceptionLog = model.ExceptionLogs[0];
    }
}
