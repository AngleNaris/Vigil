using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Recovery;
using Host = SystemAutorun.IntegrationTests.ConfigurationControlPlaneTests.ServerFixture;

namespace SystemAutorun.IntegrationTests;

public sealed class RecoveryClientTests
{
    private const string Configuration = """
        version: 1
        settings:
          default_startup_delay: 0
        tasks:
          - name: media
            type: windows
            exec: never-execute-recovery-contract-fixture.exe
            enabled: true
            expected_running: false
            health:
              type: process
              pattern: never-execute-recovery-contract-fixture
              timeout: 1
        """;

    [Fact]
    public async Task MissingStateReadIsExplicitAndDoesNotInitializeOrCreateAnOperation()
    {
        await using var host = new Host(); var store = new RecoveryControlStore(host.Paths);
        var before = await File.ReadAllBytesAsync(host.Paths.ConfigPath);
        var state = await new AgentRecoveryClient(host.Client).ReadAsync();
        Assert.False(state.Initialized); Assert.Empty(state.Tasks); Assert.False(File.Exists(store.FilePath));
        Assert.Equal(before, await File.ReadAllBytesAsync(host.Paths.ConfigPath)); Assert.Empty(OperationFiles(host));
    }

    [Fact]
    public async Task ResumeSharesRealJournalButOnlyClearsNamedIntentAndPreservesConfiguration()
    {
        await using var host = new Host(); await File.WriteAllTextAsync(host.Paths.ConfigPath, Configuration);
        var store = new RecoveryControlStore(host.Paths); await Initialize(store); await store.SetPausedAsync("media", true, recordIntent: true);
        await store.SetPausedAsync("another", true, recordIntent: true);
        var client = new AgentRecoveryClient(host.Client); var state = await client.ReadAsync(); var id = AgentProtocol.NewOperationId();
        var configuration = await host.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var configBytes = await File.ReadAllBytesAsync(host.Paths.ConfigPath); var stateBytes = await File.ReadAllBytesAsync(store.FilePath);
        Assert.Equal("authorization_required", (await Assert.ThrowsAsync<AgentCommandException>(() => client.SubmitResumeAsync("media", state.Revision, id, false))).Code);
        Assert.Empty(OperationFiles(host)); Assert.Equal(stateBytes, await File.ReadAllBytesAsync(store.FilePath));
        var ack = await client.SubmitResumeAsync("media", state.Revision, id.ToUpperInvariant(), true);
        Assert.True(ack.Ok); Assert.False(ack.Replayed); Assert.Equal(id, ack.OperationId);
        var acknowledged = Assert.IsType<RecoveryControlSnapshot>(ack.Data);
        Assert.False(acknowledged.Tasks.Single(task => task.TaskName == "media").Paused);
        Assert.True(acknowledged.Tasks.Single(task => task.TaskName == "another").Paused);
        var wire = await host.Client.SendAsync<RecoveryControlSnapshot>("recovery-state");
        Assert.Equal(ack.Revision, wire.Revision); Assert.Equal(wire.Revision, (await client.ReadAsync()).Revision);
        var console = await ReadConsoleState(host.Root);
        Assert.True(console.GetProperty("ok").GetBoolean());
        var consoleState = console.GetProperty("data").Deserialize<RecoveryControlSnapshot>(AgentProtocol.JsonOptions)!;
        Assert.Equal(wire.Revision, consoleState.Revision); Assert.Equal(wire.Tasks.ToArray(), consoleState.Tasks.ToArray());
        var receipt = await new AgentOperationClient(host.Client).ReadAsync(id);
        Assert.NotNull(receipt); Assert.Equal("recovery-resume", receipt.Command); Assert.Equal("media", receipt.Name); Assert.Equal(OperationState.Completed, receipt.State);
        Assert.Equal(id, receipt.Response!.OperationId); Assert.Single(OperationFiles(host));
        var after = await host.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        Assert.Equal(configuration.Revision, after.Revision); Assert.False(after.Preferences.AutoRecoverFailedTasks);
        Assert.Equal(configBytes, await File.ReadAllBytesAsync(host.Paths.ConfigPath));
        Assert.Empty(host.ObservedErrors); // NeverRunExecutor throws if resume invokes any workload operation.
    }

