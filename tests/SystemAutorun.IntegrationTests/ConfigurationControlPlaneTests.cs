using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SystemAutorun.Agent;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.IntegrationTests;

public sealed class ConfigurationControlPlaneTests
{
    [Fact]
    public async Task RealPipePreservesRevisionAndTypedConflict()
    {
        await using var fixture = new ServerFixture();
        var original = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var changed = await fixture.Client.SendAsync<WorkspaceMutation<ApplicationSettings>>(
            "settings-patch", payload: new ApplicationSettingsPatch(AutoRecoverFailedTasks: true),
            expectedRevision: original.Revision);
        var conflict = await Assert.ThrowsAsync<AgentCommandException>(() =>
            fixture.Client.SendAsync<JsonElement>("settings-patch",
                payload: new ApplicationSettingsPatch(AutoStartMissingOnLaunch: true),
                expectedRevision: original.Revision));
        Assert.Equal("revision_conflict", conflict.Code);
        Assert.Equal(changed.Revision, conflict.Revision);
        var reread = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("settings-get");
        Assert.True(reread.Preferences.AutoRecoverFailedTasks);
        Assert.False(reread.Preferences.AutoStartMissingOnLaunch);
    }

    [Fact]
    public async Task TaskMutationRequiresExplicitRevisionAndBooleanFlag()
    {
        await using var fixture = new ServerFixture();
        var missingRevision = await Assert.ThrowsAsync<AgentCommandException>(() =>
            fixture.Client.SendAsync<JsonElement>("settings-patch", payload: new ApplicationSettingsPatch(true)));
        Assert.Equal("invalid_argument", missingRevision.Code);
        var original = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var added = await fixture.Client.SendAsync<WorkspaceMutation<ConfigurationMutationResult>>(
            "task-add", payload: new AddTaskRequest("fixture", AutorunTaskType.WslBoot,
                Distro: "test-fixture", HealthType: HealthCheckType.WslRunning, ExpectedRunning: true),
            expectedRevision: original.Revision);
        var invalidFlag = await Assert.ThrowsAsync<AgentCommandException>(() =>
            fixture.Client.SendAsync<JsonElement>("task-monitor", "fixture", new { wrongField = false },
                expectedRevision: added.Revision));
        Assert.Equal("invalid_argument", invalidFlag.Code);
        var snapshot = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        Assert.True(snapshot.Configuration.GetTask("fixture").ExpectedRunning);
        var removed = await fixture.Client.SendAsync<WorkspaceMutation<ConfigurationMutationResult>>(
            "task-remove", "fixture", expectedRevision: snapshot.Revision);
        Assert.True(removed.Value.Succeeded);
        Assert.Empty((await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration.Tasks);
    }

    [Fact]
    public async Task WrongRootShutdownIsRejectedBeforeDispatch()
    {
        await using var fixture = new ServerFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var pipe = new NamedPipeClientStream(
            ".", fixture.Endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        var request = new AgentRequest(Guid.NewGuid().ToString("N"), "shutdown", RootId: "not-this-root");
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, AgentProtocol.JsonOptions).AsMemory(), timeout.Token);
        var line = await reader.ReadLineAsync(timeout.Token);
        var response = JsonSerializer.Deserialize<AgentResponse>(line!, AgentProtocol.JsonOptions)!;
        Assert.False(response.Ok);
        Assert.Equal("invalid_request", response.ErrorCode);
        Assert.Equal(fixture.Endpoint.RootId, response.RootId);
        Assert.False(fixture.IsStopping);
        Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task FourIdleConnectionsDoNotPreventReadinessProbe()
    {
        await using var fixture = new ServerFixture();
        var clients = new List<NamedPipeClientStream>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var pipe = new NamedPipeClientStream(
                    ".", fixture.Endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                clients.Add(pipe);
                await pipe.ConnectAsync(timeout.Token);
                await pipe.WriteAsync(Encoding.UTF8.GetBytes("{\"id\":\""), timeout.Token);
                await pipe.FlushAsync(timeout.Token);
            }

            Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2), timeout.Token));
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
    }

    [Theory]
    [InlineData(109, true)]
    [InlineData(232, true)]
    [InlineData(233, true)]
    [InlineData(5, false)]
    [InlineData(32, false)]
    public void OnlyDisconnectedAcceptsAreRecoverable(int nativeCode, bool recoverable) =>
        Assert.Equal(recoverable, AgentServer.IsClientDisconnect(new IOException("fixture", unchecked((int)0x80070000) | nativeCode)));

    [Fact]
    public async Task RapidlyDisconnectedReadinessClientsDoNotTerminateTheServer()
    {
        await using var fixture = new ServerFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var index = 0; index < 64; index++)
        {
            await using var pipe = new NamedPipeClientStream(".", fixture.Endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
        }
        Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(3), timeout.Token));
    }

    [Fact]
    public async Task AgentCacheDetectsChangedContentWithSameTimestamp()
    {
        await using var fixture = new ServerFixture();
        Assert.Empty((await fixture.Cache.GetAsync()).Tasks);
        var previousTime = File.GetLastWriteTimeUtc(fixture.Paths.ConfigPath);
        var workspace = new ConfigurationWorkspace(fixture.Paths);
        var original = await workspace.ReadAsync();
        _ = await workspace.AddTaskAsync(new("fixture", AutorunTaskType.WslBoot,
            Distro: "test-fixture", HealthType: HealthCheckType.WslRunning), original.Revision);
        File.SetLastWriteTimeUtc(fixture.Paths.ConfigPath, previousTime);
        Assert.Single((await fixture.Cache.GetAsync()).Tasks);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OriginalCommandExceptionsAreObservableLocallyButNotSentAsWireStacks(bool admission)
    {
        await using var fixture = new ServerFixture(errorObserver: (_, _) => throw new InvalidOperationException("observer must not replace the business error"));
        AgentCommandException error;
        if (admission)
        {
            // Directory at the fixed metadata leaf produces a real pre-execution access error.
            Directory.CreateDirectory(Path.Combine(fixture.Root, ".autorun-state", CurrentUserIdentity.Key, "operations", "metadata.json"));
            error = await Assert.ThrowsAsync<AgentCommandException>(() => fixture.Client.SendAsync<JsonElement>(
                "settings-patch", payload: new ApplicationSettingsPatch(true), expectedRevision: new string('a', 64)));
            Assert.Equal("permission_denied", error.Code);
            Assert.False(File.Exists(fixture.Paths.GuiSettingsPath));
        }
        else
        {
            await File.WriteAllTextAsync(fixture.Paths.GuiSettingsPath, "null");
            error = await Assert.ThrowsAsync<AgentCommandException>(() => fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration"));
            Assert.Equal("configuration_error", error.Code);
        }
        var observed = Assert.Single(fixture.ObservedErrors);
        Assert.Equal(admission ? "settings-patch" : "configuration", observed.Command);
        Assert.NotEmpty(observed.RequestId);
        Assert.Contains(admission ? "OperationJournal.ReadJsonAsync" : "ApplicationSettingsStore.LoadAsync", observed.Error.StackTrace);
        Assert.DoesNotContain("OperationJournal", error.Message); Assert.DoesNotContain("ApplicationSettingsStore", error.Message);
        Assert.DoesNotContain("observer must not replace", error.Message);
    }

    internal sealed class ServerFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _server;
        private readonly HostStatusReader _host = new();
        internal AgentMonitor? Monitor { get; }
        internal AgentRemoteHostMonitor? RemoteMonitor { get; }
        public string Root { get; } = Directory.CreateTempSubdirectory("SystemAutorun-control-tests-").FullName;
        public ProjectPaths Paths { get; }
        public AgentEndpoint Endpoint { get; }
        public AgentClient Client { get; }
        public ConfigurationCache Cache { get; }
        public bool IsStopping => _shutdown.IsCancellationRequested;
        internal ConcurrentQueue<(string Command, string RequestId, string? OperationId, Exception Error)> ObservedErrors { get; } = new();

        public ServerFixture(IHealthProbe? healthProbe = null, IRemoteHostReader? remoteReader = null,
            IStartupSnapshotReader? startupReader = null, IStartupRegistrationReader? registrationReader = null,
            Action<AgentRequest, Exception>? errorObserver = null, IStartupSlotEvidenceReader? startupSlotReader = null,
            Func<ConfigurationWorkspace, string, AgentMonitor>? monitorFactory = null,
            Func<ConfigurationWorkspace, string, AgentRemoteHostMonitor>? remoteMonitorFactory = null,
            Func<ConfigurationWorkspace, string, AgentRemoteHostMonitor?, AgentMonitor>? pairedMonitorFactory = null)
        {
            Paths = new ProjectPaths(Root);
            File.WriteAllText(Paths.ConfigPath, "version: 1\nsettings:\n  default_startup_delay: 0\ntasks: []\n");
            Endpoint = AgentProtocol.ForRoot(Root);
            Client = new AgentClient(Root);
            Cache = new ConfigurationCache(new ConfigurationLoader(), Paths);
            var orchestrator = new TaskOrchestrator(
                Cache, new OverrideStore(), new NeverRunExecutor(), healthProbe ?? new NeverProbe(), new FileLog());
            var workspace = new ConfigurationWorkspace(Paths);
            var operations = new OperationJournal(Paths);
            RemoteMonitor = remoteMonitorFactory?.Invoke(workspace, operations.InstanceId);
            Monitor = pairedMonitorFactory is not null
                ? pairedMonitorFactory(workspace, operations.InstanceId, RemoteMonitor)
                : monitorFactory?.Invoke(workspace, operations.InstanceId);
            var dispatcher = new AgentCommandDispatcher(
                orchestrator, _host, workspace, monitor: Monitor, operations: operations, remoteReader: remoteReader, startupReader: startupReader,
                registrationReader: registrationReader, errorObserver: (request, error) =>
                {
                    if (ObservedErrors.Count < 32) { ObservedErrors.Enqueue((request.Command, request.Id, request.OperationId, error)); }
                    errorObserver?.Invoke(request, error);
                }, startupSlotReader: startupSlotReader, remoteMonitor: RemoteMonitor);
            _server = new AgentServer(dispatcher, _shutdown, Root).RunAsync(_shutdown.Token);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _shutdown.Cancel();
                await _server.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                _shutdown.Dispose();
                if (RemoteMonitor is not null) { await RemoteMonitor.DisposeAsync(); }
                Monitor?.Dispose();
                _host.Dispose();
                var resolved = Path.GetFullPath(Root);
                if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(resolved).StartsWith("SystemAutorun-control-tests-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to delete a non-owned control-plane fixture.");
                }

                Directory.Delete(resolved, recursive: true);
            }
        }
    }

    private sealed class NeverProbe : IHealthProbe
    {
        public Task<HealthResult> CheckAsync(AutorunTask task, bool quick, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Configuration tests must not probe workloads.");
    }

    private sealed class NeverRunExecutor : ITaskExecutor
    {
        public Task<CommandResult> StartAsync(AutorunTask task, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Configuration tests must not start workloads.");
        public Task<CommandResult> StopAsync(AutorunTask task, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Configuration tests must not stop workloads.");
        public IReadOnlyList<int> FindProcessIds(AutorunTask task) =>
            throw new InvalidOperationException("Configuration tests must not inspect workload processes.");
    }
}
