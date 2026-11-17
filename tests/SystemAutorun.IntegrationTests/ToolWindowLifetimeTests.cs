using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Services;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class ToolWindowLifetimeTests
{
    [Fact]
    public Task RegistrationWaitsForSourceAndRetiresOnlyAfterCleanup() => WithOwner(async owner =>
    {
        var registry = new ToolWindowLifetimes(); var gate = NewGate();
        var window = new FakeTool(registry, gate.Task) { Owner = owner };
        Assert.Equal(0, registry.Count); Assert.True(window.PublishedCleanup.IsCompleted);
        try
        {
            window.Show(); registry.Track(window); Assert.Equal(1, registry.Count);
            window.CloseForApplicationExit(); await window.ClosureTask;
            Assert.DoesNotContain(window, owner.OwnedWindows.Cast<Window>());
            await PresenceMotionTests.Until(() => window.GetterCalls == 1);
            Assert.Equal(1, registry.Count); Assert.False(window.PublishedCleanup.IsCompleted);
            var closes = 0; var exit = registry.BeginExit();
            var drain = exit.CloseAndDrainAsync(() => { closes++; owner.Close(); });
            Assert.Same(exit, registry.BeginExit()); Assert.Same(drain, exit.CloseAndDrainAsync(() => throw new Exception("must not repeat")));
            Assert.False(drain.IsCompleted); Assert.False(window.Dispatcher.HasShutdownStarted);
            gate.SetResult(); Assert.Empty(await drain); Assert.Equal(0, registry.Count); Assert.Equal(1, closes);
        }
        finally { gate.TrySetResult(); window.CloseForApplicationExit(); await registry.BeginExit().CloseAndDrainAsync(owner.Close); }
    });

    [Fact]
    public Task OwnerDestructionSkipsClosingButStillJoinsTheTaskPublishedByClosed() => WithOwner(async owner =>
    {
        var registry = new ToolWindowLifetimes(); var gate = NewGate();
        var window = new FakeTool(registry, gate.Task) { Owner = owner }; var closing = 0;
        window.Closing += (_, e) => { closing++; e.Cancel = true; };
        try
        {
            window.Show(); Assert.True(window.PublishedCleanup.IsCompleted);
            var drain = registry.BeginExit().CloseAndDrainAsync(owner.Close);
            await PresenceMotionTests.Until(() => window.GetterCalls == 1);
            Assert.Equal(0, closing); Assert.True(window.ClosureTask.IsCompleted); Assert.False(drain.IsCompleted);
            gate.SetResult(); Assert.Empty(await drain); Assert.Equal(0, registry.Count);
        }
        finally { gate.TrySetResult(); owner.Close(); await registry.BeginExit().CloseAndDrainAsync(owner.Close); }
    });

    [Theory]
    [InlineData("pending")] [InlineData("reverse")] [InlineData("hidden")] [InlineData("owner-changed")]
    public Task ExitRetainsHiddenReversedAndReownedWindows(string state) => WithOwner(async owner =>
    {
        var registry = new ToolWindowLifetimes(); var gate = NewGate(); var other = Hidden(new Window()); other.Show();
        var window = new FakeTool(registry, gate.Task) { Owner = owner };
        try
        {
            window.Show(); await WorkflowMotionAssertions.Settled(window);
            window.Close();
            if (window.IsExitPending)
            {
                if (state == "reverse") { window.CancelPendingClose(); }
                if (state == "hidden") { window.Hide(); }
                if (state == "owner-changed") { window.CancelPendingClose(); window.Owner = other; }
            }
            Assert.Equal(1, registry.Count);
            var drain = registry.BeginExit().CloseAndDrainAsync(owner.Close);
            await PresenceMotionTests.Until(() => window.GetterCalls == 1);
            Assert.True(window.ClosureTask.IsCompleted); Assert.False(drain.IsCompleted);
            gate.SetResult(); Assert.Empty(await drain); Assert.Equal(0, registry.Count);
        }
        finally { gate.TrySetResult(); owner.Close(); other.Close(); await registry.BeginExit().CloseAndDrainAsync(owner.Close); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task RetiredFailuresAndGetterFailuresDoNotSkipOtherCleanup(bool getterFailure) => WithOwner(async owner =>
    {
        var registry = new ToolWindowLifetimes(); var gate = NewGate();
        var failed = new FakeTool(registry, getterFailure ? Task.CompletedTask : Task.FromException(new IOException("synthetic cleanup")))
            { Owner = owner, ThrowGetter = getterFailure };
        var slow = new FakeTool(registry, gate.Task) { Owner = owner };
        try
        {
            failed.Show(); slow.Show(); failed.CloseForApplicationExit();
            await PresenceMotionTests.Until(() => registry.Count == 1); // Failure must survive removal from live-window tables.
            var drain = registry.BeginExit().CloseAndDrainAsync(owner.Close);
            await PresenceMotionTests.Until(() => slow.GetterCalls == 1); Assert.False(drain.IsCompleted);
            gate.SetResult(); var error = Assert.Single(await drain);
            Assert.IsType<IOException>(error.InnerException); Assert.Equal(0, registry.Count);
        }
        finally { gate.TrySetResult(); owner.Close(); await registry.BeginExit().CloseAndDrainAsync(owner.Close); }
    });

    [Fact]
    public Task RefusedCloseIsReportedAndDoesNotSkipOtherResources() => WithOwner(async owner =>
    {
        var registry = new ToolWindowLifetimes(); var gate = NewGate();
        var refusing = new FakeTool(registry, Task.CompletedTask) { Owner = owner };
        var slow = new FakeTool(registry, gate.Task) { Owner = owner };
        CancelEventHandler guard = (_, e) => e.Cancel = true;
        try
        {
            refusing.Show(); slow.Show(); refusing.Closing += guard;
            var drain = registry.BeginExit().CloseAndDrainAsync(() => throw new IOException("synthetic owner-close failure"));
            await PresenceMotionTests.Until(() => slow.GetterCalls == 1); Assert.False(drain.IsCompleted);
            gate.SetResult(); var errors = await drain;
            Assert.Equal(2, errors.Count); Assert.Contains(errors, error => error.Message.Contains("incomplete", StringComparison.Ordinal));
            Assert.True(refusing.IsVisible); Assert.False(refusing.ClosureTask.IsCompleted);
        }
        finally
        {
            refusing.Closing -= guard; gate.TrySetResult(); owner.Close();
            await PresenceMotionTests.Until(() => registry.Count == 0);
        }
    });

    [Fact]
    public Task ExitSealsAdmissionAndDoesNotWaitForUnownedBusinessTasks() => WithOwner(async owner =>
    {
        var registry = new ToolWindowLifetimes(); var business = NewGate();
        var window = new FakeTool(registry, Task.CompletedTask) { Owner = owner }; window.Show();
        var exit = registry.BeginExit(); var late = new ToolWindow();
        try
        {
            Assert.Throws<InvalidOperationException>(() => registry.Track(late));
            Assert.Empty(await exit.CloseAndDrainAsync(owner.Close));
            Assert.False(business.Task.IsCompleted); Assert.Equal(0, registry.Count);
        }
        finally { business.TrySetResult(); late.CloseForApplicationExit(); owner.Close(); }
    });

    [Theory]
    [InlineData("log")] [InlineData("self")] [InlineData("recovery")]
    public async Task RealResourceWindowsExposeOnlyPostClosedCleanup(string kind)
    {
        var root = Directory.CreateTempSubdirectory("SystemAutorun-view-lifetime-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "autorun.yaml"), "tasks: []\n");
            await WithOwner(async owner =>
            {
                var registry = new ToolWindowLifetimes(); var gate = NewGate(); var paths = ProjectPaths.Resolve(root);
                IAsyncDisposable model; ToolWindow window;
                if (kind == "log")
                {
                    var value = new LogViewerViewModel(new AgentClient(root), paths); model = value; window = new LogViewerWindow(value);
                }
                else if (kind == "self")
                {
                    var value = new SelfStartupViewModel(paths); model = value; window = new SelfStartupWindow(value);
                }
                else
                {
                    var value = new RecoveryViewModel(paths, "fixture"); model = value; window = new RecoveryWindow(value);
                }
                // Never show/initialize these real models: no IPC/native read. Inject one view-owned read only.
                WorkflowMotionAssertions.HoldRead(model, gate.Task); registry.Track(window);
                try
                {
                    Assert.True(window.ResourceCleanupTask.IsCompleted);
                    var drain = registry.BeginExit().CloseAndDrainAsync(owner.Close);
                    await window.ClosureTask; Assert.False(window.ResourceCleanupTask.IsCompleted); Assert.False(drain.IsCompleted);
                    gate.SetResult(); Assert.Empty(await drain); Assert.True(window.ResourceCleanupTask.IsCompletedSuccessfully);
                    WorkflowMotionAssertions.AssertLifetimeDisposed(model); Assert.Equal(0, registry.Count);
                }
                finally { gate.TrySetResult(); window.CloseForApplicationExit(); await model.DisposeAsync(); await registry.BeginExit().CloseAndDrainAsync(owner.Close); }
            });
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(resolved).StartsWith("SystemAutorun-view-lifetime-", StringComparison.Ordinal)
                || new DirectoryInfo(resolved).Attributes.HasFlag(FileAttributes.ReparsePoint)) { throw new IOException("Unexpected fixture root."); }
            Directory.Delete(resolved, recursive: true);
        }
    }

    [Fact]
    public Task ExitSuppressesLateMessagesWithoutInterpretingConfirmationAsConsent() => WithOwner(owner =>
    {
        var app = (SystemAutorun.App.App)Application.Current;
        var property = typeof(SystemAutorun.App.App).GetProperty(nameof(SystemAutorun.App.App.IsExiting))!;
        var previous = app.IsExiting;
        try
        {
            property.SetValue(app, true);
            MessageDialog.ShowError(owner, "late", "must not open"); MessageDialog.ShowInfo(owner, "late", "must not open");
            Assert.False(MessageDialog.ShowConfirm(owner, "late", "must not consent")); Assert.Empty(owner.OwnedWindows.Cast<Window>());
        }
        finally { property.SetValue(app, previous); }
        return Task.CompletedTask;
    });

    [Fact]
    public async Task RemotePreparationFinishingAfterExitDoesNotOpenATool()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var original = await File.ReadAllTextAsync(fixture.Paths.ConfigPath);
        await WithOwner(async owner =>
        {
            var configuration = new AgentConfigurationClient(fixture.Client);
            var model = new MainWindowViewModel(fixture.Client, new NativeCacheStore(fixture.Paths.GuiCachePath),
                configuration, fixture.Paths, new NeverRemoteReader());
            var window = Hidden(new SystemAutorun.App.MainWindow(model, new AgentStartupClient(fixture.Client), new StartupCacheStore(fixture.Paths.StartupCachePath)));
            var gate = NewGate(); var app = (SystemAutorun.App.App)Application.Current; var previous = app.IsExiting;
            var property = typeof(SystemAutorun.App.App).GetProperty(nameof(SystemAutorun.App.App.IsExiting))!;
            Task? opening = null;
            try
            {
                window.Show();
                typeof(MainWindowViewModel).GetField("_settingsSaveTask", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, gate.Task);
                opening = window.OpenRemoteHostsAsync(); Assert.False(opening.IsCompleted);
                property.SetValue(app, true); gate.SetResult(); await opening.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(window.OwnedWindows.Cast<Window>());
                Assert.False(window.Dispatcher.HasShutdownStarted);
            }
            finally
            {
                gate.TrySetResult(); property.SetValue(app, true);
                try
                {
                    foreach (var child in window.OwnedWindows.Cast<Window>().ToArray())
                    { if (child is ToolWindow tool) { tool.CloseForApplicationExit(); } else { child.Close(); } }
                    window.Close(); if (opening is not null) { await opening.WaitAsync(TimeSpan.FromSeconds(5)); }
                }
                finally { property.SetValue(app, previous); }
            }
        });
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.Paths.ConfigPath)); Assert.Empty(fixture.ObservedErrors);
    }

    private sealed class NeverRemoteReader : IRemoteHostReader
    {
        public Task<SystemAutorun.Core.Models.RemoteHostStatusSnapshot> ReadAsync(SystemAutorun.Core.Models.RemoteHostDefinition host, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Exit tests must not connect to remote hosts.");
    }

    [Fact]
    public async Task AppExitWaitsForAClosedLogViewBeforeFinalDispatcherAction()
    {
        var probe = Environment.GetEnvironmentVariable("SA_LIFETIME_PROBE");
        if (probe is not null)
        {
            probe = Path.GetFullPath(probe);
            if (Path.GetDirectoryName(probe) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(probe).StartsWith("SystemAutorun-lifetime-probe-", StringComparison.Ordinal)
                || !Directory.Exists(probe) || new DirectoryInfo(probe).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Expected an ordinary owned probe root.");
        }
        var root = Directory.CreateTempSubdirectory("SystemAutorun-view-lifetime-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "autorun.yaml"), "tasks: []\n");
            await WithOwner(async owner =>
            {
                var registry = new ToolWindowLifetimes(); var read = NewGate(); var requested = NewGate();
                var app = (SystemAutorun.App.App)Application.Current; var previous = app.IsExiting;
                var state = new System.Windows.Controls.TextBlock { Text = "隔离退出等待验证：先关闭日志窗口。", Margin = new Thickness(20) };
                var exitButton = new System.Windows.Controls.Button { Content = "退出隔离验证", IsEnabled = false, FocusVisualStyle = null, Margin = new Thickness(20), Height = 40 };
                var releaseButton = new System.Windows.Controls.Button { Content = "释放合成读取", IsEnabled = false, FocusVisualStyle = null, Margin = new Thickness(20), Height = 40 };
                var panel = new System.Windows.Controls.StackPanel(); panel.Children.Add(state); panel.Children.Add(exitButton); panel.Children.Add(releaseButton); owner.Content = panel;
                owner.Title = "隔离视图清理验证 · 无Agent或启动项操作";
                exitButton.Click += (_, _) => requested.TrySetResult(); releaseButton.Click += (_, _) => read.TrySetResult();
                var model = new LogViewerViewModel(new AgentClient(root), ProjectPaths.Resolve(root));
                var window = Hidden(new LogViewerWindow(model)) ; window.Owner = owner;
                // Real view/disposal; suppress startup IPC and hold one synthetic view-owned read.
                typeof(LogViewerWindow).GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                typeof(LogViewerViewModel).GetProperty(nameof(LogViewerViewModel.StatusMessage))!.SetValue(model, "合成清理闸门；未连接 Agent 或日志源。");
                WorkflowMotionAssertions.HoldRead(model, read.Task);
                window.SourceInitialized += (_, _) => registry.Track(window);
                var shutdownCalls = 0; Task? exit = null;
                async Task Ready(string phase)
                {
                    if (probe is null) { return; }
                    await File.WriteAllTextAsync(Path.Combine(probe, "ready.txt"),
                        $"Phase={phase}\nPID={Environment.ProcessId}\nOwner={new System.Windows.Interop.WindowInteropHelper(owner).Handle}\nTool={new System.Windows.Interop.WindowInteropHelper(window).Handle}\nRoot={root}\nNo IPC or native adapter; final shutdown is an injected marker, not Application.Shutdown.\n");
                }
                try
                {
                    if (probe is not null) { owner.Left = 60; owner.Top = 60; window.Left = 180; window.Top = 140; }
                    window.Show(); await WorkflowMotionAssertions.Settled(window); await Ready("close-log");
                    if (probe is null) { SystemCommands.CloseWindow(window); }
                    await window.ClosureTask.WaitAsync(TimeSpan.FromMinutes(5));
                    Assert.False(window.ResourceCleanupTask.IsCompleted); Assert.Equal(1, registry.Count);
                    state.Text = "日志窗口已关闭；视图读取尚未结束。请点击退出。"; exitButton.IsEnabled = true;
                    await Ready("request-exit"); if (probe is null) { requested.TrySetResult(); }
                    await requested.Task.WaitAsync(TimeSpan.FromMinutes(5)); exitButton.IsEnabled = false;
                    exit = app.RequestExitAsync(() => { shutdownCalls++; owner.Close(); }, registry);
                    await app.RequestExitAsync(() => throw new Exception("Repeated exit must not repeat shutdown."), registry);
                    Assert.False(exit.IsCompleted); Assert.Equal(0, shutdownCalls); Assert.True(app.IsExiting);
                    state.Text = "正在等待合成读取回收；尚未执行最终关闭。"; releaseButton.IsEnabled = true;
                    await Ready("waiting-cleanup"); if (probe is null) { read.TrySetResult(); }
                    await exit.WaitAsync(TimeSpan.FromMinutes(5));
                    Assert.Equal(1, shutdownCalls); Assert.True(window.ResourceCleanupTask.IsCompletedSuccessfully);
                    Assert.Equal(0, registry.Count); Assert.False(window.Dispatcher.HasShutdownStarted);
                    Assert.Single(Directory.GetFiles(root));
                    if (probe is not null) { await File.WriteAllTextAsync(Path.Combine(probe, "after.json"), "{\"closedBeforeExit\":true,\"waitedForRead\":true,\"finalActionCalls\":1,\"remainingViews\":0,\"actualDispatcherShutdown\":false}"); }
                }
                finally
                {
                    try
                    {
                        read.TrySetResult(); window.CloseForApplicationExit(); await model.DisposeAsync();
                        if (exit is not null) { await exit; } else { await registry.BeginExit().CloseAndDrainAsync(owner.Close); }
                    }
                    finally { typeof(SystemAutorun.App.App).GetProperty(nameof(SystemAutorun.App.App.IsExiting))!.SetValue(app, previous); }
                }
            }, probe is null ? null : TimeSpan.FromMinutes(16));
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(resolved).StartsWith("SystemAutorun-view-lifetime-", StringComparison.Ordinal)
                || new DirectoryInfo(resolved).Attributes.HasFlag(FileAttributes.ReparsePoint)) { throw new IOException("Unexpected fixture root."); }
            Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class FakeTool : ToolWindow
    {
        private readonly Task _cleanup;
        internal Task PublishedCleanup { get; private set; } = Task.CompletedTask;
        internal bool ThrowGetter { get; init; }
        internal int GetterCalls { get; private set; }
        internal override Task ResourceCleanupTask
        {
            get
            {
                Dispatcher.VerifyAccess(); GetterCalls++;
                Assert.True(ClosureTask.IsCompleted);
                if (ThrowGetter) { throw new IOException("synthetic getter failure"); }
                return PublishedCleanup;
            }
        }
        internal FakeTool(ToolWindowLifetimes registry, Task cleanup)
        {
            _cleanup = cleanup; Width = 300; Height = 160; Content = new PresencePanel { MotionKind = PresenceMotionKind.Tool, IsPresented = true };
            Hidden(this); SourceInitialized += (_, _) => registry.Track(this);
            Closed += (_, _) => PublishedCleanup = _cleanup;
        }
    }
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Hidden<T>(T window) where T : Window
    { window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -32000; window.Top = -32000; return window; }
    private static Task WithOwner(Func<Window, Task> action, TimeSpan? timeout = null) => GuiConfigurationControlTests.RunStaAsync(async () =>
    {
        if (Application.Current is not SystemAutorun.App.App)
        { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
        WindowTheme.Install(); var owner = Hidden(new Window { Width = 960, Height = 620 });
        try { owner.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await action(owner); }
        finally { owner.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    }, timeout);
}
