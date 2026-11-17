using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SystemAutorun.App.Services;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed partial class PointerInteractionWindowTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task HostWheelAndScrollbarUseTheActualWindowHitPath(bool light) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, _) =>
        {
            model.AutoRefresh = false;
            model.CurrentPage = SystemAutorun.App.ViewModels.MainWindowViewModel.OverviewPage;
            main.Left = 24; main.Top = 24;
            for (var index = 1; index < 10; index++) { model.Drives.Add(model.Drives[0]); }
            await Task.Delay(1000); await Settle(main);
            var charts = main.MonitoringTabs.HostCharts;
            var outer = Ancestors(charts).OfType<ScrollViewer>().First();
            var items = Descendants(main.MonitoringTabs).OfType<ItemsControl>().Single(item => ReferenceEquals(item.ItemsSource, model.Drives));
            outer.ScrollToVerticalOffset(items.TranslatePoint(new Point(), outer).Y + outer.VerticalOffset - 60);
            await Settle(main);
            var point = items.TranslatePoint(new Point(80, 22), main);
            var hit = (DependencyObject?)main.InputHitTest(point);
            var route = Ancestors(hit).Select(item => item.GetType().Name).ToArray();
            var offsetBefore = outer.VerticalOffset;
            Assert.NotNull(hit);
            ((UIElement)hit).RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent });
            await Settle(main);
            var offsetAfter = outer.VerticalOffset;
            var scrollbar = Descendants(outer).OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical && ReferenceEquals(Ancestors(bar).OfType<ScrollViewer>().First(), outer));
            var thumb = Descendants(scrollbar).OfType<Thumb>().Single();
            var thumbScreen = thumb.PointToScreen(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2));
            var nativeHit = NativeHit(main, thumbScreen);
            outer.ScrollToHome(); await Settle(main);
            var buttons = new[] { charts.HostWindowButton };
            var samples = buttons.SelectMany(button => Sample(main, button)).ToArray();
            var evidence = new { scope = "Real Window.InputHitTest + routed wheel from the hit element + native WM_NCHITTEST; not OS SendInput or a physical-pointer journey", light, route, offsetBefore, offsetAfter, nativeHit,
                scrollbarWidth = scrollbar.ActualWidth, thumbScreen, samples };
            WriteEvidence($"host-hit-{(light ? "light" : "dark")}.json", evidence);
            Assert.True(offsetAfter > offsetBefore, JsonSerializer.Serialize(evidence));
            Assert.Equal(1, nativeHit); // HTCLIENT, not the resize frame.
            Assert.All(samples, sample => Assert.True(sample.expectedButtonHit, JsonSerializer.Serialize(sample)));
            model.CurrentPage = SystemAutorun.App.ViewModels.MainWindowViewModel.WorkloadsPage;
            await Settle(main);
            Assert.All(new[] { main.HealthWindowButton }.SelectMany(button => Sample(main, button)),
                sample => Assert.True(sample.expectedButtonHit, JsonSerializer.Serialize(sample)));
        });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task TaskFormComboToggleCoversItsWholeVisibleField(bool light) => FakeUiMotionTests.WithMain(light, async (_, main) =>
    {
        var form = DialogChromeTests.Create("add"); form.Owner = main; form.ShowActivated = false;
        form.WindowStartupLocation = WindowStartupLocation.Manual; form.Left = 40; form.Top = 40;
        ColorStateAssist.SetReduceMotion(form, true);
        var samples = new List<object>();
        try
        {
            form.Show(); await Task.Delay(250); await Settle(form);
            foreach (var combo in RequiredTaskFormCombos(form))
            {
                combo.BringIntoView(); await Settle(form);
                var toggle = (ToggleButton)combo.Template.FindName("DropDownToggle", combo);
                foreach (var x in new[] { .1, .5, .9 })
                foreach (var y in new[] { .15, .5, .85 })
                {
                    var hit = form.InputHitTest(combo.TranslatePoint(new Point(combo.ActualWidth * x, combo.ActualHeight * y), form)) as DependencyObject;
                    samples.Add(new { combo = combo.Name, x, y, passed = Ancestors(hit).Contains(toggle),
                        fieldWidth = combo.ActualWidth, toggleWidth = toggle.ActualWidth, declaredWidth = toggle.Width.ToString(), toggle.HorizontalAlignment,
                        hit = hit?.GetType().Name, hitName = (hit as FrameworkElement)?.Name });
                }
            }
            WriteEvidence($"combo-hit-{(light ? "light" : "dark")}.json", samples);
        }
        finally { form.Close(); await ((ToolWindow)form).ClosureTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(samples));
        Assert.All(document.RootElement.EnumerateArray(), row => Assert.True(row.GetProperty("passed").GetBoolean(), row.GetRawText()));
    });

    private static ComboBox[] RequiredTaskFormCombos(Window form)
    {
        var all = Descendants(form).OfType<ComboBox>().ToArray();
        var required = new[] { "LaunchTriggerComboBox", "TaskTypeComboBox", "WindowModeComboBox", "HealthTypeComboBox" }
            .Select(name => Assert.Single(all, item => item.Name == name)).ToArray();
        Assert.All(required, item => { Assert.True(item.IsVisible); Assert.True(item.IsEnabled); Assert.True(item.ActualWidth > 0); });
        return required;
    }

    private static IEnumerable<HitSample> Sample(Window window, Button button)
    {
        foreach (var x in new[] { .1, .5, .9 })
        foreach (var y in new[] { .15, .5, .85 })
        {
            var point = button.TranslatePoint(new Point(button.ActualWidth * x, button.ActualHeight * y), window);
            var hit = window.InputHitTest(point) as DependencyObject;
            yield return new(button.Name, x, y, ReferenceEquals(Ancestors(hit).OfType<Button>().FirstOrDefault(), button),
                hit?.GetType().Name, (hit as FrameworkElement)?.Name,
                (button.Template.FindName("Surface", button) as Border)?.Background?.ToString());
        }
    }

    private sealed record HitSample(string button, double x, double y, bool expectedButtonHit, string? hitType, string? hitName, string? background);
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject? item)
    {
        while (item is not null) { yield return item; item = VisualTreeHelper.GetParent(item); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject item)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(item); index++)
        {
            var child = VisualTreeHelper.GetChild(item, index); yield return child;
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }
    private static async Task Settle(Window main)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.Render);
    }
    private static void WriteEvidence(string name, object value)
    {
        var directory = Environment.GetEnvironmentVariable("SA_POINTER_EVIDENCE");
        if (string.IsNullOrEmpty(directory)) { return; }
        Assert.True(Directory.Exists(directory));
        File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static int NativeHit(Window window, Point screen)
    {
        var packed = unchecked((int)((ushort)(short)screen.X | ((uint)(ushort)(short)screen.Y << 16)));
        var sent = SendMessageTimeout(new WindowInteropHelper(window).Handle, 0x84, IntPtr.Zero, new IntPtr(packed), 2, 1000, out var result);
        Assert.NotEqual(IntPtr.Zero, sent);
        return unchecked((int)result.ToUInt64());
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
}
