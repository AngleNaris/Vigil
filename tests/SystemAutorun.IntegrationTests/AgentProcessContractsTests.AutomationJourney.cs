using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Recovery;

namespace SystemAutorun.IntegrationTests;

internal sealed record R121WorkerWitness(string Kind, int Pid, DateTime StartedUtc, string Image);

public sealed partial class StartupMutationAdapterTests
{
    internal static async Task<int> RunR121PassiveWorkerAsync(string root, string kind)
    {
        root = R121PolicyProcessFixture.RequireRoot(root);
        var image = Environment.ProcessPath ?? throw new IOException("No owned worker image.");
        if (kind is not ("launch" or "recovery") || image != R121PolicyProcessFixture.WorkerImage(root, kind)
            || Path.GetDirectoryName(image) != AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))
        { throw new InvalidOperationException("Only the independently named owned test image may run the passive worker."); }
        using var process = Process.GetCurrentProcess();
        var witness = new R121WorkerWitness(kind, process.Id, process.StartTime.ToUniversalTime(), image);
        var ready = Path.Combine(root, $"worker-{process.Id}.json");
        await File.WriteAllTextAsync(ready + ".tmp", JsonSerializer.Serialize(witness, AgentProtocol.JsonOptions));
        File.Move(ready + ".tmp", ready);
        var lifetime = Stopwatch.StartNew();
        while (lifetime.Elapsed < TimeSpan.FromSeconds(90) && !File.Exists(Path.Combine(root, $"exit-{process.Id}")))
        { await Task.Delay(200); }
        return 0;
    }
}

