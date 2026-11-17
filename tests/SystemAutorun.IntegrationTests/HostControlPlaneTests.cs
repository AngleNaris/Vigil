using System.IO;
using System.Text.Json;
using SystemAutorun.Agent;
using SystemAutorun.Cli;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.IntegrationTests;

public sealed class HostControlPlaneTests
{
    [Fact]
    public async Task RealPipeCliHostCreateUpdateReplayConflictAndRemoveDoNotRewriteYamlOrRunAnything()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var yaml = await File.ReadAllBytesAsync(fixture.Paths.ConfigPath);
        var host = Definition(fixture.Root, "fixture"); var initial = await Revision(fixture);
        var operation = AgentProtocol.NewOperationId();
        string[] create = ["host", "add", "--file", "-", "--expected-revision", initial, "--operation-id", operation];
        var added = await Invoke(fixture.Root, create, host);
        Assert.Equal(0, added.Exit); Assert.Empty(added.Diagnostics);
        var revision = added.Json.GetProperty("revision").GetString()!;
        Assert.NotEqual(initial, revision);
        var replay = await Invoke(fixture.Root, create, host);
        Assert.Equal(0, replay.Exit); Assert.True(replay.Json.GetProperty("replayed").GetBoolean());
        var duplicate = await Invoke(fixture.Root, ["host", "add", "--file", "-", "--expected-revision", revision], host with { Id = "FIXTURE", Name = "must not replace" });
        Assert.Equal(2, duplicate.Exit); Assert.Equal("invalid_argument", ErrorCode(duplicate));
        Assert.Equal(revision, await Revision(fixture));
        var stale = await Invoke(fixture.Root, ["host", "update", "--file", "-", "--expected-revision", initial], host with { Name = "stale" });
        Assert.Equal(5, stale.Exit); Assert.Equal("revision_conflict", ErrorCode(stale));
        var missing = await Invoke(fixture.Root, ["host", "update", "--file", "-", "--expected-revision", revision], host with { Id = "absent" });
        Assert.Equal(1, missing.Exit); Assert.Equal("not_found", ErrorCode(missing));
        var updated = await Invoke(fixture.Root, ["host", "update", "--file", "-", "--expected-revision", revision], host with { Name = "updated", Port = 2222 });
        Assert.Equal(0, updated.Exit);
        var shown = await Invoke(fixture.Root, ["host", "show", "FIXTURE"]);
        Assert.Equal(0, shown.Exit); Assert.Equal("updated", shown.Json.GetProperty("data").GetProperty("name").GetString());
        Assert.Equal(2222, shown.Json.GetProperty("data").GetProperty("port").GetInt32());
        var list = await Invoke(fixture.Root, ["host", "list"]);
        Assert.Single(list.Json.GetProperty("data").EnumerateArray());
        var remove = await Invoke(fixture.Root, ["host", "remove", "fixture", "--yes", "--expected-revision", updated.Json.GetProperty("revision").GetString()!]);
        Assert.Equal(0, remove.Exit);
        Assert.Empty((await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Preferences.RemoteHosts);
        Assert.Equal(yaml, await File.ReadAllBytesAsync(fixture.Paths.ConfigPath));
        Assert.False(fixture.IsStopping); Assert.False(File.Exists(host.KnownHostsFile));
    }

    [Fact]
    public async Task RealPipeRemoteObservationAndTypedClientPreserveRootRevisionSourceAndRemoteMetrics()
    {
        var provider = new Reader((host, _) => Task.FromResult(Online(host)));
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(remoteReader: provider);
        var host = Definition(fixture.Root, "fixture"); await Save(fixture, host);
        var revision = await Revision(fixture);
        var result = await Invoke(fixture.Root, ["host", "status", "FIXTURE"]);
        Assert.Equal(0, result.Exit); Assert.Empty(result.Diagnostics); Assert.True(result.Json.GetProperty("ok").GetBoolean());
        Assert.Equal(fixture.Endpoint.RootId, result.Json.GetProperty("rootId").GetString());
        Assert.Equal(revision, result.Json.GetProperty("revision").GetString());
        var data = result.Json.GetProperty("data");
        Assert.Equal("configured-ssh", data.GetProperty("source").GetString()); Assert.True(data.GetProperty("available").GetBoolean());
        Assert.Equal(12.5, data.GetProperty("snapshot").GetProperty("cpuUsagePercent").GetDouble());
        Assert.Equal("synthetic-remote", data.GetProperty("snapshot").GetProperty("machineName").GetString());
        Assert.Equal(JsonValueKind.String, result.Json.GetProperty("observedAt").ValueKind);
        var typed = await new AgentHostClient(fixture.Client).ReadRemoteAsync("fixture");
        Assert.True(typed.Available); Assert.Equal(revision, typed.Revision); Assert.Equal(TimeSpan.FromSeconds(91), typed.Snapshot.Uptime);
        Assert.Equal(2, provider.Calls); Assert.False(File.Exists(host.KnownHostsFile));
    }