    [Fact]
    public async Task ReplayIsOldAcknowledgedIntentAndCannotReplaceNewerStopObservation()
    {
        await using var host = new Host(); await File.WriteAllTextAsync(host.Paths.ConfigPath, Configuration);
        var store = new RecoveryControlStore(host.Paths); await Initialize(store); await store.SetPausedAsync("media", true, recordIntent: true);
        var client = new AgentRecoveryClient(host.Client); var before = await client.ReadAsync(); var id = AgentProtocol.NewOperationId();
        var first = await client.SubmitResumeAsync("media", before.Revision, id, true);
        var stopped = await store.SetPausedAsync("media", true, recordIntent: true);
        var bytes = await File.ReadAllBytesAsync(store.FilePath);
        // Deliberate test replay, not client automatic retry or permission for the future workflow to replay.
        var old = await client.SubmitResumeAsync("media", before.Revision, id, true);
        Assert.True(old.Ok); Assert.True(old.Replayed); Assert.Equal(first.Revision, old.Revision);
        Assert.False(Assert.Single(Assert.IsType<RecoveryControlSnapshot>(old.Data).Tasks).Paused);
        var current = await client.ReadAsync(); Assert.Equal(stopped.Revision, current.Revision); Assert.True(Assert.Single(current.Tasks).Paused);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(store.FilePath)); Assert.Single(OperationFiles(host)); Assert.Empty(host.ObservedErrors);
    }

    [Fact]
    public async Task ExistingRecoveryConflictIsJournalUnknownNotClientInferredPermissionToRetry()
    {
        await using var host = new Host(); await File.WriteAllTextAsync(host.Paths.ConfigPath, Configuration);
        var store = new RecoveryControlStore(host.Paths); await Initialize(store); await store.SetPausedAsync("media", true, recordIntent: true);
        var client = new AgentRecoveryClient(host.Client); var stale = await client.ReadAsync();
        await store.SetPausedAsync("media", true, recordIntent: true); var bytes = await File.ReadAllBytesAsync(store.FilePath); var id = AgentProtocol.NewOperationId();
        var response = await client.SubmitResumeAsync("media", stale.Revision, id, true);
        // Existing IsUncertain whitelist lacks recovery_revision_conflict. Preserve that wire fact; no policy change in this slice.
        Assert.False(response.Ok); Assert.Equal("outcome_unknown", response.ErrorCode); Assert.Equal(id, response.OperationId); Assert.Null(response.Data);
        var receipt = await new AgentOperationClient(host.Client).ReadAsync(id);
        Assert.Equal(OperationState.Unknown, receipt!.State); Assert.Equal(id, receipt.OperationId);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(store.FilePath)); Assert.True(Assert.Single((await client.ReadAsync()).Tasks).Paused);
        Assert.Single(OperationFiles(host));
    }

    [Fact]
    public async Task CorruptStateDoesNotBecomeEmptyOrGetResetByRead()
    {
        await using var host = new Host(); var store = new RecoveryControlStore(host.Paths); await Initialize(store);
        await File.WriteAllTextAsync(store.FilePath, "corrupt-fixture"); var before = await File.ReadAllBytesAsync(store.FilePath);
        var failure = await Assert.ThrowsAsync<AgentCommandException>(() => new AgentRecoveryClient(host.Client).ReadAsync());
        Assert.Equal("command_failed", failure.Code); Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath)); Assert.Empty(OperationFiles(host));
    }

    private static async Task<JsonElement> ReadConsoleState(string root)
    {
        var executable = typeof(RecoveryClientTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "CliExecutablePath").Value!;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in new[] { "recovery", "state", "--root", root, "--json" }) { info.ArgumentList.Add(argument); }
        using var process = Process.Start(info)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); process.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode); Assert.Equal("", await stderr.WaitAsync(deadline.Token));
            using var document = JsonDocument.Parse(await stdout.WaitAsync(deadline.Token)); return document.RootElement.Clone();
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); } }
    }

    private static string[] OperationFiles(Host host) => Directory.GetFiles(host.Root, "*.operation.json", SearchOption.AllDirectories);
    private static async Task Initialize(RecoveryControlStore store)
    { var state = await store.ReadAsync(); await store.InitializeEmptyAsync(state.Revision); }
}