public sealed partial class AgentProcessContractsTests
{
    [Fact]
    public async Task RealAgentStartsMissingRecoversOwnedExitAndKeepsManualPauseAcrossRestart()
    {
        var output = Environment.GetEnvironmentVariable("SA_AGENT_POLICY_PROCESS_EVIDENCE");
        if (!string.IsNullOrEmpty(output)) { Assert.True(Directory.Exists(output)); }
        await using var fixture = await R121PolicyProcessFixture.CreateAsync();
        var originalConfiguration = await File.ReadAllBytesAsync(fixture.Paths.ConfigPath);
        using var originalRecovery = fixture.StartWorker("recovery");
        var initialWorker = await fixture.WaitWorkerAsync("recovery");
        Assert.Equal(originalRecovery.Id, initialWorker.Pid);
        await fixture.StartAgentAsync();
        var first = await WaitForMonitoringAsync(fixture.Client, state => state.Tasks.Available && state.Host.Available);
        var firstNative = NativeWitness(first);
        var launchResults = await fixture.Client.SendAsync<CommandResult[]>("start-on-app-launch", timeout: TimeSpan.FromSeconds(10));
        Assert.NotEmpty(launchResults); Assert.All(launchResults, result => Assert.True(result.Succeeded, result.Message));
        var launched = await fixture.WaitWorkerAsync("launch");
        _ = await fixture.Client.SendAsync<CommandResult[]>("start-on-app-launch", timeout: TimeSpan.FromSeconds(10));
        Assert.Equal(launched.Pid, (await fixture.WaitWorkerAsync("launch")).Pid);
        Assert.Equal(2, fixture.ReadWitnesses().Length);
        var beforeExit = await fixture.Client.SendAsync<RecoveryControlSnapshot>("recovery-state");
        Assert.DoesNotContain(beforeExit.Tasks, state => state.TaskName == "owned-recovery" && state.AttemptId is not null);
        // Voluntary exit of this exact worker, not Agent stop and never a production PID.
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, $"exit-{initialWorker.Pid}"), "owned voluntary exit");
        await originalRecovery.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var recovered = await fixture.WaitWorkerAsync("recovery", initialWorker.Pid, TimeSpan.FromSeconds(35));
        Assert.NotEqual(initialWorker.Pid, recovered.Pid);
        var admitted = await fixture.Client.SendAsync<RecoveryControlSnapshot>("recovery-state");
        var recoveryAttempt = Assert.Single(admitted.Tasks, state => state.TaskName == "owned-recovery");
        Assert.Equal(RecoveryAttemptOutcome.Succeeded, recoveryAttempt.Outcome); Assert.False(recoveryAttempt.Paused);
        foreach (var kind in new[] { "launch", "recovery" })
        {
            var result = await fixture.Client.SendAsync<CommandResult>("stop", "owned-" + kind, timeout: TimeSpan.FromSeconds(10));
            Assert.True(result.Succeeded, result.Message);
        }
        var paused = await fixture.Client.SendAsync<RecoveryControlSnapshot>("recovery-state");
        Assert.Equal(2, paused.Tasks.Count); Assert.All(paused.Tasks, state => Assert.True(state.Paused));
        var pauseBytes = await File.ReadAllBytesAsync(fixture.Control.FilePath);
        Assert.False(fixture.IsRunning(launched)); Assert.False(fixture.IsRunning(recovered));
        await fixture.StopAgentAsync();
        var launchesBeforeRestart = fixture.ReadWitnesses().Length;
        await fixture.StartAgentAsync();
        var second = await WaitForMonitoringAsync(fixture.Client, state => state.Tasks.Available && state.Host.Available);
        var secondNative = NativeWitness(second);
        Assert.NotEqual(first.InstanceId, second.InstanceId); Assert.NotEqual(first.ProcessId, second.ProcessId);
        _ = await fixture.Client.SendAsync<CommandResult[]>("start-on-app-launch", timeout: TimeSpan.FromSeconds(10));
        // Let the real ten-second sampler publish another fault cycle to the real recovery worker.
        await Task.Delay(TimeSpan.FromSeconds(12));
        var later = await WaitForMonitoringAsync(fixture.Client, state => state.Tasks.ObservedAt > second.Tasks.ObservedAt);
        Assert.All(later.Tasks.Data!, task => Assert.False(task.Healthy));
        Assert.Equal(launchesBeforeRestart, fixture.ReadWitnesses().Length);
        Assert.Equal(pauseBytes, await File.ReadAllBytesAsync(fixture.Control.FilePath));
        var persistedPause = await fixture.Client.SendAsync<RecoveryControlSnapshot>("recovery-state");
        Assert.All(persistedPause.Tasks, state => Assert.True(state.Paused));
        var restart = await fixture.Client.SendAsync<CommandResult>("start", "owned-recovery", timeout: TimeSpan.FromSeconds(10));
        Assert.True(restart.Succeeded, restart.Message);
        var manuallyStarted = await fixture.WaitWorkerAsync("recovery", recovered.Pid);
        Assert.NotEqual(recovered.Pid, manuallyStarted.Pid);
        var resumed = await fixture.Client.SendAsync<RecoveryControlSnapshot>("recovery-state");
        Assert.False(Assert.Single(resumed.Tasks, state => state.TaskName == "owned-recovery").Paused);
        Assert.True(Assert.Single(resumed.Tasks, state => state.TaskName == "owned-launch").Paused);
        Assert.True((await fixture.Client.SendAsync<CommandResult>("stop", "owned-recovery", timeout: TimeSpan.FromSeconds(10))).Succeeded);
        await fixture.StopAgentAsync();
        Assert.Equal(originalConfiguration, await File.ReadAllBytesAsync(fixture.Paths.ConfigPath));
        await ResourceProcessEvidence(output, "summary.json", new { firstNative, secondNative, initialWorker, launched, recovered,
            manuallyStarted, beforeExit, admitted, paused, persistedPause, resumed, samples = fixture.ReadWitnesses(),
            actualWindowsWorkerProcesses = true, voluntaryOwnedExitRecovered = true, existingProcessNotDuplicated = true,
            appLaunchAndRecoveryPauseSurviveAgentRestart = true, pauseBytesUnchangedAfterRestartAndCycles = true,
            manualStartResumesOnlyItsOwnTask = true, configurationBytesUnchanged = true,
            productionWorkloadsTouched = false, nativeGuiExercised = false, cleanupFallbackKills = fixture.FallbackKills });
    }
}

internal sealed class R121PolicyProcessFixture : IAsyncDisposable
{
    private const string Prefix = "SystemAutorun-agent-policy-process-";
    private readonly List<string> _images = [];
    private readonly List<R121WorkerWitness> _startedWorkers = [];
    private Process? _agent;
    private bool _automationEnabled = true;
    internal string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"))).FullName;
    internal ProjectPaths Paths => new(Root);
    internal AgentClient Client => new(Root);
    internal RecoveryControlStore Control => new(Paths);
    internal int FallbackKills { get; private set; }