    [Fact]
    public async Task DisabledUnknownAndAdHocStatusNeverReachProvider()
    {
        var provider = new Reader((_, _) => throw new InvalidOperationException("must not probe"));
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(remoteReader: provider);
        var host = Definition(fixture.Root, "disabled") with { Enabled = false }; await Save(fixture, host);
        var disabled = await Invoke(fixture.Root, ["host", "status", "disabled"]);
        Assert.Equal(1, disabled.Exit); Assert.Equal("host_disabled", ErrorCode(disabled));
        Assert.Equal(JsonValueKind.Null, disabled.Json.GetProperty("observedAt").ValueKind);
        Assert.False(disabled.Json.GetProperty("data").GetProperty("available").GetBoolean());
        var unknown = await Invoke(fixture.Root, ["host", "status", "missing"]);
        Assert.Equal(1, unknown.Exit); Assert.Equal("not_found", ErrorCode(unknown));
        var adHoc = await fixture.Client.SendResponseAsync("remote-status", "disabled", new { host = "never-connect.invalid", command = "never-execute" });
        Assert.False(adHoc.Ok); Assert.Equal("invalid_argument", adHoc.ErrorCode); Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task RemoteFailureIsExitOneWithDataAndNoCurrentObservation()
    {
        var provider = new Reader((host, _) => Task.FromResult(RemoteHostStatusSnapshot.Offline(host, "synthetic unavailable")));
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(remoteReader: provider);
        await Save(fixture, Definition(fixture.Root, "fixture"));
        var result = await Invoke(fixture.Root, ["host", "status", "fixture"]);
        Assert.Equal(1, result.Exit); Assert.Equal("remote_unavailable", ErrorCode(result)); Assert.Empty(result.Diagnostics);
        Assert.False(result.Json.GetProperty("data").GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("observedAt").ValueKind);
    }

    [Fact]
    public async Task ProbeIsSingleFlightByCaseInsensitiveIdAndGloballyBoundedWithoutBlockingOtherReads()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var provider = new Reader(async (host, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { first.TrySetResult(); } else { second.TrySetResult(); }
            await release.Task.WaitAsync(token); return Online(host);
        });
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(remoteReader: provider);
        await Save(fixture, Definition(fixture.Root, "one"), Definition(fixture.Root, "two"), Definition(fixture.Root, "three"));
        var client = new AgentHostClient(fixture.Client);
        var one = client.ReadRemoteAsync("one");
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("remote_probe_busy", (await client.ReadRemoteAsync("ONE")).Reason);
            var two = client.ReadRemoteAsync("two");
            try
            {
                await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("remote_probe_busy", (await client.ReadRemoteAsync("three")).Reason);
                Assert.True((await fixture.Client.SendResponseAsync("configuration")).Ok);
                Assert.Equal(2, provider.Calls);
            }
            finally { release.TrySetResult(); await two.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        finally { release.TrySetResult(); await one.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.True((await client.ReadRemoteAsync("three")).Available);
    }

    [Fact]
    public async Task EditingConfigurationWhileProbeRunsDiscardsItsObservationInsteadOfResurrectingOldHost()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Reader(async (host, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Online(host); });
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(remoteReader: provider);
        var host = Definition(fixture.Root, "fixture"); await Save(fixture, host);
        var pending = new AgentHostClient(fixture.Client).ReadRemoteAsync("fixture");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var revised = await fixture.Client.SendResponseAsync("remote-update", payload: host with { Name = "new identity" }, expectedRevision: await Revision(fixture));
            Assert.True(revised.Ok);
        }
        finally { release.TrySetResult(); }
        var result = await pending;
        Assert.False(result.Available); Assert.Equal("configuration_changed", result.Reason); Assert.Null(result.ObservedAt);
        Assert.NotEqual("synthetic-remote", result.Snapshot.MachineName);
    }

