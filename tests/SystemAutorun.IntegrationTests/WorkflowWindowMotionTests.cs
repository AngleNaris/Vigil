using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Models;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Recovery;
using RealServer = SystemAutorun.IntegrationTests.ConfigurationControlPlaneTests.ServerFixture;

namespace SystemAutorun.IntegrationTests;

// Reuse the existing isolated transports and fixtures without widening their visibility.
public sealed partial class SelfStartupGuiTests
{
    [Theory]
    [InlineData("close", false)] [InlineData("reduced", false)] [InlineData("reverse", false)]
    [InlineData("veto", false)] [InlineData("reowner", false)] [InlineData("suspend", false)]
    [InlineData("close", true)] [InlineData("reduced", true)] [InlineData("reverse", true)]
    [InlineData("veto", true)] [InlineData("reowner", true)] [InlineData("suspend", true)]
    public async Task MotionExitPreservesPreparationOrUnknownUntilActualClosure(string action, bool unknown)
    {
        await using var fixture = new Fixture();
        if (unknown) { await new SelfStartupPendingStore(fixture.Paths).ReserveAsync(AgentProtocol.NewOperationId(), true); }
        var originalBytes = File.Exists(fixture.Pending) ? await File.ReadAllBytesAsync(fixture.Pending) : null;
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            EnsureApplication(); var owner = Owner(); var other = Owner(); SelfStartupWindow? window = null;
            var calls = 0; CancelEventHandler guard = (_, e) => { if (action == "veto" && ++calls == 2) { e.Cancel = true; } };
            try
            {
                owner.Show(); other.Show(); window = SelfStartupTool.Open(owner, fixture.Paths); await Loaded(window);
                ColorStateAssist.SetReduceMotion(window, action == "reduced");
                var model = Assert.IsType<SelfStartupViewModel>(window.DataContext);
                if (!unknown) { await model.EnableCommand.ExecuteAsync(null); Assert.True(model.CanConfirm); }
                var preparation = model.Snapshot.Preparation; var id = model.OperationId;
                await WorkflowMotionAssertions.Settled(window); var height = window.ActualHeight;
                window.Closing += guard; window.Close(); var shutdown = window.ShutdownTask;
                if (action != "reduced" && SystemParameters.ClientAreaAnimation)
                {
                    Assert.True(window.IsExitPending); Assert.False(shutdown.IsCompleted);
                    Assert.Equal(!unknown, model.CanConfirm); Assert.Same(preparation, fixture.Workflow.Current.Preparation);
                    Assert.Equal(height, window.ActualHeight); Assert.False(((PresencePanel)window.Content).IsEnabled);
                    switch (action)
                    {
                        case "reverse": window.CancelPendingClose(); break;
                        case "veto": await PresenceMotionTests.Until(() => calls == 2 && !window.IsExitPending); break;
                        case "reowner":
                            Assert.Same(window, SelfStartupTool.Open(other, fixture.Paths)); Assert.Same(other, window.Owner);
                            owner.Close(); Assert.False(window.ClosureTask.IsCompleted); break;
                        case "suspend":
                            SelfStartupTool.WithOwnerSuspended(owner, () => { Assert.False(window.IsVisible); Assert.False(window.IsExitPending); }); break;
                    }
                    if (action != "close")
                    {
                        await WorkflowMotionAssertions.Settled(window);
                        Assert.True(window.IsVisible); Assert.False(window.IsExitPending); Assert.False(shutdown.IsCompleted);
                        Assert.Same(shutdown, window.ShutdownTask); Assert.Same(model, window.DataContext);
                        Assert.Same(preparation, model.Snapshot.Preparation); Assert.Equal(id, model.OperationId);
                        Assert.Equal(!unknown, model.CanConfirm); Assert.True(((PresencePanel)window.Content).IsEnabled);
                        window.Closing -= guard; window.Close();
                    }
                }
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(window.ClosureTask.IsCompleted); Assert.False(model.CanConfirm);
                Assert.False(((PresencePanel)window.Content).HasAnimatedProperties);
                if (!unknown) { Assert.True(fixture.Workflow.Current.CanPrepare); }
                else { Assert.Equal(id, fixture.Workflow.Current.OperationId); Assert.True(fixture.Workflow.Current.IsLocked); }
            }
            finally
            {
                if (window is not null) { window.Closing -= guard; }
                owner.Close(); other.Close(); if (window is not null) { await window.ShutdownTask; }
            }
        });
        if (originalBytes is null) { Assert.False(File.Exists(fixture.Pending)); }
        else { Assert.Equal(originalBytes, await File.ReadAllBytesAsync(fixture.Pending)); }
        Assert.DoesNotContain(fixture.Requests, request => request.Command == "startup-self-set");
    }

    [Fact]
    public async Task MotionShutdownFaultStillJoinsViewReadsAndDisposesLifetime()
    {
        await using var fixture = new Fixture();
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            EnsureApplication(); var owner = Owner(); await using var model = new SelfStartupViewModel(fixture.Paths);
            var window = Hidden(new SelfStartupWindow(model)); var read = Signal();
            try
            {
                owner.Show(); window.Owner = owner; window.Show(); await Loaded(window);
                WorkflowMotionAssertions.HoldRead(model, read.Task);
                WorkflowMotionAssertions.FailInitialization(window);
                ColorStateAssist.SetReduceMotion(window, true); window.Close();
                Assert.True(window.ClosureTask.IsCompleted); Assert.False(window.ShutdownTask.IsCompleted);
                read.SetResult(); await Assert.ThrowsAsync<IOException>(() => window.ShutdownTask);
                WorkflowMotionAssertions.AssertLifetimeDisposed(model);
            }
            finally
            {
                read.TrySetResult(); owner.Close();
                if (window.ShutdownTask.IsFaulted) { _ = window.ShutdownTask.Exception; }
                else { await window.ShutdownTask; }
            }
        });
    }

    [Theory]
    [InlineData(false, 920, 580)] [InlineData(false, 960, 620)] [InlineData(false, 1320, 820)]
    [InlineData(true, 920, 580)] [InlineData(true, 960, 620)] [InlineData(true, 1320, 820)]
    public async Task MotionSelfConfirmationKeepsBoundedGeometry(bool light, double width, double height)
    {
        await using var fixture = new Fixture();
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            EnsureApplication(); var previous = ThemeService.Preference; var owner = Owner();
            await using var model = new SelfStartupViewModel(fixture.Paths); var window = Hidden(new SelfStartupWindow(model));
            try
            {
                ThemeService.Apply(light ? ThemePreference.Light : ThemePreference.Dark, persist: null);
                owner.Width = width; owner.Height = height; owner.Show(); window.Owner = owner; window.Show(); await Loaded(window);
                await model.EnableCommand.ExecuteAsync(null); await WorkflowMotionAssertions.Settled(window); await Idle(window);
                WorkflowMotionAssertions.Geometry(window, owner, window.ConfirmButton, window.CancelButton, window.ChromeCloseButton);
                Assert.True(model.CanConfirm); Assert.False(window.ConfirmButton.IsDefault);
                StartupGuiWindowTests.CaptureStateEvidence(window, $"self-{(light ? "light" : "dark")}-owner-{width}.png");
                window.Close(); await window.ShutdownTask;
            }
            finally { owner.Close(); await window.ShutdownTask; ThemeService.Apply(previous, persist: null); }
        });
        Assert.DoesNotContain(fixture.Requests, request => request.Command == "startup-self-set");
    }
}

