using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.IntegrationTests;

public sealed partial class AgentProcessContractsTests
{
    [Fact]
    public async Task RealAgentsIsolateRootsAndJunctionDoesNotStartDuplicate()
    {
        await using var first = new OwnedAgent();
        await using var second = new OwnedAgent();
        await first.StartAsync();
        await second.StartAsync();
        var firstIdentity = await first.IdentityAsync();
        var secondIdentity = await second.IdentityAsync();
        Assert.NotEqual(firstIdentity.GetProperty("pid").GetInt32(), secondIdentity.GetProperty("pid").GetInt32());
        Assert.NotEqual(AgentProtocol.ForRoot(first.Root).PipeName, AgentProtocol.ForRoot(second.Root).PipeName);
        var alias = Path.Combine(first.Root, "alias");
        try
        {
            await CreateJunctionAsync(alias, first.Root);
            using var duplicate = OwnedAgent.Launch(alias);
            try
            {
                await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, duplicate.ExitCode);
            }
            finally
            {
                if (!duplicate.HasExited)
                {
                    duplicate.Kill(entireProcessTree: true);
                    await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            var aliasIdentity = await new AgentClient(alias).SendAsync<JsonElement>("ping");
            Assert.Equal(firstIdentity.GetProperty("pid").GetInt32(), aliasIdentity.GetProperty("pid").GetInt32());
            await first.StopAsync();
            Assert.True(await second.Client.PingAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            if (Directory.Exists(alias))
            {
                Assert.Equal(Path.GetFullPath(first.Root), Path.GetDirectoryName(Path.GetFullPath(alias)));
                Directory.Delete(alias);
            }
        }
    }

    [Fact]
    public async Task GuiLauncherDisposalLeavesTheSameAgentAvailableToOtherClients()
    {
        await using var fixture = new OwnedAgent();
        var manager = fixture.CreateGuiLauncher();
        await manager.EnsureRunningAsync();
        var first = await fixture.IdentityAsync();
        await manager.DisposeAsync();
        await manager.DisposeAsync();
        var after = await fixture.IdentityAsync();
        Assert.Equal(first.GetProperty("pid").GetInt32(), after.GetProperty("pid").GetInt32());
        Assert.Equal(first.GetProperty("instanceId").GetString(), after.GetProperty("instanceId").GetString());
        await using var another = new AgentProcessManager(fixture.Client, new ProjectPaths(fixture.Root),
            _ => throw new InvalidOperationException("A running root must not launch another Agent."));
        await another.EnsureRunningAsync();
        await fixture.StopAsync(); // Only explicit shutdown stops the daemon.
    }

    [Fact]
    public async Task CancelledGuiStartupDoesNotKillAnAgentThatWasAlreadyLaunched()
    {
        await using var fixture = new OwnedAgent();
        using var cancellation = new CancellationTokenSource();
        await using var manager = fixture.CreateGuiLauncher(cancellation.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.EnsureRunningAsync(cancellation.Token));
        await manager.DisposeAsync();
        await fixture.WaitUntilReadyAsync();
        Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RealAgentSamplesAndPersistsWhileNoClientIsConnected()
    {
        await using var fixture = new OwnedAgent();
        var before = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "autorun.yaml"));
        await fixture.StartAsync();
        var first = await WaitForMonitoringAsync(fixture.Client, snapshot => snapshot.Persistence.LastWrittenAt is not null
            && snapshot.Tasks.Available && snapshot.Host.Available);
        Assert.True(first.Tasks.Available);
        Assert.Empty(first.Tasks.Data!);
        Assert.True(first.Host.Available);
        Assert.Equal((await fixture.IdentityAsync()).GetProperty("instanceId").GetString(), first.InstanceId);
        // Every SendAsync owns/disposes its pipe. Deliberately make no IPC call for a full cycle.
        await Task.Delay(TimeSpan.FromSeconds(12));
        var later = await fixture.Client.SendAsync<AgentMonitoringSnapshot>("monitoring-snapshot");
        Assert.Equal(first.InstanceId, later.InstanceId);
        Assert.True(later.HeartbeatAt > first.HeartbeatAt);
        Assert.True(later.Persistence.LastWrittenAt > first.Persistence.LastWrittenAt);
        Assert.Equal(1, later.SamplingIntervalSeconds); // Legacy whole-second compatibility field is rounded up.
        Assert.Equal(500, later.ResourceSamplingIntervalMilliseconds); Assert.Equal(2, later.RecentResourceSchemaVersion);
        var now = DateTimeOffset.UtcNow;
        var recentQuery = new ResourceHistoryQuery(now.AddSeconds(-12), now, 601);
        var recent = await fixture.Client.SendAsync<RecentResourceHistoryResult>("recent-resources-v2", payload: recentQuery);
        ResourceHistoryResponseValidator.Validate(recent, recentQuery);
        Assert.Equal(first.InstanceId, recent.AgentInstanceId); Assert.Equal(500, recent.SourceIntervalMilliseconds);
        Assert.True(recent.Points.Count >= 3);
        Assert.Contains(recent.Points.Zip(recent.Points.Skip(1)), pair =>
            pair.Second.At - pair.First.At > TimeSpan.FromMilliseconds(200) && pair.Second.At - pair.First.At < TimeSpan.FromMilliseconds(850));
        var legacyQuery = recentQuery with { BucketCount = 301 };
        var legacy = await fixture.Client.SendAsync<ResourceHistoryResult>("resource-history", payload: legacyQuery);
        ResourceHistoryResponseValidator.Validate(legacy, legacyQuery);
        Assert.All(legacy.Points, point => Assert.Contains(recent.Points, actual => actual.At == point.At));
        var history = await fixture.Client.SendAsync<MonitoringHistoryResult>("monitoring-history",
            payload: new MonitoringHistoryQuery("missing-fixture", new string('a', 64), now.AddSeconds(-30), now, 2));
        Assert.Equal(30, history.UnknownSeconds);
        Assert.Equal(0, history.HealthySeconds);
        Assert.Null(history.RetainedFrom);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "autorun.yaml")));
    }

    [Fact]
    public async Task HistoryInitializationFailureDoesNotStopRealAgentControlOrObservation()
    {
        await using var fixture = new OwnedAgent();
        var parent = Path.Combine(fixture.Root, ".autorun-state", CurrentUserIdentity.Key);
        Directory.CreateDirectory(parent);
        await File.WriteAllTextAsync(Path.Combine(parent, "monitoring"), "Owned fixture: blocks only history directory creation.");
        await fixture.StartAsync();
        var snapshot = await WaitForMonitoringAsync(fixture.Client, value => value.Tasks.Available && value.Host.Available);
        Assert.False(snapshot.Persistence.Available);
        Assert.NotNull(snapshot.Persistence.Error);
        var now = DateTimeOffset.UtcNow;
        var error = await Assert.ThrowsAsync<AgentCommandException>(() => fixture.Client.SendAsync<MonitoringHistoryResult>(
            "monitoring-history", payload: new MonitoringHistoryQuery("fixture", new string('a', 64), now.AddSeconds(-10), now)));
        Assert.Equal("history_unavailable", error.Code);
        var configuration = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var result = await fixture.Client.SendAsync<WorkspaceMutation<ApplicationSettings>>("settings-patch",
            payload: new ApplicationSettingsPatch(AutoRecoverFailedTasks: false), expectedRevision: configuration.Revision);
        Assert.False(result.Value.AutoRecoverFailedTasks);
    }

    private static async Task<AgentMonitoringSnapshot> WaitForMonitoringAsync(AgentClient client,
        Func<AgentMonitoringSnapshot, bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (true)
        {
            var snapshot = await client.SendAsync<AgentMonitoringSnapshot>("monitoring-snapshot", cancellationToken: deadline.Token);
            if (ready(snapshot)) { return snapshot; }
            await Task.Delay(50, deadline.Token);
        }
    }

    [Fact]
    public async Task PersistedResultReplaysAfterRestartAndOldShutdownDoesNotStopNewInstance()
    {
        await using var fixture = new OwnedAgent();
        await fixture.StartAsync();
        var initialIdentity = await fixture.IdentityAsync();
        var configuration = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var operationId = AgentProtocol.NewOperationId();
        var patch = new ApplicationSettingsPatch(AutoStartMissingOnLaunch: true);
        var original = await fixture.Client.SendAsync<WorkspaceMutation<ApplicationSettings>>(
            "settings-patch", payload: patch, expectedRevision: configuration.Revision, operationId: operationId);
        var shutdownId = AgentProtocol.NewOperationId();
        await fixture.StopAsync(shutdownId);
        await fixture.StartAsync();
        var restartedIdentity = await fixture.IdentityAsync();
        Assert.NotEqual(initialIdentity.GetProperty("instanceId").GetString(), restartedIdentity.GetProperty("instanceId").GetString());
        // The original revision is now stale. Success here must be the persisted original
        // result, not a second mutation (which would get revision_conflict).
        var replay = await fixture.Client.SendAsync<WorkspaceMutation<ApplicationSettings>>(
            "settings-patch", payload: patch, expectedRevision: configuration.Revision, operationId: operationId);
        Assert.Equal(original.Revision, replay.Revision);
        Assert.Equal(original.CapturedAt, replay.CapturedAt);
        Assert.True(replay.Value.AutoStartMissingOnLaunch);
        var receipt = await fixture.Client.SendAsync<OperationReceipt>("operation", operationId);
        Assert.Equal(OperationState.Completed, receipt.State);
        _ = await fixture.Client.SendAsync<JsonElement>("shutdown", operationId: shutdownId);
        await Task.Delay(150);
        Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(restartedIdentity.GetProperty("pid").GetInt32(), (await fixture.IdentityAsync()).GetProperty("pid").GetInt32());
    }

    [Fact]
    public async Task DisconnectedWriterCanQueryAndReplayOriginalMutation()
    {
        await using var fixture = new OwnedAgent();
        await fixture.StartAsync();
        var configuration = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var operationId = AgentProtocol.NewOperationId();
        var patch = new ApplicationSettingsPatch(AutoStartMissingOnLaunch: true);
        var endpoint = AgentProtocol.ForRoot(fixture.Root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(timeout.Token);
            var request = new AgentRequest(Guid.NewGuid().ToString("N"), "settings-patch",
                Payload: JsonSerializer.SerializeToElement(patch, AgentProtocol.JsonOptions),
                TimeoutMilliseconds: 5000, RootId: endpoint.RootId, ExpectedRevision: configuration.Revision,
                OperationId: operationId);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, AgentProtocol.JsonOptions) + "\n");
            await pipe.WriteAsync(bytes, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            // Intentionally abandon the acknowledgement, not the stable operation ID.
        }

        OperationReceipt? receipt = null;
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                receipt = await fixture.Client.SendAsync<OperationReceipt>("operation", operationId,
                    timeout: TimeSpan.FromSeconds(2), cancellationToken: timeout.Token);
                if (receipt.State != OperationState.Running) { break; }
            }
            catch (AgentCommandException exception) when (exception.Code == "operation_not_found") { }
            await Task.Delay(30, timeout.Token);
        }

        Assert.NotNull(receipt);
        Assert.Equal(OperationState.Completed, receipt.State);
        Assert.True(receipt.Response!.Ok);
        var recorded = ((JsonElement)receipt.Response.Data!).Deserialize<WorkspaceMutation<ApplicationSettings>>(AgentProtocol.JsonOptions)!;
        var replay = await fixture.Client.SendAsync<WorkspaceMutation<ApplicationSettings>>(
            "settings-patch", payload: patch, expectedRevision: configuration.Revision, operationId: operationId);
        Assert.Equal(recorded.CapturedAt, replay.CapturedAt);
        Assert.Equal(recorded.Revision, replay.Revision);
        Assert.True((await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Preferences.AutoStartMissingOnLaunch);
    }

    private static async Task CreateJunctionAsync(string alias, string target)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", alias, target }) { start.ArgumentList.Add(argument); }
        using var command = Process.Start(start)!;
        try
        {
            await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, command.ExitCode);
        }
        finally
        {
            if (!command.HasExited)
            {
                command.Kill(entireProcessTree: true);
                await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private sealed class OwnedAgent : IAsyncDisposable
    {
        private Process? _process;
        public string Root { get; } = Directory.CreateTempSubdirectory("SystemAutorun-agent-process-tests-").FullName;
        public AgentClient Client => new(Root);

        public OwnedAgent()
        {
            // Empty configuration: these process tests must NEVER start a real workload.
            File.WriteAllText(Path.Combine(Root, "autorun.yaml"), "version: 1\nsettings:\n  default_startup_delay: 0\ntasks: []\n");
        }

        public static Process Launch(string root)
        {
            var executable = typeof(AgentProcessContractsTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "AgentExecutablePath").Value!;
            if (Environment.GetEnvironmentVariable("VIGIL_ISOLATED_TEST_ROOT") is { Length: > 0 } isolated)
            {
                var isolatedPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(isolated)) + Path.DirectorySeparatorChar;
                Assert.True(Path.GetFullPath(executable).StartsWith(isolatedPrefix, StringComparison.OrdinalIgnoreCase), "Owned Agent executable is outside the isolated artifacts root.");
            }
            Assert.True(File.Exists(executable), $"Build Agent before process-contract tests: {executable}");
            var start = new ProcessStartInfo(Path.GetFullPath(executable))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            };
            start.ArgumentList.Add("--root");
            start.ArgumentList.Add(root);
            start.Environment["VIGIL_ROOT"] = root;
            return Process.Start(start) ?? throw new InvalidOperationException("Owned Agent did not start.");
        }

        public async Task StartAsync()
        {
            Assert.Null(_process);
            Assert.Empty((await new ConfigurationLoader().LoadAsync(Root)).Tasks);
            _process = Launch(Root);
            await WaitUntilReadyAsync();
        }

        public AgentProcessManager CreateGuiLauncher(Action? afterStart = null) => new(Client, new ProjectPaths(Root), info =>
        {
            Assert.Null(_process);
            info.FileName = typeof(AgentProcessContractsTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "AgentExecutablePath").Value!;
            info.WorkingDirectory = Root;
            var launched = Process.Start(info) ?? throw new InvalidOperationException("Owned Agent did not start.");
            // An independent handle survives the launcher's disposal for strict fixture cleanup.
            _process = Process.GetProcessById(launched.Id);
            afterStart?.Invoke();
            return launched;
        });

        public async Task WaitUntilReadyAsync()
        {
            Assert.NotNull(_process);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (_process.HasExited)
                {
                    var fatalPath = Path.Combine(Root, "agent-fatal.log");
                    var fatal = File.Exists(fatalPath) ? await File.ReadAllTextAsync(fatalPath) : "No owned fatal log.";
                    throw new InvalidOperationException($"Isolated Agent exited {_process.ExitCode} before readiness. {fatal[..Math.Min(fatal.Length, 8000)]}");
                }
                if (await Client.PingAsync(TimeSpan.FromMilliseconds(300)))
                {
                    Assert.Equal(_process.Id, (await IdentityAsync()).GetProperty("pid").GetInt32());
                    return;
                }
                await Task.Delay(30);
            }
            throw new TimeoutException("Isolated Agent was not ready within ten seconds.");
        }

        public Task<JsonElement> IdentityAsync() => Client.SendAsync<JsonElement>("ping", timeout: TimeSpan.FromSeconds(2));

        public async Task StopAsync(string? operationId = null)
        {
            Assert.NotNull(_process);
            _ = await Client.SendAsync<JsonElement>("shutdown", timeout: TimeSpan.FromSeconds(5), operationId: operationId);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, _process.ExitCode);
            _process.Dispose();
            _process = null;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_process is not null)
                {
                    if (!_process.HasExited)
                    {
                        try { await StopAsync(); }
                        catch (Exception)
                        {
                            // Kill only the exact Process object created by this fixture.
                            if (_process is not null && !_process.HasExited)
                            {
                                _process.Kill(entireProcessTree: true);
                                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                            }
                        }
                    }
                    _process?.Dispose();
                    _process = null;
                }
            }
            finally
            {
                var resolved = Path.GetFullPath(Root);
                if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(resolved).StartsWith("SystemAutorun-agent-process-tests-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing to delete a non-owned Agent fixture.");
                }
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