    [Fact]
    public async Task RemoveAndReAddWithIdenticalBytesStillInvalidatesAnInFlightObservation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Reader(async (host, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Online(host); });
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(remoteReader: provider);
        var host = Definition(fixture.Root, "fixture"); await Save(fixture, host);
        var revision = await Revision(fixture);
        var pending = new AgentHostClient(fixture.Client).ReadRemoteAsync("fixture");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var removed = await fixture.Client.SendResponseAsync("remote-remove", "fixture", expectedRevision: revision);
            Assert.True(removed.Ok);
            var added = await fixture.Client.SendResponseAsync("remote-add", payload: host, expectedRevision: await Revision(fixture));
            Assert.True(added.Ok); Assert.Equal(revision, await Revision(fixture)); // Actual content-hash ABA.
        }
        finally { release.TrySetResult(); }
        var result = await pending;
        Assert.False(result.Available); Assert.Equal("configuration_changed", result.Reason); Assert.Null(result.ObservedAt);
    }

    [Fact]
    public async Task CallerCancellationReleasesProbeSlotsAndIsNotConvertedIntoOfflineSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        var provider = new Reader(async (host, token) =>
        {
            if (first) { first = false; entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            return Online(host);
        });
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var host = Definition(fixture.Root, "fixture"); await Save(fixture, host);
        var service = new AgentRemoteHostService(new ConfigurationWorkspace(fixture.Paths), provider);
        using var cancellation = new CancellationTokenSource();
        var pending = service.ReadAsync("fixture", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True((await service.ReadAsync("fixture", CancellationToken.None)).Available);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task MismatchedOrStaleProviderResultsCannotBecomeCurrentRemoteMetrics(bool wrongIdentity)
    {
        var provider = new Reader((host, _) => Task.FromResult(wrongIdentity
            ? Online(host) with { Id = "other-root-host" } : Online(host) with { CapturedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }));
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(remoteReader: provider);
        await Save(fixture, Definition(fixture.Root, "fixture"));
        var result = await new AgentHostClient(fixture.Client).ReadRemoteAsync("fixture");
        Assert.False(result.Available); Assert.Equal("remote_source_mismatch", result.Reason); Assert.Null(result.ObservedAt);
    }

    private static RemoteHostDefinition Definition(string root, string id) => new()
    { Id = id, Name = id, Host = "127.0.0.1", Port = 1, UserName = "synthetic", Platform = RemoteHostPlatform.Linux, KnownHostsFile = Path.Combine(root, "known hosts"), ConnectTimeoutSeconds = 1, CommandTimeoutSeconds = 2 };
    private static RemoteHostStatusSnapshot Online(RemoteHostDefinition host) => new(host.Id, host.Name, host.Platform, host.Host,
        true, null, DateTimeOffset.UtcNow, "synthetic-remote", "synthetic Linux", TimeSpan.FromSeconds(91), 12.5,
        10000, 5000, [], "127.0.0.1", 10, 20, 4, 17, 23);
    private static async Task Save(ConfigurationControlPlaneTests.ServerFixture fixture, params RemoteHostDefinition[] hosts)
    {
        var response = await fixture.Client.SendResponseAsync("settings-set", payload: new ApplicationSettings { RemoteHosts = hosts }, expectedRevision: await Revision(fixture));
        Assert.True(response.Ok, response.Error);
    }
    private static async Task<string> Revision(ConfigurationControlPlaneTests.ServerFixture fixture) =>
        (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Revision;
    private static string? ErrorCode((int Exit, JsonElement Json, string Diagnostics) result) => result.Json.GetProperty("error").GetProperty("code").GetString();
    private static async Task<(int Exit, JsonElement Json, string Diagnostics)> Invoke(string root, string[] args, RemoteHostDefinition? host = null)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var code = await new CliApplication().RunAsync(["--root", root, "--json", .. args], new StringReader(host is null ? "" : JsonSerializer.Serialize(host, AgentProtocol.JsonOptions)), output, error);
        using var parsed = JsonDocument.Parse(output.ToString()); return (code, parsed.RootElement.Clone(), error.ToString());
    }
    private sealed class Reader(Func<RemoteHostDefinition, CancellationToken, Task<RemoteHostStatusSnapshot>> read) : IRemoteHostReader
    {
        private int _calls; public int Calls => Volatile.Read(ref _calls);
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition definition, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _calls); return read(definition, cancellationToken); }
    }
}
