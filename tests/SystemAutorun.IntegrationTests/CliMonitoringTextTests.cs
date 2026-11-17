using System.Diagnostics;
using System.IO;
using SystemAutorun.Windows.Health;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SystemAutorun.Agent;
using SystemAutorun.Cli;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;
using SystemAutorun.Core.Services;
using Xunit;

namespace SystemAutorun.IntegrationTests;

public sealed partial class CliMonitoringTextTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("windows", "01:00:00")]
    [InlineData("windows_service", "unknown")]
    [InlineData("wsl_command", "unknown")]
    [InlineData("wsl_systemd", "unknown")]
    public async Task DistinctUptimeMeaningsUseObservationTimeNotCurrentClock(string type, string expected)
    {
        var snapshot = Snapshot(type); var text = await Text(snapshot);
        Assert.Contains($"process-uptime: {expected}", text);
        Assert.Contains("continuous-health: 00:00:10", text);
        Assert.Contains($"observedAt: {At:O}", text);
        Assert.Contains("not service uptime", text); Assert.Contains("no extrapolation", text);
        Assert.Contains("windows-process-observed-instances", text);
        Assert.Contains(snapshot.Runtime[0].TaskIdentity, text);
        var original = JsonSerializer.SerializeToElement(snapshot, AgentProtocol.JsonOptions);
        foreach (var group in new[] { "tasks", "processes", "host", "persistence" })
        { Assert.Contains($"{group}: {original.GetProperty(group)}", text); }
    }

    [Theory]
    [InlineData("task-unavailable")][InlineData("task-null")][InlineData("task-unobserved")]
    [InlineData("task-failed")][InlineData("task-disabled")][InlineData("future-row")]
    [InlineData("before-task-sample")][InlineData("unknown-health-source")][InlineData("future-since")]
    public async Task UncertainHealthCannotLookLikeAContinuousHealthyRun(string kind)
    {
        var snapshot = Snapshot(); var task = snapshot.Tasks.Data!.Single(); var row = snapshot.Runtime.Single();
        snapshot = kind switch
        {
            "task-unavailable" => snapshot with { Tasks = snapshot.Tasks with { Available = false, Error = "stale-fixture" } },
            "task-null" => snapshot with { Tasks = snapshot.Tasks with { Data = null } },
            "task-unobserved" => snapshot with { Tasks = snapshot.Tasks with { Data = [task with { HealthObserved = false }] } },
            "task-failed" => snapshot with { Tasks = snapshot.Tasks with { Data = [task with { Healthy = false }] } },
            "task-disabled" => snapshot with { Tasks = snapshot.Tasks with { Data = [task with { Enabled = false }] } },
            "future-row" => snapshot with { Runtime = [row with { ObservedAt = At.AddSeconds(1) }] },
            "before-task-sample" => snapshot with { Runtime = [row with { ObservedAt = At.AddSeconds(-1) }] },
            "unknown-health-source" => snapshot with { Runtime = [row with { ConsecutiveHealthSource = "future-unknown-source" }] },
            _ => snapshot with { Runtime = [row with { ConsecutiveHealthySince = At.AddSeconds(1) }] },
        };
        var text = await Text(snapshot); Assert.Contains("continuous-health: unknown", text);
        Assert.DoesNotContain("continuous-health: 00:00:00", text);
        if (kind == "task-unavailable") { Assert.Contains("stale-fixture", text); Assert.Contains("\"available\":false", text); }
    }

    [Theory]
    [InlineData("process-unavailable")][InlineData("unknown-source")][InlineData("missing-identity")]
    [InlineData("null-start")][InlineData("future-start")][InlineData("missing-process-time")]
    public async Task InvalidProcessEvidenceNeverBecomesZeroOrHostUptime(string kind)
    {
        var snapshot = Snapshot(); var row = snapshot.Runtime.Single();
        snapshot = kind switch
        {
            "process-unavailable" => snapshot with { Processes = snapshot.Processes with { Available = false } },
            "unknown-source" => snapshot with { Runtime = [row with { ProcessUptimeSource = "service-host-start" }] },
            "missing-identity" => snapshot with { Runtime = [row with { ProcessIdentity = null }] },
            "null-start" => snapshot with { Runtime = [row with { ProcessStartedAt = null }] },
            "future-start" => snapshot with { Runtime = [row with { ProcessStartedAt = At.AddSeconds(1) }] },
            _ => snapshot with { Processes = snapshot.Processes with { ObservedAt = null } },
        };
        var text = await Text(snapshot); Assert.Contains("process-uptime: unknown", text);
        Assert.Contains("continuous-health: 00:00:10", text);
    }

    [Fact]
    public async Task MeasuredZeroAndLongDurationsRemainDistinctFromUnknown()
    {
        var snapshot = Snapshot(); var row = snapshot.Runtime[0];
        var zero = await Text(snapshot with { Runtime = [row with { ProcessStartedAt = At, ConsecutiveHealthySince = At }] });
        Assert.Contains("process-uptime: 00:00:00", zero); Assert.Contains("continuous-health: 00:00:00", zero);
        var longRun = await Text(snapshot with { Runtime = [row with { ProcessStartedAt = At.AddDays(-2).AddHours(-3) }] });
        Assert.Contains("process-uptime: 2.03:00:00", longRun);
        Assert.Contains("runtime: no current rows", await Text(snapshot with { Runtime = [] }));
    }

    [Theory]
    [InlineData(null, 0, "unknown")][InlineData(0d, 50, "0%")][InlineData(50d, 20, "50%")][InlineData(100d, 10, "100%")]
    public async Task HistoryShowsBackendAvailabilityWithCoverageWithoutRecomputing(double? availability, double coverage, string expected)
    {
        // Deliberately different totals: presentation consumes these fields, not another ratio.
        var history = History() with { AvailabilityPercent = availability, CoveragePercent = coverage };
        var text = await Text(history); Assert.Contains($"observed-availability: {expected}", text);
        Assert.Contains($"coverage: {coverage}%", text); Assert.Contains("unknown: 70s", text);
        Assert.Contains("NOT precise SLA", text); Assert.Contains("not service uptime", text);
        Assert.Contains("retainedFrom:", text); Assert.Contains("truncated: True", text);
        Assert.Contains("fixture warning", text);
    }

    [Fact]
    public async Task RuntimeFieldsAreTerminalSafeAndJsonRetainsTheCompleteOriginalEnvelope()
    {
        var snapshot = Snapshot(); var row = snapshot.Runtime[0];
        snapshot = snapshot with { Runtime = [row with { TaskName = "fixture\u001b[31m\n\u202e\u2028", ProcessIdentity = new string('x', 500), ProcessUptimeSource = "unavailable\u001b\u202e" }] };
        var text = await Text(snapshot); Assert.DoesNotContain(text, value => value is '\u001b' or '\u202e' or '\u2028');
        Assert.Contains("use --json", text);
        foreach (var data in new object[] { snapshot, History() })
        {
            var envelope = Envelope(data); using var output = new StringWriter(); using var error = new StringWriter();
            await CliOutput.WriteAsync(output, error, true, envelope);
            Assert.Equal(JsonSerializer.Serialize(envelope, AgentProtocol.JsonOptions) + output.NewLine, output.ToString());
            Assert.Empty(error.ToString());
        }
    }

    [Fact]
    public async Task UnexpectedMonitoringShapesKeepRawMetadataInsteadOfInventingADuration()
    {
        using var json = JsonDocument.Parse("{\"schemaVersion\":1,\"runtime\":\"old-shape\",\"tasks\":null,\"processes\":null,\"returnedAt\":\"not-a-date\"}");
        var text = await Text(json.RootElement); Assert.Contains("old-shape", text);
        Assert.DoesNotContain("process-uptime:", text); Assert.DoesNotContain("continuous-health:", text);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task UnknownSchemaRetainsOriginalMetadataWithoutApplyingKnownVersionSemantics(bool history)
    {
        object data = history ? History() with { SchemaVersion = 2 } : Snapshot() with { SchemaVersion = 2 };
        var text = await Text(data); Assert.Contains("schemaVersion: 2", text);
        Assert.DoesNotContain("process-uptime:", text); Assert.DoesNotContain("observed-availability:", text);
    }

    [Fact]
    public async Task IncompleteHistoryDoesNotDefaultMissingDurationsToZero()
    {
        using var json = JsonDocument.Parse("{\"schemaVersion\":1,\"taskIdentity\":\"fixture\",\"availabilityPercent\":0,\"coveragePercent\":0,\"intervals\":[],\"buckets\":[],\"unknownSeconds\":0,\"note\":\"old-shape\"}");
        var text = await Text(json.RootElement); Assert.Contains("old-shape", text);
        Assert.DoesNotContain("observed-availability:", text); Assert.DoesNotContain("healthy: 0s", text);
    }

    [Theory]
    [InlineData(-1d)][InlineData(101d)]
    public async Task OutOfRangeHistoryPercentagesAreUnknownNotClampedHealthy(double value)
    {
        var text = await Text(History() with { AvailabilityPercent = value, CoveragePercent = value, HealthySeconds = -1 });
        Assert.Contains("observed-availability: unknown | coverage: unknown", text); Assert.Contains("healthy: unknown", text);
    }

    [Fact]
    public async Task IndependentConsoleReadsRealMonitorAndHistoryOverPipeWithoutSamplingOnRead()
    {
        await using var fixture = new MonitorFixture(); await fixture.Sample();
        fixture.Clock.Now = At.AddSeconds(10); await fixture.Sample();
        var snapshot = fixture.Monitor.GetSnapshot();
        Assert.True(snapshot.Tasks.Available, JsonSerializer.Serialize(snapshot, AgentProtocol.JsonOptions));
        Assert.Single(snapshot.Runtime);
        var query = new MonitoringHistoryQuery("fixture", snapshot.Runtime.Single().TaskIdentity, At, At.AddSeconds(20), 2);
        var queryFile = Path.Combine(fixture.Root, "query.json"); await File.WriteAllTextAsync(queryFile, JsonSerializer.Serialize(query, AgentProtocol.JsonOptions));
        var before = Fingerprint(fixture.Root); var calls = fixture.Source.Calls;
        var human = await ProcessCli(fixture.Root, ["monitor", "status"]);
        Assert.Equal(0, human.Exit); Assert.Empty(human.Error); Assert.Contains("process-uptime: 01:00:10", human.Text);
        Assert.Contains("continuous-health: 00:00:10", human.Text);
        var watch = await ProcessCli(fixture.Root, ["monitor", "watch", "--count", "2", "--interval", "1", "--json"]);
        Assert.Equal(0, watch.Exit); var frames = watch.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries); Assert.Equal(2, frames.Length);
        foreach (var frame in frames)
        {
            using var parsed = JsonDocument.Parse(frame); var root = parsed.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean()); Assert.Equal(fixture.Endpoint.RootId, root.GetProperty("rootId").GetString());
            var data = root.GetProperty("data"); Assert.Equal(snapshot.ReturnedAt, data.GetProperty("returnedAt").GetDateTimeOffset());
            Assert.Equal(snapshot.Runtime.Single().ProcessStartedAt, data.GetProperty("runtime")[0].GetProperty("processStartedAt").GetDateTimeOffset());
        }
        var humanWatch = await ProcessCli(fixture.Root, ["monitor", "watch", "--count", "2", "--interval", "1"]);
        Assert.Equal(0, humanWatch.Exit); Assert.Empty(humanWatch.Error);
        Assert.Equal(3, humanWatch.Text.Split("process-uptime: 01:00:10", StringSplitOptions.None).Length);
        fixture.Clock.Now = At.AddSeconds(20); // Queries cannot request a future window.
        var expectedHistory = await fixture.Monitor.QueryHistoryAsync(query, default);
        var history = await ProcessCli(fixture.Root, ["monitor", "history", "--file", queryFile]);
        Assert.Equal(0, history.Exit); Assert.Empty(history.Error);
        Assert.Contains($"observed-availability: {expectedHistory.AvailabilityPercent!.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}%", history.Text);
        Assert.Contains($"coverage: {expectedHistory.CoveragePercent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}%", history.Text);
        var historyJson = await ProcessCli(fixture.Root, ["monitor", "history", "--file", queryFile, "--json"]);
        Assert.Equal(0, historyJson.Exit); using var historyEnvelope = JsonDocument.Parse(historyJson.Text);
        var historyData = historyEnvelope.RootElement.GetProperty("data");
        Assert.Equal(expectedHistory.HealthySeconds, historyData.GetProperty("healthySeconds").GetDouble());
        Assert.Equal(expectedHistory.LatestRecordAt, historyData.GetProperty("latestRecordAt").GetDateTimeOffset());
        fixture.Clock.Now = At.AddSeconds(41);
        var stale = await ProcessCli(fixture.Root, ["monitor", "status"]); Assert.Equal(0, stale.Exit);
        Assert.Contains("runtime: no current rows", stale.Text); Assert.Contains("stale-or-future-observation", stale.Text);
        Assert.Equal(calls, fixture.Source.Calls); Assert.Equal(before, Fingerprint(fixture.Root)); Assert.Empty(fixture.Errors);
        Assert.True(await new AgentClient(fixture.Root).PingAsync(TimeSpan.FromSeconds(2)));
    }

    private static CliEnvelope Envelope(object data) => new(1, "fixture-request", null, true, data, null, "fixture-revision", At, "fixture-root");
    private static async Task<string> Text(object data)
    { using var output = new StringWriter(); using var error = new StringWriter(); await CliOutput.WriteAsync(output, error, false, Envelope(data)); Assert.Empty(error.ToString()); return output.ToString(); }
    private static AgentMonitoringSnapshot Snapshot(string type = "windows")
    {
        var task = new TaskSnapshot("OK", true, true, "fixture", type, "fixture", "fixture", "synthetic", 0, null, [], null, null, null, [], null!);
        return new(1, "fixture-root", Guid.NewGuid().ToString("N"), 123, "fixture", At.AddHours(-2), At, At, "fixture-revision", 10,
            new(true, At, At, new[] { task }, null), new(true, At, At, Array.Empty<ManagedProcessSnapshot>(), null), new(false, null, At, null, "fixture-no-host"),
            [new("fixture", new string('a', 64), At, At.AddHours(-1), "windows-process-observed-instances", "123:fixture", At.AddSeconds(-10), "consecutive-passive-health-observations")],
            new(false, null, "fixture-history-disabled", 1024, 30));
    }
    private static MonitoringHistoryResult History() => new(1, "fixture", new string('a', 64), At, At.AddSeconds(100), At.AddSeconds(20), At,
        20, 10, 70, 20d * 100 / 30, 30, [], [], true, "fixture warning");
    private static string[] Fingerprint(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path =>
        {
            var info = new FileInfo(path);
            // The live HistoryStore owns this exclusive zero-byte lease. Its zero length
            // proves empty content without violating the lock; still compare name/mtime/length.
            var content = info.Name == "writer.lock" && info.Length == 0 ? "empty-exclusive-writer-lease"
                : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            return path + ":" + info.LastWriteTimeUtc.Ticks + ":" + info.Length + ":" + content;
        }).ToArray();
    private static async Task<(int Exit, string Text, string Error)> ProcessCli(string root, string[] arguments)
    {
        var path = typeof(CliMonitoringTextTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == "CliExecutablePath").Value!;
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var arg in arguments.Concat(["--root", root, "--timeout", "5", "--non-interactive"])) { info.ArgumentList.Add(arg); }
        using var process = Process.Start(info)!;
        try
        { var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); return (process.ExitCode, await stdout, await stderr); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = At;
        public bool Live { get; set; }
        public override DateTimeOffset GetUtcNow() => Live ? DateTimeOffset.UtcNow : Now;
    }
    private sealed class Source(Clock clock) : IAgentMonitoringSource
    {
        public int Calls;
        public HealthResult Health { get; set; } = HealthResult.Ok("fixture", "synthetic");
        public bool FailProcesses { get; set; }
        public Task<IReadOnlyList<TaskSnapshot>> ObserveTasksAsync(AutorunConfiguration config, CancellationToken token)
        { Calls++; return Task.FromResult<IReadOnlyList<TaskSnapshot>>(config.Tasks.Select(task => TaskSnapshot.From(task, Health)).ToArray()); }
        public Task<IReadOnlyList<ManagedProcessSnapshot>> ObserveProcessesAsync(AutorunConfiguration config, IReadOnlyList<TaskSnapshot> observations, CancellationToken token)
        { Calls++; if (FailProcesses) { throw new IOException("Synthetic process source unavailable."); } return Task.FromResult<IReadOnlyList<ManagedProcessSnapshot>>(config.Tasks.Select(task => new ManagedProcessSnapshot(task.Name, "fixture", "windows", true, true, true, "fixture", "fixture", "synthetic", clock.Now, [new(123, "fixture", null, At.AddHours(-1), 0, 100, 100, 1)], true, MonitoringTaskIdentity.For(task))).ToArray()); }
        public Task<HostStatusSnapshot> ObserveHostAsync(CancellationToken token)
        { Calls++; return Task.FromResult(new HostStatusSnapshot("fixture", "fixture", clock.Now, TimeSpan.FromHours(3), 0, 1000, 500, [], [])); }
    }
    private sealed class MonitorFixture : IAsyncDisposable
    {
        public string Root { get; }
        public Clock Clock { get; } = new(); public Source Source { get; }
        public AgentMonitor Monitor { get; } public AgentEndpoint Endpoint { get; }
        public System.Collections.Concurrent.ConcurrentQueue<Exception> Errors { get; } = new();
        private readonly CancellationTokenSource _shutdown = new(); private readonly Task _server;
        private readonly MonitoringHistoryStore _history; private readonly OperationJournal _operations;
        private readonly bool _ownsRoot; private bool _stopped;
        public MonitorFixture(DateTimeOffset? initialAt = null, MonitorFixture? previous = null)
        {
            if (previous is not null && !previous._stopped) { throw new InvalidOperationException("Stop the old stack before reopening its history writer."); }
            Root = previous?.Root ?? Directory.CreateTempSubdirectory("SystemAutorun-cli-monitor-").FullName;
            _ownsRoot = previous is null;
            try
            {
                if (initialAt is { } initial) { Clock.Now = initial; }
                var paths = new ProjectPaths(Root);
                if (_ownsRoot) { File.WriteAllText(paths.ConfigPath, "version: 1\nsettings:\n  default_startup_delay: 0\ntasks:\n  - name: fixture\n    type: windows\n    exec: fixture-never-execute.exe\n    expected_running: true\n    health:\n      type: process\n      pattern: fixture-never-execute\n"); }
                var workspace = new ConfigurationWorkspace(paths); Endpoint = AgentProtocol.ForRoot(Root);
                Source = new(Clock); _history = new(paths, clock: Clock); _operations = new(paths);
                Monitor = new(workspace, Source, _operations.InstanceId, _history, clock: Clock);
                var orchestrator = new TaskOrchestrator(new ConfigurationCache(new ConfigurationLoader(), paths), new OverrideStore(), new NeverExecute(), new NeverProbe(), new FileLog());
                var dispatcher = new AgentCommandDispatcher(orchestrator, new HostStatusReader(), workspace, Monitor, _operations,
                    errorObserver: (_, error) => Errors.Enqueue(error));
                _server = new AgentServer(dispatcher, _shutdown, Root).RunAsync(_shutdown.Token);
            }
            catch
            { Monitor?.Dispose(); _history?.Dispose(); _shutdown.Dispose(); if (_ownsRoot) { CleanupRoot(); } throw; }
        }
        public Task Sample() => Monitor.SampleOnceAsync(CancellationToken.None);
        public async Task StopAsync()
        {
            if (_stopped) { return; }
            _shutdown.Cancel(); await _server.WaitAsync(TimeSpan.FromSeconds(10));
            Monitor.Dispose(); _history.Dispose(); _stopped = true;
        }
        public async ValueTask DisposeAsync()
        {
            try { await StopAsync(); }
            finally
            {
                _shutdown.Dispose(); if (_stopped && _ownsRoot) { CleanupRoot(); }
            }
        }
        private void CleanupRoot()
        {
            var full = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(full) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(full).StartsWith("SystemAutorun-cli-monitor-", StringComparison.Ordinal)) { throw new InvalidOperationException("Non-owned monitor root."); }
            Directory.Delete(full, recursive: true);
        }
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
