using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Cli;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.IntegrationTests;

public sealed class StartupAdoptionControlPlaneTests
{
    internal static StartupEntry Entry => new("fixture-id", "HKCU", "fixture", "unparsed source",
        @"\\unreachable.invalid\do-not-probe\source.exe", "native args", null, @"HKCU\Run", null,
        false, null, null, false, true, null);
    internal static StartupAdoptionRequest Request => new(Entry, new("adopted-fixture", AutorunTaskType.WslBoot,
        Distro: "never-run-fixture", HealthType: HealthCheckType.WslRunning), true);

    [Fact]
    public async Task TypedRealPipeWritesOnlySelectedRootAndReplaysWithoutRescanning()
    {
        var source = new Reader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        await using var other = new ConfigurationControlPlaneTests.ServerFixture();
        var before = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var client = new AgentStartupClient(fixture.Client); var operation = AgentProtocol.NewOperationId();
        var first = await client.AdoptAsync(Entry.Id, Request, before.Revision, operation);
        Assert.NotEqual(before.Revision, first.Revision); Assert.Equal("add", first.Value.Configuration.Action);
        Assert.False(first.Value.NativeWritePerformed); Assert.False(first.Value.ExecutionRequested);
        Assert.Equal(Request.Task.Name, (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration.Tasks.Single().Name);
        Assert.Empty((await other.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration.Tasks);
        Assert.False(File.Exists(fixture.Paths.StartupBackupPath)); Assert.Equal(1, source.Calls);
        source.Entries = [];
        var replay = await client.AdoptAsync(Entry.Id, Request, before.Revision, operation);
        Assert.Equal(first.Revision, replay.Revision); Assert.Equal(1, source.Calls);
        var changed = await fixture.Client.SendResponseAsync("startup-adopt", Entry.Id,
            Request with { Task = Request.Task with { Name = "another" } }, expectedRevision: before.Revision, operationId: operation);
        Assert.Equal("operation_id_conflict", changed.ErrorCode); Assert.Equal(1, source.Calls);
        Assert.Single((await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration.Tasks);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CliReadsReviewedJsonFromStdinOrOwnedFileAndWarnsInText(bool file)
    {
        var source = new Reader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var original = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var payload = JsonSerializer.Serialize(Request with { Confirmed = false }, AgentProtocol.JsonOptions);
        var path = Path.Combine(fixture.Root, "reviewed.json");
        if (file) { await File.WriteAllTextAsync(path, payload); }
        var args = new[] { "startup", "adopt", Entry.Id, "--file", file ? path : "-", "--expected-revision", original.Revision, "--yes" };
        var result = await Run(fixture.Root, args, payload, json: false);
        Assert.Equal(0, result.Exit); Assert.Empty(result.Error);
        Assert.Contains("adopted entry:", result.Text); Assert.Contains("duplicate launch is possible", result.Text);
        Assert.Contains("no native startup writes or execution calls", result.Text);
        Assert.Equal(1, source.Calls);
    }

    [Theory]
    [InlineData("command")] [InlineData("arguments")] [InlineData("target")]
    [InlineData("origin")] [InlineData("permissions")] [InlineData("disabled")]
    [InlineData("managed")] [InlineData("error")] [InlineData("missing")]
    public async Task FreshMetadataMismatchRejectsWithoutConfigurationWriteAndIsDurablyReplayed(string change)
    {
        var source = new Reader { Entries = change == "missing" ? [] : [change switch
        {
            "command" => Entry with { RawCommand = "changed" }, "arguments" => Entry with { Arguments = "changed" },
            "target" => Entry with { Target = "changed" }, "origin" => Entry with { Origin = "changed" },
            "permissions" => Entry with { CanModify = false }, "disabled" => Entry with { IsDisabled = true },
            "managed" => Entry with { ManagedTask = "existing" }, "error" => Entry with { Error = "unreadable" }, _ => Entry,
        }] };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var before = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var bytes = await File.ReadAllBytesAsync(fixture.Paths.ConfigPath); var operation = AgentProtocol.NewOperationId();
        var args = new[] { "startup", "adopt", Entry.Id, "--file", "-", "--expected-revision", before.Revision, "--yes", "--operation-id", operation };
        var payload = JsonSerializer.Serialize(Request, AgentProtocol.JsonOptions);
        var first = await Run(fixture.Root, args, payload); Assert.Equal(5, first.Exit);
        using var json = JsonDocument.Parse(first.Text);
        Assert.Equal("startup_entry_changed", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        source.Entries = [Entry];
        var replay = await Run(fixture.Root, args, payload); Assert.Equal(5, replay.Exit);
        using var receipt = JsonDocument.Parse(replay.Text); Assert.True(receipt.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(1, source.Calls); Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Paths.ConfigPath));
    }

    [Theory]
    [InlineData("truncated")] [InlineData("source")] [InlineData("duplicate")]
    public async Task IncompleteNativeCensusCannotAuthorizeAdoption(string failure)
    {
        var source = new Reader { Truncated = failure == "truncated", Entries = failure == "duplicate" ? [Entry, Entry] : [Entry],
            Issues = failure == "source" ? [new("HKCU", "permission_denied")] : [] };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var before = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var result = await fixture.Client.SendResponseAsync("startup-adopt", Entry.Id, Request, expectedRevision: before.Revision);
        Assert.False(result.Ok); Assert.Equal("startup_inventory_incomplete", result.ErrorCode);
        Assert.Equal(before.Revision, (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Revision);
    }

    [Theory]
    [InlineData(true, "startup_preflight_permission_denied", 4)]
    [InlineData(false, "startup_preflight_unavailable", 1)]
    public async Task UnavailablePreflightIsTerminalButDoesNotClaimAConfigurationWrite(bool permission, string code, int exit)
    {
        var source = new Reader { BeforeReturn = _ => Task.FromException(permission ? new UnauthorizedAccessException("fixture") : new IOException("fixture")) };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var before = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var operation = AgentProtocol.NewOperationId();
        var args = new[] { "startup", "adopt", Entry.Id, "--file", "-", "--expected-revision", before.Revision, "--yes", "--operation-id", operation };
        var payload = JsonSerializer.Serialize(Request, AgentProtocol.JsonOptions);
        var first = await Run(fixture.Root, args, payload); Assert.Equal(exit, first.Exit);
        using var json = JsonDocument.Parse(first.Text); Assert.Equal(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        var replay = await Run(fixture.Root, args, payload); Assert.Equal(exit, replay.Exit);
        using var replayJson = JsonDocument.Parse(replay.Text); Assert.True(replayJson.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(1, source.Calls); Assert.Equal(before.Revision, (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Revision);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConfigurationConflictBeforeOrDuringScanNeverSilentlyRebases(bool during)
    {
        var source = new Reader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var workspace = new ConfigurationWorkspace(fixture.Paths); var before = await workspace.ReadAsync();
        if (during) { source.BeforeReturn = async token => { await workspace.PatchSettingsAsync(new(AutoRecoverFailedTasks: true), before.Revision, token); }; }
        else { await workspace.PatchSettingsAsync(new(AutoRecoverFailedTasks: true), before.Revision); }
        var result = await fixture.Client.SendResponseAsync("startup-adopt", Entry.Id, Request, expectedRevision: before.Revision);
        Assert.False(result.Ok); Assert.Equal("revision_conflict", result.ErrorCode);
        Assert.Equal(during ? 1 : 0, source.Calls); Assert.Empty((await workspace.ReadAsync()).Configuration.Tasks);
    }

    [Theory]
    [InlineData("confirmation")] [InlineData("unknown")] [InlineData("nested-unknown")]
    [InlineData("missing-entry")] [InlineData("missing-task")] [InlineData("id")]
    public async Task RawRpcRejectsMissingAuthorityOrUnknownSemanticsBeforeNativeRead(string failure)
    {
        var source = new Reader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var before = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var node = JsonSerializer.SerializeToNode(Request, AgentProtocol.JsonOptions)!;
        if (failure == "confirmation") { node["confirmed"] = false; }
        if (failure == "unknown") { node["disableOriginal"] = true; }
        if (failure == "nested-unknown") { node["task"]!["executeNow"] = true; }
        if (failure == "missing-entry") { node.AsObject().Remove("expectedEntry"); }
        if (failure == "missing-task") { node.AsObject().Remove("task"); }
        var result = await fixture.Client.SendResponseAsync("startup-adopt", failure == "id" ? "wrong-id" : Entry.Id,
            JsonSerializer.SerializeToElement(node), expectedRevision: before.Revision);
        Assert.False(result.Ok); Assert.Equal(failure == "confirmation" ? "authorization_required" : "invalid_argument", result.ErrorCode);
        Assert.Equal(0, source.Calls); Assert.Equal(before.Revision, (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Revision);
    }

    [Fact]
    public async Task CliMissingConfirmationRejectsBeforeConnecting()
    {
        var app = new CliApplication(_ => throw new InvalidOperationException("Must not connect."));
        var output = new StringWriter(); var error = new StringWriter();
        var exit = await app.RunAsync(["startup", "adopt", Entry.Id, "--file", "-", "--expected-revision", new string('a', 64), "--json"], new StringReader("{}"), output, error);
        Assert.Equal(4, exit); Assert.Empty(error.ToString());
        Assert.Contains("authorization_required", output.ToString());
    }

    [Fact]
    public async Task SeparateConsoleUsesRealPipeCommitAndReceiptWithoutASecondNativeRead()
    {
        var source = new Reader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var original = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var operation = AgentProtocol.NewOperationId();
        var args = new[] { "startup", "adopt", Entry.Id, "--file", "-", "--expected-revision", original.Revision, "--yes", "--operation-id", operation };
        var payload = JsonSerializer.Serialize(Request with { Confirmed = false }, AgentProtocol.JsonOptions);
        var first = await Console(fixture.Root, args, payload); Assert.Equal(0, first.Exit); Assert.Empty(first.Error);
        using var firstJson = JsonDocument.Parse(first.Text);
        Assert.Equal(operation, firstJson.RootElement.GetProperty("operationId").GetString());
        Assert.False(firstJson.RootElement.GetProperty("data").GetProperty("value").GetProperty("nativeWritePerformed").GetBoolean());
        source.Entries = [];
        var replay = await Console(fixture.Root, args, payload); Assert.Equal(0, replay.Exit);
        using var replayJson = JsonDocument.Parse(replay.Text); Assert.True(replayJson.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(1, source.Calls);
        Assert.Single((await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration.Tasks);
    }

    [Fact]
    public async Task TimedOutPreflightKeepsNativeSlotAndCancellationSourceUntilProducerFinishes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captured = default;
        var source = new Reader { BeforeReturn = async token => { captured = token; entered.TrySetResult(); await release.Task; } };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var original = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var pending = fixture.Client.SendResponseAsync("startup-adopt", Entry.Id, Request, expectedRevision: original.Revision);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(result.Ok); Assert.Equal("startup_preflight_unavailable", result.ErrorCode);
            Assert.Contains("startup_scan_timeout", result.Error);
            Assert.True(captured.WaitHandle.WaitOne(0));
            Assert.Equal("startup_scan_busy", (await new AgentStartupClient(fixture.Client).ReadAsync(new())).Reason);
            Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2)));
            Assert.Empty((await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration.Tasks);
            Assert.Equal(1, source.Calls);
        }
        finally { release.TrySetResult(); await pending; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        StartupInventorySnapshot after;
        do
        {
            after = await new AgentStartupClient(fixture.Client).ReadAsync(new(), deadline.Token);
            if (after.Reason == "startup_scan_busy") { await Task.Delay(10, deadline.Token); }
        } while (after.Reason == "startup_scan_busy");
        Assert.True(after.Available);
    }

    private static async Task<(int Exit, string Text, string Error)> Console(string root, string[] args, string input)
    {
        var path = typeof(StartupAdoptionControlPlaneTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(item => item.Key == "CliExecutablePath").Value!;
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args.Concat(["--root", root, "--json"])) { info.ArgumentList.Add(argument); }
        using var process = Process.Start(info)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(input); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            return (process.ExitCode, await output, await error);
        }
        finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
    }

    private static async Task<(int Exit, string Text, string Error)> Run(string root, string[] args, string input, bool json = true)
    {
        var output = new StringWriter(); var error = new StringWriter();
        var app = new CliApplication();
        var exit = await app.RunAsync([.. args, "--root", root, .. json ? new[] { "--json" } : []], new StringReader(input), output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private sealed class Reader : IStartupSnapshotReader
    {
        public int Calls; public StartupEntry[] Entries = [Entry]; public bool Truncated;
        public StartupSourceIssue[] Issues = [];
        public Func<CancellationToken, Task>? BeforeReturn;
        public async Task<StartupManagementSnapshot> ReadAsync(AutorunConfiguration configuration, StartupListQuery query, CancellationToken cancellationToken = default)
        {
            _ = configuration; _ = query; Interlocked.Increment(ref Calls);
            if (BeforeReturn is not null) { await BeforeReturn(cancellationToken); }
            return new(Entries, Entries.Count(entry => !entry.IsDisabled), Entries.Count(entry => !entry.IsDisabled && entry.IsManaged),
                Entries.Count(entry => entry.IsDisabled), false) { ReadableSources = 1, Truncated = Truncated, SourceIssues = Issues };
        }
    }
}
