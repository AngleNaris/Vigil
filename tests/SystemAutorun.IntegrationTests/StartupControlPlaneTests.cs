using System.IO;
using System.Text.Json;
using SystemAutorun.Cli;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.IntegrationTests;

public sealed class StartupControlPlaneTests
{
    [Fact]
    public async Task RealPipeCliAndTypedReaderShareFiltersIdentityAndReadOnlyConfiguration()
    {
        var provider = new Reader((_, _, _) => Task.FromResult(Native(
            Entry("user", "HKCU", "user metadata"), Entry("machine", "HKLM", "machine metadata"),
            Entry("backup", "Startup", "backup metadata") with { IsDisabled = true, BackupId = "backup" })));
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: provider);
        var yaml = await File.ReadAllBytesAsync(fixture.Paths.ConfigPath);
        var original = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var result = await Invoke(fixture.Root, ["startup", "list", "--scope", "user", "--type", "registry", "--state", "active", "--search", "METADATA", "--limit", "1"]);
        Assert.Equal(0, result.Exit); Assert.Empty(result.Diagnostics);
        Assert.Equal(fixture.Endpoint.RootId, result.Json.GetProperty("rootId").GetString());
        Assert.Equal(original.Revision, result.Json.GetProperty("revision").GetString());
        var entry = Assert.Single(result.Json.GetProperty("data").GetProperty("entries").EnumerateArray());
        Assert.Equal("user", entry.GetProperty("id").GetString());
        var query = new StartupListQuery("user", "registry", "active", "metadata", 1);
        var typed = await new AgentStartupClient(fixture.Client).ReadAsync(query);
        Assert.Equal("user", Assert.Single(typed.Entries).Id); Assert.Equal(2, provider.Calls);
        Assert.Equal(original.Revision, (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Revision);
        Assert.Equal(yaml, await File.ReadAllBytesAsync(fixture.Paths.ConfigPath));
        Assert.False(File.Exists(fixture.Paths.StartupBackupPath)); Assert.False(File.Exists(fixture.Paths.StartupCachePath));
        Assert.False(fixture.IsStopping);
    }

