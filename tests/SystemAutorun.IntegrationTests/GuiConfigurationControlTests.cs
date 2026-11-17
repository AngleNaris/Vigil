using System.Collections.Concurrent;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.IntegrationTests;

public sealed class GuiConfigurationControlTests
{
    [Fact]
    public async Task TaskCommandsPersistThroughAgentAndRemovalNeverStopsAWorkload()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(new OfflineProbe());
        await RunStaAsync(async () =>
        {
            var errors = new List<string>();
            var viewModel = CreateViewModel(fixture, errors);
            await viewModel.InitializeAsync();
            var executable = Path.Combine(fixture.Root, "never-run-fixture.exe");
            // The editor requires an existing file. This is deliberately NOT a PE image;
            // the injected executor still throws if any operation tries to run it.
            await File.WriteAllTextAsync(executable, "fixture, not an executable image");
            Assert.True(await viewModel.AddTaskAsync(new AddTaskRequest("fixture", AutorunTaskType.Windows,
                Executable: executable, StartupDelay: 0, HealthType: HealthCheckType.Process,
                HealthPattern: "never-run-fixture.exe", ExpectedRunning: true)), string.Join("; ", errors));
            Assert.Single(viewModel.Tasks);
            // R112: the GUI has no tracking toggle; the new-task default remains expected-running.
            var tracked = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
            Assert.True(tracked.Configuration.GetTask("fixture").ExpectedRunning);
            Assert.True(tracked.Configuration.GetTask("fixture").Enabled);
            Assert.False(viewModel.SelectedStartOnAppLaunch);
            Assert.False(viewModel.SelectedAutoRecover);
            await viewModel.ToggleTaskAppLaunchCommand.ExecuteAsync(null);
            Assert.True(viewModel.SelectedStartOnAppLaunch);
            Assert.False(viewModel.SelectedAutoRecover);
            await viewModel.ToggleTaskAutoRecoverCommand.ExecuteAsync(null);
            Assert.True(viewModel.SelectedStartOnAppLaunch);
            Assert.True(viewModel.SelectedAutoRecover);
            await viewModel.ToggleTaskAppLaunchCommand.ExecuteAsync(null);
            var policies = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
            Assert.False(policies.Configuration.GetTask("fixture").StartOnAppLaunch);
            Assert.True(policies.Configuration.GetTask("fixture").AutoRecover);
            Assert.False(policies.Preferences.AutoStartMissingOnLaunch);
            Assert.False(policies.Preferences.AutoRecoverFailedTasks);
            await viewModel.DisableCommand.ExecuteAsync(null);
            var disabled = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
            Assert.False(disabled.Configuration.GetTask("fixture").Enabled);
            Assert.True(disabled.Configuration.GetTask("fixture").ExpectedRunning);
            await viewModel.EnableCommand.ExecuteAsync(null);
            viewModel.ConfirmRequested = _ => true;
            await viewModel.RemoveTaskCommand.ExecuteAsync(null);
            Assert.Empty((await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration.Tasks);
            Assert.Empty(viewModel.Tasks);
            Assert.Empty(errors);
            // Executor methods throw on start/stop; none of these config actions may call them.
        });
    }