public sealed partial class RecoveryGuiTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MotionRecoveryCallerWaitsAfterModalReturn(bool hideInsteadOfClose)
    {
        await using var host = new RealServer(); await Initialize(host);
        var configuration = await host.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            EnsureApplication(); var app = SystemAutorun.App.App.CurrentApp;
            var pathsField = typeof(SystemAutorun.App.App).GetField("_paths", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var previousPaths = pathsField.GetValue(app); pathsField.SetValue(app, host.Paths);
            var dashboard = new MainWindowViewModel(host.Client, new NativeCacheStore(host.Paths.GuiCachePath),
                new AgentConfigurationClient(host.Client), host.Paths, new MotionNeverRemoteReader()) { AutoRefresh = false };
            var selected = new TaskRowViewModel(TaskSnapshot.From(configuration.Configuration.GetTask("media"), HealthResult.Unknown("fixture", "合成关闭测试")));
            dashboard.Tasks.Add(selected); dashboard.SelectedTask = selected; dashboard.SearchText = "media";
            var main = new SystemAutorun.App.MainWindow(dashboard, new AgentStartupClient(host.Client), new StartupCacheStore(host.Paths.StartupCachePath))
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            var read = Signal(); Task? entry = null; RecoveryWindow? child = null;
            try
            {
                main.Show(); await Idle(main);
                var interaction = main.Dispatcher.InvokeAsync(async () =>
                {
                    child = Assert.Single(main.OwnedWindows.OfType<RecoveryWindow>());
                    try
                    {
                        await PresenceMotionTests.Until(() => child.IsLoaded); await child.InitializationTask;
                        WorkflowMotionAssertions.HoldRead(child.DataContext, read.Task);
                        if (hideInsteadOfClose) { child.Hide(); } else { child.Close(); }
                    }
                    catch { ColorStateAssist.SetReduceMotion(child, true); child.Close(); throw; }
                }, DispatcherPriority.Background).Task.Unwrap();
                entry = main.OpenRecoveryAsync(main.ServiceMoreButton); await interaction;
                Assert.NotNull(child); Assert.True(child.ClosureTask.IsCompleted); Assert.False(child.IsVisible);
                Assert.False(entry.IsCompleted); Assert.False(child.ShutdownTask.IsCompleted); Assert.False(main.Dispatcher.HasShutdownStarted);
                Assert.Same(selected, dashboard.SelectedTask); Assert.Equal("media", dashboard.SearchText);
                read.SetResult(); await entry.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(child.ShutdownTask.IsCompletedSuccessfully);
            }
            finally
            {
                read.TrySetResult();
                if (child is not null && !child.ClosureTask.IsCompleted) { ColorStateAssist.SetReduceMotion(child, true); child.Close(); }
                if (entry is not null) { await entry.WaitAsync(TimeSpan.FromSeconds(5)); }
                var closing = typeof(SystemAutorun.App.MainWindow).GetMethod("Window_Closing", BindingFlags.Instance | BindingFlags.NonPublic)!;
                main.Closing -= (CancelEventHandler)closing.CreateDelegate(typeof(CancelEventHandler), main);
                main.Close(); dashboard.DetachDashboard(); pathsField.SetValue(app, previousPaths);
            }
        });
        Assert.Empty(Directory.GetFiles(host.Root, "*.operation.json", SearchOption.AllDirectories)); Assert.Empty(host.ObservedErrors);
    }

    private sealed class MotionNeverRemoteReader : SystemAutorun.Core.Services.IRemoteHostReader
    {
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Recovery window tests never connect to remote hosts.");
    }

    [Theory]
    [InlineData("close")] [InlineData("reduced")] [InlineData("reverse")] [InlineData("veto")] [InlineData("hide")] [InlineData("owner")]
    public async Task MotionRecoveryPreparationIsDiscardedOnlyAfterActualClosure(string action)
    {
        await using var host = new RealServer(); await Initialize(host);
        var store = new RecoveryControlStore(host.Paths); var before = await File.ReadAllBytesAsync(store.FilePath);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            EnsureApplication(); var owner = Owner(); await using var model = new RecoveryViewModel(host.Paths, "media");
            var window = Hidden(new RecoveryWindow(model)); var calls = 0;
            CancelEventHandler guard = (_, e) => { if (action == "veto" && ++calls == 2) { e.Cancel = true; } };
            try
            {
                owner.Show(); window.Owner = owner; window.Show(); await Loaded(window);
                ColorStateAssist.SetReduceMotion(window, action == "reduced");
                await model.PrepareCommand.ExecuteAsync(null); Assert.True(model.CanConfirm);
                var preparation = model.Snapshot.Preparation; await WorkflowMotionAssertions.Settled(window);
                window.Closing += guard; if (action == "owner") { owner.Close(); } else { window.Close(); }
                var shutdown = window.ShutdownTask;
                if (action is not ("reduced" or "owner") && SystemParameters.ClientAreaAnimation)
                {
                    Assert.True(window.IsExitPending); Assert.False(shutdown.IsCompleted); Assert.True(model.CanConfirm);
                    switch (action)
                    {
                        case "reverse": window.CancelPendingClose(); break;
                        case "veto": await PresenceMotionTests.Until(() => calls == 2 && !window.IsExitPending); break;
                        case "hide": window.Hide(); window.Show(); break;
                    }
                    if (action != "close")
                    {
                        await WorkflowMotionAssertions.Settled(window);
                        Assert.True(window.IsVisible); Assert.False(window.IsExitPending); Assert.False(shutdown.IsCompleted);
                        Assert.Same(shutdown, window.ShutdownTask); Assert.Same(preparation, model.Snapshot.Preparation); Assert.True(model.CanConfirm);
                        window.Closing -= guard; window.Close();
                    }
                }
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(window.ClosureTask.IsCompleted);
                Assert.False(model.CanConfirm); Assert.Null(RecoveryWorkflow.ForTask(host.Paths, "media").Current.Preparation);
                Assert.False(((PresencePanel)window.Content).HasAnimatedProperties);
            }
            finally { window.Closing -= guard; owner.Close(); await window.ShutdownTask; }
        });
        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath));
        Assert.Empty(Directory.GetFiles(host.Root, "*.operation.json", SearchOption.AllDirectories)); Assert.Empty(host.ObservedErrors);
    }

    [Fact]
    public async Task MotionRecoveryShutdownFaultStillJoinsViewReads()
    {
        await using var host = new RealServer(); await Initialize(host);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            EnsureApplication(); var owner = Owner(); await using var model = new RecoveryViewModel(host.Paths, "media");
            var window = Hidden(new RecoveryWindow(model)); var read = Signal();
            try
            {
                owner.Show(); window.Owner = owner; window.Show(); await Loaded(window);
                WorkflowMotionAssertions.HoldRead(model, read.Task); WorkflowMotionAssertions.FailInitialization(window);
                ColorStateAssist.SetReduceMotion(window, true); window.Close();
                Assert.True(window.ClosureTask.IsCompleted); Assert.False(window.ShutdownTask.IsCompleted);
                read.SetResult(); await Assert.ThrowsAsync<IOException>(() => window.ShutdownTask);
                WorkflowMotionAssertions.AssertLifetimeDisposed(model);
            }
            finally
            {
                read.TrySetResult(); owner.Close();
                if (window.ShutdownTask.IsFaulted) { _ = window.ShutdownTask.Exception; }
                else { await window.ShutdownTask; }
            }
        });
    }

    [Theory]
    [InlineData(false, 920, 580)] [InlineData(false, 960, 620)] [InlineData(false, 1320, 820)]
    [InlineData(true, 920, 580)] [InlineData(true, 960, 620)] [InlineData(true, 1320, 820)]
    public async Task MotionRecoveryConfirmationKeepsBoundedGeometry(bool light, double width, double height)
    {
        await using var host = new RealServer(); await Initialize(host);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            EnsureApplication(); var previous = ThemeService.Preference; var owner = Owner();
            await using var model = new RecoveryViewModel(host.Paths, "media"); var window = Hidden(new RecoveryWindow(model));
            try
            {
                ThemeService.Apply(light ? ThemePreference.Light : ThemePreference.Dark, persist: null);
                owner.Width = width; owner.Height = height; owner.Show(); window.Owner = owner; window.Show(); await Loaded(window);
                await model.PrepareCommand.ExecuteAsync(null); await WorkflowMotionAssertions.Settled(window); await Idle(window);
                WorkflowMotionAssertions.Geometry(window, owner, window.ConfirmButton, window.CancelButton, window.ChromeCloseButton);
                Assert.True(model.CanConfirm); Assert.False(window.ConfirmButton.IsDefault);
                StartupGuiWindowTests.CaptureStateEvidence(window, $"recovery-{(light ? "light" : "dark")}-owner-{width}.png");
                window.Close(); await window.ShutdownTask;
            }
            finally { owner.Close(); await window.ShutdownTask; ThemeService.Apply(previous, persist: null); }
        });
        Assert.Empty(Directory.GetFiles(host.Root, "*.operation.json", SearchOption.AllDirectories)); Assert.Empty(host.ObservedErrors);
    }
}

