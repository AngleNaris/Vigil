using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using SystemAutorun.Cli;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.IntegrationTests;

public sealed class CliControlPlaneTests
{
    private static readonly string Revision = new('a', 64);
    private static string CliPath => typeof(CliControlPlaneTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(item => item.Key == "CliExecutablePath").Value!;

    [Fact]
    public void ConsoleEntryDoesNotReferenceWpfAndUsesConsolePe()
    {
        Assert.DoesNotContain(typeof(CliApplication).Assembly.GetReferencedAssemblies(), item =>
            item.Name is "PresentationFramework" or "PresentationCore" or "WindowsBase" or "SystemAutorun.App");
        using var file = File.OpenRead(CliPath);
        using var pe = new PEReader(file);
        Assert.Equal(Subsystem.WindowsCui, pe.PEHeaders.PEHeader!.Subsystem);
    }

    [Theory]
    [InlineData("task list", "list")]
    [InlineData("task show fixture", "status")]
    [InlineData("task start fixture", "start")]
    [InlineData("task stop fixture", "stop")]
    [InlineData("task restart fixture", "restart")]
    [InlineData("task start-missing", "start-missing")]
    [InlineData("config show", "configuration")]
    [InlineData("settings get", "settings-get")]
    [InlineData("host status", "host-status")]
    [InlineData("monitor status", "monitoring-snapshot")]
    [InlineData("recovery state", "recovery-state")]
    [InlineData("daemon status", "ping")]
    [InlineData("daemon stop", "shutdown")]
    [InlineData("operation show 01a19976949a727aa5e2b4f835695a8e", "operation")]
    [InlineData("doctor", "doctor")]
    public async Task RoutesUseOneSharedRpcAndNeverStartTheDaemon(string command, string rpc)
    {
        using var root = new TempRoot();
        var transport = new FakeTransport
        {
            Data = rpc == "recovery-state" ? new Core.Recovery.RecoveryControlSnapshot(Revision, [], false)
                : rpc == "operation" ? null : new { status = "fixture", capturedAt = DateTimeOffset.UtcNow },
            RootId = AgentProtocol.ForRoot(root.Path).RootId,
        }; var starts = 0;
        var app = new CliApplication(_ => transport, (_, _) => { starts++; return Task.CompletedTask; });
        var result = await Invoke(app, root.Path, command.Split(' '));
        Assert.Equal(0, result.Exit); Assert.Empty(result.Error);
        Assert.Equal(rpc, Assert.Single(transport.Calls).Command); Assert.Equal(0, starts);
        Assert.Equal("wire-request", result.Json.GetProperty("requestId").GetString());
        Assert.Equal(1, result.Json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(AgentProtocol.IsMutation(rpc), transport.Calls[0].OperationId is not null);
    }

    [Theory]
    [InlineData("task enable fixture", "task-enable", true)]
    [InlineData("task disable fixture", "task-enable", false)]
    [InlineData("task track fixture", "task-monitor", true)]
    [InlineData("task untrack fixture", "task-monitor", false)]
    public async Task ConfigurationFlagsCarryExplicitRevisionAndOperation(string command, string rpc, bool expected)
    {
        using var root = new TempRoot(); var transport = new FakeTransport();
        var operation = AgentProtocol.NewOperationId();
        var result = await Invoke(new(_ => transport), root.Path, [.. command.Split(' '), "--expected-revision", Revision, "--operation-id", operation]);
        Assert.Equal(0, result.Exit);
        var call = Assert.Single(transport.Calls);
        Assert.Equal(rpc, call.Command); Assert.Equal("fixture", call.Name);
        Assert.Equal(Revision, call.Revision); Assert.Equal(operation, call.OperationId);
        Assert.Equal(expected, JsonSerializer.SerializeToElement(call.Payload).GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData("task remove fixture --expected-revision", 2)]
    [InlineData("task enable fixture", 2)]
    [InlineData("task remove fixture --expected-revision {revision}", 4)]
    [InlineData("recovery resume fixture --expected-revision {revision}", 4)]
    [InlineData("task list --operation-id abc", 2)]
    [InlineData("task list --root one --root two", 2)]
    [InlineData("task list --timeout NaN", 2)]
    [InlineData("task list --timeout 601", 2)]
    [InlineData("task list --file -", 2)]
    [InlineData("monitor history fixture --window 90d", 2)]
    [InlineData("monitor watch --interval 0", 2)]
    [InlineData("monitor history fixture --buckets 1001", 2)]
    [InlineData("monitor status --count 2", 2)]
    [InlineData("startup pretend", 2)]
    [InlineData("operation show invalid-id", 2)]
    public async Task InvalidAndUnconfirmedInputsNeverReachTheAgent(string command, int exit)
    {
        using var root = new TempRoot(); var transport = new FakeTransport();
        var result = await Invoke(new(_ => transport), root.Path, command.Replace("{revision}", Revision).Split(' '));
        Assert.Equal(exit, result.Exit); Assert.Empty(transport.Calls); Assert.Empty(result.Error);
        Assert.False(result.Json.GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("revision_conflict", 5)]
    [InlineData("invalid_argument", 2)]
    [InlineData("permission_denied", 4)]
    [InlineData("operation_in_progress", 6)]
    [InlineData("operation_result_unavailable", 6)]
    [InlineData("request_timeout", 6)]
    [InlineData("operation_id_conflict", 1)]
    public async Task ErrorsAreStableJsonWithoutStderrPollution(string code, int exit)
    {
        using var root = new TempRoot();
        var transport = new FakeTransport { Error = new AgentCommandException(code, "fixture failure", Revision, "stable-operation", "wire-error") };
        var result = await Invoke(new(_ => transport), root.Path, ["task", "list"]);
        Assert.Equal(exit, result.Exit); Assert.Empty(result.Error);
        Assert.Equal(code, result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("wire-error", result.Json.GetProperty("requestId").GetString());
        Assert.Equal(Revision, result.Json.GetProperty("revision").GetString());
    }

    [Fact]
    public async Task UnknownWriteOutcomeIsNotRetriedAndRetainsCallerId()
    {
        using var root = new TempRoot(); var id = AgentProtocol.NewOperationId();
        var transport = new FakeTransport { Error = new AgentOutcomeUnknownException(id, new IOException("lost response"), "wire-lost") };
        var result = await Invoke(new(_ => transport), root.Path, ["task", "stop", "fixture", "--operation-id", id]);
        Assert.Equal(6, result.Exit); Assert.Single(transport.Calls);
        Assert.Equal(id, result.Json.GetProperty("operationId").GetString());
        Assert.Equal("wire-lost", result.Json.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task BusinessAndBatchFailuresAreNotReportedAsSuccessfulRpc()
    {
        using var root = new TempRoot();
        var transport = new FakeTransport { Data = new[] { new { name = "one", succeeded = true }, new { name = "two", succeeded = false } } };
        var result = await Invoke(new(_ => transport), root.Path, ["task", "start-missing"]);
        Assert.Equal(1, result.Exit); Assert.Equal(2, result.Json.GetProperty("data").GetArrayLength());
        Assert.Equal("execution_failed", result.Json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task BoundedTypedStdinRejectsTyposAndOversizedInputBeforeRpc()
    {
        using var root = new TempRoot(); var transport = new FakeTransport(); var app = new CliApplication(_ => transport);
        var args = new[] { "settings", "patch", "--file", "-", "--expected-revision", Revision };
        var typo = await Invoke(app, root.Path, args, "{\"autoRecoverFailedTask\":true}");
        Assert.Equal(2, typo.Exit);
        var oversized = await Invoke(app, root.Path, args, new string(' ', AgentProtocol.MaximumRequestCharacters + 1));
        Assert.Equal(2, oversized.Exit); Assert.Empty(transport.Calls);
        var valid = await Invoke(app, root.Path, args, "{\"autoRecoverFailedTasks\":false}");
        Assert.Equal(0, valid.Exit);
        Assert.False(Assert.IsType<ApplicationSettingsPatch>(Assert.Single(transport.Calls).Payload).AutoRecoverFailedTasks);
    }

    [Fact]
    public async Task NamedHistoryUsesSharedTaskIdentityAndSelectedWindow()
    {
        using var root = new TempRoot();
        await File.WriteAllTextAsync(System.IO.Path.Combine(root.Path, "autorun.yaml"),
            "version: 1\nsettings:\n  default_startup_delay: 0\ntasks:\n  - name: fixture\n    type: wsl_boot\n    distro: fixture-only\n    health:\n      type: wsl_running\n      distro: fixture-only\n");
        var configuration = await new ConfigurationLoader().LoadAsync(root.Path);
        var task = Assert.Single(configuration.Tasks);
        var transport = new FakeTransport { Data = new ConfigurationWorkspaceSnapshot(Revision, DateTimeOffset.UtcNow,
            configuration, new()) };
        var result = await Invoke(new(_ => transport), root.Path, ["monitor", "history", "fixture", "--window", "7d"]);
        Assert.Equal(0, result.Exit); Assert.Equal(2, transport.Calls.Count);
        var query = Assert.IsType<MonitoringHistoryQuery>(transport.Calls[1].Payload);
        Assert.Equal(MonitoringTaskIdentity.For(task), query.TaskIdentity);
        Assert.Equal(TimeSpan.FromDays(7), query.To - query.From);
    }

    [Fact]
    public async Task WatchWritesOneJsonEnvelopePerPollAndCancellationDoesNotStopDaemon()
    {
        using var root = new TempRoot(); using var cancelled = new CancellationTokenSource();
        var transport = new FakeTransport(); transport.AfterCall = () => cancelled.Cancel();
        var result = await Invoke(new(_ => transport), root.Path, ["monitor", "watch"], cancellationToken: cancelled.Token);
        Assert.Equal(0, result.Exit); Assert.Single(transport.Calls);
        Assert.Equal("monitoring-snapshot", transport.Calls[0].Command);
        Assert.True(result.Json.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task DaemonIsStartedOnlyByAnExplicitStart()
    {
        using var root = new TempRoot(); var transport = new FakeTransport(); var starts = 0;
        var app = new CliApplication(_ => transport, (paths, _) => { Assert.Equal(root.Path, paths.RootDirectory); starts++; return Task.CompletedTask; });
        Assert.Equal(0, (await Invoke(app, root.Path, ["daemon", "start"])).Exit);
        Assert.Equal(1, starts); Assert.Equal("ping", Assert.Single(transport.Calls).Command);
    }

    [Theory]
    [InlineData("host list")]
    [InlineData("host show fixture")]
    [InlineData("monitor history fixture")]
    public async Task DerivedReadCommandsPreserveRawBusinessFailureMetadata(string command)
    {
        using var root = new TempRoot(); var transport = new FakeTransport { ResponseErrorCode = "permission_denied", Data = null };
        var result = await Invoke(new(_ => transport), root.Path, command.Split(' '));
        Assert.Equal(4, result.Exit); Assert.Equal("permission_denied", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("wire-request", result.Json.GetProperty("requestId").GetString());
        Assert.Equal(Revision, result.Json.GetProperty("revision").GetString()); Assert.Single(transport.Calls);
    }

    [Fact]
    public async Task HeartbeatIsNotMisrepresentedAsBusinessObservationTime()
    {
        using var root = new TempRoot();
        var transport = new FakeTransport { Data = new { heartbeatAt = DateTimeOffset.UtcNow, tasks = new { available = false } } };
        var result = await Invoke(new(_ => transport), root.Path, ["monitor", "status"]);
        Assert.Equal(0, result.Exit); Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("observedAt").ValueKind);
        Assert.False(result.Json.GetProperty("data").GetProperty("tasks").GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task CancelledReadinessCannotLaunchADaemon()
    {
        using var root = new TempRoot(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var launches = 0;
        var launcher = new AgentDaemonLauncher(new AgentClient(root.Path), new ProjectPaths(root.Path), _ => { launches++; return null; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.EnsureRunningAsync(cancelled.Token));
        Assert.Equal(0, launches);
    }

    [Theory]
    [InlineData("daemon", 1)]
    [InlineData("task", 0)]
    public async Task OnlyHistoricalShutdownReceiptsCannotClaimCurrentStop(string domain, int expectedExit)
    {
        using var root = new TempRoot(); var transport = new FakeTransport { Replayed = true };
        var result = await Invoke(new(_ => transport), root.Path, domain == "daemon" ? ["daemon", "stop"] : ["task", "stop", "fixture"]);
        Assert.Equal(expectedExit, result.Exit); Assert.True(result.Json.GetProperty("replayed").GetBoolean());
        Assert.Single(transport.Calls);
    }

    [Fact]
    public async Task ConsoleDaemonStartReleasesCapturedHandlesAndOldStopCannotStopANewInstance()
    {
        using var root = new TempRoot(); var client = new AgentClient(root.Path);
        try
        {
            var first = await ProcessCli(root.Path, ["daemon", "start"]);
            Assert.Equal(0, first.Exit);
            Assert.True(await client.PingAsync(TimeSpan.FromSeconds(2)));
            var firstIdentity = await client.SendAsync<JsonElement>("ping");
            using var firstProcess = Process.GetProcessById(firstIdentity.GetProperty("pid").GetInt32());
            var id = AgentProtocol.NewOperationId();
            var stop = await ProcessCli(root.Path, ["daemon", "stop", "--operation-id", id]);
            Assert.Equal(0, stop.Exit);
            await firstProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var restarted = await ProcessCli(root.Path, ["daemon", "start"]);
            Assert.Equal(0, restarted.Exit);
            var secondIdentity = await client.SendAsync<JsonElement>("ping");
            Assert.NotEqual(firstIdentity.GetProperty("instanceId").GetString(), secondIdentity.GetProperty("instanceId").GetString());
            var old = await ProcessCli(root.Path, ["daemon", "stop", "--operation-id", id]);
            Assert.Equal(1, old.Exit); Assert.True(old.Json.GetProperty("replayed").GetBoolean());
            Assert.Equal("shutdown_already_consumed", old.Json.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(secondIdentity.GetProperty("pid").GetInt32(), (await client.SendAsync<JsonElement>("ping")).GetProperty("pid").GetInt32());
        }
        finally
        {
            if (await client.PingAsync(TimeSpan.FromSeconds(2)))
            {
                var identity = await client.SendAsync<JsonElement>("ping");
                using var owned = Process.GetProcessById(identity.GetProperty("pid").GetInt32());
                await client.SendAsync<JsonElement>("shutdown");
                await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public async Task SeparateConsoleProcessUsesRealPipeAndPreservesConflictAndReplayMetadata()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var current = await ProcessCli(fixture.Root, ["config", "show"]);
        Assert.Equal(0, current.Exit);
        var revision = current.Json.GetProperty("revision").GetString()!;
        var id = AgentProtocol.NewOperationId();
        var args = new[] { "settings", "patch", "--file", "-", "--expected-revision", revision, "--operation-id", id };
        var first = await ProcessCli(fixture.Root, args, "{\"autoRecoverFailedTasks\":true}");
        Assert.Equal(0, first.Exit); Assert.Equal(id, first.Json.GetProperty("operationId").GetString());
        Assert.NotEqual(revision, first.Json.GetProperty("revision").GetString());
        var replay = await ProcessCli(fixture.Root, args, "{\"autoRecoverFailedTasks\":true}");
        Assert.Equal(0, replay.Exit); Assert.True(replay.Json.GetProperty("replayed").GetBoolean());
        Assert.Equal(first.Json.GetProperty("revision").GetString(), replay.Json.GetProperty("revision").GetString());
        var conflictArgs = args.ToArray(); conflictArgs[^1] = AgentProtocol.NewOperationId();
        var conflict = await ProcessCli(fixture.Root, conflictArgs, "{\"autoRecoverFailedTasks\":false}");
        Assert.Equal(5, conflict.Exit);
        var replayedFailure = await ProcessCli(fixture.Root, conflictArgs, "{\"autoRecoverFailedTasks\":false}");
        Assert.Equal(5, replayedFailure.Exit); Assert.True(replayedFailure.Json.GetProperty("replayed").GetBoolean());
        Assert.Equal(conflict.Json.GetProperty("operationId").GetString(), replayedFailure.Json.GetProperty("operationId").GetString());
        var saved = await new ConfigurationWorkspace(fixture.Paths).ReadAsync();
        Assert.True(saved.Preferences.AutoRecoverFailedTasks);
        Assert.Equal(saved.Revision, conflict.Json.GetProperty("revision").GetString());
        var receipt = await ProcessCli(fixture.Root, ["operation", "show", id]);
        Assert.Equal(0, receipt.Exit); Assert.Equal("completed", receipt.Json.GetProperty("data").GetProperty("state").GetString());
    }

    private static async Task<Output> Invoke(CliApplication app, string root, string[] args, string stdin = "", CancellationToken cancellationToken = default)
    {
        using var input = new StringReader(stdin); using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await app.RunAsync([.. args, "--root", root, "--json"], input, output, error, cancellationToken);
        return new(exit, Parse(output.ToString()), error.ToString());
    }

    private static async Task<Output> ProcessCli(string root, string[] args, string stdin = "")
    {
        var info = new ProcessStartInfo(CliPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in args.Concat(["--root", root, "--json"])) { info.ArgumentList.Add(argument); }
        using var process = Process.Start(info)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(stdin); process.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, Parse(await stdout.WaitAsync(deadline.Token)), await stderr.WaitAsync(deadline.Token));
        }
        finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
    }

    private static JsonElement Parse(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private sealed record Output(int Exit, JsonElement Json, string Error);
    private sealed class FakeTransport : ICliTransport
    {
        public List<CliRequest> Calls { get; } = [];
        public object? Data { get; init; } = new { status = "fixture", capturedAt = DateTimeOffset.UtcNow };
        public string RootId { get; init; } = "fixture-root";
        public bool Replayed { get; init; }
        public string? ResponseErrorCode { get; init; }
        public Exception? Error { get; init; }
        public Action? AfterCall { get; set; }
        public Task<AgentResponse> SendAsync(CliRequest request, CancellationToken cancellationToken)
        {
            Calls.Add(request); AfterCall?.Invoke();
            return Error is not null ? Task.FromException<AgentResponse>(Error)
                : Task.FromResult(new AgentResponse("wire-request", ResponseErrorCode is null, Data, Error: ResponseErrorCode is null ? null : "fixture permission failure",
                    RootId: RootId, Revision: Revision, ErrorCode: ResponseErrorCode, OperationId: request.OperationId, Replayed: Replayed));
        }
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("SystemAutorun-cli-tests-").FullName;
        public TempRoot() => File.WriteAllText(System.IO.Path.Combine(Path, "autorun.yaml"), "version: 1\nsettings:\n  default_startup_delay: 0\ntasks: []\n");
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            Assert.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), resolved, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("SystemAutorun-cli-tests-", System.IO.Path.GetFileName(resolved), StringComparison.Ordinal);
            Directory.Delete(resolved, true);
        }
    }
}
