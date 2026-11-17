using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;

namespace SystemAutorun.IntegrationTests;

public sealed partial class PointerInteractionWindowTests
{
    // Opt-in native OS input, never part of unattended regression. The caller first activates
    // this exact owned HWND, then creates go.json. Every input refuses another foreground PID.
    [Fact]
    public Task NativePointerJourneyIsExplicitAndOwned()
    {
        var directory = Environment.GetEnvironmentVariable("SA_POINTER_NATIVE");
        if (string.IsNullOrEmpty(directory)) { return Task.CompletedTask; }
        Assert.True(Directory.Exists(directory));
        var visualOnly = Environment.GetEnvironmentVariable("SA_POINTER_NATIVE_VIS_ONLY") == "1";
        return ResourceDashboardWindowTests.WithResourceWindow(false, async (model, main, source) =>
        {
            model.AutoRefresh = false;
            var at = DateTimeOffset.UtcNow;
            var host = ResourceWindowSource.SyntheticHost(at, 0);
            void FreshHost()
            {
                var observed = DateTimeOffset.UtcNow;
                source.Observe(observed, model.Tasks.Select(task => ResourceWindowSource.Synthetic(task, observed, 0)).ToArray(),
                    host with { CapturedAt = observed, Drives = Enumerable.Range(0, 10).Select(index => host.Drives[0] with { Name = $"{(char)('C' + index)}:\\" }).ToArray(),
                        Networks = Enumerable.Range(0, 10).Select(index => host.Networks[0] with { Name = $"合成网络 {index}", InterfaceId = $"fixture-{index}" }).ToArray() });
                source.Apply(model);
                Assert.Equal(10, model.Drives.Count); Assert.Equal(10, model.Networks.Count);
            }
            FreshHost(); model.CurrentPage = MainWindowViewModel.OverviewPage;
            main.Title = "SystemAutorun R122 isolated pointer test";
            main.Left = 16; main.Top = 16; main.Width = 920; main.Height = 580;
            await Task.Delay(1200); await Settle(main);
            var hwnd = new WindowInteropHelper(main).Handle;
            var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
            await File.WriteAllTextAsync(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId, processStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime(), hwnd = hwnd.ToInt64(), main.Title,
                deadline, visualOnly, scope = "Owned synthetic WPF fixture; no production Agent; explicitly owned foreground; visualOnly skips injected input; leave foreground to cancel"
            }, new JsonSerializerOptions { WriteIndented = true }));
            while (!File.Exists(Path.Combine(directory, "go.json")) && DateTimeOffset.UtcNow < deadline) { await Task.Delay(100); }
            Assert.True(File.Exists(Path.Combine(directory, "go.json")), "Native journey was not armed before its bounded deadline.");
            OwnForeground(); GetCursorPos(out var oldCursor);
            var results = new List<object>();
            var trace = new List<object>(); var current = "setup";
            MouseButtonEventHandler input = (_, args) => trace.Add(new { current, kind = args.RoutedEvent.Name, args.Timestamp,
                original = (args.OriginalSource as FrameworkElement)?.Name, type = args.OriginalSource?.GetType().Name,
                over = (Mouse.DirectlyOver as FrameworkElement)?.Name, captured = Mouse.Captured?.GetType().Name, position = args.GetPosition(main) });
            RoutedEventHandler click = (_, args) => trace.Add(new { current, kind = "Click", source = (args.Source as FrameworkElement)?.Name });
            main.AddHandler(Mouse.PreviewMouseDownEvent, input, true); main.AddHandler(Mouse.PreviewMouseUpEvent, input, true);
            main.AddHandler(Button.ClickEvent, click, true);
            foreach (var button in new[] { main.HealthWindowButton, main.MonitoringTabs.HostCharts.HostWindowButton,
                         (Button)main.FindName("SettingsButton") })
            {
                button.ContextMenu!.Opened += (_, _) => trace.Add(new { current, kind = "Opened", button = button.Name });
                button.ContextMenu.Closed += (_, _) => trace.Add(new { current, kind = "Closed", button = button.Name });
            }
            var traceOnly = Environment.GetEnvironmentVariable("SA_POINTER_NATIVE_TRACE_ONLY") == "1";
            try
            {
                foreach (var light in traceOnly ? new[] { false } : new[] { false, true })
                foreach (var (width, height) in traceOnly ? new[] { (920, 580) } : new[] { (920, 580), (1152, 648), (1320, 820) })
                {
                    OwnForeground(); ThemeService.Apply(light ? ThemePreference.Light : ThemePreference.Dark, persist: null);
                    main.Width = width; main.Height = height; model.CurrentPage = MainWindowViewModel.OverviewPage;
                    await Task.Delay(450); await Settle(main);
                    var charts = main.MonitoringTabs.HostCharts;
                    var outer = Ancestors(charts).OfType<ScrollViewer>().First();
                    Keyboard.ClearFocus(); FreshHost(); outer.ScrollToHome();
                    await Task.Delay(1400); await Settle(main);
                    outer.ScrollToHome(); await Task.Delay(80); await Settle(main);
                    CaptureNative(main, Path.Combine(directory, $"host-{(light ? "light" : "dark")}-{width}x{height}.png"));
                    if (visualOnly)
                    {
                        FreshHost(); outer.ScrollToEnd(); await Task.Delay(1400); await Settle(main);
                        CaptureNative(main, Path.Combine(directory, $"host-scrolled-{(light ? "light" : "dark")}-{width}x{height}.png"));
                        results.Add(new { light, width, height, kind = "stable-native-pixels", passed = true,
                            dpi = VisualTreeHelper.GetDpi(main).PixelsPerInchX, outer.VerticalOffset, outer.ScrollableHeight });
                        continue;
                    }
                    foreach (var button in new[] { charts.HostWindowButton, (Button)main.FindName("SettingsButton") })
                    { await Buttons(button); }
                    foreach (var collection in new object[] { model.Drives, model.Networks })
                    {
                        FreshHost(); await Settle(main);
                        var items = Descendants(main.MonitoringTabs).OfType<ItemsControl>().Single(item => ReferenceEquals(item.ItemsSource, collection));
                        outer.ScrollToVerticalOffset(items.TranslatePoint(new Point(), outer).Y + outer.VerticalOffset - 60); await Settle(main);
                        var point = items.PointToScreen(new Point(80, 22));
                        var before = outer.VerticalOffset; Move(main, point); MouseInput(main, point, 0x0800, unchecked((uint)-120));
                        await Task.Delay(120); await Settle(main);
                        results.Add(new { light, width, height, kind = ReferenceEquals(collection, model.Drives) ? "drive-wheel" : "network-wheel", before, after = outer.VerticalOffset, passed = outer.VerticalOffset > before });
                    }
                    FreshHost(); outer.ScrollToHome(); await Settle(main);
                    var blank = outer.PointToScreen(new Point(4, 20)); var blankBefore = outer.VerticalOffset;
                    Move(main, blank); MouseInput(main, blank, 0x0800, unchecked((uint)-120)); await Task.Delay(120); await Settle(main);
                    results.Add(new { light, width, height, kind = "blank-wheel", before = blankBefore, after = outer.VerticalOffset, passed = outer.VerticalOffset > blankBefore });
                    outer.ScrollToHome(); await Settle(main);
                    var bar = Descendants(outer).OfType<ScrollBar>().Single(item => item.Orientation == Orientation.Vertical && ReferenceEquals(Ancestors(item).OfType<ScrollViewer>().First(), outer));
                    var thumb = Descendants(bar).OfType<Thumb>().Single();
                    var start = thumb.PointToScreen(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2));
                    var native = NativeHit(main, start); var beforeDrag = outer.VerticalOffset; GetWindowRect(hwnd, out var windowBefore);
                    Move(main, start); MouseInput(main, start, 0x0002);
                    try
                    {
                        for (var step = 1; step <= 8; step++) { Move(main, new(start.X, start.Y + step * 10)); await Task.Delay(18); }
                    }
                    finally { Send([MousePacket(0x0004)]); } // Release only the down this journey already injected, even if ownership was lost.
                    await Task.Delay(120); await Settle(main); GetWindowRect(hwnd, out var windowAfter);
                    results.Add(new { light, width, height, kind = "thumb-drag", native, before = beforeDrag, after = outer.VerticalOffset,
                        windowUnchanged = windowBefore.Equals(windowAfter), passed = native == 1 && outer.VerticalOffset > beforeDrag && windowBefore.Equals(windowAfter) });
                    CaptureNative(main, Path.Combine(directory, $"host-scrolled-{(light ? "light" : "dark")}-{width}x{height}.png"));
                    model.CurrentPage = MainWindowViewModel.WorkloadsPage; await Task.Delay(220); await Settle(main);
                    await Buttons(main.HealthWindowButton);
                    if (width == 1152)
                    {
                        var form = DialogChromeTests.Create("add"); form.Owner = main;
                        try
                        {
                            form.Show(); await Task.Delay(800); await Settle(form); OwnForeground();
                            var formPoint = form.PointToScreen(new Point(20, 20));
                            Assert.True(NativeTargetOwned(form, formPoint)); Assert.False(NativeTargetOwned(main, formPoint));
                            results.Add(new { light, width, height, kind = "reject-other-owned-hwnd", passed = true, injectedInput = false });
                            foreach (var combo in RequiredTaskFormCombos(form))
                            {
                                combo.BringIntoView(); await Task.Delay(180); await Settle(form);
                                foreach (var x in new[] { .1, .5, .9 })
                                foreach (var y in new[] { .15, .5, .85 })
                                {
                                    current = $"{light}/{width}/combo/{combo.Name}/{x}/{y}";
                                    var point = combo.PointToScreen(new Point(combo.ActualWidth * x, combo.ActualHeight * y));
                                    var nativeHit = NativeHit(form, point);
                                    var expectedHit = ReferenceEquals(Ancestors(form.InputHitTest(form.PointFromScreen(point)) as DependencyObject).OfType<ComboBox>().FirstOrDefault(), combo);
                                    Move(form, point); await Task.Delay(20); Click(form, point); await Task.Delay(85);
                                    var opened = combo.IsDropDownOpen;
                                    if (opened) { Escape(); await Task.Delay(65); }
                                    var closed = !combo.IsDropDownOpen;
                                    results.Add(new { light, width, height, kind = "combobox-click", control = combo.Name, x, y, nativeHit, expectedHit, opened, escaped = closed,
                                        passed = nativeHit == 1 && expectedHit && opened && closed });
                                    combo.IsDropDownOpen = false;
                                }
                            }
                            CaptureNative(form, Path.Combine(directory, $"task-form-{(light ? "light" : "dark")}.png"));
                        }
                        finally
                        {
                            form.Close();
                            await ((ToolWindow)form).ClosureTask.WaitAsync(TimeSpan.FromSeconds(5));
                        }
                    }
                    await File.WriteAllTextAsync(Path.Combine(directory, "journey.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

                    async Task Buttons(Button button)
                    {
                        Assert.NotNull(button.ContextMenu);
                        foreach (var x in new[] { .1, .5, .9 })
                        foreach (var y in new[] { .15, .5, .85 })
                        {
                            current = $"{light}/{width}/{button.Name}/{x}/{y}";
                            var point = button.PointToScreen(new Point(button.ActualWidth * x, button.ActualHeight * y));
                            var nativeHit = NativeHit(main, point); var expectedHit = ReferenceEquals(Ancestors(main.InputHitTest(main.PointFromScreen(point)) as DependencyObject).OfType<Button>().FirstOrDefault(), button);
                            Move(main, point); await Task.Delay(20); Click(main, point); await Task.Delay(85);
                            var opened = button.ContextMenu.IsOpen;
                            trace.Add(new { current, kind = "observed", opened, button.IsMouseOver, button.IsPressed,
                                captured = Mouse.Captured?.GetType().Name, over = (Mouse.DirectlyOver as FrameworkElement)?.Name });
                            if (opened) { Escape(); await Task.Delay(65); }
                            var closed = !button.ContextMenu.IsOpen;
                            results.Add(new { light, width, height, kind = "dropdown-click", button = button.Name, x, y, nativeHit, expectedHit, opened, escaped = closed,
                                passed = nativeHit == 1 && expectedHit && opened && closed });
                            // Cleanup is not accepted as an Escape pass.
                            button.ContextMenu.IsOpen = false;
                        }
                    }
                }
            }
            finally
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "journey.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                await File.WriteAllTextAsync(Path.Combine(directory, "input-trace.json"), JsonSerializer.Serialize(trace, new JsonSerializerOptions { WriteIndented = true }));
                main.RemoveHandler(Mouse.PreviewMouseDownEvent, input); main.RemoveHandler(Mouse.PreviewMouseUpEvent, input); main.RemoveHandler(Button.ClickEvent, click);
                if (ForegroundOwned()) { SetCursorPos(oldCursor.X, oldCursor.Y); }
                main.WindowState = WindowState.Minimized;
            }
            Assert.Equal(visualOnly ? (traceOnly ? 1 : 6) : traceOnly ? 49 : 368, results.Count);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "journey.json")));
            var failures = document.RootElement.EnumerateArray().Where(row => !row.GetProperty("passed").GetBoolean()).Select(row => row.GetRawText()).ToArray();
            Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures));
        }, TimeSpan.FromMinutes(6));
    }

    private static bool ForegroundOwned()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid); return pid == Environment.ProcessId;
    }
    private static void OwnForeground() => Assert.True(ForegroundOwned(), "Foreground left the explicitly activated fixture; no input sent.");
    private static bool NativeTargetOwned(Window target, Point point)
    {
        var expected = new WindowInteropHelper(target).Handle;
        var under = WindowFromPoint(new() { X = (int)point.X, Y = (int)point.Y });
        GetWindowThreadProcessId(under, out var pid);
        return expected != IntPtr.Zero && pid == Environment.ProcessId && GetAncestor(under, 2) == expected;
    }
    private static void OwnTarget(Window target, Point point, bool currentCursor)
    {
        OwnForeground();
        Assert.True(NativeTargetOwned(target, point), "Native recipient is not the exact intended fixture HWND; no input sent.");
        var capture = GetCapture();
        Assert.True(capture == IntPtr.Zero || GetAncestor(capture, 2) == new WindowInteropHelper(target).Handle,
            "Mouse capture belongs to another HWND; no input sent.");
        if (!currentCursor) { return; }
        Assert.True(GetCursorPos(out var actual));
        Assert.InRange(Math.Abs(actual.X - point.X), 0, 1); Assert.InRange(Math.Abs(actual.Y - point.Y), 0, 1);
        Assert.True(NativeTargetOwned(target, new(actual.X, actual.Y)), "Cursor moved to another HWND; no input sent.");
    }
    private static void Move(Window target, Point point)
    {
        OwnTarget(target, point, currentCursor: false); Assert.True(SetCursorPos((int)point.X, (int)point.Y));
        OwnTarget(target, point, currentCursor: true);
    }
    private static void Click(Window target, Point point)
    {
        OwnTarget(target, point, currentCursor: true); Send([MousePacket(0x0002), MousePacket(0x0004)]);
    }
    private static void Escape()
    {
        OwnForeground(); Send([new() { Type = 1, Data = new() { Keyboard = new() { Key = 0x1b } } },
            new() { Type = 1, Data = new() { Keyboard = new() { Key = 0x1b, Flags = 2 } } }]);
    }
    private static void MouseInput(Window target, Point point, uint flags, uint data = 0)
    {
        OwnTarget(target, point, currentCursor: true); Send([MousePacket(flags, data)]);
    }
    private static InputPacket MousePacket(uint flags, uint data = 0) => new() { Data = new() { Mouse = new() { Flags = flags, Data = data } } };
    private static void Send(InputPacket[] packets)
    {
        var size = Marshal.SizeOf<InputPacket>();
        var sent = SendInput((uint)packets.Length, packets, size);
        if (sent == 1 && packets.Length == 2)
        {
            var release = packets[0];
            if (release.Type == 1) { release.Data.Keyboard.Flags |= 2; }
            else { release.Data.Mouse.Flags = 0x0004; }
            SendInput(1, [release], size); // Release a partially injected down before reporting failure.
        }
        Assert.Equal((uint)packets.Length, sent);
    }
    private static void CaptureNative(Window window, string path)
    {
        OwnForeground(); var hwnd = new WindowInteropHelper(window).Handle; Assert.True(GetWindowRect(hwnd, out var rectangle));
        using var bitmap = new System.Drawing.Bitmap(rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc(); try { Assert.True(PrintWindow(hwnd, hdc, 2)); } finally { graphics.ReleaseHdc(hdc); }
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
    [StructLayout(LayoutKind.Sequential)] private struct InputPacket { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public MousePacketData Mouse;
        [FieldOffset(0)] public KeyboardPacketData Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MousePacketData { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardPacketData { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct ScreenRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(ScreenPoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out ScreenPoint point);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, InputPacket[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out ScreenRect rectangle);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