    [Fact]
    public async Task StrictRpcAndCliInputsRejectPathsCommandsAndMutatingFlagsBeforeScanning()
    {
        var provider = new Reader((_, _, _) => throw new InvalidOperationException("must not scan"));
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: provider);
        foreach (var payload in new object[] { new { scope = "remote" }, new { path = @"\\never-connect.invalid\share" }, new { command = "never-run" } })
        { Assert.Equal("invalid_argument", (await fixture.Client.SendResponseAsync("startup-list", payload: payload)).ErrorCode); }
        Assert.Equal("invalid_argument", (await fixture.Client.SendResponseAsync("startup-list", "not-a-selector", new StartupListQuery())).ErrorCode);
        foreach (var args in new string[][] { ["startup", "list", "--scope", "remote"], ["startup", "list", "--limit", "201"],
            ["startup", "list", "--file", "-"], ["startup", "list", "--operation-id", AgentProtocol.NewOperationId()], ["task", "list", "--scope", "user"] })
        { Assert.Equal(2, (await Invoke(fixture.Root, args)).Exit); }
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task TimeoutReturnsUnknownButRetainsTheActualNativeSlotAndOtherReadsWork()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StartupManagementSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Reader((_, _, _) => { started.TrySetResult(); return release.Task; });
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: provider);
        var client = new AgentStartupClient(fixture.Client);
        var first = client.ReadAsync(new());
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("startup_scan_busy", (await client.ReadAsync(new())).Reason);
            Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2)));
            var timeout = await first.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(timeout.Available); Assert.Equal("startup_scan_timeout", timeout.Reason);
            Assert.Null(timeout.ObservedAt); Assert.Null(timeout.ScannedEntries);
            Assert.Equal("startup_scan_busy", (await client.ReadAsync(new())).Reason);
            Assert.Equal(1, provider.Calls);
            Assert.True((await fixture.Client.SendResponseAsync("configuration")).Ok);
        }
        finally { release.TrySetResult(Native(Entry("released", "HKCU", "released"))); await first; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        StartupInventorySnapshot next;
        do { next = await client.ReadAsync(new(), deadline.Token); if (next.Reason == "startup_scan_busy") { await Task.Delay(10, deadline.Token); } }
        while (next.Reason == "startup_scan_busy");
        Assert.True(next.Available); Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task ConfigurationDriftCannotPublishOldManagedTaskMatching()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StartupManagementSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Reader((_, _, _) => { started.TrySetResult(); return release.Task; });
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: provider);
        var pending = new AgentStartupClient(fixture.Client).ReadAsync(new());
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var configuration = new AgentConfigurationClient(fixture.Client); _ = await configuration.ReadAsync();
            _ = await configuration.MutateAsync<ApplicationSettings>("settings-patch", payload: new ApplicationSettingsPatch(AutoStartMissingOnLaunch: true));
        }
        finally { release.TrySetResult(Native(Entry("late", "HKCU", "late"))); }
        var result = await pending;
        Assert.False(result.Available); Assert.Equal("configuration_changed", result.Reason); Assert.Empty(result.Entries);
    }

    [Theory]
    [InlineData("denied", 4, "permission_denied")]
    [InlineData("json", 1, "startup_backup_invalid")]
    [InlineData("io", 1, "startup_io_error")]
    [InlineData("partial", 1, "startup_source_unavailable")]
    [InlineData("entry", 1, "startup_entry_unavailable")]
    public async Task UnavailableAndPartialResultsNeverBecomeEmptySuccess(string kind, int exit, string reason)
    {
        var provider = new Reader((_, _, _) => Task.FromResult(kind switch
        {
            "denied" => throw new UnauthorizedAccessException("fixture"),
            "json" => throw new JsonException("fixture"),
            "io" => throw new IOException("fixture"),
            "partial" => Native(Entry("ok", "HKLM", "readable")) with { SourceIssues = [new("HKCU", "permission_denied")] },
            _ => Native(Entry("bad", "Startup", "unreadable") with { Error = "shortcut_read_failed" }),
        }));
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: provider);
        var result = await Invoke(fixture.Root, ["startup", "list"]);
        Assert.Equal(exit, result.Exit); Assert.False(result.Json.GetProperty("ok").GetBoolean());
        Assert.Equal(reason, result.Json.GetProperty("error").GetProperty("code").GetString());
        var data = result.Json.GetProperty("data");
        if (kind is "partial" or "entry") { Assert.Single(data.GetProperty("entries").EnumerateArray()); }
        else { Assert.Equal(JsonValueKind.Null, data.GetProperty("observedAt").ValueKind); Assert.Equal(JsonValueKind.Null, data.GetProperty("matchedEntries").ValueKind); }
    }

    [Fact]
    public async Task ReplyEntryCountAndUtf8BytesAreBoundedWithVisibleTruncationAndSafeTextOutput()
    {
        var entries = Enumerable.Range(0, 300).Select(index => Entry($"item-{index}", "HKCU", $"entry-{index}")
            with { RawCommand = new string('x', 2048), Origin = "metadata\tspoof\u001b[31m" }).ToArray();
        var provider = new Reader((_, _, _) => Task.FromResult(Native(entries)));
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: provider);
        var result = await new AgentStartupClient(fixture.Client).ReadAsync(new(Limit: 200));
        Assert.True(result.Truncated); Assert.Equal(300, result.MatchedEntries); Assert.True(result.Entries.Count < 200);
        Assert.True(result.Entries.Sum(entry => JsonSerializer.SerializeToUtf8Bytes(entry, AgentProtocol.JsonOptions).Length) <= StartupListQuery.MaximumSerializedEntriesBytes);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result, AgentProtocol.JsonOptions).Length < 128 * 1024);
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await new CliApplication().RunAsync(["--root", fixture.Root, "startup", "list", "--limit", "2"], TextReader.Null, output, error);
        Assert.Equal(0, exit); Assert.Empty(error.ToString()); Assert.Contains("source\tname\tstate", output.ToString());
        Assert.DoesNotContain('\u001b', output.ToString()); Assert.Contains("truncated: True", output.ToString());
        Assert.DoesNotContain(new string('x', 100), output.ToString());
    }

    private sealed class Reader(Func<AutorunConfiguration, StartupListQuery, CancellationToken, Task<StartupManagementSnapshot>> read) : IStartupSnapshotReader
    {
        public int Calls;
        public Task<StartupManagementSnapshot> ReadAsync(AutorunConfiguration configuration, StartupListQuery query, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return read(configuration, query, cancellationToken); }
    }

    private static StartupManagementSnapshot Native(params StartupEntry[] entries) => new(entries,
        entries.Count(entry => !entry.IsDisabled), entries.Count(entry => !entry.IsDisabled && entry.IsManaged),
        entries.Count(entry => entry.IsDisabled), false) { ReadableSources = 1 };

    private static StartupEntry Entry(string id, string source, string name) => new(id, source, name,
        "never-execute.exe --metadata", "never-execute.exe", "--metadata", null, $"fixture-origin-{id}", null,
        false, null, null, source is "HKLM" or "HKLM-WOW64" or "Common Startup", false, null);

    private static async Task<(int Exit, JsonElement Json, string Diagnostics)> Invoke(string root, string[] args)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await new CliApplication().RunAsync(["--root", root, "--json", .. args], TextReader.Null, output, error);
        using var document = JsonDocument.Parse(output.ToString());
        return (exit, document.RootElement.Clone(), error.ToString());
    }
}