    internal static string RequireRoot(string root)
    {
        var name = Path.GetFileName(root);
        if (root != Path.GetFullPath(root) || Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[Prefix.Length..], "N", out _)
            || (File.GetAttributes(root) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
        { throw new InvalidOperationException("A canonical ordinary owned policy-process root is required."); }
        return root;
    }
    internal static string WorkerImage(string root, string kind) => Path.Combine(AppContext.BaseDirectory,
        $"SAFixture_R121_{Path.GetFileName(RequireRoot(root))[Prefix.Length..]}_{kind}.exe");

    internal static async Task<R121PolicyProcessFixture> CreateAsync(bool automationEnabled = true)
    {
        var fixture = new R121PolicyProcessFixture { _automationEnabled = automationEnabled };
        try
        {
            await File.WriteAllTextAsync(fixture.Paths.ConfigPath, "version: 1\nsettings:\n  default_startup_delay: 0\ntasks: []\n");
            var workspace = new ConfigurationWorkspace(fixture.Paths);
            var state = await workspace.ReadAsync();
            foreach (var kind in new[] { "launch", "recovery" })
            {
                var image = WorkerImage(fixture.Root, kind);
                if (File.Exists(image)) { throw new IOException("Owned worker image already exists."); }
                File.Copy(Path.ChangeExtension(typeof(StartupMutationAdapterTests).Assembly.Location, ".exe"), image);
                fixture._images.Add(image);
                var result = await workspace.AddTaskAsync(new AddTaskRequest("owned-" + kind, AutorunTaskType.Windows,
                    "R121 disposable passive test worker, never a production task", image,
                    ["--r121-passive-worker", fixture.Root, kind], fixture.Root, StartupDelay: 0,
                    HealthType: HealthCheckType.Process, HealthPattern: Path.GetFileName(image),
                    Launch: new(RunAsMode.CurrentUser, LaunchTrigger.Logon, WindowMode.Hidden, ProcessLifecycle.Tracked),
                    ExpectedRunning: true, StartOnAppLaunch: automationEnabled && kind == "launch", AutoRecover: automationEnabled && kind == "recovery"), state.Revision);
                state = await workspace.ReadAsync(); Assert.Equal(result.Revision, state.Revision);
            }
            var control = await fixture.Control.ReadAsync();
            await fixture.Control.InitializeEmptyAsync(control.Revision);
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    internal Process StartWorker(string kind)
    {
        var start = new ProcessStartInfo(WorkerImage(Root, kind)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Root };
        foreach (var argument in new[] { "--r121-passive-worker", Root, kind }) { start.ArgumentList.Add(argument); }
        var process = Process.Start(start) ?? throw new IOException("Owned worker did not start.");
        try
        {
            _startedWorkers.Add(new(kind, process.Id, process.StartTime.ToUniversalTime(), WorkerImage(Root, kind)));
            return process;
        }
        catch
        {
            if (!process.HasExited)
            {
                Assert.Equal(WorkerImage(Root, kind), process.MainModule!.FileName);
                process.Kill(entireProcessTree: true); FallbackKills++; process.WaitForExit();
            }
            process.Dispose(); throw;
        }
    }
    internal R121WorkerWitness[] ReadWitnesses() => Directory.GetFiles(Root, "worker-*.json").Select(path =>
        JsonSerializer.Deserialize<R121WorkerWitness>(File.ReadAllText(path), AgentProtocol.JsonOptions) ?? throw new IOException("Invalid worker witness.")).ToArray();
    internal bool IsRunning(R121WorkerWitness witness)
    {
        Assert.Equal(WorkerImage(Root, witness.Kind), witness.Image);
        try
        {
            using var process = Process.GetProcessById(witness.Pid);
            return !process.HasExited && process.StartTime.ToUniversalTime() == witness.StartedUtc && process.MainModule!.FileName == witness.Image;
        }
        catch (ArgumentException) { return false; }
    }
    internal async Task<R121WorkerWitness> WaitWorkerAsync(string kind, int excluded = 0, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < (timeout ?? TimeSpan.FromSeconds(8)))
        {
            var live = ReadWitnesses().Where(witness => witness.Kind == kind && witness.Pid != excluded && IsRunning(witness)).ToArray();
            Assert.InRange(live.Length, 0, 1);
            if (live.Length == 1) { return live[0]; }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Owned {kind} worker did not become ready.");
    }
    internal async Task StartAgentAsync()
    {
        Assert.Null(_agent);
        var configuration = await new ConfigurationLoader().LoadAsync(Root);
        Assert.Empty((await new ConfigurationWorkspace(Paths).ReadAsync()).Preferences.RemoteHosts);
        Assert.Equal(new[] { "owned-launch", "owned-recovery" }, configuration.Tasks.Select(task => task.Name).Order().ToArray());
        Assert.All(configuration.Tasks, task =>
        {
            var kind = task.Name == "owned-launch" ? "launch" : "recovery";
            Assert.Equal(WorkerImage(Root, kind), task.Executable);
            Assert.Equal(AutorunTaskType.Windows, task.Type); Assert.Empty(task.DependsOn);
            Assert.Equal(HealthCheckType.Process, task.Health.Type); Assert.Equal(Path.GetFileName(task.Executable), task.Health.Pattern);
            Assert.Null(task.Health.Host); Assert.Null(task.Health.Port); Assert.Null(task.Health.Distro); Assert.Null(task.Health.Service);
            Assert.Null(task.Distro); Assert.Null(task.Service); Assert.Null(task.Command);
            Assert.Equal(new[] { "--r121-passive-worker", Root, kind }, task.Arguments);
            Assert.Equal(Root, task.WorkingDirectory);
            Assert.True(task.Enabled); Assert.True(task.ExpectedRunning);
            Assert.Equal(RunAsMode.CurrentUser, task.Launch.RunAs); Assert.Equal(LaunchTrigger.Logon, task.Launch.Trigger);
            Assert.Equal(ProcessLifecycle.Tracked, task.Launch.Lifecycle);
            Assert.Equal(_automationEnabled && kind == "launch", task.StartOnAppLaunch);
            Assert.Equal(_automationEnabled && kind == "recovery", task.AutoRecover);
        });
        _agent = AgentProcessContractsTests.LaunchOwnedPolicyAgent(Root);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            Assert.False(_agent.HasExited);
            if (await Client.PingAsync(TimeSpan.FromMilliseconds(300))) { return; }
            await Task.Delay(50);
        }
        throw new TimeoutException("Owned policy Agent was not ready.");
    }
    internal async Task StopAgentAsync()
    {
        Assert.NotNull(_agent);
        _ = await Client.SendAsync<JsonElement>("shutdown", timeout: TimeSpan.FromSeconds(5));
        await _agent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, _agent.ExitCode); _agent.Dispose(); _agent = null;
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_agent is not null && !_agent.HasExited)
            {
                try { await StopAgentAsync(); }
                catch { if (_agent is not null && !_agent.HasExited) { _agent.Kill(entireProcessTree: true); FallbackKills++; await _agent.WaitForExitAsync(); } }
            }
            _agent?.Dispose(); _agent = null;
            foreach (var witness in ReadWitnesses().Concat(_startedWorkers).Distinct())
            {
                if (!IsRunning(witness)) { continue; }
                await File.WriteAllTextAsync(Path.Combine(Root, $"exit-{witness.Pid}"), "owned fixture cleanup");
                using var process = Process.GetProcessById(witness.Pid);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { if (IsRunning(witness)) { process.Kill(entireProcessTree: true); FallbackKills++; await process.WaitForExitAsync(); } }
            }
        }
        finally
        {
            foreach (var image in _images)
            {
                Assert.Contains(image, new[] { WorkerImage(Root, "launch"), WorkerImage(Root, "recovery") });
                Assert.False((File.GetAttributes(image) & FileAttributes.ReparsePoint) != 0); File.Delete(image);
            }
            Directory.Delete(RequireRoot(Root), recursive: true);
        }
    }
}

public sealed partial class AgentProcessContractsTests
{
    internal static Process LaunchOwnedPolicyAgent(string root) => OwnedAgent.Launch(R121PolicyProcessFixture.RequireRoot(root));
}
