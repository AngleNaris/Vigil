using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed partial class SelfStartupGuiTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExplicitConfirmationReadsIndependentRegistrationNotAckData(bool readFails)
    {
        await using var fixture = new Fixture();
        fixture.Handler = (request, _) => Task.FromResult<AgentResponse?>(request.Command == "startup-self-status" && readFails && fixture.StatusReads > 1
            ? fixture.Ack(request) with { Data = StartupRegistrationContract.Unavailable("self", "registration_source_unavailable") } : fixture.Default(request));
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            await using var model = new SelfStartupViewModel(fixture.Paths); await model.InitializeAsync();
            Assert.Equal("未登记", model.RegistrationText); Assert.True(model.CanChoose); Assert.False(model.CanConfirm);
            await model.EnableCommand.ExecuteAsync(null); Assert.True(model.CanConfirm); Assert.False(model.Snapshot.Preparation!.Confirmed);
            Assert.False(File.Exists(fixture.Pending)); Assert.DoesNotContain(fixture.Requests, row => row.Command == "startup-self-set");
            await model.ConfirmCommand.ExecuteAsync(null);
            Assert.Equal(SelfStartupWorkflowState.Completed, model.Snapshot.State);
            Assert.Equal(readFails ? "登记尚未核对" : "未登记", model.RegistrationText); // ACK lies that registered=true; never used.
            Assert.Equal(2, fixture.StatusReads); Assert.Single(fixture.Requests, row => row.Command == "startup-self-set"); Assert.False(File.Exists(fixture.Pending));
            await model.ConfirmCommand.ExecuteAsync(null); Assert.Single(fixture.Requests, row => row.Command == "startup-self-set");
        });
    }

    [Fact]
    public async Task InitialRegistrationReplyArrivingAfterIntentDoesNotOverwriteNewerUnknownState()
    {
        await using var fixture = new Fixture(); var reading = Signal(); var allowStatus = Signal(); var preparing = Signal(); var allowPrepare = Signal();
        fixture.Handler = async (request, token) =>
        {
            if (request.Command == "startup-self-status") { reading.TrySetResult(); await allowStatus.Task.WaitAsync(token); }
            if (request.Command == "startup-self-prepare") { preparing.TrySetResult(); await allowPrepare.Task.WaitAsync(token); }
            return fixture.Default(request);
        };
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            await using var model = new SelfStartupViewModel(fixture.Paths); var initialization = model.InitializeAsync(); Task prepare = Task.CompletedTask;
            try
            {
                await reading.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(model.CanChoose);
                prepare = model.EnableCommand.ExecuteAsync(null); allowStatus.TrySetResult(); await initialization;
                await preparing.Task.WaitAsync(TimeSpan.FromSeconds(5)); model.RefreshSnapshot();
                Assert.Equal("登记尚未核对", model.RegistrationText); Assert.Equal("", model.RegistrationSource);
            }
            finally { allowStatus.TrySetResult(); allowPrepare.TrySetResult(); await initialization; await prepare; }
            Assert.True(model.CanConfirm); Assert.False(File.Exists(fixture.Pending));
        });
    }

    [Fact]
    public async Task ClosingPreparedViewDiscardsOnlyItsPlanWithoutSending()
    {
        await using var fixture = new Fixture();
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var model = new SelfStartupViewModel(fixture.Paths); await model.InitializeAsync(); await model.DisableCommand.ExecuteAsync(null);
            Assert.True(model.CanConfirm); Assert.Contains("无需原生变更", model.ChangeSummary);
            await model.DisposeAsync(); Assert.True(fixture.Workflow.Current.CanPrepare);
            Assert.False(File.Exists(fixture.Pending)); Assert.DoesNotContain(fixture.Requests, row => row.Command == "startup-self-set");
            await using var reopened = new SelfStartupViewModel(fixture.Paths); await reopened.InitializeAsync(); Assert.False(reopened.CanConfirm);
        });
    }

    [Fact]
    public async Task LatePreparationAfterViewCloseNeverConfirmsOrNotifiesClosedView()
    {
        await using var fixture = new Fixture(); var entered = Signal(); var release = Signal();
        fixture.Handler = async (request, token) => { if (request.Command == "startup-self-prepare") { entered.TrySetResult(); await release.Task.WaitAsync(token); } return fixture.Default(request); };
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var model = new SelfStartupViewModel(fixture.Paths); await model.InitializeAsync(); var prepare = model.EnableCommand.ExecuteAsync(null);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var disposal = model.DisposeAsync().AsTask(); var changes = 0;
                model.PropertyChanged += (_, _) => changes++; await disposal; release.TrySetResult(); await prepare;
                Assert.Equal(0, changes); Assert.True(fixture.Workflow.Current.CanPrepare); Assert.False(File.Exists(fixture.Pending));
                Assert.DoesNotContain(fixture.Requests, row => row.Command == "startup-self-set");
            }
            finally { release.TrySetResult(); await model.DisposeAsync(); await prepare; }
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ClosedWindowOrDestroyedOwnerKeepsConfirmedWorkflowUntilFreshAck(bool destroyOwner)
    {
        await using var fixture = new Fixture(); var entered = Signal(); var release = Signal();
        fixture.Handler = async (request, token) => { if (request.Command == "startup-self-set") { entered.TrySetResult(); await release.Task.WaitAsync(token); } return fixture.Default(request); };
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            EnsureApplication(); var owner = Owner(); var model = new SelfStartupViewModel(fixture.Paths); var window = Hidden(new SelfStartupWindow(model));
            Task submit = Task.CompletedTask; SelfStartupWindow? reopened = null; SelfStartupViewModel? next = null;
            try
            {
                owner.Show(); window.Owner = owner; window.Show(); await Loaded(window); await model.EnableCommand.ExecuteAsync(null);
                submit = model.ConfirmCommand.ExecuteAsync(null); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var originalId = fixture.Workflow.Current.OperationId; var before = await File.ReadAllBytesAsync(fixture.Pending);
                if (destroyOwner) { owner.Close(); } else { window.Close(); }
                await window.ShutdownTask; var changes = 0; model.PropertyChanged += (_, _) => changes++;
                Assert.False(submit.IsCompleted); Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Pending));
                next = new(fixture.Paths); reopened = Hidden(new SelfStartupWindow(next)); reopened.Show(); await Loaded(reopened);
                Assert.Equal(originalId, next.OperationId); Assert.False(next.CanChoose); Assert.False(next.CanConfirm);
                release.TrySetResult(); await submit; next.ObserveSnapshot(); Assert.Equal(0, changes);
                Assert.Equal(SelfStartupWorkflowState.Completed, next.Snapshot.State); Assert.Single(fixture.Requests, row => row.Command == "startup-self-set");
                Assert.False(File.Exists(fixture.Pending));
            }
            finally
            {
                release.TrySetResult(); await submit; window.Close(); await window.ShutdownTask;
                if (reopened is not null) { reopened.Close(); await reopened.ShutdownTask; } if (next is not null) { await next.DisposeAsync(); }
                owner.Close(); await model.DisposeAsync();
            }
        });
    }

    [Theory]
    [InlineData("NotFound")] [InlineData("Running")] [InlineData("Unknown")] [InlineData("Completed")]
    [InlineData("Omitted")] [InlineData("Failure")]
    public async Task RecoveryQueriesRemainLockedWithOriginalBytesAndNoRegistrationOrMutation(string state)
    {
        await using var fixture = new Fixture(); var id = AgentProtocol.NewOperationId(); await new SelfStartupPendingStore(fixture.Paths).ReserveAsync(id, true);
        var before = await File.ReadAllBytesAsync(fixture.Pending);
        fixture.Handler = (request, _) => Task.FromResult<AgentResponse?>(fixture.Ack(request) with
        {
            Ok = state != "Failure", ErrorCode = state == "Failure" ? "fixture_failure" : null,
            Data = state == "NotFound" ? null : new OperationReceipt(1, id, "startup-self-set", null, new string('b', 64), "fixture-owner",
                state is "Completed" or "Omitted" ? OperationState.Completed : state == "Running" ? OperationState.Running : OperationState.Unknown,
                DateTimeOffset.UtcNow, state is "Completed" or "Omitted" ? DateTimeOffset.UtcNow : null,
                state == "Completed" ? new AgentResponse("past", true, new { registered = true }, RootId: fixture.Endpoint.RootId, OperationId: id) : null, state == "Omitted")
        });
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            await using var model = new SelfStartupViewModel(fixture.Paths); await model.InitializeAsync(); Assert.Equal("结果未知", model.Heading);
            Assert.Equal(id, model.OperationId); await model.QueryCommand.ExecuteAsync(null); Assert.NotEmpty(model.ObservationText);
            var expected = state switch { "Completed" => SelfStartupReceiptState.CompletedWithResponse, "Omitted" => SelfStartupReceiptState.ResultUnavailable, "Failure" => SelfStartupReceiptState.QueryFailed, _ => Enum.Parse<SelfStartupReceiptState>(state) };
            Assert.Equal(expected, model.Snapshot.Observation!.State);
            Assert.True(model.Snapshot.IsLocked); Assert.False(model.CanChoose); Assert.False(model.CanConfirm); Assert.Equal("登记尚未核对", model.RegistrationText);
            Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Pending)); Assert.Single(fixture.Requests); Assert.Equal("operation", fixture.Requests.Single().Command);
        });
    }

    [Fact]
    public async Task InvalidPendingShowsNoInventedQueryKeyAndNoNetwork()
    {
        await using var fixture = new Fixture(); await File.WriteAllTextAsync(fixture.Pending, "{}");
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            await using var model = new SelfStartupViewModel(fixture.Paths); await model.InitializeAsync(); Assert.True(model.Snapshot.IsLocked);
            Assert.Equal("", model.OperationId); Assert.False(model.CanCopy); Assert.False(model.CanQuery); Assert.False(model.CanChoose); Assert.Empty(fixture.Requests);
        });
    }

    [Fact]
    public async Task SameRootToolIsSharedAndHasBoundedRealWindowGeometry()
    {
        var evidence = Environment.GetEnvironmentVariable("SA_SELF_GUI_EVIDENCE"); var probe = Environment.GetEnvironmentVariable("SA_SELF_GUI_PROBE");
        if (probe is not null) { ValidateProbe(probe); }
        await using var fixture = new Fixture();
        if (probe is not null && Environment.GetEnvironmentVariable("SA_SELF_GUI_PROBE_CASE") == "unknown")
        { await new SelfStartupPendingStore(fixture.Paths).ReserveAsync(AgentProtocol.NewOperationId(), true); }
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            EnsureApplication(); var owner = Owner(); SelfStartupWindow? window = null;
            try
            {
                if (probe is not null) { owner.Left = 40; owner.Top = 40; owner.ShowActivated = false; }
                owner.Show(); window = SelfStartupTool.Open(owner, fixture.Paths); await Loaded(window);
                window.ToolTitle.Text += "（隔离夹具）";
                Assert.Same(window, SelfStartupTool.Open(owner, fixture.Paths)); Assert.False(window.ConfirmButton.IsDefault); Assert.Null(window.ConfirmButton.FocusVisualStyle);
                var model = Assert.IsType<SelfStartupViewModel>(window.DataContext);
                if (probe is not null)
                {
                    await File.WriteAllTextAsync(Path.Combine(probe, "ready.txt"), $"PID={Environment.ProcessId}\nRoot={fixture.Root}\nDPI={VisualTreeHelper.GetDpi(window).DpiScaleX}\nSG-F1: synthetic CurrentUserOnly Pipe; no native adapters, no real Run/Boot.\n");
                    await SaveProbe(fixture, model, probe, "before.json");
                    var closed = Signal(); window.Closed += (_, _) => closed.TrySetResult(); await closed.Task.WaitAsync(TimeSpan.FromMinutes(12)); await window.ShutdownTask;
                    await SaveProbe(fixture, model, probe, "after.json");
                }
                else
                {
                    var rows = new List<object>();
                    // Actual user resizing switches WPF out of SizeToContent; exercise that mode explicitly.
                    window.SizeToContent = SizeToContent.Manual; window.Height = window.ActualHeight;
                    foreach (var viewport in new[] { (Width: 1120, Height: 700), (Width: 1320, Height: 820), (Width: 1620, Height: 651) })
                    {
                        owner.Width = viewport.Width; owner.Height = viewport.Height; window.Width = viewport.Width == 1120 ? 640 : 720;
                        await model.EnableCommand.ExecuteAsync(null); await Idle(window);
                        Assert.True(window.ConfirmButton.IsVisible); Assert.True(window.ConfirmButton.IsEnabled); Assert.False(window.QueryButton.IsVisible);
                        Assert.Equal(viewport.Width == 1120 ? 640 : 720, window.ActualWidth);
                        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        { while (!ReferenceEquals(FocusManager.GetFocusedElement(window), window.CancelButton)) { await Task.Delay(20, deadline.Token); } }
                        foreach (var button in Descendants(window).OfType<Button>().Where(button => button.IsVisible))
                        {
                            Assert.Null(button.FocusVisualStyle); var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                            Assert.True(bounds.Top >= -1 && bounds.Left >= -1 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1, $"Clipped {button.Name}: {bounds}");
                        }
                        Assert.Equal(fixture.Paths.RootDirectory, model.RootDirectory); Assert.True(window.RootReadout.ActualHeight >= 24);
                        rows.Add(new { viewport.Width, viewport.Height, ToolWidth = window.ActualWidth, ToolHeight = window.ActualHeight, Dpi = VisualTreeHelper.GetDpi(window).DpiScaleX });
                        if (evidence is not null) { Capture(window, Path.Combine(evidence, $"confirm-{viewport.Width}x{viewport.Height}.png")); }
                        fixture.Workflow.DiscardPreparation(); model.RefreshSnapshot();
                        window.TechnicalExpander.IsExpanded = true; await Idle(window);
                        var disclosure = Assert.IsType<SystemAutorun.App.Controls.DisclosurePresenter>(window.TechnicalExpander.Template.FindName("DisclosureBody", window.TechnicalExpander));
                        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        { while (disclosure.HasAnimatedProperties) { await Task.Delay(20, deadline.Token); await Idle(window); } }
                        Assert.Equal(1, disclosure.Progress); Assert.True(disclosure.Child.IsVisible);
                        window.ContentScroll.ScrollToBottom(); await Idle(window);
                        if (evidence is not null) { Capture(window, Path.Combine(evidence, $"details-{viewport.Width}x{viewport.Height}.png")); }
                        window.TechnicalExpander.IsExpanded = false;
                        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        { while (disclosure.HasAnimatedProperties) { await Task.Delay(20, deadline.Token); await Idle(window); } }
                        Assert.Equal(0, disclosure.Progress); Assert.False(disclosure.Child.IsVisible);
                    }
                    if (evidence is not null) { await File.WriteAllTextAsync(Path.Combine(evidence, "geometry.json"), JsonSerializer.Serialize(rows)); }
                    window.Close(); await window.ShutdownTask;
                    var next = SelfStartupTool.Open(owner, fixture.Paths); try { Assert.NotSame(window, next); await Loaded(next); } finally { next.Close(); await next.ShutdownTask; }
                }
            }
            finally { if (window is not null) { window.Close(); await window.ShutdownTask; } owner.Close(); }
        }, probe is null ? null : TimeSpan.FromMinutes(14));
    }

    [Theory]
    [InlineData("prepared", false)] [InlineData("prepared", true)]
    [InlineData("unknown", false)] [InlineData("unknown", true)]
    [InlineData("submitting", false)] [InlineData("submitting", true)]
    public async Task MainMenuAndHostTabEntriesPreserveOneViewAndItsWorkflow(string state, bool reopenInMonitoring)
    {
        await using var fixture = new Fixture(); var entered = Signal(); var release = Signal();
        if (state == "unknown") { await new SelfStartupPendingStore(fixture.Paths).ReserveAsync(AgentProtocol.NewOperationId(), true); }
        fixture.Handler = async (request, token) =>
        {
            if (state == "submitting" && request.Command == "startup-self-set") { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            return fixture.Default(request);
        };
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            EnsureApplication(); var app = SystemAutorun.App.App.CurrentApp;
            // Test-only isolation: exercise the actual App/menu/button handlers without
            // starting a runtime host or adding a production target override API.
            var pathsField = typeof(SystemAutorun.App.App).GetField("_paths", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var previousPaths = pathsField.GetValue(app); pathsField.SetValue(app, fixture.Paths);
            var client = new AgentClient(fixture.Root);
            var dashboard = new MainWindowViewModel(client, new NativeCacheStore(fixture.Paths.GuiCachePath), new AgentConfigurationClient(client), fixture.Paths, new NeverRemoteReader()) { AutoRefresh = false, SearchText = "retain-filter" };
            var main = new SystemAutorun.App.MainWindow(dashboard, new AgentStartupClient(client), new StartupCacheStore(fixture.Paths.StartupCachePath))
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            SelfStartupWindow? tool = null; Task submit = Task.CompletedTask;
            var trace = new List<string>(); var completed = Signal(); var evidence = Environment.GetEnvironmentVariable("SA_SELF_GUI_EVIDENCE");
            void Mark(string value) => trace.Add($"{DateTimeOffset.UtcNow:O} {value}");
            try
            {
                main.Show(); await Idle(main);
                var entry = main.SettingsButton.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Windows 启动"))
                        .Items.OfType<MenuItem>().Single(item => Equals(item.Header, "工具登录启动…"));
                entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); tool = Assert.Single(main.OwnedWindows.OfType<SelfStartupWindow>()); await Loaded(tool);
                var model = Assert.IsType<SelfStartupViewModel>(tool.DataContext);
                if (state != "unknown") { await model.EnableCommand.ExecuteAsync(null); }
                if (state == "submitting") { submit = model.ConfirmCommand.ExecuteAsync(null); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                var preparation = model.Snapshot.Preparation; var originalId = model.OperationId;
                var originalBytes = File.Exists(fixture.Pending) ? await File.ReadAllBytesAsync(fixture.Pending) : null;
                Mark($"before modal state={model.Snapshot.State} nativeEnabled={NativeEnabled(tool)}");
                // R105 preserves the logical tab and the same modeless tool. R118 suspends the
                // owner's rendered scene behind its backdrop until the tool actually closes.
                main.HostsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(main);
                Assert.True(dashboard.IsOverviewPage); Assert.Equal(Visibility.Visible, main.MonitoringTabs.Visibility);
                Assert.False(main.MainScene.IsHitTestVisible);
                Mark($"host tab visible={tool.IsVisible} toolEnabled={NativeEnabled(tool)} mainEnabled={NativeEnabled(main)}");
                Assert.True(tool.IsVisible); Assert.True(NativeEnabled(main)); Assert.True(NativeEnabled(tool));
                if (reopenInMonitoring)
                {
                    // R109: the host tab no longer carries its own button; the settings entry reopens the same view.
                    main.SettingsButton.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Windows 启动"))
                        .Items.OfType<MenuItem>().Single(item => Equals(item.Header, "工具登录启动…")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Idle(tool);
                    Mark($"reopened sameView={ReferenceEquals(tool, main.OwnedWindows.OfType<SelfStartupWindow>().SingleOrDefault())} nativeEnabled={NativeEnabled(tool)}");
                    Assert.Same(tool, Assert.Single(main.OwnedWindows.OfType<SelfStartupWindow>()));
                    Assert.Same(model, tool.DataContext); Assert.True(tool.IsVisible); Assert.True(NativeEnabled(tool));
                    Assert.Same(preparation, model.Snapshot.Preparation);
                    if (state == "prepared") { Assert.True(model.CanConfirm); }
                    else { Assert.Equal(originalId, model.OperationId); Assert.False(model.CanChoose); Assert.False(model.CanConfirm); }
                    if (originalBytes is not null) { Assert.Equal(originalBytes, await File.ReadAllBytesAsync(fixture.Pending)); }
                }
                main.ServicesTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(main);
                Assert.False(main.Dispatcher.HasShutdownStarted);
                Assert.True(NativeEnabled(main)); Assert.Equal("retain-filter", dashboard.SearchText); Assert.True(dashboard.IsWorkloadsPage);
                Assert.True(tool.IsVisible); Assert.Same(main, tool.Owner); Assert.True(NativeEnabled(tool)); Assert.Same(model, tool.DataContext); Assert.Same(preparation, model.Snapshot.Preparation);
                if (state == "prepared") { Assert.DoesNotContain(fixture.Requests, row => row.Command == "startup-self-set"); Assert.False(File.Exists(fixture.Pending)); }
                else
                {
                    Assert.Equal(originalBytes, await File.ReadAllBytesAsync(fixture.Pending));
                    if (state == "submitting") { Assert.False(submit.IsCompleted); release.TrySetResult(); await submit; Assert.False(File.Exists(fixture.Pending)); }
                    else { Assert.Equal(originalId, model.OperationId); Assert.DoesNotContain(fixture.Requests, row => row.Command == "startup-self-prepare"); }
                }
                tool.Close(); await tool.ClosureTask; await tool.ShutdownTask;
                await PresenceMotionTests.Until(() => main.MainScene.IsVisible && main.MainScene.IsHitTestVisible);
                Assert.True(main.ServicesPage.IsVisible);
                Mark("modeless tool truly closed; owner scene, workflow and bytes checked before STA cleanup");
            }
            finally
            {
                release.TrySetResult(); await submit;
                if (tool is not null) { tool.Close(); await tool.ShutdownTask; }
                var closing = typeof(SystemAutorun.App.MainWindow).GetMethod("Window_Closing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                main.Closing -= (System.ComponentModel.CancelEventHandler)closing.CreateDelegate(typeof(System.ComponentModel.CancelEventHandler), main);
                main.Close(); pathsField.SetValue(app, previousPaths);
                if (evidence is not null) { await File.WriteAllLinesAsync(Path.Combine(evidence, $"owner-{state}-{reopenInMonitoring}.txt"), trace); }
            }
        });
        Assert.Equal(state == "submitting" ? 1 : 0, fixture.Requests.Count(row => row.Command == "startup-self-set"));
    }

    [Fact]
    public async Task DialogFailureRestoresSameUnconfirmedViewWithoutSending()
    {
        await using var fixture = new Fixture();
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            EnsureApplication(); var owner = Owner(); SelfStartupWindow? tool = null;
            try
            {
                owner.Show(); tool = SelfStartupTool.Open(owner, fixture.Paths); await Loaded(tool);
                var model = Assert.IsType<SelfStartupViewModel>(tool.DataContext); await model.EnableCommand.ExecuteAsync(null);
                var preparation = model.Snapshot.Preparation;
                var error = Assert.Throws<InvalidOperationException>(() => SelfStartupTool.WithOwnerSuspended(owner, () =>
                { Assert.False(tool.IsVisible); throw new InvalidOperationException("synthetic dialog construction failure"); }));
                Assert.Contains("synthetic", error.Message); Assert.True(tool.IsVisible); Assert.True(NativeEnabled(tool));
                Assert.Same(model, tool.DataContext); Assert.Same(preparation, model.Snapshot.Preparation); Assert.True(model.CanConfirm);
                Assert.False(File.Exists(fixture.Pending)); Assert.DoesNotContain(fixture.Requests, row => row.Command == "startup-self-set");
            }
            finally { if (tool is not null) { tool.Close(); await tool.ShutdownTask; } owner.Close(); }
        });
    }

    [Fact]
    public async Task ActualSelfMenuEntryCanOpenAndCloseWithoutRuntimeStartup()
    {
        var probe = Environment.GetEnvironmentVariable("SA_SELF_GUI_PROBE_CASE") == "entries" ? Environment.GetEnvironmentVariable("SA_SELF_GUI_PROBE") : null;
        if (probe is not null) { ValidateProbe(probe); }
        await using var fixture = new Fixture();
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            EnsureApplication(); var app = SystemAutorun.App.App.CurrentApp;
            var field = typeof(SystemAutorun.App.App).GetField("_paths", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var previous = field.GetValue(app); field.SetValue(app, fixture.Paths); var client = new AgentClient(fixture.Root);
            var model = new MainWindowViewModel(client, new NativeCacheStore(fixture.Paths.GuiCachePath), new AgentConfigurationClient(client), fixture.Paths, new NeverRemoteReader()) { AutoRefresh = false };
            var main = new SystemAutorun.App.MainWindow(model, new AgentStartupClient(client), new StartupCacheStore(fixture.Paths.StartupCachePath))
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = probe is null ? -32000 : 40, Top = probe is null ? -32000 : 40 };
            var observedTools = new HashSet<SelfStartupWindow>();
            void LabelProbeTools(object? sender, EventArgs args)
            {
                foreach (var tool in main.OwnedWindows.OfType<SelfStartupWindow>())
                { observedTools.Add(tool); tool.ToolTitle.Text = "SystemAutorun · 登录启动（隔离夹具）"; }
            }
            try
            {
                main.Show(); await Idle(main);
                if (probe is null)
                {
                    main.SettingsButton.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Windows 启动"))
                        .Items.OfType<MenuItem>().Single(item => Equals(item.Header, "工具登录启动…")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    var tool = Assert.Single(main.OwnedWindows.OfType<SelfStartupWindow>()); await Loaded(tool); tool.Close(); await tool.ShutdownTask;
                    Assert.Empty(main.OwnedWindows.OfType<SelfStartupWindow>()); Assert.DoesNotContain(fixture.Requests, row => row.Command == "startup-self-set");
                }
                else
                {
                    main.Title = "SystemAutorun · 隔离入口探针"; main.LayoutUpdated += LabelProbeTools;
                    await File.WriteAllTextAsync(Path.Combine(probe, "ready.txt"), $"PID={Environment.ProcessId}\nRoot={fixture.Root}\nDPI={VisualTreeHelper.GetDpi(main).DpiScaleX}\nSynthetic CurrentUserOnly Pipe; no runtime host, native adapter or workload executor. Close main last.\n");
                    var hidden = Signal(); main.IsVisibleChanged += (_, _) => { if (!main.IsVisible) { hidden.TrySetResult(); } };
                    await hidden.Task.WaitAsync(TimeSpan.FromMinutes(10));
                    await File.WriteAllTextAsync(Path.Combine(probe, "after.json"), JsonSerializer.Serialize(new { fixture.Root, State = fixture.Workflow.Current.State.ToString(), Requests = fixture.Requests.Select(row => new { row.Command, row.Name, row.OperationId }).ToArray() }));
                }
            }
            finally
            {
                main.LayoutUpdated -= LabelProbeTools;
                foreach (var tool in main.OwnedWindows.OfType<SelfStartupWindow>()) { observedTools.Add(tool); }

                foreach (var tool in observedTools)
                {
                    tool.Close(); await tool.ShutdownTask;
                    if (tool.DataContext is SelfStartupViewModel view && view.ConfirmCommand.ExecutionTask is { } confirmation) { await confirmation; }
                }
                var closing = typeof(SystemAutorun.App.MainWindow).GetMethod("Window_Closing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                main.Closing -= (System.ComponentModel.CancelEventHandler)closing.CreateDelegate(typeof(System.ComponentModel.CancelEventHandler), main);
                main.Close(); field.SetValue(app, previous);
            }
        }, probe is null ? null : TimeSpan.FromMinutes(12));
    }

    private static bool NativeEnabled(Window window) => IsWindowEnabled(new System.Windows.Interop.WindowInteropHelper(window).Handle);
    [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);
    private sealed class NeverRemoteReader : SystemAutorun.Core.Services.IRemoteHostReader
    {
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Self window entry tests never connect to remote hosts.");
    }

    private static async Task SaveProbe(Fixture fixture, SelfStartupViewModel model, string directory, string file)
    {
        ValidateProbe(directory);
        var pending = File.Exists(fixture.Pending) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(fixture.Pending))) : null;
        await File.WriteAllTextAsync(Path.Combine(directory, file), JsonSerializer.Serialize(new
        {
            fixture.Root, State = model.Snapshot.State.ToString(), model.OperationId, PendingSha256 = pending,
            Requests = fixture.Requests.Select(row => new { row.Command, row.Name, row.OperationId }).ToArray()
        }));
    }
    private static void EnsureApplication()
    { if (Application.Current is not SystemAutorun.App.App) { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); } WindowTheme.Install(); }
    private static Window Owner() => new() { Width = 1120, Height = 700, ShowActivated = false, Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual };
    private static SelfStartupWindow Hidden(SelfStartupWindow window)
    { window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -32000; window.Top = -32000; return window; }
    private static async Task Loaded(SelfStartupWindow window)
    { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await window.InitializationTask; await Idle(window); }
    private static async Task Idle(Window window)
    { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var value in Descendants(child)) { yield return value; } }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(path); encoder.Save(output);
    }
    private static void ValidateProbe(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) || !Path.GetFileName(full).StartsWith("SystemAutorun-self-gui-probe-", StringComparison.Ordinal)
            || !Directory.Exists(full) || new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint)) { throw new InvalidOperationException("Expected owned ordinary private probe directory."); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "SystemAutorun-self-gui-" + Guid.NewGuid().ToString("N"));
        internal ProjectPaths Paths { get; }
        internal AgentEndpoint Endpoint { get; }
        internal SelfStartupWorkflow Workflow => SelfStartupWorkflow.ForRoot(Paths);
        internal string Pending => Path.Combine(Root, SelfStartupPendingStore.FileName);
        internal ConcurrentQueue<AgentRequest> Requests { get; } = new();
        internal int StatusReads => Requests.Count(row => row.Command == "startup-self-status");
        internal Func<AgentRequest, CancellationToken, Task<AgentResponse?>>? Handler { get; set; }
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromMinutes(15));
        private readonly Task _server;
        internal Fixture()
        {
            Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, "autorun.yaml"), "version: 1\ntasks: []\n"); Paths = new(Root); Endpoint = AgentProtocol.ForRoot(Root);
            _server = Task.Run(async () =>
            {
                while (true)
                {
                    await using var pipe = new NamedPipeServerStream(Endpoint.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_stop.Token); using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                    var line = await reader.ReadLineAsync(_stop.Token); if (line is null) { continue; }
                    var request = JsonSerializer.Deserialize<AgentRequest>(line, AgentProtocol.JsonOptions)!; Requests.Enqueue(request);
                    var response = Handler is { } handler ? await handler(request, _stop.Token) : Default(request);
                    if (response is not null)
                    {
                        try { await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true }; await writer.WriteLineAsync(JsonSerializer.Serialize(response, AgentProtocol.JsonOptions).AsMemory(), _stop.Token); }
                        catch (IOException) { /* Read-only clients can close/cancel while the test reply is held. Handler failures are NOT swallowed. */ }
                    }
                }
            });
        }
        internal AgentResponse Ack(AgentRequest request) => new(request.Id, true, new { registered = true, synthetic = true }, RootId: Endpoint.RootId, OperationId: request.OperationId);
        internal AgentResponse Default(AgentRequest request) => Ack(request) with
        {
            Data = request.Command switch
            {
                "startup-self-status" => StartupRegistrationContract.Observe("self", new(false), Root),
                "startup-self-prepare" => new SelfStartupMutationRequest { SchemaVersion = 1, Enabled = request.Name == "enable", Confirmed = false, TargetContextId = new string('a', 64), ExpectedRegistration = new(false) },
                "operation" => null,
                "startup-self-set" => new { registered = true, synthetic = true },
                _ => throw new InvalidOperationException("Unexpected GUI fixture command.")
            }
        };
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); try { await _server; } catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally
            {
                _stop.Dispose();
                if (Path.GetDirectoryName(Root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) || !Path.GetFileName(Root).StartsWith("SystemAutorun-self-gui-", StringComparison.Ordinal)
                    || new DirectoryInfo(Root).Attributes.HasFlag(FileAttributes.ReparsePoint)) { throw new InvalidOperationException("Unsafe fixture cleanup."); }
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
