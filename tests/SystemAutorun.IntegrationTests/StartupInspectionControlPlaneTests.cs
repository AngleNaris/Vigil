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

public sealed partial class StartupInspectionControlPlaneTests
{
    private static StartupEntry Entry => StartupAdoptionControlPlaneTests.Entry;

    [Fact]
    public async Task TypedRealPipePublishesFreshDigestAndRepeatedInspectionHasNoWriteOrExecution()
    {
        var source = new Reader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var original = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var client = new AgentStartupClient(fixture.Client);
        var selected = Assert.Single((await client.ReadAsync(new())).Entries);
        var before = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order().ToArray();
        var bytes = await File.ReadAllBytesAsync(fixture.Paths.ConfigPath);
        for (var index = 0; index < 2; index++)
        {
            var inspection = await client.InspectAsync(selected.Id, StartupInspectionRequest.FromEntry(selected), original.Revision);
            Assert.Equal(selected, inspection.Entry); Assert.Equal(selected.MetadataDigest, inspection.MetadataDigest);
            Assert.Equal("reviewable", inspection.Actions[0].State); Assert.Equal("not_applicable", inspection.OriginalBackupState);
            Assert.False(inspection.NativeWriteReady); Assert.False(inspection.NativeWritePerformed); Assert.False(inspection.ExecutionRequested);
        }
        Assert.Equal(3, source.Calls); Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Paths.ConfigPath));
        Assert.Equal(before, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.Empty((await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration.Tasks);
    }

    [Theory]
    [InlineData("disabled-shortcut", "native_control_unavailable")] [InlineData("disabled-registry", "shortcut_required")]
    [InlineData("unsupported", "entry_active")] [InlineData("managed", "entry_active")]
    public async Task MetadataReviewNeverPromotesUnknownBackupsOrEntryErrorsToNativeAuthority(string state, string rebuildReason)
    {
        var entry = state switch
        {
            "disabled-shortcut" => Entry with { Source = "Startup", IsDisabled = true, BackupId = "record", CanModify = false },
            "disabled-registry" => Entry with { IsDisabled = true, BackupId = "record", CanModify = false },
            "unsupported" => Entry with { Error = "unsupported_registry_value_kind", Target = null, CanModify = false },
            _ => Entry with { ManagedTask = "owned" },
        };
        var source = new Reader { Entries = [entry] };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var revision = (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Revision;
        var inspection = await new AgentStartupClient(fixture.Client).InspectAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), revision);
        Assert.Equal(entry.IsDisabled ? "not_checked" : "not_applicable", inspection.OriginalBackupState);
        Assert.Equal(rebuildReason, inspection.Actions[3].Reason);
        Assert.Equal("blocked", inspection.Actions[0].State); Assert.False(inspection.NativeWriteReady);
        Assert.False(File.Exists(fixture.Paths.StartupBackupPath)); Assert.Equal(1, source.Calls);
    }

    [Theory]
    [InlineData("command")] [InlineData("arguments")] [InlineData("target")] [InlineData("origin")]
    [InlineData("permissions")] [InlineData("disabled")] [InlineData("managed")] [InlineData("error")] [InlineData("missing")]
    public async Task FreshChangedOrDisappearedMetadataIsConflictWithoutWrites(string change)
    {
        var source = new Reader { Entries = change == "missing" ? [] : [change switch
        {
            "command" => Entry with { RawCommand = "changed" }, "arguments" => Entry with { Arguments = "changed" },
            "target" => Entry with { Target = "changed" }, "origin" => Entry with { Origin = "changed" },
            "permissions" => Entry with { CanModify = false }, "disabled" => Entry with { IsDisabled = true },
            "managed" => Entry with { ManagedTask = "existing" }, _ => Entry with { Error = "unreadable" },
        }] };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var original = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var result = await Run(fixture.Root, Entry, original.Revision);
        Assert.Equal(5, result.Exit);
        using var json = JsonDocument.Parse(result.Text);
        Assert.Equal("startup_entry_changed", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Null(json.RootElement.GetProperty("operationId").GetString()); Assert.Equal(1, source.Calls);
        Assert.Equal(original.Revision, (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Revision);
    }

    [Theory]
    [InlineData("truncated")] [InlineData("source")] [InlineData("duplicate")] [InlineData("backups")]
    public async Task IncompleteSelectedSourceCannotProduceAPreparation(string defect)
    {
        var entry = defect == "backups" ? Entry with { IsDisabled = true, BackupId = "record", CanModify = false } : Entry;
        var source = new Reader
        {
            Entries = defect == "duplicate" ? [entry, entry] : [entry], Truncated = defect == "truncated",
            Issues = defect is "source" or "backups" ? [new(defect == "backups" ? "backups" : entry.Source, "permission_denied")] : [],
        };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var revision = (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Revision;
        var error = await Assert.ThrowsAsync<AgentCommandException>(() => new AgentStartupClient(fixture.Client)
            .InspectAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), revision));
        Assert.Equal("startup_inventory_incomplete", error.Code);
        Assert.Empty((await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration.Tasks);
    }

    [Theory]
    [InlineData("target")] [InlineData("path")] [InlineData("command")] [InlineData("missing-state")]
    [InlineData("source")] [InlineData("digest")]
    public async Task UnrequestedPathsCommandsOrIncompleteReferenceAreRejectedBeforeScan(string defect)
    {
        var source = new Reader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var revision = (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Revision;
        var node = JsonSerializer.SerializeToNode(StartupInspectionRequest.FromEntry(Entry), AgentProtocol.JsonOptions)!;
        if (defect == "missing-state") { node.AsObject().Remove("isDisabled"); }
        else { node[defect] = defect == "source" ? "arbitrary" : defect == "digest" ? "bad" : @"\\unreachable.invalid\never-probe"; }
        if (defect == "digest") { node["metadataDigest"] = "bad"; node.AsObject().Remove("digest"); }
        var response = await fixture.Client.SendResponseAsync("startup-inspect", Entry.Id, node, expectedRevision: revision);
        Assert.False(response.Ok); Assert.Equal("invalid_argument", response.ErrorCode); Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RevisionConflictBeforeOrDuringScanDoesNotAcceptANewBaseline(bool during)
    {
        var source = new Reader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var original = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        async Task Change() => _ = await fixture.Client.SendAsync<JsonElement>("settings-patch", payload: new { autoStartMissingOnLaunch = true }, expectedRevision: original.Revision);
        if (during) { source.BeforeReturn = _ => Change(); } else { await Change(); }
        var result = await Run(fixture.Root, Entry, original.Revision);
        Assert.Equal(5, result.Exit);
        using var json = JsonDocument.Parse(result.Text);
        Assert.Equal("revision_conflict", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(during ? 1 : 0, source.Calls);
        Assert.Empty((await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration.Tasks);
    }

    [Fact]
    public async Task InspectionSharesTheNativeScanSlotWithListingAndCancellationDoesNotStartAnotherWorker()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Reader { BeforeReturn = _ => { entered.TrySetResult(); return release.Task; } };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var client = new AgentStartupClient(fixture.Client);
        var revision = (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Revision;
        using var cancellation = new CancellationTokenSource();
        var pending = client.InspectAsync(Entry.Id, StartupInspectionRequest.FromEntry(Entry), revision, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            var busy = await client.ReadAsync(new());
            Assert.False(busy.Available); Assert.Equal("startup_scan_busy", busy.Reason); Assert.Equal(1, source.Calls);
            _ = await fixture.Client.SendAsync<JsonElement>("ping");
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task CancelledCliInspectionIsAnExplicitErrorNotAnEmptySuccessfulQuery()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Reader { BeforeReturn = _ => { entered.TrySetResult(); return release.Task; } };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var revision = (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Revision;
        using var cancellation = new CancellationTokenSource();
        var output = new StringWriter(); var error = new StringWriter();
        var pending = new CliApplication().RunAsync(["startup", "inspect", Entry.Id, "--file", "-", "--expected-revision", revision,
            "--root", fixture.Root, "--json"], new StringReader(JsonSerializer.Serialize(StartupInspectionRequest.FromEntry(Entry), AgentProtocol.JsonOptions)),
            output, error, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            Assert.Equal(6, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            using var json = JsonDocument.Parse(output.ToString());
            Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("cancelled", json.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Null(json.RootElement.GetProperty("operationId").GetString());
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("permission", 4, "startup_preflight_permission_denied")]
    [InlineData("io", 1, "startup_preflight_unavailable")]
    public async Task UnreadableSelectedSourceIsNotASuccessfulOrEmptyInspection(string failure, int exit, string code)
    {
        var source = new Reader { BeforeReturn = _ => failure == "permission" ? throw new UnauthorizedAccessException() : throw new IOException() };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var revision = (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Revision;
        var result = await Run(fixture.Root, Entry, revision);
        Assert.Equal(exit, result.Exit);
        using var json = JsonDocument.Parse(result.Text);
        Assert.Equal(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("data").ValueKind);
        Assert.Empty((await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration.Tasks);
    }

    [Fact]
    public async Task UnrelatedMachineSourceFailureDoesNotBecomeSelectedEntryFailureOrAdoptionElevation()
    {
        var entry = Entry with { Source = "HKLM", RequiresAdministrator = true, CanModify = false };
        var source = new Reader { Entries = [entry], Issues = [new("HKLM-WOW64", "permission_denied")] };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var revision = (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Revision;
        var inspection = await new AgentStartupClient(fixture.Client).InspectAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), revision);
        Assert.Equal("reviewable", inspection.Actions[0].State); Assert.False(inspection.Actions[0].RequiresAdministrator);
        Assert.All(inspection.Actions.Skip(1), action => Assert.True(action.RequiresAdministrator));
        Assert.False(inspection.NativeWriteReady);
    }

    [Fact]
    public async Task LargeDisplayedMetadataUsesOnlyASmallReferenceOnTheRequestWire()
    {
        var entry = Entry with { RawCommand = new string('x', 32768), Arguments = new string('y', 32768) };
        Assert.True(JsonSerializer.Serialize(entry, AgentProtocol.JsonOptions).Length > AgentProtocol.MaximumRequestCharacters);
        Assert.True(JsonSerializer.Serialize(StartupInspectionRequest.FromEntry(entry), AgentProtocol.JsonOptions).Length < 2048);
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: new Reader { Entries = [entry] });
        var revision = (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Revision;
        var inspection = await new AgentStartupClient(fixture.Client).InspectAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), revision);
        Assert.Equal(entry, inspection.Entry); Assert.False(inspection.NativeWriteReady);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealConsoleAcceptsOwnedFileOrStdinAndExplainsReadOnlyReview(bool file)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: new Reader());
        var revision = (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Revision;
        var payload = JsonSerializer.Serialize(StartupInspectionRequest.FromEntry(Entry), AgentProtocol.JsonOptions);
        var path = Path.Combine(fixture.Root, "inspection.json"); if (file) { await File.WriteAllTextAsync(path, payload); }
        var args = new[] { "startup", "inspect", Entry.Id, "--file", file ? path : "-", "--expected-revision", revision };
        var result = await Console(fixture.Root, args, payload);
        Assert.Equal(0, result.Exit); Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Text);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean()); Assert.Null(json.RootElement.GetProperty("operationId").GetString());
        Assert.False(json.RootElement.GetProperty("data").GetProperty("nativeWriteReady").GetBoolean());
        var text = await Run(fixture.Root, Entry, revision, json: false);
        Assert.Equal(0, text.Exit); Assert.Contains("metadata-only", text.Text); Assert.Contains("no native write authority", text.Text);
        Assert.Empty((await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration.Tasks);
    }

    [Theory]
    [InlineData("--yes", "")] [InlineData("--operation-id", "019abcde-1234-7abc-8abc-123456789abc")]
    public async Task ReadOnlyInspectionDoesNotAcceptConfirmationOrMutationIds(string flag, string value)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: new Reader());
        var args = new List<string> { "startup", "inspect", Entry.Id, "--file", "-", "--expected-revision", new string('a', 64), flag };
        if (value.Length > 0) { args.Add(value); }
        var output = new StringWriter(); var app = new CliApplication(_ => throw new InvalidOperationException("No connection is permitted."));
        var exit = await app.RunAsync([..args, "--root", fixture.Root, "--json"], new StringReader("{}"), output, new StringWriter());
        Assert.Equal(2, exit);
    }

    private static async Task<(int Exit, string Text, string Error)> Run(string root, StartupEntry entry, string revision, bool json = true)
    {
        var output = new StringWriter(); var error = new StringWriter();
        var args = new[] { "startup", "inspect", entry.Id, "--file", "-", "--expected-revision", revision, "--root", root };
        var exit = await new CliApplication().RunAsync([..args, ..json ? new[] { "--json" } : []],
            new StringReader(JsonSerializer.Serialize(StartupInspectionRequest.FromEntry(entry), AgentProtocol.JsonOptions)), output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private static async Task<(int Exit, string Text, string Error)> Console(string root, string[] args, string input)
    {
        var path = typeof(StartupInspectionControlPlaneTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
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

    private sealed class Reader : IStartupSnapshotReader
    {
        internal int Calls; internal StartupEntry[] Entries = [Entry]; internal bool Truncated;
        internal StartupSourceIssue[] Issues = [];
        internal Func<CancellationToken, Task>? BeforeReturn;
        public async Task<StartupManagementSnapshot> ReadAsync(AutorunConfiguration configuration, StartupListQuery query, CancellationToken cancellationToken = default)
        {
            _ = configuration; _ = query; Interlocked.Increment(ref Calls);
            if (BeforeReturn is not null) { await BeforeReturn(cancellationToken); }
            return new(Entries, Entries.Count(entry => !entry.IsDisabled), Entries.Count(entry => !entry.IsDisabled && entry.IsManaged),
                Entries.Count(entry => entry.IsDisabled), false) { ReadableSources = 1, Truncated = Truncated, SourceIssues = Issues };
        }
    }
}
