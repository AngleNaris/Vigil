using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class ToolWindowMotionTests
{
    [Theory]
    [InlineData("true", false)] [InlineData("false", false)] [InlineData("close", false)]
    [InlineData("cancel", false)] [InlineData("native", false)]
    [InlineData("true", true)] [InlineData("false", true)] [InlineData("close", true)]
    [InlineData("cancel", true)] [InlineData("native", true)]
    public Task ModalResultsSurviveDeferredCloseAndRepeatedRequests(string request, bool reduced) => WithTool(async (_, tool) =>
    {
        ColorStateAssist.SetReduceMotion(tool, reduced);
        var results = new List<bool?>(); var closed = 0; double height = 0;
        tool.Closing += (_, _) => { results.Add(tool.DialogResult); if (height > 0) { Assert.InRange(Math.Abs(tool.ActualHeight - height), 0, 1); } };
        tool.Closed += (_, _) => closed++;
        var interaction = tool.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await PresenceMotionTests.Until(() => tool.IsLoaded);
                var surface = Assert.IsType<PresencePanel>(tool.Content);
                await PresenceMotionTests.Until(() => !surface.HasAnimatedProperties);
                height = tool.ActualHeight;
                switch (request)
                {
                    case "true": tool.DialogResult = true; break;
                    case "false": tool.DialogResult = false; break;
                    case "cancel": ((CancelButton)((Grid)surface.Children[0]).Children[0]).PerformClick(); break;
                    case "native": SystemCommands.CloseWindow(tool); break;
                    default: tool.Close(); break;
                }
                if (tool.IsExitPending)
                {
                    Assert.False(surface.IsEnabled); Assert.False(surface.IsHitTestVisible);
                    Assert.False(tool.ClosureTask.IsCompleted);
                    tool.Close(); // Must not replace a latched true result with WPF's cleared null.
                }
                await PresenceMotionTests.Until(() => tool.ClosureTask.IsCompleted);
            }
            finally { ForceClose(tool); }
        }).Task.Unwrap();
        var result = tool.ShowDialog();
        await interaction;
        Assert.Equal(request == "true", result); Assert.Equal(1, closed);
        Assert.True(tool.ClosureTask.IsCompletedSuccessfully);
        if (request is "true" or "false") { Assert.All(results, value => Assert.Equal(request == "true", value)); }
        Assert.InRange(results.Count, 1, 2);
        // Returning here with a live Dispatcher proves normal modal return; teardown does not release the frame.
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }, show: false);

    [Fact]
    public Task InitialShowWaitsForNativeLayoutBeforeStartingContentEntrance() => WithTool(async (_, tool) =>
    {
        double? loadedOpacity = null; bool? loadedClock = null; bool? renderedClock = null;
        var surface = Assert.IsType<PresencePanel>(tool.Content);
        void Observe()
        {
            if (loadedOpacity is null && surface.IsLoaded && surface.IsVisible)
            { loadedOpacity = surface.Opacity; loadedClock = LineMotion.IsPlaying(surface); }
        }
        surface.Loaded += (_, _) => Observe();
        surface.IsVisibleChanged += (_, _) => Observe();
        tool.ContentRendered += (_, _) => renderedClock = LineMotion.IsPlaying(surface);
        tool.Show(); await PresenceMotionTests.Until(() => loadedOpacity is not null);
        Assert.Equal(1d, loadedOpacity); // Stable body; the native shell is held until initial layout finishes.
        Assert.False(loadedClock);
        await PresenceMotionTests.Until(() => renderedClock is not null);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); // The override finishes after base raises ContentRendered.
        Assert.Equal(SystemParameters.ClientAreaAnimation, LineMotion.IsPlaying(surface));
        Assert.False(surface.HasAnimatedProperties);
        await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(surface));
    }, show: false);

    [Fact]
    public Task ModalFinalVetoCannotLeakAnEarlierTrueResultIntoCancel() => WithTool(async (_, tool) =>
    {
        var calls = 0;
        CancelEventHandler guard = (_, e) => { if (++calls == 2) { e.Cancel = true; } };
        tool.Closing += guard;
        var interaction = tool.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await PresenceMotionTests.Until(() => tool.IsLoaded);
                var surface = Assert.IsType<PresencePanel>(tool.Content);
                await PresenceMotionTests.Until(() => !surface.HasAnimatedProperties);
                if (SystemParameters.ClientAreaAnimation)
                {
                    tool.DialogResult = true;
                    await PresenceMotionTests.Until(() => !tool.IsExitPending);
                    Assert.Equal(2, calls); Assert.Null(tool.DialogResult); Assert.False(tool.ClosureTask.IsCompleted);
                    await PresenceMotionTests.Until(() => !surface.HasAnimatedProperties);
                }
                tool.Closing -= guard;
                tool.DialogResult = false;
                await PresenceMotionTests.Until(() => tool.ClosureTask.IsCompleted);
            }
            finally { tool.Closing -= guard; ForceClose(tool); }
        }).Task.Unwrap();
        Assert.False(tool.ShowDialog()); await interaction;
    }, show: false);

    [Fact]
    public Task GuardsCanRejectBeforeExitAndAgainAfterExitWithoutLosingBodyState() => WithTool(async (_, tool) =>
    {
        var surface = Assert.IsType<PresencePanel>(tool.Content);
        await PresenceMotionTests.Until(() => !surface.HasAnimatedProperties);
        var input = ((Grid)surface.Children[0]).Children[0]; input.IsEnabled = false;
        var calls = 0; var rejectFirst = true; var rejectFinal = true;
        CancelEventHandler guard = (_, e) => { calls++; e.Cancel = rejectFirst || (rejectFinal && calls > 1); };
        tool.Closing += guard;
        try
        {
            tool.Close(); Assert.False(tool.IsExitPending); Assert.True(tool.IsVisible); Assert.Equal(1, surface.Opacity);
            rejectFirst = false; calls = 0;
            if (!SystemParameters.ClientAreaAnimation) { rejectFinal = false; tool.Close(); return; }
            tool.Close(); Assert.True(tool.IsExitPending);
            await PresenceMotionTests.Until(() => !tool.IsExitPending);
            await PresenceMotionTests.Until(() => !surface.HasAnimatedProperties);
            Assert.Equal(2, calls); Assert.True(tool.IsVisible); Assert.False(tool.ClosureTask.IsCompleted);
            Assert.Equal(1, surface.Opacity); Assert.True(surface.IsEnabled); Assert.False(input.IsEnabled);
            Assert.Equal(Visibility.Visible, surface.Visibility);
            rejectFinal = false; tool.Close(); await tool.ClosureTask;
        }
        finally { tool.Closing -= guard; }
    });

    [Fact]
    public Task ReopenReversesFromCurrentProgressAndInvalidatesTheOldClose() => WithTool(async (_, tool) =>
    {
        var surface = Assert.IsType<PresencePanel>(tool.Content);
        await PresenceMotionTests.Until(() => !surface.HasAnimatedProperties);
        tool.Close();
        if (!SystemParameters.ClientAreaAnimation) { Assert.True(tool.ClosureTask.IsCompleted); return; }
        await PresenceMotionTests.Until(() => surface.Opacity is > .01 and < .99);
        var progress = surface.Opacity;
        tool.CancelPendingClose();
        Assert.Equal(progress, surface.Opacity, 8); Assert.True(surface.IsEnabled);
        await PresenceMotionTests.Until(() => !surface.HasAnimatedProperties);
        await Task.Delay(200);
        Assert.True(tool.IsVisible); Assert.False(tool.ClosureTask.IsCompleted); Assert.Equal(1, surface.Opacity);
    });

    [Fact]
    public Task HideAndShowInterruptExitWithoutDestroyingTheTool() => WithTool(async (_, tool) =>
    {
        var surface = Assert.IsType<PresencePanel>(tool.Content);
        await PresenceMotionTests.Until(() => !surface.HasAnimatedProperties);
        tool.Close();
        if (!SystemParameters.ClientAreaAnimation) { return; }
        tool.Hide(); Assert.False(tool.IsExitPending); Assert.False(surface.HasAnimatedProperties);
        await Task.Delay(200); Assert.False(tool.ClosureTask.IsCompleted);
        tool.Show(); await PresenceMotionTests.Until(() => !surface.HasAnimatedProperties);
        Assert.True(tool.IsVisible); Assert.True(surface.IsEnabled); Assert.Equal(1, surface.Opacity);
    });

    [Fact]
    public Task ReducingMotionMidExitStillCommitsAfterTheOriginalClosingStack() => WithTool(async (_, tool) =>
    {
        var surface = Assert.IsType<PresencePanel>(tool.Content);
        await PresenceMotionTests.Until(() => !surface.HasAnimatedProperties);
        tool.Close();
        if (!SystemParameters.ClientAreaAnimation) { return; }
        ColorStateAssist.SetReduceMotion(tool, true);
        Assert.False(surface.HasAnimatedProperties); Assert.Equal(0, surface.Opacity);
        Assert.False(tool.ClosureTask.IsCompleted);
        await tool.ClosureTask; Assert.False(tool.IsVisible);
    });

    [Fact]
    public Task ForcedOwnerCloseInvalidatesAnimationAndQueuedCallbacks() => WithTool(async (owner, tool) =>
    {
        var surface = Assert.IsType<PresencePanel>(tool.Content);
        await PresenceMotionTests.Until(() => !surface.HasAnimatedProperties);
        tool.Close(); owner.Close();
        await tool.ClosureTask;
        Assert.False(surface.HasAnimatedProperties); Assert.False(tool.IsVisible);
        await Task.Delay(200); Assert.True(tool.ClosureTask.IsCompletedSuccessfully);
    });

    [Fact]
    public Task NeverShownToolClosesWithoutAnAnimationOrDispatcherCallback() => WithTool((_, tool) =>
    {
        tool.Close(); Assert.True(tool.ClosureTask.IsCompletedSuccessfully); return Task.CompletedTask;
    }, show: false);

    [Fact]
    public Task DoctorUsesOneMotionSurfaceAndKeepsItsRealBindings() => WithTool(async (owner, _) =>
    {
        var doctor = new DoctorWindow(new DoctorReport([new DoctorCheck("合成工具动效", true, "隔离样例，不运行系统诊断。")]))
        { Owner = owner, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        try
        {
            doctor.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var surface = Assert.IsType<PresencePanel>(doctor.Content);
            Assert.Equal(PresenceMotionKind.Tool, surface.MotionKind);
            Assert.False(SurfaceMotion.GetFadeOnShow(surface)); Assert.False(SurfaceMotion.GetFadeOnShow(surface.Children[0]));
            Assert.Single(doctor.DoctorGrid.Items);
            await PresenceMotionTests.Until(() => !surface.HasAnimatedProperties);
            StartupGuiWindowTests.CaptureStateEvidence(doctor, "doctor-motion-open.png");
            doctor.Close(); await doctor.ClosureTask;
            Assert.False(surface.HasAnimatedProperties);
        }
        finally { ForceClose(doctor); }
    });

    private sealed class CancelButton : Button
    {
        internal void PerformClick() => OnClick();
    }

    private static Task WithTool(Func<Window, ToolWindow, Task> action, bool show = true) => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        if (Application.Current is not SystemAutorun.App.App)
        { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
        WindowTheme.Install();
        var owner = new Window { Width = 960, Height = 620, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        var body = new Grid { Height = 160 };
        body.Children.Add(new CancelButton { IsCancel = true, Content = "取消" });
        var surface = new PresencePanel { MotionKind = PresenceMotionKind.Tool, IsPresented = true };
        surface.Children.Add(body);
        var tool = new ToolWindow { Width = 400, SizeToContent = SizeToContent.Height, Content = surface,
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        ColorStateAssist.SetReduceMotion(tool, false);
        try
        {
            owner.Show(); tool.Owner = owner;
            if (show) { tool.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
            await action(owner, tool);
        }
        finally
        {
            ForceClose(tool);
            if (owner.IsLoaded) { owner.Close(); }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    });

    [Fact]
    public Task ShellHasThemedPerPixelCompositionBeforeFirstNativeShow() => WithTool(async (_, tool) =>
    {
        var observed = false;
        tool.SourceInitialized += (_, _) =>
        {
            var source = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(tool).Handle);
            Assert.NotNull(source); Assert.True(source.CompositionTarget!.UsesPerPixelOpacity);
            Assert.True(tool.AllowsTransparency); Assert.Equal(WindowStyle.None, tool.WindowStyle);
            Assert.NotNull(tool.Style); Assert.NotNull(tool.Background);
            Assert.Same(tool.FindResource("Semantic.Brush.WindowBackground"), tool.Background);
            observed = true;
        };
        tool.Show();
        await PresenceMotionTests.Until(() => observed && !tool.HasAnimatedProperties);
        Assert.Equal(1, tool.Opacity);
        var surface = Assert.IsType<PresencePanel>(tool.Content);
        tool.Close();
        if (tool.IsExitPending)
        {
            Assert.False(surface.IsHitTestVisible); Assert.False(tool.ClosureTask.IsCompleted);
            await PresenceMotionTests.Until(() => tool.ClosureTask.IsCompleted);
        }
        Assert.False(tool.HasAnimatedProperties);
    }, show: false);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task ReducingMotionMidShellTransitionSettlesAndCannotStrandClose(bool exiting) => WithTool(async (_, tool) =>
    {
        tool.Show();
        if (exiting)
        {
            await PresenceMotionTests.Until(() => !tool.HasAnimatedProperties);
            tool.Close();
        }
        ColorStateAssist.SetReduceMotion(tool, true);
        Assert.False(tool.HasAnimatedProperties);
        if (exiting) { await PresenceMotionTests.Until(() => tool.ClosureTask.IsCompleted); }
        else { Assert.Equal(1, tool.Opacity); Assert.True(tool.IsVisible); }
    }, show: false);

    [Fact]
    public Task BackdropReversesFromCurrentPixelsAndUnloadsItsOpacityClock() => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        var backdrop = new DialogBackdrop();
        var owner = new Window { Width = 400, Height = 260, Content = backdrop,
            ShowActivated = false, Left = -32000, Top = -32000 };
        ColorStateAssist.SetReduceMotion(backdrop, false);
        try
        {
            owner.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            backdrop.IsPresented = true;
            Assert.Equal(Visibility.Visible, backdrop.Visibility);
            if (SystemParameters.ClientAreaAnimation)
            {
                Assert.True(backdrop.HasAnimatedProperties); Assert.Equal(0, backdrop.Opacity);
                await PresenceMotionTests.Until(() => backdrop.Opacity is > .05 and < .95);
                var current = backdrop.Opacity;
                backdrop.IsPresented = false;
                Assert.Equal(current, backdrop.GetAnimationBaseValue(UIElement.OpacityProperty));
                Assert.InRange(Math.Abs(backdrop.Opacity - current), 0, .000001);
                Assert.Equal(Visibility.Visible, backdrop.Visibility);
                backdrop.IsPresented = true;
            }
            await PresenceMotionTests.Until(() => !backdrop.HasAnimatedProperties);
            Assert.Equal(1, backdrop.Opacity);
            backdrop.IsPresented = false;
            ColorStateAssist.SetReduceMotion(backdrop, true);
            Assert.False(backdrop.HasAnimatedProperties); Assert.Equal(0, backdrop.Opacity);
            Assert.Equal(Visibility.Collapsed, backdrop.Visibility);
            ColorStateAssist.SetReduceMotion(backdrop, false); backdrop.IsPresented = true;
            owner.Close();
            Assert.False(backdrop.HasAnimatedProperties);
        }
        finally { if (owner.IsLoaded) { owner.Close(); } }
    });

    [Fact]
    public Task RetiringTheBodyEarlyStillWaitsForTheShellFade() => WithTool(async (_, tool) =>
    {
        await PresenceMotionTests.Until(() => !tool.HasAnimatedProperties);
        double? finalOpacity = null;
        tool.Closing += (_, _) => { if (tool.IsExitPending) { finalOpacity = tool.Opacity; } };
        tool.Close();
        if (!tool.IsExitPending) { return; }
        Assert.IsType<PresencePanel>(tool.Content).FinishPresentation();
        await Dispatcher.Yield(DispatcherPriority.Normal);
        Assert.False(tool.ClosureTask.IsCompleted);
        await PresenceMotionTests.Until(() => tool.ClosureTask.IsCompleted);
        Assert.Equal(0d, finalOpacity);
    });

    [Fact]
    public Task BackdropCoversAFrozenSceneBeforeItHidesItsContent() => FakeUiMotionTests.WithMain(false, async (_, main) =>
    {
        var tool = new DoctorWindow(new DoctorReport([new DoctorCheck("合成遮罩", true, "不执行系统操作。")]))
        { Owner = main, ShowActivated = false, Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            tool.Show();
            await PresenceMotionTests.Until(() => main.DialogScrim.IsPresented);
            Assert.True(LineMotion.GetIsSuspended(main)); Assert.False(main.MainScene.IsHitTestVisible);
            if (SystemParameters.ClientAreaAnimation && main.DialogScrim.Opacity < 1)
            { Assert.Equal(Visibility.Visible, main.MainScene.Visibility); Assert.True(main.DialogScrim.HasAnimatedProperties); }
            await PresenceMotionTests.Until(() => !main.DialogScrim.HasAnimatedProperties);
            Assert.Equal(Visibility.Hidden, main.MainScene.Visibility);
            tool.Close(); await PresenceMotionTests.Until(() => !tool.IsVisible);
            Assert.Equal(Visibility.Visible, main.MainScene.Visibility);
            await PresenceMotionTests.Until(() => !main.DialogScrim.HasAnimatedProperties);
            Assert.Equal(Visibility.Collapsed, main.DialogScrim.Visibility); Assert.True(main.MainScene.IsHitTestVisible);
        }
        finally { ForceClose(tool); }
    });

    [Fact]
    public Task NativeDialogFramesUseOnlyAnIsolatedFixture() => FakeUiMotionTests.WithMain(
        Environment.GetEnvironmentVariable("SA_DIALOG_PROBE_THEME") == "light", async (model, main) =>
    {
        var probe = Environment.GetEnvironmentVariable("SA_DIALOG_PROBE");
        if (string.IsNullOrEmpty(probe)) { return; }
        Assert.True(System.IO.Directory.Exists(probe));
        main.Title = "SystemAutorun · R118 isolated UI"; main.Left = 12; main.Top = 12;
        ColorStateAssist.SetReduceMotion(main, true); AnimationRegressionTests.Seed(model);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        ColorStateAssist.SetReduceMotion(main, false);
        DoctorWindow? tool = null; DoctorWindow? nested = null; var closing = false; var nestedClosing = false;
        try
        {
            await Record("ready", main);
            var deadline = DateTime.UtcNow.AddMinutes(8);
            while (!System.IO.File.Exists(System.IO.Path.Combine(probe, "finish.txt")))
            {
                Assert.True(DateTime.UtcNow < deadline, "Dialog frame probe exceeded its bounded observation period.");
                if (tool is null && Requested("open")) { tool = Create(main); tool.Show(); await Record("opened", tool); }
                if (tool is { IsVisible: true } && nested is null && Requested("nested"))
                {
                    nested = Create(tool); nested.MinWidth = 480; nested.MinHeight = 260; nested.Width = 640; nested.Height = 320;
                    nested.Show(); await Record("nested-opened", nested);
                }
                if (nested is { IsVisible: true } && !nestedClosing && Requested("nested-close"))
                { nestedClosing = true; nested.Close(); }
                if (tool is { IsVisible: true } && !closing && Requested("close")) { closing = true; tool.Close(); }
                await Task.Delay(15);
            }
            if (tool is not null) { Assert.True(tool.ClosureTask.IsCompletedSuccessfully); }
            if (nested is not null) { Assert.True(nested.ClosureTask.IsCompletedSuccessfully); }
        }
        finally
        {
            if (nested is not null) { ForceClose(nested); }
            if (tool is not null) { ForceClose(tool); }
        }
        bool Requested(string name) => System.IO.File.Exists(System.IO.Path.Combine(probe, name + ".txt"));
        Task Record(string name, Window window) => System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(probe, name + ".json"),
            System.Text.Json.JsonSerializer.Serialize(new { pid = Environment.ProcessId,
                hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle.ToInt64(),
                dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window).PixelsPerInchX }));
        static DoctorWindow Create(Window owner) => new(new DoctorReport([
            new DoctorCheck("合成弹窗首末帧", true, "隔离原生窗口；未执行系统诊断或服务操作。")]))
            { Owner = owner, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
    }, TimeSpan.FromMinutes(9));

    private static void ForceClose(ToolWindow tool)
    {
        if (tool.ClosureTask.IsCompleted) { return; }
        tool.CancelPendingClose(); ColorStateAssist.SetReduceMotion(tool, true); tool.Close();
    }
}
