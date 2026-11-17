using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.IntegrationTests;

// Optional owned-window evidence only. This observer never injects input, activates windows or touches the clipboard.
internal static class RecoveryGuiEntryProbe
{
    internal static string? ForCase(string source, bool remote)
    {
        var probe = source == "more" && !remote && Environment.GetEnvironmentVariable("SA_RECOVERY_GUI_PROBE_CASE") == "entries"
            ? Environment.GetEnvironmentVariable("SA_RECOVERY_GUI_PROBE") : null;
        if (probe is null) { return null; }
        var full = Path.GetFullPath(probe);
        if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
            || !Path.GetFileName(full).StartsWith("SystemAutorun-recovery-gui-probe-", StringComparison.Ordinal)
            || !Directory.Exists(full) || new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint)
            || Directory.EnumerateFileSystemEntries(full).Any()) { throw new InvalidOperationException("Expected an empty ordinary owned recovery probe directory."); }
        return full;
    }

    internal static async Task RunAsync(string directory, ConfigurationControlPlaneTests.ServerFixture host, SystemAutorun.App.MainWindow main, MainWindowViewModel dashboard)
    {
        // The row was appended after the initial filter. Use the real filter path, not hard-coded derived counts.
        var filter = dashboard.SearchText; dashboard.SearchText = ""; dashboard.SearchText = filter;
        Assert.Equal(1, dashboard.TotalTaskCount); Assert.False(dashboard.IsEmpty); Assert.Single(dashboard.TasksView.Cast<object>());
        var hidden = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new HashSet<RecoveryWindow>(); var windows = new List<Window> { main }; string? last = null; var pointerEvents = 0; var keyEvents = 0; var wheelEvents = 0;
        var workflow = RecoveryWorkflow.ForTask(host.Paths, "media");
        string FocusName() => Keyboard.FocusedElement is FrameworkElement element ? string.IsNullOrEmpty(element.Name) ? element.GetType().Name : element.Name : "none";
        object ControlState(Control control) => new
        {
            control.Name, Type = control.GetType().Name, Role = SystemAutorun.App.Services.ColorStateAssist.GetRole(control).ToString(),
            control.IsVisible, control.IsEnabled, control.IsMouseOver, control.IsKeyboardFocused,
            Foreground = control.Foreground?.ToString(), Background = control.Background?.ToString(), Border = control.BorderBrush?.ToString(),
            Thickness = control.BorderThickness.ToString(), FocusVisualIsNull = control.FocusVisualStyle is null
        };
        object? Footer(Window window)
        {
            if (window is not RecoveryWindow { IsVisible: true } tool) { return null; }
            var origin = tool.ActionFooter.TranslatePoint(new Point(), tool); var footer = new Rect(origin, new Size(tool.ActionFooter.ActualWidth, tool.ActionFooter.ActualHeight));
            return new { footer.X, footer.Y, footer.Width, footer.Height, InsideWindow = footer.Left >= 0 && footer.Top >= 0 && footer.Right <= tool.ActualWidth && footer.Bottom <= tool.ActualHeight };
        }
        object State(Window window) => new
        {
            Window = ReferenceEquals(window, main) ? "main" : "recovery", Hwnd = (long)new WindowInteropHelper(window).Handle,
            ForegroundHwnd = (long)GetForegroundWindow(), window.IsActive, NativeOwnerEnabled = IsWindowEnabled(new WindowInteropHelper(main).Handle),
            Focus = FocusName(), SelectedTask = dashboard.SelectedTask?.Name, dashboard.SearchText, dashboard.IsWorkloadsPage, dashboard.TotalTaskCount, dashboard.IsEmpty,
            WorkflowState = workflow.Current.State.ToString(), workflow.Current.OperationId,
            ToolState = (window.DataContext as RecoveryViewModel)?.Heading,
            Surface = (window.Content as Border)?.Background?.ToString(),
            Controls = (window is RecoveryWindow view ? new Control[] { view.ChromeCloseButton, view.RefreshButton, view.PrepareButton, view.QueryButton, view.CopyButton, view.CancelButton, view.ConfirmButton } : new Control[] { main.ServiceMoreButton }).Select(ControlState).ToArray(),
            ScrollOffset = (window as RecoveryWindow)?.ContentScroll.VerticalOffset, ScrollableHeight = (window as RecoveryWindow)?.ContentScroll.ScrollableHeight,
            ViewportHeight = (window as RecoveryWindow)?.ContentScroll.ViewportHeight, TechnicalExpanded = (window as RecoveryWindow)?.TechnicalExpander.IsExpanded,
            ActionFooter = Footer(window)
        };
        void Record(string kind, Window window, string detail) => File.AppendAllText(Path.Combine(directory, "events.jsonl"), JsonSerializer.Serialize(new { At = DateTimeOffset.Now, Kind = kind, Detail = detail, State = State(window) }) + "\n");
        void OnPointer(object sender, MouseButtonEventArgs args) { pointerEvents++; Record("pointer", (Window)sender, args.ChangedButton.ToString()); }
        void OnKey(object sender, KeyEventArgs args) { keyEvents++; Record("key", (Window)sender, $"{Keyboard.Modifiers}:{args.Key}"); }
        void OnWheel(object sender, MouseWheelEventArgs args) { wheelEvents++; Record("wheel", (Window)sender, args.Delta.ToString()); }
        void OnFocus(object sender, KeyboardFocusChangedEventArgs args) => Record("focus", (Window)sender, FocusName());
        void Attach(Window window)
        {
            window.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPointer), true);
            window.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel), true);
            window.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnKey), true);
            window.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnFocus), true);
        }
        void Discover(object? sender, EventArgs args)
        {
            foreach (var tool in main.OwnedWindows.OfType<RecoveryWindow>())
            {
                if (!observed.Add(tool)) { continue; } windows.Add(tool); tool.ToolTitle.Text = "SystemAutorun（隔离恢复入口夹具）"; Attach(tool); Record("tool-open", tool, "Actual MainWindow handler; no probe-created dialog.");
            }
            var active = windows.LastOrDefault(window => window.IsVisible) ?? main; var state = JsonSerializer.Serialize(State(active));
            if (last == state) { return; } last = state; Record("observation", active, "Native foreground is observed, not inferred from WPF logical focus.");
        }
        void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs args) { if (!main.IsVisible) { hidden.TrySetResult(); } }
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, Discover, main.Dispatcher);
        main.Title = "SystemAutorun · 隔离恢复入口探针"; Attach(main); main.IsVisibleChanged += OnVisibleChanged;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "ready.txt"), $"PID={Environment.ProcessId}\nRoot={host.Root}\nHWND={new WindowInteropHelper(main).Handle}\nDPI={VisualTreeHelper.GetDpi(main).DpiScaleX}\nSelected=media; Search=med; View=Workloads\nRG-F1: real AgentServer; task row is an explicit UNKNOWN test projection; no monitor, workload executor, Run/Boot or clipboard operations. Close main last.\n");
            Discover(null, EventArgs.Empty); await hidden.Task.WaitAsync(TimeSpan.FromMinutes(10));
        }
        finally
        {
            timer.Stop(); main.IsVisibleChanged -= OnVisibleChanged;
            foreach (var tool in main.OwnedWindows.OfType<RecoveryWindow>()) { observed.Add(tool); }
            foreach (var tool in observed)
            {
                tool.Close(); await tool.ShutdownTask;
                if (tool.DataContext is RecoveryViewModel view && view.ConfirmCommand.ExecutionTask is { } confirmation) { await confirmation; }
            }
            foreach (var window in windows)
            {
                window.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPointer));
                window.RemoveHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));
                window.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnKey));
                window.RemoveHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnFocus));
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "after.json"), JsonSerializer.Serialize(new
            {
                PointerEvents = pointerEvents, KeyEvents = keyEvents, WheelEvents = wheelEvents, ToolCount = observed.Count, SelectedTask = dashboard.SelectedTask?.Name, dashboard.SearchText,
                dashboard.IsWorkloadsPage, State = workflow.Current.State.ToString(), workflow.Current.OperationId,
                Pending = (await new RecoveryPendingStore(host.Paths, "media").ReadAsync()).State.ToString(),
                JournalFiles = Directory.GetFiles(host.Root, "*.operation.json", SearchOption.AllDirectories).Length,
                Errors = host.ObservedErrors.Select(row => new { row.Command, Code = (row.Error as AgentCommandException)?.Code, Type = row.Error.GetType().Name }).ToArray()
            }));
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true)] private static extern nint GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint window);
}
