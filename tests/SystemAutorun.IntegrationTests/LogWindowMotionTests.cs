using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class LogWindowMotionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task ShutdownIsPendingBeforeExitAndJoinsResourcesAfterClosed(bool reduced) => WithLog(async (_, _, window, model) =>
    {
        ColorStateAssist.SetReduceMotion(window, reduced);
        await model.FollowCommand.ExecuteAsync(null);
        Assert.True(model.IsFollowing); Assert.False(model.FollowTask.IsCompleted);
        var export = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetExport(window, export.Task);
        try
        {
            window.Close(); var shutdown = window.ShutdownTask;
            Assert.False(shutdown.IsCompleted);
            if (!reduced && SystemParameters.ClientAreaAnimation)
            {
                Assert.True(window.IsExitPending); Assert.False(window.ClosureTask.IsCompleted);
                Assert.True(model.IsFollowing); Assert.False(model.FollowTask.IsCompleted);
                Assert.False(((PresencePanel)window.Content).IsEnabled);
                var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window)!, Environment.TickCount, System.Windows.Input.Key.F5)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(key); Assert.True(key.Handled);
            }
            await window.ClosureTask.WaitAsync(TimeSpan.FromSeconds(5));
            await model.FollowTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(shutdown.IsCompleted); Assert.False(model.IsFollowing); Assert.False(model.CanExport);
            export.SetResult(); await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(shutdown, window.ShutdownTask); Assert.True(shutdown.IsCompletedSuccessfully);
            Assert.False(window.Dispatcher.HasShutdownStarted);
        }
        finally { export.TrySetResult(); }
    });

    [Theory]
    [InlineData("reverse")] [InlineData("veto")] [InlineData("hide")]
    public Task ReversalAndFinalVetoKeepTheSameLiveModelAndPendingPromise(string action) => WithLog(async (_, _, window, model) =>
    {
        if (!SystemParameters.ClientAreaAnimation) { window.Close(); await window.ShutdownTask; return; }
        var source = model.SelectedSource; var calls = 0;
        CancelEventHandler guard = (_, e) => { if (++calls == 2 && action == "veto") { e.Cancel = true; } };
        window.Closing += guard;
        try
        {
            window.Close(); var shutdown = window.ShutdownTask;
            Assert.True(window.IsExitPending); Assert.False(shutdown.IsCompleted); Assert.True(model.CanExport);
            if (action == "veto") { await PresenceMotionTests.Until(() => calls == 2 && !window.IsExitPending); }
            else if (action == "hide") { window.Hide(); window.Show(); }
            else { window.CancelPendingClose(); }
            await PresenceMotionTests.Until(() => !((PresencePanel)window.Content).HasAnimatedProperties);
            Assert.True(window.IsVisible); Assert.False(window.IsExitPending); Assert.False(shutdown.IsCompleted);
            Assert.Same(shutdown, window.ShutdownTask); Assert.Same(source, model.SelectedSource);
            Assert.True(model.CanExport); Assert.True(((PresencePanel)window.Content).IsEnabled);
        }
        finally { window.Closing -= guard; }
        window.Close(); await window.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
    });

    [Theory]
    [InlineData("closed")] [InlineData("pending")] [InlineData("reversed")]
    public Task PickerReturnCannotExportThroughAnOldWindowLifetime(string action)
    {
        string? destination = null; var picks = 0;
        return WithLog(async (fixture, _, window, model) =>
        {
            destination = Path.Combine(fixture.Root, "late-export.ndjson");
            window.ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, picks); Assert.False(File.Exists(destination));
            if (action == "reversed" && SystemParameters.ClientAreaAnimation)
            {
                Assert.True(window.IsVisible); Assert.True(model.CanExport);
                await PresenceMotionTests.Until(() => !((PresencePanel)window.Content).HasAnimatedProperties);
                window.ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await GetExport(window);
                Assert.Equal(2, picks); Assert.True(File.Exists(destination));
                Assert.EndsWith("\n", await File.ReadAllTextAsync(destination));
            }
        }, picker: owner =>
        {
            var window = Assert.IsType<LogViewerWindow>(owner);
            if (++picks == 1)
            {
                if (action == "closed") { ColorStateAssist.SetReduceMotion(window, true); }
                window.Close();
                if (action == "reversed") { window.CancelPendingClose(); }
            }
            return destination;
        });
    }

    [Fact]
    public Task AFailedOwnedTaskStillJoinsTheOtherWorkAndDisposesTheExportToken() => WithLog(async (_, _, window, _) =>
    {
        ColorStateAssist.SetReduceMotion(window, true);
        var initialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(LogViewerWindow).GetProperty(nameof(LogViewerWindow.InitializationTask), BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, initialization.Task);
        SetExport(window, Task.FromException(new IOException("owned-export-failure")));
        var lifetime = (CancellationTokenSource)typeof(LogViewerWindow).GetField("_lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        try
        {
            window.Close(); Assert.True(window.ClosureTask.IsCompleted); Assert.False(window.ShutdownTask.IsCompleted);
            initialization.SetResult();
            var error = await Assert.ThrowsAsync<IOException>(() => window.ShutdownTask);
            Assert.Equal("owned-export-failure", error.Message);
            Assert.Throws<ObjectDisposedException>(() => lifetime.Token);
        }
        finally { initialization.TrySetResult(); }
    }, expectedShutdownFailure: true);

    [Fact]
    public async Task ViewDisposalJoinsReadsAndReleasesResourcesEvenWhenFollowFaults()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var model = new LogViewerViewModel(fixture.Client, fixture.Paths);
            var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(LogViewerViewModel).GetField("_followTask", flags)!.SetValue(model, Task.FromException(new IOException("owned-follow-failure")));
            ((HashSet<Task>)typeof(LogViewerViewModel).GetField("_reads", flags)!.GetValue(model)!).Add(read.Task);
            var lifetime = (CancellationTokenSource)typeof(LogViewerViewModel).GetField("_lifetime", flags)!.GetValue(model)!;
            var gate = (SemaphoreSlim)typeof(LogViewerViewModel).GetField("_gate", flags)!.GetValue(model)!;
            var disposal = model.DisposeAsync().AsTask();
            try
            {
                Assert.False(disposal.IsCompleted); Assert.True(lifetime.IsCancellationRequested);
                read.SetResult();
                var error = await Assert.ThrowsAsync<IOException>(() => disposal);
                Assert.Equal("owned-follow-failure", error.Message);
                Assert.Throws<ObjectDisposedException>(() => lifetime.Token);
                Assert.Throws<ObjectDisposedException>(() => gate.Wait(0));
                Assert.Same(disposal, model.DisposeAsync().AsTask());
            }
            finally { read.TrySetResult(); await Assert.ThrowsAsync<IOException>(() => disposal); }
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MainCallerWaitsForShutdownAfterTheModalFrameReturns(bool hideInsteadOfClose)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            InstallApplication();
            var model = new MainWindowViewModel(fixture.Client, new NativeCacheStore(fixture.Paths.GuiCachePath),
                new AgentConfigurationClient(fixture.Client), fixture.Paths, new NeverRemoteReader()) { AutoRefresh = false };
            var main = new SystemAutorun.App.MainWindow(model, new AgentStartupClient(fixture.Client), new StartupCacheStore(fixture.Paths.StartupCachePath))
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            var export = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? entry = null; LogViewerWindow? child = null;
            try
            {
                main.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var interaction = main.Dispatcher.InvokeAsync(async () =>
                {
                    child = Assert.Single(main.OwnedWindows.OfType<LogViewerWindow>());
                    try
                    {
                        await PresenceMotionTests.Until(() => child.IsLoaded); await child.InitializationTask;
                        SetExport(child, export.Task);
                        if (hideInsteadOfClose) { child.Hide(); } else { child.Close(); }
                    }
                    catch { ColorStateAssist.SetReduceMotion(child, true); child.Close(); throw; }
                }, DispatcherPriority.Background).Task.Unwrap();
                entry = main.OpenLogViewerAsync(null); // Returns a pending Task after ShowDialog's frame has really unwound.
                await interaction;
                Assert.NotNull(child); Assert.True(child.ClosureTask.IsCompleted); Assert.False(child.IsVisible);
                Assert.False(entry.IsCompleted); Assert.False(child.ShutdownTask.IsCompleted);
                Assert.False(main.Dispatcher.HasShutdownStarted);
                export.SetResult(); await entry.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(child.ShutdownTask.IsCompletedSuccessfully);
            }
            finally
            {
                export.TrySetResult();
                if (child is not null && !child.ClosureTask.IsCompleted) { ColorStateAssist.SetReduceMotion(child, true); child.Close(); }
                if (entry is not null) { await entry.WaitAsync(TimeSpan.FromSeconds(5)); }
                var closing = typeof(SystemAutorun.App.MainWindow).GetMethod("Window_Closing", BindingFlags.Instance | BindingFlags.NonPublic)!;
                main.Closing -= (CancelEventHandler)closing.CreateDelegate(typeof(CancelEventHandler), main);
                main.Close(); model.DetachDashboard();
            }
        });
        Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2))); Assert.False(fixture.IsStopping);
    }

    [Theory]
    [InlineData(false, 920, 580)] [InlineData(false, 960, 620)] [InlineData(false, 1320, 820)]
    [InlineData(true, 920, 580)] [InlineData(true, 960, 620)] [InlineData(true, 1320, 820)]
    public Task LogBodyKeepsApprovedGeometryAndControlsAcrossOwnerSizes(bool light, double width, double height) => WithLog(async (_, owner, window, model) =>
    {
        var previous = ThemeService.Preference;
        try
        {
            ThemeService.Apply(light ? ThemePreference.Light : ThemePreference.Dark, persist: null);
            owner.Width = width; owner.Height = height;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var surface = Assert.IsType<PresencePanel>(window.Content);
            Assert.Equal(PresenceMotionKind.Tool, surface.MotionKind); Assert.True(surface.IsPresented);
            Assert.False(SurfaceMotion.GetFadeOnShow(surface));
            Assert.True(window.ActualWidth < owner.ActualWidth && window.ActualHeight < owner.ActualHeight);
            Assert.InRange(window.ActualWidth, 760, 800); Assert.InRange(window.ActualHeight, 460, 500);
            Assert.True(window.EventList.ActualHeight >= 48); Assert.True(model.CanExport);
            foreach (var button in new[] { window.CloseButton, window.BackButton, window.FollowButton, window.RefreshButton,
                window.ExportButton, window.FromStartButton, window.RecentButton, window.NextButton })
            {
                Assert.True(button.IsVisible); Assert.Null(button.FocusVisualStyle);
                var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                Assert.True(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1, $"Clipped log action: {bounds}");
            }
            StartupGuiWindowTests.CaptureStateEvidence(window, $"logs-{(light ? "light" : "dark")}-owner-{width}.png");
            window.Close(); await window.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(window.ClosureTask.IsCompleted); Assert.False(surface.HasAnimatedProperties);
        }
        finally { ThemeService.Apply(previous, persist: null); }
    });

    private static async Task WithLog(Func<ConfigurationControlPlaneTests.ServerFixture, Window, LogViewerWindow, LogViewerViewModel, Task> action,
        Func<Window, string?>? picker = null, bool expectedShutdownFailure = false)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var configuration = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var settings = await fixture.Client.SendAsync<WorkspaceMutation<ApplicationSettings>>("settings-set", payload: new ApplicationSettings
        { LogSources = [new() { Id = "motion", Path = "motion.log" }] }, expectedRevision: configuration.Revision);
        Assert.Equal("motion", Assert.Single(settings.Value.LogSources).Id);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "motion.log"), "[2026-11-06 09:00:00 +08:00] INFO 合成生命周期样例\n");
        var before = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            InstallApplication();
            await using var model = new LogViewerViewModel(fixture.Client, fixture.Paths, pollInterval: TimeSpan.FromMilliseconds(20));
            var owner = new Window { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, Width = 960, Height = 620 };
            var window = new LogViewerWindow(model, picker) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            try
            {
                owner.Show(); window.Owner = owner;
                ColorStateAssist.SetReduceMotion(window, false);
                window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await window.InitializationTask;
                model.SelectedSource = model.Sources.Single(item => item.Source.Id == "motion"); await model.RefreshAsync();
                Assert.True(model.CanExport);
                await PresenceMotionTests.Until(() => !((PresencePanel)window.Content).HasAnimatedProperties);
                await action(fixture, owner, window, model);
            }
            finally
            {
                owner.Close();
                if (expectedShutdownFailure) { await Assert.ThrowsAsync<IOException>(() => window.ShutdownTask); }
                else { await window.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                Assert.True(window.ClosureTask.IsCompleted); Assert.False(((PresencePanel)window.Content).HasAnimatedProperties);
            }
        });
        var after = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        Assert.Equal(before.Revision, after.Revision);
        Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2))); Assert.False(fixture.IsStopping);
    }

    private static void InstallApplication()
    {
        if (Application.Current is not SystemAutorun.App.App) { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
        WindowTheme.Install();
    }
    private static void SetExport(LogViewerWindow window, Task task) => typeof(LogViewerWindow).GetField("_exportTask", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, task);
    private static Task GetExport(LogViewerWindow window) => (Task)typeof(LogViewerWindow).GetField("_exportTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private sealed class NeverRemoteReader : IRemoteHostReader
    {
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Log motion fixtures never connect to remote hosts.");
    }
}