    [Fact]
    public async Task InitializationChecksPerTaskLaunchOnlyOnceAndRefreshNeverReplaysIt()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(new OfflineProbe());
        var executable = Path.Combine(fixture.Root, "never-run-policy-fixture.exe");
        await File.WriteAllTextAsync(executable, "fixture, not an executable image");
        var configuration = new AgentConfigurationClient(fixture.Client);
        await configuration.ReadAsync();
        await configuration.MutateAsync<ConfigurationMutationResult>("task-add", payload:
            new AddTaskRequest("policy", AutorunTaskType.Windows, Executable: executable,
                HealthPattern: "never-run-policy-fixture.exe", StartOnAppLaunch: true, AutoRecover: false));
        await RunStaAsync(async () =>
        {
            var errors = new List<string>();
            var model = CreateViewModel(fixture, errors);
            try
            {
                await model.InitializeAsync();
                Assert.Equal(1, CountLaunchChecks());
                Assert.Contains("持久控制状态缺失", model.StatusMessage);
                await model.RefreshCommand.ExecuteAsync(null);
                await model.InitializeAsync();
                Assert.Equal(1, CountLaunchChecks());
                Assert.Empty(errors);
            }
            finally { model.DetachDashboard(); }
        });
        int CountLaunchChecks() => Directory.EnumerateFiles(fixture.Root, "*.operation.json", SearchOption.AllDirectories)
            .Count(path =>
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                return document.RootElement.TryGetProperty("command", out var command) && command.GetString() == "start-on-app-launch";
            });
    }

    [Fact]
    public async Task AddingWhileBusyReportsRetentionInsteadOfSilentlyDroppingTheRequest()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        await RunStaAsync(async () =>
        {
            var model = CreateViewModel(fixture, []);
            model.IsBusy = true;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                model.AddTaskAsync(new AddTaskRequest("retained", AutorunTaskType.Windows)));
            Assert.Contains("输入已保留", error.Message);
            Assert.Empty((await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration.Tasks);
        });
    }

    [Fact]
    public async Task RapidPreferenceTogglesSaveInOrderWithoutSelfConflicts()
    {
        using var deniedTrace = new AccessDeniedTrace();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var remote = DisabledRemote(fixture.Root);
        var workspace = new ConfigurationWorkspace(fixture.Paths);
        var original = await workspace.ReadAsync();
        _ = await workspace.UpsertRemoteHostAsync(remote, original.Revision);
        await RunStaAsync(async () =>
        {
            var errors = new List<string>();
            var viewModel = CreateViewModel(fixture, errors);
            await viewModel.InitializeAsync();
            var loadedRevision = (await workspace.ReadAsync()).Revision;
            await viewModel.FlushSettingsAsync();
            Assert.Equal(loadedRevision, (await workspace.ReadAsync()).Revision);
            for (var index = 0; index < 20; index++)
            {
                viewModel.StartMissingOnLaunch = true;
                viewModel.AutoRecoverFailedTasks = true;
                viewModel.StartMissingOnLaunch = false;
                viewModel.AutoRecoverFailedTasks = false;
            }
            viewModel.StartMissingOnLaunch = true;
            await viewModel.FlushSettingsAsync();
            var actual = await workspace.ReadAsync();
            Assert.True(actual.Preferences.AutoStartMissingOnLaunch, string.Join("; ", errors) + deniedTrace.Snapshot());
            Assert.False(actual.Preferences.AutoRecoverFailedTasks);
            Assert.Equal(remote, Assert.Single(actual.Preferences.RemoteHosts));
            Assert.True(viewModel.StartMissingOnLaunch);
            Assert.False(viewModel.AutoRecoverFailedTasks);
            Assert.Empty(errors);
        });
    }

    [Fact]
    public async Task ForeignConflictDropsQueuedStaleIntentsAndReloadsPersistedFlags()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        await RunStaAsync(async () =>
        {
            var errors = new List<string>();
            var viewModel = CreateViewModel(fixture, errors);
            await viewModel.InitializeAsync();
            var other = new AgentConfigurationClient(fixture.Client);
            _ = await other.ReadAsync();
            _ = await other.MutateAsync<ApplicationSettings>("settings-patch",
                payload: new ApplicationSettingsPatch(AutoRecoverFailedTasks: true));
            viewModel.StartMissingOnLaunch = true;
            viewModel.AutoRecoverFailedTasks = true;
            await viewModel.FlushSettingsAsync();
            var actual = await other.ReadAsync();
            Assert.False(actual.Preferences.AutoStartMissingOnLaunch);
            Assert.True(actual.Preferences.AutoRecoverFailedTasks);
            Assert.False(viewModel.StartMissingOnLaunch);
            Assert.True(viewModel.AutoRecoverFailedTasks);
            Assert.Single(errors);
            Assert.Contains("其他客户端", errors[0]);
        });
    }

    [Fact]
    public async Task RemoteEditorUsesOpeningRevisionAndDoesNotOverwriteForeignPreferences()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        await RunStaAsync(async () =>
        {
            var errors = new List<string>();
            var viewModel = CreateViewModel(fixture, errors);
            await viewModel.InitializeAsync();
            var opened = await viewModel.PrepareRemoteHostsEditAsync();
            var other = new AgentConfigurationClient(fixture.Client);
            _ = await other.ReadAsync();
            _ = await other.MutateAsync<ApplicationSettings>("settings-patch",
                payload: new ApplicationSettingsPatch(AutoRecoverFailedTasks: true));
            var remote = DisabledRemote(fixture.Root);
            var conflict = await Assert.ThrowsAsync<AgentCommandException>(() =>
                viewModel.ApplyRemoteHostsAsync([remote], opened));
            Assert.Equal("revision_conflict", conflict.Code);
            var actual = await other.ReadAsync();
            Assert.True(actual.Preferences.AutoRecoverFailedTasks);
            Assert.Empty(actual.Preferences.RemoteHosts);
            Assert.Empty(viewModel.RemoteHostDefinitions);
            var reopened = await viewModel.PrepareRemoteHostsEditAsync();
            await viewModel.ApplyRemoteHostsAsync([remote], reopened);
            Assert.Equal(remote, Assert.Single((await other.ReadAsync()).Preferences.RemoteHosts));
            Assert.Equal(remote, Assert.Single(viewModel.RemoteHostDefinitions));
            Assert.True(viewModel.AutoRecoverFailedTasks);
            Assert.Empty(errors);
        });
    }

    [Fact]
    public void AccessDeniedDiagnosticsAreBoundedPrivateAndDetachedAfterDispose()
    {
        using var trace = new AccessDeniedTrace();
        var caught = 0;
        for (var index = 0; index < 20; index++)
        {
            try { throw new UnauthorizedAccessException("private-test-value-not-for-diagnostics"); }
            catch (UnauthorizedAccessException) { caught++; }
        }
        var snapshot = trace.Snapshot();
        Assert.Equal(20, caught);
        Assert.Equal(16, snapshot.Split("UnauthorizedAccessException", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("private-test-value-not-for-diagnostics", snapshot);
        trace.Dispose();
        try { throw new UnauthorizedAccessException("after-detach"); }
        catch (UnauthorizedAccessException) { caught++; }
        Assert.Equal(21, caught); Assert.Equal(snapshot, trace.Snapshot());
    }

    private static MainWindowViewModel CreateViewModel(ConfigurationControlPlaneTests.ServerFixture fixture, List<string> errors)
    {
        var viewModel = new MainWindowViewModel(fixture.Client, new NativeCacheStore(fixture.Paths.GuiCachePath),
            new AgentConfigurationClient(fixture.Client), fixture.Paths, new NeverRemoteReader());
        viewModel.ErrorRequested += (_, message) => errors.Add(message);
        return viewModel;
    }

    private static RemoteHostDefinition DisabledRemote(string root) => new()
    {
        Id = "fixture-remote", Name = "Disabled fixture", Enabled = false, Platform = RemoteHostPlatform.Linux,
        Host = "192.0.2.25", UserName = "fixture", Authentication = RemoteAuthenticationMode.SshAgent,
        KnownHostsFile = Path.Combine(root, "known_hosts"),
    };

    internal static async Task RunStaAsync(Func<Task> action, TimeSpan? timeout = null)
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "owned-gui-config-test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var dispatcher = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Exception? actionError = null;
        try
        {
            await dispatcher.InvokeAsync(action).Task.Unwrap().WaitAsync(timeout ?? TimeSpan.FromSeconds(45));
        }
        catch (Exception exception)
        {
            actionError = exception;
            throw;
        }
        finally
        {
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            // A completed DispatcherOperation may resume this continuation inline
            // on its STA. Never synchronously Join that same dispatcher thread.
            var stopped = await Task.Run(() => thread.Join(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            Assert.True(stopped, $"Owned STA dispatcher did not stop (started={dispatcher.HasShutdownStarted}, finished={dispatcher.HasShutdownFinished}). Original error: {actionError}");
        }
    }

    // Test-only diagnostics: capture the throwing layer rather than inferring ACL/pipe/file
    // failure from the UI's short message. Other concurrent tests may contribute traces.
    private sealed class AccessDeniedTrace : IDisposable
    {
        private readonly ConcurrentQueue<string> _traces = new();
        [ThreadStatic] private static bool _capturing;
        internal AccessDeniedTrace() => AppDomain.CurrentDomain.FirstChanceException += Observe;
        private void Observe(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (_capturing || args.Exception is not UnauthorizedAccessException
                && !(args.Exception is IOException && (args.Exception.HResult & 0xFFFF) == 5)) { return; }
            _capturing = true;
            try
            {
                // No exception Message, payload, command, target or file contents are recorded.
                var stack = args.Exception.StackTrace ?? "<no throwing stack>";
                _traces.Enqueue($"{DateTimeOffset.UtcNow:O} thread={Environment.CurrentManagedThreadId} {args.Exception.GetType().Name} 0x{args.Exception.HResult:X8}\n{stack[..Math.Min(stack.Length, 8192)]}");
                while (_traces.Count > 16) { _traces.TryDequeue(out _); }
            }
            catch (Exception) { /* Diagnostics cannot change exception propagation. */ }
            finally { _capturing = false; }
        }
        internal string Snapshot() => "\nRecent process-wide ACCESS_DENIED first-chance stacks (not root-attributed):\n" + string.Join("\n", _traces);
        public void Dispose() => AppDomain.CurrentDomain.FirstChanceException -= Observe;
    }

    private sealed class OfflineProbe : IHealthProbe
    {
        public Task<HealthResult> CheckAsync(AutorunTask task, bool quick, CancellationToken cancellationToken = default) =>
            Task.FromResult(HealthResult.Fail("fixture_offline", "Deliberately offline fixture; no probe executed."));
    }

    private sealed class NeverRemoteReader : IRemoteHostReader
    {
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("GUI configuration tests must not connect to remote hosts.");
    }
}
