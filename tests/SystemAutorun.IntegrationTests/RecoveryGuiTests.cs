using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Recovery;
using RealServer = SystemAutorun.IntegrationTests.ConfigurationControlPlaneTests.ServerFixture;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed partial class RecoveryGuiTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ProductionAgentExplicitConfirmOrKnownPreflightFailureNeverInventsSuccess(bool drift)
    {
        await using var host = new RealServer(); await Initialize(host);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            await using var model = new RecoveryViewModel(host.Paths, "media"); await model.InitializeAsync();
            Assert.Equal("自动恢复已暂停", model.StatusText); Assert.True(model.CanPrepare); Assert.False(model.CanConfirm);
            await model.PrepareCommand.ExecuteAsync(null); Assert.True(model.CanConfirm); Assert.False(model.Snapshot.Preparation!.Confirmed);
            Assert.Equal("确认解除恢复暂停", model.Heading); Assert.Contains("不直接启动", model.Notice);
            Assert.Equal(RecoveryPendingState.Missing, (await Pending(host.Paths).ReadAsync()).State);
            if (drift) { await File.WriteAllTextAsync(host.Paths.ConfigPath, "version: 99\ntasks: []\n"); }
            await model.ConfirmCommand.ExecuteAsync(null); Assert.False(model.CanConfirm);
            if (drift)
            {
                Assert.Equal(RecoveryWorkflowState.ReadFailed, model.Snapshot.State); Assert.Null(model.Snapshot.OperationId);
                Assert.Contains("配置无法读取", model.Notice); Assert.Empty(Directory.GetFiles(host.Root, "*.operation.json", SearchOption.AllDirectories));
                Assert.Equal("configuration", Assert.Single(host.ObservedErrors).Command);
            }
            else
            {
                Assert.Equal(RecoveryWorkflowState.Completed, model.Snapshot.State); Assert.Equal("自动恢复未暂停", model.StatusText);
                Assert.False(model.CanPrepare); Assert.Contains("不代表服务已运行", model.Notice); Assert.Empty(host.ObservedErrors);
                Assert.Single(Directory.GetFiles(host.Root, "*.operation.json", SearchOption.AllDirectories));
            }
            Assert.Equal(RecoveryPendingState.Missing, (await Pending(host.Paths).ReadAsync()).State);
            await model.ConfirmCommand.ExecuteAsync(null); // Direct calls cannot promote an old preparation.
        });
    }

    [Theory]
    [InlineData("uninitialized")] [InlineData("disabled")] [InlineData("missing-task")]
    [InlineData("unpaused")] [InlineData("corrupt")] [InlineData("configuration")]
    public async Task ActualReadOnlyStatesNeverOfferAnInventedResumeOrRepair(string state)
    {
        await using var host = new RealServer(); await Initialize(host, state != "uninitialized"); var store = new RecoveryControlStore(host.Paths);
        if (state == "disabled") { await File.WriteAllTextAsync(host.Paths.ConfigPath, RecoveryWorkflowIntegrationTests.Configuration.Replace("enabled: true", "enabled: false", StringComparison.Ordinal)); }
        if (state == "missing-task") { await File.WriteAllTextAsync(host.Paths.ConfigPath, RecoveryWorkflowIntegrationTests.Configuration.Replace("name: media", "name: other", StringComparison.Ordinal)); }
        if (state == "unpaused") { await store.SetPausedAsync("media", false, recordIntent: true); }
        if (state == "corrupt") { await File.WriteAllTextAsync(store.FilePath, "{}"); }
        if (state == "configuration") { await File.WriteAllTextAsync(host.Paths.ConfigPath, "version: 99\ntasks: []\n"); }
        var before = File.Exists(store.FilePath) ? File.ReadAllBytes(store.FilePath) : null;
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            await using var model = new RecoveryViewModel(host.Paths, "media"); await model.InitializeAsync();
            Assert.False(model.CanPrepare); Assert.False(model.CanConfirm); Assert.True(model.CanRead); Assert.Equal("media", model.TaskName);
            Assert.Equal(host.Paths.RootDirectory, model.RootDirectory); Assert.Equal("", model.OperationId);
            Assert.Equal(state switch { "uninitialized" => "恢复状态尚未初始化", "unpaused" => "自动恢复未暂停", "corrupt" or "configuration" => "当前恢复意图尚未核对", _ => "自动恢复已暂停" }, model.StatusText);
            if (state == "disabled") { Assert.Contains("服务已禁用", model.Notice); }
            if (state == "missing-task") { Assert.Contains("没有该服务", model.Notice); }
            if (state == "corrupt") { Assert.Contains("不重置", model.Notice); }
            await model.PrepareCommand.ExecuteAsync(null); await model.ConfirmCommand.ExecuteAsync(null);
            Assert.Empty(Directory.GetFiles(host.Root, "*.operation.json", SearchOption.AllDirectories));
            Assert.Equal(RecoveryPendingState.Missing, (await Pending(host.Paths).ReadAsync()).State);
            if (before is null) { Assert.False(File.Exists(store.FilePath)); } else { Assert.Equal(before, File.ReadAllBytes(store.FilePath)); }
        });
        if (state is "corrupt" or "configuration") { Assert.Single(host.ObservedErrors); } else { Assert.Empty(host.ObservedErrors); }
    }

    [Fact]
    public async Task ClosingPreparedViewDiscardsOnlyItsOwnPreparationAndNeverNotifiesClosedView()
    {
        await using var host = new RealServer(); await Initialize(host);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var model = new RecoveryViewModel(host.Paths, "media"); await model.InitializeAsync(); await model.PrepareCommand.ExecuteAsync(null);
            Assert.True(model.CanConfirm); await model.DisposeAsync(); var changes = 0; model.PropertyChanged += (_, _) => changes++;
            await model.ConfirmCommand.ExecuteAsync(null); model.RefreshSnapshot(); Assert.Equal(0, changes);
            await using var next = new RecoveryViewModel(host.Paths, "MEDIA"); await next.InitializeAsync();
            Assert.True(next.CanPrepare); Assert.False(next.CanConfirm); Assert.Equal("MEDIA", next.TaskName);
            Assert.Empty(Directory.GetFiles(host.Root, "*.operation.json", SearchOption.AllDirectories));
        });
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task WindowOrOwnerCloseKeepsClaimedSubmissionAndActualReadCleanup(bool destroyOwner, bool checking)
    {
        await using var host = new RecoveryWorkflowDispatchFixture(); await host.InitializeAsync(); var entered = Signal(); var release = Signal();
        host.BeforeDispatch = async request =>
        {
            if (checking && request.Command == "configuration" && host.Requests.Count(row => row.Command == "configuration") == 3)
            { entered.TrySetResult(); await release.Task.WaitAsync(host.Token); }
        };
        host.AfterDispatch = async (request, response) => { if (request.Command == "recovery-resume") { Assert.True(response.Ok); entered.TrySetResult(); await release.Task.WaitAsync(host.Token); } };
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            EnsureApplication(); var owner = Owner(); var model = new RecoveryViewModel(host.Paths, "media"); var window = Hidden(new RecoveryWindow(model));
            Task submit = Task.CompletedTask; RecoveryWindow? reopened = null; RecoveryViewModel? next = null;
            try
            {
                owner.Show(); window.Owner = owner; window.Show(); await Loaded(window); await model.PrepareCommand.ExecuteAsync(null);
                submit = model.ConfirmCommand.ExecuteAsync(null); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                model.ObserveSnapshot(); var id = model.OperationId; if (checking) { Assert.Empty(id); } else { Assert.NotEmpty(id); }
                var path = Path.Combine(host.Root, Pending(host.Paths).FileName); var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
                Assert.Equal(checking, bytes is null);
                if (destroyOwner) { owner.Close(); } else { window.Close(); }
                await window.ShutdownTask; var changes = 0; model.PropertyChanged += (_, _) => changes++; Assert.False(submit.IsCompleted);
                next = new(host.Paths, "media"); reopened = Hidden(new RecoveryWindow(next)); reopened.Show(); await Loaded(reopened);
                Assert.Equal(id, next.OperationId); Assert.False(next.CanPrepare); Assert.False(next.CanConfirm);
                if (bytes is null) { Assert.False(File.Exists(path)); } else { Assert.Equal(bytes, File.ReadAllBytes(path)); }
                release.TrySetResult(); await submit; next.ObserveSnapshot(); Assert.Equal(0, changes); Assert.Equal(RecoveryWorkflowState.Completed, next.Snapshot.State);
                Assert.Equal("自动恢复未暂停", next.StatusText); Assert.False(File.Exists(path));
            }
            finally
            {
                release.TrySetResult(); await submit; window.Close(); await window.ShutdownTask;
                if (reopened is not null) { reopened.Close(); await reopened.ShutdownTask; } if (next is not null) { await next.DisposeAsync(); }
                owner.Close(); await model.DisposeAsync();
            }
        });
        Assert.Empty(host.Errors); Assert.Equal(0, host.WorkloadCalls); Assert.Single(host.Requests, request => request.Command == "recovery-resume");
    }

    [Fact]
    public async Task LostAckRealOmittedHistoryAndFreshCurrentReadRemainSeparateAndLocked()
    {
        await using var host = new RecoveryWorkflowDispatchFixture(1) { DropResumeAck = true }; await host.InitializeAsync();
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            await using var model = new RecoveryViewModel(host.Paths, "media"); await model.InitializeAsync(); await model.PrepareCommand.ExecuteAsync(null); await model.ConfirmCommand.ExecuteAsync(null);
            Assert.Equal("结果未确认", model.Heading); Assert.Contains("保留原操作", model.Notice); Assert.True(model.CanQuery); Assert.True(model.CanCopy); Assert.False(model.ShowRead);
            var id = model.OperationId; var bytes = File.ReadAllBytes(Path.Combine(host.Root, Pending(host.Paths).FileName));
            await model.QueryCommand.ExecuteAsync(null); Assert.Equal(RecoveryReceiptState.ResultUnavailable, model.Snapshot.Receipt!.State);
            Assert.Contains("结果未保留", model.ReceiptText); Assert.Equal("自动恢复未暂停", model.StatusText); Assert.True(model.Snapshot.IsLocked);
            await host.Store.SetPausedAsync("media", true, recordIntent: true); await model.QueryCommand.ExecuteAsync(null);
            Assert.Equal("自动恢复已暂停", model.StatusText); Assert.Equal(id, model.OperationId); Assert.False(model.CanPrepare); Assert.False(model.CanConfirm);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(host.Root, Pending(host.Paths).FileName)));
        });
        Assert.Empty(host.Errors); Assert.Equal(0, host.WorkloadCalls); Assert.Single(host.Requests, request => request.Command == "recovery-resume");
    }

    [Fact]
    public async Task FreshAckWithFailedCurrentReadRemainsKnownCompletedWithoutDefaultNormal()
    {
        await using var host = new RecoveryWorkflowDispatchFixture(); await host.InitializeAsync();
        host.AfterDispatch = async (request, _) => { if (request.Command == "recovery-resume") { await File.WriteAllTextAsync(host.Paths.ConfigPath, "version: 99\ntasks: []\n"); } };
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            await using var model = new RecoveryViewModel(host.Paths, "media"); await model.InitializeAsync(); await model.PrepareCommand.ExecuteAsync(null); await model.ConfirmCommand.ExecuteAsync(null);
            Assert.Equal(RecoveryWorkflowState.Completed, model.Snapshot.State); Assert.Equal("configuration_error", model.Snapshot.ReadErrorCode);
            Assert.Equal("当前恢复意图尚未核对", model.StatusText); Assert.False(model.CanPrepare); Assert.Contains("请求已返回", model.Notice);
            Assert.Equal(RecoveryPendingState.Missing, (await Pending(host.Paths).ReadAsync()).State);
        });
        Assert.IsType<ConfigurationException>(Assert.Single(host.Errors).Error); Assert.Equal(0, host.WorkloadCalls);
    }

    [Fact]
    public async Task ActualWindowGeometryAndOptionalOwnedProbe()
    {
        var evidence = Environment.GetEnvironmentVariable("SA_RECOVERY_GUI_EVIDENCE"); var probe = Environment.GetEnvironmentVariable("SA_RECOVERY_GUI_PROBE");
        if (probe is not null) { ValidateProbe(probe); }
        await using var host = new RealServer(); await Initialize(host);
        var unknown = probe is not null && Environment.GetEnvironmentVariable("SA_RECOVERY_GUI_PROBE_CASE") == "unknown";
        if (unknown) { await Pending(host.Paths).ReserveAsync(AgentProtocol.NewOperationId(), (await new RecoveryControlStore(host.Paths).ReadAsync()).Revision); }
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            EnsureApplication(); var owner = Owner(); var model = new RecoveryViewModel(host.Paths, "media"); var window = Hidden(new RecoveryWindow(model));
            try
            {
                if (probe is not null) { owner.Left = 40; owner.Top = 40; owner.Width = 1320; owner.Height = 820; window.Left = 160; window.Top = 120; }
                owner.Show(); window.Owner = owner; window.Show(); await Loaded(window); window.ToolTitle.Text += "（隔离恢复夹具）";
                Assert.False(window.ConfirmButton.IsDefault); Assert.Null(window.ConfirmButton.FocusVisualStyle);
                if (probe is not null)
                {
                    if (Environment.GetEnvironmentVariable("SA_RECOVERY_GUI_PROBE_CASE") == "confirm") { await model.PrepareCommand.ExecuteAsync(null); }
                    var closed = Signal(); window.Closed += (_, _) => closed.TrySetResult();
                    await File.WriteAllTextAsync(Path.Combine(probe, "ready.txt"), $"PID={Environment.ProcessId}\nRoot={host.Root}\nHWND={new System.Windows.Interop.WindowInteropHelper(window).Handle}\nDPI={VisualTreeHelper.GetDpi(window).DpiScaleX}\nRG-F1: owned v2 config, real AgentServer, no monitor/workload/Run/Boot.\n");
                    await closed.Task.WaitAsync(TimeSpan.FromMinutes(12)); await window.ShutdownTask;
                    await File.WriteAllTextAsync(Path.Combine(probe, "after.json"), JsonSerializer.Serialize(new { State = RecoveryWorkflow.ForTask(host.Paths, "media").Current.State.ToString(), model.OperationId, Pending = (await Pending(host.Paths).ReadAsync()).State.ToString(), Errors = host.ObservedErrors.Select(row => new { row.Command, Code = (row.Error as AgentCommandException)?.Code, Type = row.Error.GetType().Name }).ToArray() }));
                }
                else
                {
                    var rows = new List<object>(); window.SizeToContent = SizeToContent.Manual; window.Height = window.ActualHeight;
                    foreach (var viewport in new[] { (Width: 1120, Height: 700, Tool: 680), (Width: 1320, Height: 820, Tool: 820), (Width: 1620, Height: 651, Tool: 820) })
                    {
                        owner.Width = viewport.Width; owner.Height = viewport.Height; window.Width = viewport.Tool; await Idle(window);
                        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        { while (Math.Abs(window.ActualWidth - viewport.Tool) > 1) { await Task.Delay(20, deadline.Token); await Idle(window); } }
                        Assert.Equal(viewport.Tool, window.ActualWidth); window.ContentScroll.ScrollToTop(); await Idle(window);
                        CheckBounds(window); if (evidence is not null) { Capture(window, Path.Combine(evidence, $"paused-{viewport.Width}x{viewport.Height}.png")); }
                        window.ContentScroll.ScrollToBottom(); await Idle(window);
                        await model.PrepareCommand.ExecuteAsync(null); await Idle(window); Assert.Equal(0, window.ContentScroll.VerticalOffset); Assert.True(window.ConfirmButton.IsVisible); Assert.False(window.PrepareButton.IsVisible); Assert.False(window.QueryButton.IsVisible);
                        Assert.Equal(host.Paths.RootDirectory, window.RootReadout.Text); Assert.Equal("media", window.TaskReadout.Text);
                        CheckBounds(window); if (evidence is not null) { Capture(window, Path.Combine(evidence, $"confirm-{viewport.Width}x{viewport.Height}.png")); }
                        var prepared = model.Snapshot.Preparation!; Assert.True(RecoveryWorkflow.ForTask(host.Paths, "media").TryDiscardPreparation(prepared)); model.RefreshSnapshot();
                        await Idle(window); var restingHeight = window.ActualHeight;
                        var details = Assert.IsType<StackPanel>(window.TechnicalExpander.Content);
                        // IsExpanded alone does not establish completed content layout.
                        // Exercise actual expanded-content overflow at the existing minimum.
                        window.SizeToContent = SizeToContent.Manual; window.Height = window.MinHeight; window.TechnicalExpander.IsExpanded = true; await Idle(window);
                        var disclosure = Assert.IsType<SystemAutorun.App.Controls.DisclosurePresenter>(window.TechnicalExpander.Template.FindName("DisclosureBody", window.TechnicalExpander));
                        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        { while (disclosure.HasAnimatedProperties || Math.Abs(window.ActualHeight - window.MinHeight) > 1 || !details.IsVisible || details.ActualHeight <= 0
                            || Math.Abs(details.TransformToAncestor(window.ContentScroll).TransformBounds(new Rect(details.RenderSize)).Height - details.ActualHeight) > 1
                            || details.TransformToAncestor(window.ContentScroll).TransformBounds(new Rect(details.RenderSize)).Bottom
                                + window.ContentScroll.VerticalOffset > window.ContentScroll.ExtentHeight + 1)
                            { await Task.Delay(20, deadline.Token); await Idle(window); } }
                        Assert.Equal(window.MinHeight, window.ActualHeight);
                        Assert.Contains(Descendants(details).OfType<TextBlock>(), text => text.Text == model.TechnicalDetails && text.IsVisible && text.ActualHeight > 0);
                        CheckBounds(window); Assert.True(window.ContentScroll.ScrollableHeight > 0,
                            $"owner={viewport.Width}x{viewport.Height}; tool={window.ActualWidth}x{window.ActualHeight}; min={window.MinHeight}; max={window.MaxHeight}; extent={window.ContentScroll.ExtentHeight}; viewport={window.ContentScroll.ViewportHeight}; expanded={window.TechnicalExpander.IsExpanded}");
                        window.ContentScroll.ScrollToBottom(); await Idle(window);
                        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        { while (Math.Abs(window.ContentScroll.VerticalOffset - window.ContentScroll.ScrollableHeight) > 1)
                            { await Task.Delay(20, deadline.Token); await Idle(window); } }
                        CheckBounds(window);
                        rows.Add(new { viewport.Width, viewport.Height, ToolWidth = window.ActualWidth, ToolHeight = window.ActualHeight, window.ContentScroll.ScrollableHeight,
                            DetailsHeight = details.ActualHeight, RenderedDetailsHeight = details.TransformToAncestor(window.ContentScroll).TransformBounds(new Rect(details.RenderSize)).Height,
                            window.ContentScroll.ExtentHeight, window.ContentScroll.ViewportHeight, window.ContentScroll.VerticalOffset,
                            Dpi = VisualTreeHelper.GetDpi(window).DpiScaleX });
                        if (evidence is not null) { Capture(window, Path.Combine(evidence, $"details-scrolled-{viewport.Width}x{viewport.Height}.png")); }
                        window.TechnicalExpander.IsExpanded = false; window.Height = Math.Min(restingHeight, window.MaxHeight); await Idle(window);
                        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        { while (details.IsVisible) { await Task.Delay(20, deadline.Token); await Idle(window); } }
                    }
                    if (evidence is not null) { await File.WriteAllTextAsync(Path.Combine(evidence, "geometry.json"), JsonSerializer.Serialize(rows)); }
                }
            }
            finally { window.Close(); await window.ShutdownTask; owner.Close(); await model.DisposeAsync(); }
        }, probe is null ? null : TimeSpan.FromMinutes(14));
        if (unknown)
        {
            Assert.All(host.ObservedErrors, row => { Assert.Equal("operation", row.Command); Assert.Equal("operation_not_found", Assert.IsType<AgentCommandException>(row.Error).Code); });
        }
        else { Assert.Empty(host.ObservedErrors); }
    }

    private static RecoveryPendingStore Pending(ProjectPaths paths) => new(paths, "media");
    private static async Task Initialize(RealServer host, bool initialized = true)
    {
        await File.WriteAllTextAsync(host.Paths.ConfigPath, RecoveryWorkflowIntegrationTests.Configuration);
        if (initialized) { var store = new RecoveryControlStore(host.Paths); await store.InitializePausedAsync(new[] { "media" }, (await store.ReadAsync()).Revision); }
    }
    private static void EnsureApplication()
    { if (Application.Current is not SystemAutorun.App.App) { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); } WindowTheme.Install(); }
    private static Window Owner() => new() { Width = 1120, Height = 700, ShowActivated = false, Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual };
    private static RecoveryWindow Hidden(RecoveryWindow window) { window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -32000; window.Top = -32000; return window; }
    private static async Task Loaded(RecoveryWindow window) { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await window.InitializationTask; await Idle(window); }
    private static async Task Idle(Window window) { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var value in Descendants(child)) { yield return value; } } }
    private static void CheckBounds(RecoveryWindow window)
    {
        foreach (var button in Descendants(window).OfType<Button>().Where(button => button.IsVisible && !window.ContentScroll.IsAncestorOf(button)))
        {
            Assert.Null(button.FocusVisualStyle); var rect = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
            Assert.True(rect.Top >= -1 && rect.Left >= -1 && rect.Right <= window.ActualWidth + 1 && rect.Bottom <= window.ActualHeight + 1, $"Clipped {button.Name}: {rect}");
        }
        Assert.InRange(window.ActualWidth, window.MinWidth, window.MaxWidth); Assert.True(window.ActualHeight <= window.MaxHeight + 1);
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(window); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(path); encoder.Save(output);
    }
    private static void ValidateProbe(string path)
    {
        var full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) || !Path.GetFileName(full).StartsWith("SystemAutorun-recovery-gui-probe-", StringComparison.Ordinal)
            || !Directory.Exists(full) || new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint)) { throw new InvalidOperationException("Expected an owned ordinary recovery probe directory."); }
    }
}