internal static class WorkflowMotionAssertions
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static Task Settled(ToolWindow window) => PresenceMotionTests.Until(() => !((PresencePanel)window.Content).HasAnimatedProperties);
    internal static void HoldRead(object model, Task read) => ((HashSet<Task>)model.GetType().GetField("_reads", Private)!.GetValue(model)!).Add(read);
    internal static void FailInitialization(ToolWindow window) => window.GetType().GetProperty("InitializationTask", Private)!
        .SetValue(window, Task.FromException(new IOException("synthetic-initialization-failure")));
    internal static void AssertLifetimeDisposed(object model)
    {
        var lifetime = (CancellationTokenSource)model.GetType().GetField("_lifetime", Private)!.GetValue(model)!;
        Assert.Throws<ObjectDisposedException>(() => lifetime.Token);
    }
    internal static void Geometry(ToolWindow window, Window owner, params Button[] actions)
    {
        Assert.True(window.ActualWidth < owner.ActualWidth && window.ActualHeight < owner.ActualHeight);
        Assert.True(window.ActualHeight <= window.MaxHeight + 1);
        var surface = Assert.IsType<PresencePanel>(window.Content);
        Assert.True(surface.IsPresented); Assert.False(SurfaceMotion.GetFadeOnShow(surface));
        foreach (var action in actions)
        {
            Assert.True(action.IsVisible); Assert.Null(action.FocusVisualStyle);
            var bounds = action.TransformToAncestor(window).TransformBounds(new Rect(action.RenderSize));
            Assert.True(bounds.Top >= -1 && bounds.Left >= -1 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1, $"Clipped {action.Name}: {bounds}");
        }
    }
}
