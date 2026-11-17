using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Agent;
using SystemAutorun.Cli;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;
using SystemAutorun.Core.Services;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.IntegrationTests;

public sealed class CliMonitoringFilterTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Names = ["Alpha", "Alphabeta", "服务 空格"];
    private static TaskSnapshot TaskRow(string name) => new("OK", true, true, name, "windows", "fixture", "fixture", "synthetic", 0,
        "fixture-never-execute.exe", [], null, null, null, [], LaunchOptions.LegacyDefault(AutorunTaskType.Windows));
    private static JsonObject Snapshot()
    {
        var tasks = Names.Select(TaskRow).ToArray();
        var processes = tasks.Select(task => new ManagedProcessSnapshot(task.Name, "fixture", "windows", true, true, true,
            "fixture", "fixture", "synthetic", At, [new(123, "fixture", null, At.AddHours(-1), 0, 100, 100, 1)], true, MonitoringTaskIdentity.For(task))).ToArray();
        var runtime = tasks.Select(task => new TaskRuntimeObservation(task.Name, MonitoringTaskIdentity.For(task), At,
            At.AddHours(-1), "windows-process-observed-instances", "123:fixture", At.AddSeconds(-10), "consecutive-passive-health-observations")).ToArray();
        var data = (JsonObject)JsonSerializer.SerializeToNode(new AgentMonitoringSnapshot(1, "fixture-root", Guid.NewGuid().ToString("N"), 123, "fixture", At.AddHours(-2), At, At,
            "fixture-revision", 10, new(true, At, At, tasks, null), new(true, At, At, processes, null),
            new(false, null, At, null, "fixture-no-host"), runtime, new(false, null, "fixture-no-history", 1024, 30)), AgentProtocol.JsonOptions)!;
        data["extension"] = new JsonObject { ["future"] = new JsonArray(1, 2, 3) };
        data["tasks"]!["extension"] = "retain group extension";
        data["tasks"]!["data"]![0]!["extension"] = "retain row extension";
        return data;
    }

    [Theory]
    [InlineData("aLpHa", "Alpha")][InlineData("服务 空格", "服务 空格")]
    public async Task ProjectionChangesOnlyThreeRowArraysAndPreservesOriginalEnvelopeAndExtensions(string selector, string expected)
    {
        using var root = new Root(); var source = Snapshot(); var original = source.ToJsonString(); var transport = new Transport(_ => source); var starts = 0;
        var app = new CliApplication(_ => transport, (_, _) => { starts++; return Task.CompletedTask; });
        var result = await Invoke(app, root.Path, ["monitor", "status", "--task", selector, "--json"]);
        Assert.Equal(0, result.Exit); Assert.Empty(result.Error); var envelope = Parse(result.Text); var data = envelope.GetProperty("data");
        Assert.True(envelope.GetProperty("ok").GetBoolean()); Assert.Equal("fixture-request", envelope.GetProperty("requestId").GetString());
        Assert.Equal("fixture-revision", envelope.GetProperty("revision").GetString()); Assert.True(envelope.GetProperty("replayed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("operationId").ValueKind);
        var projected = JsonNode.Parse(data.GetRawText())!.AsObject(); var expectedNode = source.DeepClone().AsObject();
        foreach (var (group, property) in new[] { ("tasks", "name"), ("processes", "taskName") })
        {
            var rows = expectedNode[group]!["data"]!.AsArray();
            expectedNode[group]!["data"] = new JsonArray(rows.Where(row => row![property]!.GetValue<string>() == expected).Select(row => row!.DeepClone()).ToArray());
            Assert.Equal(expected, Assert.Single(data.GetProperty(group).GetProperty("data").EnumerateArray()).GetProperty(property).GetString());
        }
        expectedNode["runtime"] = new JsonArray(expectedNode["runtime"]!.AsArray().Where(row => row!["taskName"]!.GetValue<string>() == expected).Select(row => row!.DeepClone()).ToArray());
        Assert.True(JsonNode.DeepEquals(expectedNode, projected)); Assert.Equal(original, source.ToJsonString()); Assert.Equal(0, starts);
        var call = Assert.Single(transport.Calls); Assert.Equal("monitoring-snapshot", call.Command);
        Assert.Null(call.Name); Assert.Null(call.Payload); Assert.Null(call.OperationId); Assert.Null(call.Revision); Assert.False(AgentProtocol.IsMutation(call.Command));
        Assert.Single(Directory.GetFiles(root.Path));
    }

    [Fact]
    public async Task UnfilteredSnapshotRemainsExactlyTheOriginalData()
    {
        using var root = new Root(); var source = Snapshot(); var result = await Invoke(new(_ => new Transport(_ => source)), root.Path, ["monitor", "status", "--json"]);
        Assert.Equal(0, result.Exit); Assert.True(JsonNode.DeepEquals(source, JsonNode.Parse(Parse(result.Text).GetProperty("data").GetRawText())));
    }

    [Theory]
    [InlineData("cached")][InlineData("null")][InlineData("empty")][InlineData("missing")]
    public async Task UnavailableSourcePreservesDiagnosticMetadataAndCannotProveNameAbsent(string kind)
    {
        using var root = new Root(); var source = Snapshot(); source["tasks"]!["available"] = false; source["tasks"]!["error"] = "fixture-unavailable";
        if (kind == "null") { source["tasks"]!["data"] = null; }
        if (kind == "empty") { source["tasks"]!["data"] = new JsonArray(); }
        var selector = kind == "missing" ? "missing" : "Alpha";
        var app = new CliApplication(_ => new Transport(_ => source)); var result = await Invoke(app, root.Path, ["monitor", "status", "--task", selector, "--json"]);
        Assert.Equal(0, result.Exit); var data = Parse(result.Text).GetProperty("data");
        Assert.False(data.GetProperty("tasks").GetProperty("available").GetBoolean());
        Assert.Equal("fixture-unavailable", data.GetProperty("tasks").GetProperty("error").GetString());
        Assert.Equal(At, data.GetProperty("tasks").GetProperty("observedAt").GetDateTimeOffset());
        if (kind == "null") { Assert.Equal(JsonValueKind.Null, data.GetProperty("tasks").GetProperty("data").ValueKind); }
        var human = await Invoke(app, root.Path, ["monitor", "status", "--task", selector]); Assert.Equal(0, human.Exit);
        Assert.DoesNotContain("process-uptime: 01:00:00", human.Text); Assert.Contains("not proof of existence or health", human.Text);
    }

    [Theory]
    [InlineData("missing")][InlineData("Alph")][InlineData("Alpha*")][InlineData(" Alpha ")]
    public async Task ExactNameMissIsAnEmptyViewNotHealthOrNotFound(string selector)
    {
        using var root = new Root(); var source = Snapshot(); var result = await Invoke(new(_ => new Transport(_ => source)), root.Path, ["monitor", "status", "--task", selector, "--json"]);
        Assert.Equal(0, result.Exit); var data = Parse(result.Text).GetProperty("data");
        Assert.True(data.GetProperty("tasks").GetProperty("available").GetBoolean());
        Assert.Empty(data.GetProperty("tasks").GetProperty("data").EnumerateArray()); Assert.Empty(data.GetProperty("processes").GetProperty("data").EnumerateArray()); Assert.Empty(data.GetProperty("runtime").EnumerateArray());
    }

    [Fact]
    public async Task SameNameOldTargetIdentityIsNotIncludedAndWatchReappliesProjectionEachFrame()
    {
        using var root = new Root(); var first = Snapshot();
        var oldProcess = first["processes"]!["data"]![0]!.DeepClone(); oldProcess["taskIdentity"] = new string('f', 64); first["processes"]!["data"]!.AsArray().Add(oldProcess);
        var oldRuntime = first["runtime"]![0]!.DeepClone(); oldRuntime["taskIdentity"] = new string('f', 64); first["runtime"]!.AsArray().Add(oldRuntime);
        var second = Snapshot(); second["tasks"]!["data"]!.AsArray().RemoveAt(0);
        var transport = new Transport(index => index == 0 ? first : second);
        var result = await Invoke(new(_ => transport), root.Path, ["monitor", "watch", "--task", "Alpha", "--count", "2", "--interval", "1", "--json"]);
        Assert.Equal(0, result.Exit); var lines = result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries); Assert.Equal(2, lines.Length); Assert.Equal(2, transport.Calls.Count);
        var a = Parse(lines[0]).GetProperty("data"); var b = Parse(lines[1]).GetProperty("data");
        Assert.Single(a.GetProperty("processes").GetProperty("data").EnumerateArray()); Assert.Single(a.GetProperty("runtime").EnumerateArray());
        Assert.Empty(b.GetProperty("tasks").GetProperty("data").EnumerateArray()); Assert.Empty(b.GetProperty("processes").GetProperty("data").EnumerateArray()); Assert.Empty(b.GetProperty("runtime").EnumerateArray());
    }

    [Theory]
    [InlineData("schema")][InlineData("tasks")][InlineData("processes")][InlineData("runtime")]
    [InlineData("badTasks")][InlineData("available")][InlineData("availableNullData")][InlineData("taskName")]
    [InlineData("launch")][InlineData("arguments")][InlineData("identityField")][InlineData("runAs")][InlineData("duplicateName")][InlineData("badProcessRow")][InlineData("badRuntimeRow")]
    public async Task CannotApplySelectionToIncompatibleSuccessInsteadOfReturningUnfilteredOrFakeEmpty(string kind)
    {
        using var root = new Root(); var source = Snapshot();
        switch (kind)
        {
            case "schema": source["schemaVersion"] = 2; break;
            case "tasks": case "processes": case "runtime": source.Remove(kind); break;
            case "badTasks": source["tasks"]!["data"] = "bad"; break;
            case "available": source["tasks"]!["available"] = "bad"; break;
            case "availableNullData": source["tasks"]!["data"] = null; break;
            case "taskName": source["tasks"]!["data"]![0]!["name"] = null; break;
            case "launch": source["tasks"]!["data"]![0]!["launch"] = null; break;
            case "arguments": source["tasks"]!["data"]![0]!["arguments"] = null; break;
            case "identityField": source["tasks"]!["data"]![0]!.AsObject().Remove("health"); break;
            case "runAs": source["tasks"]!["data"]![0]!["launch"]!.AsObject().Remove("runAs"); break;
            case "duplicateName": source["tasks"]!["data"]!.AsArray().Add(source["tasks"]!["data"]![0]!.DeepClone()); break;
            case "badProcessRow": source["processes"]!["data"]![0] = null; break;
            case "badRuntimeRow": source["runtime"]![0]!["taskName"] = 123; break;
        }
        var result = await Invoke(new(_ => new Transport(_ => source)), root.Path, ["monitor", "status", "--task", "Alpha", "--json"]);
        Assert.Equal(3, result.Exit); var envelope = Parse(result.Text);
        Assert.Equal("protocol_mismatch", envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("fixture-request", envelope.GetProperty("requestId").GetString()); Assert.Equal(JsonValueKind.Null, envelope.GetProperty("data").ValueKind);
    }

    [Theory]
    [InlineData("type")][InlineData("pattern")][InlineData("host")][InlineData("port")]
    [InlineData("timeoutSeconds")][InlineData("distro")][InlineData("service")]
    public async Task MissingNestedIdentityInputCannotTurnIntoDefaultedIdentityOrEmptySuccess(string missing)
    {
        using var root = new Root(); var source = Snapshot();
        source["tasks"]!["data"]![0]!["health"] = JsonSerializer.SerializeToNode(new HealthCheckDefinition(HealthCheckType.Process, "fixture", null, null, 30, null, null), AgentProtocol.JsonOptions);
        var identity = MonitoringTaskIdentity.For(source["tasks"]!["data"]![0]!.Deserialize<TaskSnapshot>(AgentProtocol.JsonOptions)!);
        source["processes"]!["data"]![0]!["taskIdentity"] = identity; source["runtime"]![0]!["taskIdentity"] = identity;
        source["tasks"]!["data"]![0]!["health"]!.AsObject().Remove(missing);
        var result = await Invoke(new(_ => new Transport(_ => source)), root.Path, ["monitor", "status", "--task", "Alpha", "--json"]);
        Assert.Equal(3, result.Exit); var envelope = Parse(result.Text);
        Assert.Equal("protocol_mismatch", envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("fixture-request", envelope.GetProperty("requestId").GetString()); Assert.Equal(JsonValueKind.Null, envelope.GetProperty("data").ValueKind);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task UnavailableProcessGroupKeepsItsNullOrDiagnosticRowsAndOriginalMetadata(bool hasCache)
    {
        using var root = new Root(); var source = Snapshot(); source["processes"]!["available"] = false; source["processes"]!["error"] = "fixture-process-unavailable";
        if (!hasCache) { source["processes"]!["data"] = null; }
        var result = await Invoke(new(_ => new Transport(_ => source)), root.Path, ["monitor", "status", "--task", "Alpha", "--json"]);
        Assert.Equal(0, result.Exit); var group = Parse(result.Text).GetProperty("data").GetProperty("processes");
        Assert.False(group.GetProperty("available").GetBoolean()); Assert.Equal("fixture-process-unavailable", group.GetProperty("error").GetString());
        Assert.Equal(At, group.GetProperty("observedAt").GetDateTimeOffset()); Assert.Equal(At, group.GetProperty("attemptedAt").GetDateTimeOffset());
        if (hasCache) { Assert.Single(group.GetProperty("data").EnumerateArray()); }
        else { Assert.Equal(JsonValueKind.Null, group.GetProperty("data").ValueKind); }
    }

    [Theory]
    [InlineData("--yes")][InlineData("--file=-")][InlineData("--expected-revision=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("--operation-id=019a0000000070008000000000000000")][InlineData("--window=24h")][InlineData("--export=never.ndjson")]
    [InlineData("--source=control:agent")][InlineData("--search=Alpha")][InlineData("--task=Beta")][InlineData("--count=2")][InlineData("--task=")]
    public async Task UnsupportedOrDuplicateOptionsFailBeforeConnection(string option)
    {
        using var root = new Root(); var connections = 0; var app = new CliApplication(_ => { connections++; throw new InvalidOperationException("Must not connect"); });
        var result = await Invoke(app, root.Path, ["monitor", "status", "--task", "Alpha", option, "--json"]);
        Assert.Equal(2, result.Exit); Assert.Equal(0, connections); Assert.Equal("invalid_argument", Parse(result.Text).GetProperty("error").GetProperty("code").GetString());
        Assert.Single(Directory.GetFiles(root.Path));
    }

    [Fact]
    public async Task AgentErrorPassesThroughWithoutTryingToProjectItsData()
    {
        using var root = new Root(); var result = await Invoke(new(_ => new Transport(_ => new { wrong = true }, "permission_denied")), root.Path, ["monitor", "status", "--task", "Alpha", "--json"]);
        Assert.Equal(4, result.Exit); var envelope = Parse(result.Text); Assert.Equal("permission_denied", envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.True(envelope.GetProperty("data").GetProperty("wrong").GetBoolean());
    }

    [Fact]
    public void TaskOptionRemainsValidForLogsAndNotHistoryOrUnrelatedRoutes()
    {
        Assert.Equal("Alpha", CliArguments.Parse(["logs", "--task", "Alpha"]).Option("task"));
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(["monitor", "history", "Alpha", "--task", "Alpha"]));
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(["host", "status", "--task", "Alpha"]));
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(["monitor", "status", "--task"]));
        Assert.Contains("monitor status [--task NAME]", CliApplication.HelpText);
    }

    [Fact]
    public async Task IndependentConsoleProjectsRealMonitorWithoutResamplingWritesOrWorkloadExecution()
    {
        await using var fixture = new Fixture(); await fixture.Monitor.SampleOnceAsync(); fixture.Clock.Now = At.AddSeconds(10); await fixture.Monitor.SampleOnceAsync();
        var snapshot = fixture.Monitor.GetSnapshot(); Assert.True(snapshot.Tasks.Available, JsonSerializer.Serialize(snapshot, AgentProtocol.JsonOptions)); Assert.Equal(3, snapshot.Runtime.Count);
        var before = Fingerprint(fixture.Root); var calls = fixture.Source.Calls;
        var full = await ProcessCli(fixture.Root, ["monitor", "status", "--json"]); Assert.Equal(0, full.Exit); Assert.Equal(3, Parse(full.Text).GetProperty("data").GetProperty("runtime").GetArrayLength());
        var selected = await ProcessCli(fixture.Root, ["monitor", "status", "--task", "aLpHa", "--json"]); Assert.Equal(0, selected.Exit); Assert.Empty(selected.Error);
        var data = Parse(selected.Text).GetProperty("data"); Assert.Equal("Alpha", Assert.Single(data.GetProperty("runtime").EnumerateArray()).GetProperty("taskName").GetString());
        var original = JsonSerializer.SerializeToElement(snapshot, AgentProtocol.JsonOptions);
        foreach (var field in new[] { "host", "persistence", "rootId", "instanceId", "heartbeatAt", "returnedAt", "configurationRevision" })
        { Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original.GetProperty(field).GetRawText()), JsonNode.Parse(data.GetProperty(field).GetRawText())), field); }
        var human = await ProcessCli(fixture.Root, ["monitor", "status", "--task", "服务 空格"]); Assert.Equal(0, human.Exit);
        Assert.True(human.Text.Contains("runtime: 服务 空格", StringComparison.Ordinal), human.Text); Assert.DoesNotContain("runtime: Alpha", human.Text); Assert.Contains("process-uptime: 01:00:10", human.Text);
        var watch = await ProcessCli(fixture.Root, ["monitor", "watch", "--task", "Alpha", "--json", "--count", "2", "--interval", "1"]); Assert.Equal(0, watch.Exit);
        var lines = watch.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries); Assert.Equal(2, lines.Length);
        foreach (var line in lines) { Assert.Equal("Alpha", Assert.Single(Parse(line).GetProperty("data").GetProperty("runtime").EnumerateArray()).GetProperty("taskName").GetString()); }
        var missing = await ProcessCli(fixture.Root, ["monitor", "status", "--task", "absent", "--json"]); Assert.Equal(0, missing.Exit); Assert.Empty(Parse(missing.Text).GetProperty("data").GetProperty("runtime").EnumerateArray());
        fixture.Clock.Now = At.AddSeconds(41);
        var stale = await ProcessCli(fixture.Root, ["monitor", "status", "--task", "Alpha", "--json"]); Assert.Equal(0, stale.Exit);
        var cached = Parse(stale.Text).GetProperty("data"); Assert.False(cached.GetProperty("tasks").GetProperty("available").GetBoolean()); Assert.Single(cached.GetProperty("tasks").GetProperty("data").EnumerateArray()); Assert.Empty(cached.GetProperty("runtime").EnumerateArray());
        Assert.Equal(calls, fixture.Source.Calls); Assert.Equal(before, Fingerprint(fixture.Root)); Assert.Empty(fixture.Errors);
        Assert.True((await new AgentClient(fixture.Root).SendResponseAsync("ping")).Ok);
    }

    private static async Task<(int Exit, string Text, string Error)> Invoke(CliApplication app, string root, string[] arguments)
    { using var output = new StringWriter(); using var error = new StringWriter(); var code = await app.RunAsync([.. arguments, "--root", root, "--non-interactive"], TextReader.Null, output, error); return (code, output.ToString(), error.ToString()); }
    private static JsonElement Parse(string text) { using var json = JsonDocument.Parse(text); return json.RootElement.Clone(); }
    private sealed class Transport(Func<int, object?> source, string? error = null) : ICliTransport
    {
        public List<CliRequest> Calls { get; } = [];
        public Task<AgentResponse> SendAsync(CliRequest request, CancellationToken token)
        { var index = Calls.Count; Calls.Add(request); return Task.FromResult(new AgentResponse("fixture-request", error is null, source(index), Error: error, ErrorCode: error, Revision: "fixture-revision", Replayed: true)); }
    }
    private sealed class Root : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("SystemAutorun-cli-filter-").FullName;
        public Root() => File.WriteAllText(System.IO.Path.Combine(Path, "autorun.yaml"), "version: 1\ntasks: []\n");
        public void Dispose() => DeleteRoot(Path);
    }
    private static void DeleteRoot(string root)
    {
        var full = Path.GetFullPath(root);
        if (Path.GetDirectoryName(full) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("SystemAutorun-cli-filter-", StringComparison.Ordinal)) { throw new InvalidOperationException("Non-owned filter root."); }
        Directory.Delete(full, recursive: true);
    }
    private static string[] Fingerprint(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => path + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    private static async Task<(int Exit, string Text, string Error)> ProcessCli(string root, string[] arguments)
    {
        var path = typeof(CliMonitoringFilterTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == "CliExecutablePath").Value!;
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false, true), StandardErrorEncoding = new System.Text.UTF8Encoding(false, true) };
        foreach (var arg in arguments.Concat(["--root", root, "--timeout", "5", "--non-interactive"])) { info.ArgumentList.Add(arg); }
        using var process = Process.Start(info)!;
        try { var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); return (process.ExitCode, await stdout, await stderr); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = At; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Source(Clock clock) : IAgentMonitoringSource
    {
        public int Calls;
        public Task<IReadOnlyList<TaskSnapshot>> ObserveTasksAsync(AutorunConfiguration config, CancellationToken token)
        { Calls++; return Task.FromResult<IReadOnlyList<TaskSnapshot>>(config.Tasks.Select(task => TaskSnapshot.From(task, HealthResult.Ok("fixture", "synthetic"))).ToArray()); }
        public Task<IReadOnlyList<ManagedProcessSnapshot>> ObserveProcessesAsync(AutorunConfiguration config, IReadOnlyList<TaskSnapshot> tasks, CancellationToken token)
        { Calls++; return Task.FromResult<IReadOnlyList<ManagedProcessSnapshot>>(tasks.Select(task => new ManagedProcessSnapshot(task.Name, "fixture", task.Type, true, true, true, "fixture", "fixture", "synthetic", clock.Now, [new(123, "fixture", null, At.AddHours(-1), 0, 100, 100, 1)], true, MonitoringTaskIdentity.For(task))).ToArray()); }
        public Task<HostStatusSnapshot> ObserveHostAsync(CancellationToken token)
        { Calls++; return Task.FromResult(new HostStatusSnapshot("fixture", "fixture", clock.Now, TimeSpan.FromHours(3), 0, 1000, 500, [], [])); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("SystemAutorun-cli-filter-").FullName;
        public Clock Clock { get; } = new(); public Source Source { get; } public AgentMonitor Monitor { get; }
        public System.Collections.Concurrent.ConcurrentQueue<Exception> Errors { get; } = new();
        private readonly CancellationTokenSource _shutdown = new(); private readonly Task _server;
        public Fixture()
        {
            try
            {
                var paths = new ProjectPaths(Root); File.WriteAllText(paths.ConfigPath, "version: 1\nsettings:\n  default_startup_delay: 0\ntasks:\n" + string.Concat(Names.Select(name => $"  - name: '{name}'\n    type: windows\n    exec: fixture-never-execute.exe\n    expected_running: true\n    health:\n      type: process\n      pattern: fixture-never-execute\n")));
                var workspace = new ConfigurationWorkspace(paths); var operations = new OperationJournal(paths); Source = new(Clock);
                Monitor = new(workspace, Source, operations.InstanceId, null, clock: Clock);
                var orchestrator = new TaskOrchestrator(new ConfigurationCache(new ConfigurationLoader(), paths), new OverrideStore(), new NeverExecute(), new NeverProbe(), new FileLog());
                var dispatcher = new AgentCommandDispatcher(orchestrator, new HostStatusReader(), workspace, Monitor, operations, errorObserver: (_, error) => Errors.Enqueue(error));
                _server = new AgentServer(dispatcher, _shutdown, Root).RunAsync(_shutdown.Token);
            }
            catch { Monitor?.Dispose(); _shutdown.Dispose(); DeleteRoot(Root); throw; }
        }
        public async ValueTask DisposeAsync()
        { try { _shutdown.Cancel(); await _server.WaitAsync(TimeSpan.FromSeconds(10)); } finally { Monitor.Dispose(); _shutdown.Dispose(); DeleteRoot(Root); } }
    }
    private sealed class NeverProbe : IHealthProbe
    { public Task<HealthResult> CheckAsync(AutorunTask task, bool quick, CancellationToken token = default) => throw new InvalidOperationException("No live probes."); }
    private sealed class NeverExecute : ITaskExecutor
    {
        public Task<CommandResult> StartAsync(AutorunTask task, CancellationToken token = default) => throw new InvalidOperationException("No workload start.");
        public Task<CommandResult> StopAsync(AutorunTask task, CancellationToken token = default) => throw new InvalidOperationException("No workload stop.");
        public IReadOnlyList<int> FindProcessIds(AutorunTask task) => throw new InvalidOperationException("No process inspection.");
    }
}
