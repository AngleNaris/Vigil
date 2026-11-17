using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SystemAutorun.Cli;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

public sealed class LogControlPlaneTests
{
    private const string Stamp = "[2026-11-01 09:00:00 +08:00] ";

    [Fact]
    public async Task RealPipeListsConfiguredSourcesAndReturnsOnlyTheSelectedTaskSource()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        await Prepare(fixture);
        var before = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var sources = await fixture.Client.SendAsync<LogSourcesSnapshot>("log-sources");
        Assert.Equal(before.Revision, sources.Revision);
        Assert.Contains(sources.Sources, item => item.Id == "business" && item.TaskName == "fixture");
        var page = await fixture.Client.SendAsync<LogPage>("logs", payload: new LogQuery("business", "fixture"));
        Assert.True(page.Available, page.Reason); Assert.Equal(2, page.Entries.Count);
        Assert.Contains("stack", page.Entries[0].Message);
        var control = sources.Sources.Single(source => source.TaskName == "fixture" && source.Origin == "control");
        Directory.CreateDirectory(Path.GetDirectoryName(control.Path!)!);
        await File.WriteAllTextAsync(control.Path!, Stamp + "INFO control only\n");
        var selected = await fixture.Client.SendAsync<LogPage>("logs", payload: new LogQuery(Task: "fixture"));
        Assert.Contains("control only", Assert.Single(selected.Entries).Message);
        Assert.Equal(control.Id, selected.Source.Id);
        var mismatch = await Assert.ThrowsAsync<AgentCommandException>(() => fixture.Client.SendAsync<LogPage>("logs", payload: new LogQuery("control:agent", "fixture")));
        Assert.Equal("invalid_argument", mismatch.Code);
        var head = await Invoke(new(), fixture.Root, ["logs", "--source", "business", "--from-start", "--tail", "1"]);
        Assert.Equal(0, head.Exit); Assert.True(head.Lines[0].GetProperty("data").GetProperty("hasMore").GetBoolean());
        Assert.Single(head.Lines[0].GetProperty("data").GetProperty("entries").EnumerateArray());
        var cursor = head.Lines[0].GetProperty("data").GetProperty("nextCursor").GetString()!;
        var continuation = await Invoke(new(), fixture.Root, ["logs", "--source", "business", "--cursor", cursor, "--tail", "1"]);
        Assert.Equal(0, continuation.Exit); Assert.Single(continuation.Lines[0].GetProperty("data").GetProperty("entries").EnumerateArray());
        var after = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        Assert.Equal(before.Revision, after.Revision); Assert.False(fixture.IsStopping);
    }

    [Theory]
    [InlineData("wsl", "provider_unsupported")]
    [InlineData("events", "provider_unsupported")]
    [InlineData("missing", "source_missing")]
    public async Task UnavailableProviderIsNonzeroWithStructuredDataAndNoImplicitStartup(string source, string reason)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); await Prepare(fixture);
        var starts = 0;
        var app = new CliApplication(startDaemon: (_, _) => { starts++; throw new InvalidOperationException("No startup allowed"); });
        var result = await Invoke(app, fixture.Root, ["logs", "--source", source]);
        Assert.Equal(1, result.Exit); Assert.Empty(result.Error); Assert.Equal(0, starts);
        var json = Assert.Single(result.Lines);
        Assert.False(json.GetProperty("ok").GetBoolean()); Assert.Equal(reason, json.GetProperty("error").GetProperty("code").GetString());
        Assert.False(json.GetProperty("data").GetProperty("available").GetBoolean());
        Assert.Equal(reason, json.GetProperty("data").GetProperty("reason").GetString());
        Assert.False(fixture.IsStopping);
    }

    [Fact]
    public async Task FollowPollsWithLatestCursorAndCancellationNeverStopsTheDaemon()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); await Prepare(fixture);
        var transport = new RecordingTransport(fixture.Client);
        transport.AfterRead = async count =>
        { if (count == 1) { await File.AppendAllTextAsync(Path.Combine(fixture.Root, "business.log"), Stamp + "INFO appended\n"); } };
        var app = new CliApplication(_ => transport, (_, _) => throw new InvalidOperationException("No startup allowed"));
        var result = await Invoke(app, fixture.Root, ["logs", "--source", "business", "--follow", "--count", "2", "--interval", "1"]);
        Assert.Equal(0, result.Exit); Assert.Equal(2, result.Lines.Length);
        Assert.Null(((LogQuery)transport.Calls[0].Payload!).Cursor);
        Assert.Equal(result.Lines[0].GetProperty("data").GetProperty("nextCursor").GetString(), ((LogQuery)transport.Calls[1].Payload!).Cursor);
        Assert.Contains("appended", Assert.Single(result.Lines[1].GetProperty("data").GetProperty("entries").EnumerateArray()).GetProperty("message").GetString());
        using var cancellation = new CancellationTokenSource();
        transport = new RecordingTransport(fixture.Client) { AfterRead = _ => { cancellation.Cancel(); return Task.CompletedTask; } };
        var cancelled = await Invoke(new(_ => transport), fixture.Root, ["logs", "--source", "business", "--follow"], cancellation.Token);
        Assert.Equal(0, cancelled.Exit); Assert.Single(transport.Calls); Assert.Single(cancelled.Lines);
        Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2))); Assert.False(fixture.IsStopping);
    }

    [Fact]
    public async Task ExportIsABoundedNewFileAndNeverOverwritesConfigurationOrExistingOutput()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); await Prepare(fixture);
        var output = Path.Combine(fixture.Root, "selected.ndjson");
        var args = new[] { "logs", "--source", "business", "--level", "error", "--since", "2026-11-01T00:00:00Z", "--export", output };
        var result = await Invoke(new(), fixture.Root, args);
        Assert.Equal(0, result.Exit); Assert.Empty(result.Error);
        using var exported = JsonDocument.Parse(await File.ReadAllTextAsync(output));
        Assert.True(exported.RootElement.GetProperty("ok").GetBoolean());
        Assert.Single(exported.RootElement.GetProperty("data").GetProperty("entries").EnumerateArray());
        var old = await File.ReadAllBytesAsync(output);
        var again = await Invoke(new(), fixture.Root, args);
        Assert.Equal(1, again.Exit); Assert.Equal("export_failed", again.Lines[0].GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(old, await File.ReadAllBytesAsync(output));
        var config = await File.ReadAllTextAsync(fixture.Paths.ConfigPath);
        var forbidden = await Invoke(new(), fixture.Root, ["logs", "--source", "business", "--export", fixture.Paths.ConfigPath]);
        Assert.Equal(2, forbidden.Exit); Assert.Equal(config, await File.ReadAllTextAsync(fixture.Paths.ConfigPath));
    }

    [Theory]
    [InlineData("logs --tail 201")]
    [InlineData("logs --since 2026-11-01T00:00:00")]
    [InlineData("logs --level fake")]
    [InlineData("logs --follow --export output.ndjson")]
    [InlineData("logs sources --source business")]
    [InlineData("logs path --tail 2")]
    [InlineData("task list --follow")]
    [InlineData("logs --count 2")]
    [InlineData("logs --from-start --cursor arbitrary")]
    [InlineData("task list --from-start")]
    public async Task InvalidLogOptionsNeverReachTransport(string command)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var transport = new RecordingTransport(fixture.Client);
        var result = await Invoke(new(_ => transport), fixture.Root, command.Split(' '));
        Assert.Equal(2, result.Exit); Assert.Empty(transport.Calls); Assert.Empty(result.Error);
    }

    [Fact]
    public async Task SeparateConsoleUsesActualPipeAndTextOutputRetainsMultilineContent()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); await Prepare(fixture);
        var cli = typeof(LogControlPlaneTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == "CliExecutablePath").Value!;
        var info = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "logs", "--source", "business", "--root", fixture.Root, "--json" }) { info.ArgumentList.Add(argument); }
        using var process = Process.Start(info)!;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token); Assert.Equal(0, process.ExitCode); Assert.Empty(await stderr);
            using var json = JsonDocument.Parse(await stdout); Assert.Equal(fixture.Endpoint.RootId, json.RootElement.GetProperty("rootId").GetString());
            Assert.Equal(2, json.RootElement.GetProperty("data").GetProperty("entries").GetArrayLength());
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        using var text = new StringWriter(); using var error = new StringWriter();
        var exit = await new CliApplication().RunAsync(["logs", "--source", "business", "--root", fixture.Root], TextReader.Null, text, error);
        Assert.Equal(0, exit); Assert.Contains("\n  stack", text.ToString()); Assert.Contains("unknown", text.ToString()); Assert.Empty(error.ToString());
        Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task SourceRegistryHasASerializedByteBudgetNotJustAnItemLimit()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var directory = string.Join('/', Enumerable.Repeat(new string('x', 240), 9));
        var tasks = string.Concat(Enumerable.Range(0, 180).Select(index =>
            $"  - name: fixture-{index}\n    type: wsl_boot\n    enabled: false\n    distro: never-launch-for-logs\n    health:\n      type: wsl_running\n"));
        await File.WriteAllTextAsync(fixture.Paths.ConfigPath, $"version: 1\nsettings:\n  log_dir: {directory}\n  default_startup_delay: 0\ntasks:\n{tasks}");
        var sources = await fixture.Client.SendAsync<LogSourcesSnapshot>("log-sources");
        Assert.Equal(182, sources.TotalSources); Assert.True(sources.Truncated);
        Assert.InRange(sources.Sources.Count, 1, 181);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(sources, AgentProtocol.JsonOptions).Length < 257 * 1024);
    }

    [Fact]
    public async Task TaskNamedAgentCannotMasqueradeAsAnIsolatedTaskControlLog()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var snapshot = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        await fixture.Client.SendAsync<WorkspaceMutation<ConfigurationMutationResult>>("task-add",
            payload: new AddTaskRequest("agent", AutorunTaskType.WslBoot, Distro: "never-launch-for-logs", HealthType: HealthCheckType.WslRunning), expectedRevision: snapshot.Revision);
        var result = await fixture.Client.SendAsync<LogPage>("logs", payload: new LogQuery(Task: "agent"));
        Assert.False(result.Available); Assert.Equal("control_category_ambiguous", result.Reason);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task SameSourceIdInAnotherRootRejectsTheCursor()
    {
        await using var first = new ConfigurationControlPlaneTests.ServerFixture(); await Prepare(first);
        await using var other = new ConfigurationControlPlaneTests.ServerFixture(); await Prepare(other);
        var page = await first.Client.SendAsync<LogPage>("logs", payload: new LogQuery("business"));
        var error = await Assert.ThrowsAsync<AgentCommandException>(() => other.Client.SendAsync<LogPage>("logs", payload: new LogQuery("business", Cursor: page.NextCursor)));
        Assert.Equal("invalid_argument", error.Code);
    }

    private static async Task Prepare(ConfigurationControlPlaneTests.ServerFixture fixture)
    {
        var original = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var added = await fixture.Client.SendAsync<WorkspaceMutation<ConfigurationMutationResult>>("task-add",
            payload: new AddTaskRequest("fixture", AutorunTaskType.WslBoot, Distro: "never-launch-for-logs", HealthType: HealthCheckType.WslRunning), expectedRevision: original.Revision);
        await fixture.Client.SendAsync<WorkspaceMutation<ApplicationSettings>>("settings-set", payload: new ApplicationSettings { LogSources =
        [
            new() { Id = "business", TaskName = "fixture", Path = "business.log" },
            new() { Id = "missing", Path = "missing.log" },
            new() { Id = "wsl", Kind = "wsl-journal", TaskName = "fixture" },
            new() { Id = "events", Kind = "windows-event" },
        ] }, expectedRevision: added.Revision);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "business.log"), Stamp + "ERROR first\n  stack\n" + Stamp + "unrecognized level raw content\n");
    }

    private sealed class RecordingTransport(AgentClient client) : ICliTransport
    {
        public List<CliRequest> Calls { get; } = [];
        public Func<int, Task>? AfterRead { get; set; }
        public async Task<AgentResponse> SendAsync(CliRequest request, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            var response = await client.SendResponseAsync(request.Command, request.Name, request.Payload, request.Timeout, cancellationToken, request.Revision, request.OperationId);
            if (AfterRead is not null) { await AfterRead(Calls.Count); }
            return response;
        }
    }
    private sealed record Output(int Exit, JsonElement[] Lines, string Error);
    private static async Task<Output> Invoke(CliApplication app, string root, string[] args, CancellationToken token = default)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await app.RunAsync([.. args, "--root", root, "--json"], TextReader.Null, output, error, token);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToArray();
        return new(exit, lines, error.ToString());
    }
}
