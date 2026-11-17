using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Services;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class PresenceMotionTests
{
    [Fact]
    public Task ReducedMotionSnapsAndNeverLeavesAnInvisibleInputBlocker() => WithWindow(async (window, panel) =>
    {
        ColorStateAssist.SetReduceMotion(window, true);
        Assert.Equal(Visibility.Collapsed, panel.Visibility); Assert.Equal(0, panel.Opacity);
        panel.IsPresented = true;
        Assert.Equal(Visibility.Visible, panel.Visibility); Assert.Equal(1, panel.Opacity);
        Assert.True(panel.IsEnabled); Assert.True(panel.IsHitTestVisible); Assert.False(panel.HasAnimatedProperties);
        panel.IsPresented = false;
        Assert.Equal(Visibility.Collapsed, panel.Visibility); Assert.Equal(0, panel.Opacity);
        Assert.False(panel.IsEnabled); Assert.False(panel.IsHitTestVisible); Assert.False(panel.HasAnimatedProperties);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    });

    [Fact]
    public Task ReversalStartsAtCurrentOpacityAndOldCompletionCannotCollapseIt() => WithWindow(async (window, panel) =>
    {
        ColorStateAssist.SetReduceMotion(window, false); panel.IsPresented = true;
        if (!SystemParameters.ClientAreaAnimation) { Assert.Equal(1, panel.Opacity); Assert.False(panel.HasAnimatedProperties); return; }
        await Until(() => panel.Opacity is > .01 and < .99);
        var from = panel.Opacity;
        panel.IsPresented = false;
        Assert.InRange(Math.Abs(panel.Opacity - from), 0, .001);
        Assert.Equal(Visibility.Visible, panel.Visibility); Assert.False(panel.IsHitTestVisible); Assert.False(panel.IsEnabled);
        panel.IsPresented = true;
        Assert.InRange(Math.Abs(panel.Opacity - from), 0, .001);
        Assert.True(panel.IsHitTestVisible);
        await Until(() => !panel.HasAnimatedProperties);
        Assert.Equal(1, panel.Opacity); Assert.Equal(Visibility.Visible, panel.Visibility);
        panel.IsPresented = false;
        Assert.Equal(Visibility.Visible, panel.Visibility); Assert.False(panel.IsHitTestVisible);
        await Until(() => !panel.HasAnimatedProperties);
        Assert.Equal(0, panel.Opacity); Assert.Equal(Visibility.Collapsed, panel.Visibility);
        await Task.Delay(200); Assert.Equal(Visibility.Collapsed, panel.Visibility); Assert.False(panel.HasAnimatedProperties);
    });

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public Task ReduceMotionDuringEitherDirectionClearsTheClockImmediately(bool entering) => WithWindow(async (window, panel) =>
    {
        ColorStateAssist.SetReduceMotion(window, true); panel.IsPresented = !entering;
        ColorStateAssist.SetReduceMotion(window, false); panel.IsPresented = entering;
        ColorStateAssist.SetReduceMotion(window, true);
        Assert.Equal(entering ? 1 : 0, panel.Opacity); Assert.False(panel.HasAnimatedProperties);
        Assert.Equal(entering ? Visibility.Visible : Visibility.Collapsed, panel.Visibility);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    });

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public Task HidingOrClosingTheOwnerSettlesAndReleasesClocks(bool entering) => WithWindow(async (window, panel) =>
    {
        ColorStateAssist.SetReduceMotion(window, true); panel.IsPresented = !entering;
        ColorStateAssist.SetReduceMotion(window, false); panel.IsPresented = entering;
        window.Hide();
        Assert.False(panel.HasAnimatedProperties); Assert.Equal(entering ? 1 : 0, panel.Opacity);
        window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(panel.HasAnimatedProperties); Assert.Equal(entering ? 1 : 0, panel.Opacity);
        panel.IsPresented = !entering; window.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(panel.IsLoaded); Assert.False(panel.HasAnimatedProperties);
    });

    [Fact]
    public Task PageVariantKeepsItsSkeletonStationaryDuringEntranceExitAndReversal() => WithWindow(async (window, panel) =>
    {
        panel.MotionKind = PresenceMotionKind.Page;
        var shift = Assert.IsType<System.Windows.Media.TranslateTransform>(panel.RenderTransform);
        Assert.Equal(0, shift.Y);
        ColorStateAssist.SetReduceMotion(window, true); panel.IsPresented = true;
        Assert.Equal(0, shift.Y); Assert.Equal(1, panel.Opacity);
        ColorStateAssist.SetReduceMotion(window, false); panel.IsPresented = false;
        if (SystemParameters.ClientAreaAnimation)
        {
            // Exercise an actual mid-flight reversal, not the sub-0.001 endpoint snap path.
            await Until(() => panel.Opacity is > .01 and < .99);
            var opacity = panel.Opacity; var y = shift.Y;
            Assert.Equal(0, y);
            panel.IsPresented = true;
            Assert.Equal(opacity, panel.Opacity, 8); Assert.Equal(y, shift.Y, 8);
        }
        else { panel.IsPresented = true; }
        await Until(() => !panel.HasAnimatedProperties);
        Assert.Equal(0, shift.Y); Assert.Equal(1, panel.Opacity);
        panel.IsPresented = false; ColorStateAssist.SetReduceMotion(window, true);
        Assert.Equal(0, shift.Y); Assert.Equal(Visibility.Collapsed, panel.Visibility);
        Assert.False(panel.HasAnimatedProperties); Assert.False(shift.HasAnimatedProperties);
    });

    [Fact]
    public Task IntentBindingSurvivesTransitionsAndParentDisableIsRespected() => WithWindow(async (window, panel) =>
    {
        ColorStateAssist.SetReduceMotion(window, true);
        var source = new ToggleButton { IsChecked = false };
        panel.SetBinding(PresencePanel.IsPresentedProperty, new Binding(nameof(ToggleButton.IsChecked)) { Source = source });
        source.IsChecked = true; await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.True(panel.IsPresented); Assert.True(panel.IsEnabled);
        window.IsEnabled = false; Assert.False(panel.IsEnabled);
        source.IsChecked = false; await Dispatcher.Yield(DispatcherPriority.DataBind);
        source.IsChecked = true; await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.False(panel.IsEnabled); Assert.True(BindingOperations.IsDataBound(panel, PresencePanel.IsPresentedProperty));
        window.IsEnabled = true; Assert.True(panel.IsEnabled);
    });

    internal static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline) { await Task.Delay(5); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
        Assert.True(condition(), "Presence transition did not reach the required observable state.");
    }

    private static Task WithWindow(Func<Window, PresencePanel, Task> action) => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        var panel = new PresencePanel();
        var window = new Window { Content = panel, Width = 480, Height = 320, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        var closed = false; window.Closed += (_, _) => closed = true;
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(panel.IsLoaded);
            await action(window, panel);
        }
        finally
        {
            if (!closed) { window.Close(); }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    });
}
