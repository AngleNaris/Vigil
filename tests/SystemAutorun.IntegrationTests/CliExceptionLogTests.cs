using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SystemAutorun.Cli;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

public sealed class CliExceptionLogTests
{
    private static readonly DateTimeOffset Time = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly ExceptionLogSnapshot Snapshot = new(Time,
        [new(Time, ExceptionLogLevel.Error, "fixture", "error detail", "fixture.log"),
         new(Time.AddMinutes(-1), ExceptionLogLevel.Warning, "other", "警告 detail", "other.log")], 3, true);

    [Fact]
    public async Task OneReadOnlyRpcKeepsSnapshotIdentityAndFiltersOnlyItsReturnedEntries()
    {
        using var root = new Root(); var transport = new Transport(Snapshot); var starts = 0;
        var result = await Invoke(new(_ => transport, (_, _) => { starts++; return Task.CompletedTask; }), root.Path,
            ["--level", "WaRnInG", "--search", " OTHER.LOG "], true);
        Assert.Equal(0, result.Exit); Assert.Empty(result.Error);
        var json = Parse(result.Text); Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("fixture-request", json.GetProperty("requestId").GetString());
        Assert.Equal(Time, json.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("operationId").ValueKind);
        var data = json.GetProperty("data"); Assert.Equal(3, data.GetProperty("sourceCount").GetInt32());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.Equal("other.log", Assert.Single(data.GetProperty("entries").EnumerateArray()).GetProperty("fileName").GetString());
        var call = Assert.Single(transport.Calls); Assert.Equal("exception-logs", call.Command);
        Assert.Null(call.Name); Assert.Null(call.Payload); Assert.Null(call.Revision); Assert.Null(call.OperationId);
        Assert.False(AgentProtocol.IsMutation(call.Command)); Assert.Equal(0, starts);
    }

    [Fact]
    public async Task HumanOutputShowsMetadataCaveatsAndSanitizesUntrustedMultilineFields()
    {
        using var root = new Root();
        var snapshot = Snapshot with { Entries = [new(Time, ExceptionLogLevel.Critical, "source\u001b[31m\n\u202e\u2028",
            "fatal\nstack\r\t\u001b]52;c;payload\a" + new string('x', 1400), "bad\u009bfile.log")] };
        var result = await Invoke(new(_ => new Transport(snapshot)), root.Path, [], false);
        Assert.Equal(0, result.Exit); Assert.Empty(result.Error);
        Assert.Contains("legacy exceptions", result.Text); Assert.Contains("source files: 3", result.Text);
        Assert.Contains("truncated: True", result.Text); Assert.Contains(Time.ToString("O"), result.Text);
        Assert.Contains("Critical", result.Text); Assert.Contains("use --json", result.Text);
        Assert.Contains("not a health or source-availability check", result.Text);
        Assert.DoesNotContain(result.Text, c => char.IsControl(c) && c is not ('\n' or '\r'));
        Assert.Contains("fatal stack", result.Text); Assert.DoesNotContain("\u202e", result.Text, StringComparison.Ordinal); Assert.DoesNotContain("\u2028", result.Text, StringComparison.Ordinal);
        var jsonResult = await Invoke(new(_ => new Transport(snapshot)), root.Path, [], true);
        var retained = Parse(jsonResult.Text).GetProperty("data").GetProperty("entries")[0];
        Assert.Equal(snapshot.Entries[0].Source, retained.GetProperty("source").GetString());
        Assert.Equal(snapshot.Entries[0].Message, retained.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("--yes")] [InlineData("--raw")] [InlineData("--native")]
    [InlineData("--file=-")] [InlineData("--operation-id=019a0000000070008000000000000000")]
    [InlineData("--expected-revision=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("--source=control:agent")] [InlineData("--task=fixture")] [InlineData("--since=2026-01-01T00:00:00Z")]
    [InlineData("--follow")] [InlineData("--tail=1")] [InlineData("--cursor=fixture")]
    [InlineData("--from-start")] [InlineData("--export=must-not-exist.ndjson")]
    [InlineData("--interval=1")] [InlineData("--count=2")] [InlineData("--scope=all")]
    [InlineData("--type=all")] [InlineData("--state=all")] [InlineData("--limit=1")]
    [InlineData("--level=debug")] [InlineData("--level=0")]
    public async Task UnsupportedOptionsFailBeforeConnection(string option)
    {
        using var root = new Root(); var connections = 0;
        var result = await Invoke(new(_ => { connections++; throw new InvalidOperationException("Must not connect"); }), root.Path, [option], true);
        Assert.Equal(2, result.Exit); Assert.Equal(0, connections);
        Assert.Equal("invalid_argument", Parse(result.Text).GetProperty("error").GetProperty("code").GetString());
        Assert.Single(Directory.GetFiles(root.Path));
    }

    [Theory]
    [InlineData("capturedAt")] [InlineData("entries")] [InlineData("sourceCount")] [InlineData("truncated")]
    [InlineData("nullEntries")] [InlineData("negativeSources")] [InlineData("nullEntry")]
    [InlineData("unknownLevel")] [InlineData("missingMessage")] [InlineData("missingLevel")] [InlineData("zeroTime")]
    public async Task MalformedSuccessIsProtocolFailureNotEmptyHealthyData(string kind)
    {
        using var root = new Root();
        var entry = new Dictionary<string, object?> { ["timestamp"] = Time, ["level"] = "error", ["source"] = "fixture", ["message"] = "error", ["fileName"] = "fixture.log" };
        var data = new Dictionary<string, object?> { ["capturedAt"] = Time, ["entries"] = new object?[] { entry }, ["sourceCount"] = 1, ["truncated"] = false };
        if (kind is "capturedAt" or "entries" or "sourceCount" or "truncated") { data.Remove(kind); }
        if (kind == "nullEntries") { data["entries"] = null; }
        if (kind == "negativeSources") { data["sourceCount"] = -1; }
        if (kind == "nullEntry") { data["entries"] = new object?[] { null }; }
        if (kind == "unknownLevel") { entry["level"] = 99; }
        if (kind == "missingMessage") { entry.Remove("message"); }
        if (kind == "missingLevel") { entry.Remove("level"); }
        if (kind == "zeroTime") { data["capturedAt"] = default(DateTimeOffset); }
        var transport = new Transport(data);
        var result = await Invoke(new(_ => transport), root.Path, [], true);
        Assert.Equal(3, result.Exit); var json = Parse(result.Text);
        Assert.Equal("protocol_mismatch", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("fixture-request", json.GetProperty("requestId").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("data").ValueKind); Assert.Single(transport.Calls);
    }

    [Theory]
    [InlineData("permission_denied", 4)] [InlineData("request_timeout", 6)] [InlineData("command_failed", 1)] [InlineData("unknown_command", 1)]
    public async Task BusinessFailuresKeepTheirIdentityWithoutFormattingThemAsLogs(string code, int exit)
    {
        using var root = new Root(); var transport = new Transport(null, code);
        var result = await Invoke(new(_ => transport), root.Path, [], true); var json = Parse(result.Text);
        Assert.Equal(exit, result.Exit); Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal(code, json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("fixture-request", json.GetProperty("requestId").GetString()); Assert.Single(transport.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task IndependentConsoleUsesRealAggregateAndPipeWithoutChangingPrivateFiles(bool capped)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var configuration = await fixture.Cache.GetAsync(); var logDirectory = configuration.Settings.LogDirectory;
        Assert.StartsWith(fixture.Root + System.IO.Path.DirectorySeparatorChar, logDirectory, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(logDirectory);
        var file = System.IO.Path.Combine(logDirectory, "fixture.log");
        await File.WriteAllLinesAsync(file, Enumerable.Range(0, capped ? 205 : 2).Select(index => $"[{Time.AddSeconds(index):O}] error fixture detail {index}"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(fixture.Root, "other.log"), $"[{Time.AddDays(-1):O}] warning other detail\n");
        var before = Fingerprint(fixture.Root);
        var result = await ProcessCli(fixture.Root, ["--json"]); Assert.Equal(0, result.Exit); Assert.Empty(result.Error);
        var json = Parse(result.Text); Assert.Equal(fixture.Endpoint.RootId, json.GetProperty("rootId").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("operationId").ValueKind); Assert.False(json.GetProperty("replayed").GetBoolean());
        var snapshot = json.GetProperty("data").Deserialize<ExceptionLogSnapshot>(AgentProtocol.JsonOptions)!;
        Assert.Equal(2, snapshot.SourceCount); Assert.Equal(capped, snapshot.Truncated); Assert.Equal(capped ? 200 : 3, snapshot.Entries.Count);
        Assert.Equal(capped ? "error fixture detail 204" : "error fixture detail 1", snapshot.Entries[0].Message);
        Assert.Equal(snapshot.CapturedAt, json.GetProperty("observedAt").GetDateTimeOffset());
        var guiContract = await fixture.Client.SendAsync<ExceptionLogSnapshot>("exception-logs");
        Assert.Equal(guiContract.Entries, snapshot.Entries); Assert.Equal(guiContract.SourceCount, snapshot.SourceCount);
        var filtered = await ProcessCli(fixture.Root, ["--level", "warning", "--search", "OTHER.LOG", "--json"]);
        Assert.Equal(0, filtered.Exit); var page = Parse(filtered.Text).GetProperty("data");
        Assert.Equal(capped ? 0 : 1, page.GetProperty("entries").GetArrayLength());
        Assert.Equal(2, page.GetProperty("sourceCount").GetInt32()); Assert.Equal(capped, page.GetProperty("truncated").GetBoolean());
        var human = await ProcessCli(fixture.Root, []); Assert.Equal(0, human.Exit); Assert.Contains("legacy exceptions", human.Text);
        Assert.Equal(before, Fingerprint(fixture.Root)); Assert.False(fixture.IsStopping); Assert.Empty(fixture.ObservedErrors);
    }

    [Fact]
    public async Task EmptyConsoleSnapshotDoesNotClaimAvailabilityOrCleanHealth()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var result = await ProcessCli(fixture.Root, []); Assert.Equal(0, result.Exit);
        Assert.Contains("entries: 0", result.Text); Assert.Contains("not a health or source-availability check", result.Text);
        Assert.Contains("read failures may be omitted", result.Text); Assert.DoesNotContain("healthy", result.Text);
    }

    [Fact]
    public async Task LockedFileIsAttemptedButNotReportedAsAvailableOrClean()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var file = System.IO.Path.Combine(fixture.Root, "locked.log");
        await File.WriteAllTextAsync(file, $"[{Time:O}] fatal hidden by sharing lock");
        var before = Fingerprint(fixture.Root);
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await ProcessCli(fixture.Root, ["--json"]); Assert.Equal(0, result.Exit);
            var data = Parse(result.Text).GetProperty("data"); Assert.Equal(1, data.GetProperty("sourceCount").GetInt32());
            Assert.Empty(data.GetProperty("entries").EnumerateArray()); Assert.False(data.GetProperty("truncated").GetBoolean());
            Assert.False(data.TryGetProperty("available", out _));
            var human = await ProcessCli(fixture.Root, []); Assert.Contains("read failures may be omitted", human.Text);
        }
        Assert.Equal(before, Fingerprint(fixture.Root)); Assert.Empty(fixture.ObservedErrors);
    }

    [Fact]
    public async Task MoreThan32FilesRetainsLegacyFileCapAndTruncationWithoutWriting()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        foreach (var index in Enumerable.Range(0, 33))
        { await File.WriteAllTextAsync(System.IO.Path.Combine(fixture.Root, $"fixture-{index}.log"), $"[{Time.AddSeconds(index):O}] error detail {index}"); }
        var before = Fingerprint(fixture.Root); var result = await ProcessCli(fixture.Root, ["--json"]);
        Assert.Equal(0, result.Exit); var data = Parse(result.Text).GetProperty("data");
        Assert.Equal(32, data.GetProperty("sourceCount").GetInt32()); Assert.Equal(32, data.GetProperty("entries").GetArrayLength());
        Assert.True(data.GetProperty("truncated").GetBoolean()); Assert.Equal(before, Fingerprint(fixture.Root));
        Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2))); Assert.Empty(fixture.ObservedErrors);
    }

    private static async Task<Result> Invoke(CliApplication app, string root, string[] options, bool json)
    {
        using var input = new StringReader(""); using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await app.RunAsync(["logs", "exceptions", "--root", root, "--non-interactive", .. options, .. (json ? new[] { "--json" } : [])], input, output, error);
        return new(exit, output.ToString(), error.ToString());
    }
    private static async Task<Result> ProcessCli(string root, string[] options)
    {
        var path = typeof(CliExceptionLogTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == "CliExecutablePath").Value!;
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in new[] { "logs", "exceptions", "--root", root, "--timeout", "5", "--non-interactive" }.Concat(options)) { info.ArgumentList.Add(argument); }
        using var process = Process.Start(info)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            return new(process.ExitCode, await stdout.WaitAsync(TimeSpan.FromSeconds(5)), await stderr.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
    private static string[] Fingerprint(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => path + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    private static JsonElement Parse(string text) { using var json = JsonDocument.Parse(text); return json.RootElement.Clone(); }
    private sealed record Result(int Exit, string Text, string Error);
    private sealed class Transport(object? data, string? error = null) : ICliTransport
    {
        public List<CliRequest> Calls { get; } = [];
        public Task<AgentResponse> SendAsync(CliRequest request, CancellationToken token)
        { Calls.Add(request); return Task.FromResult(new AgentResponse("fixture-request", error is null, data, Error: error, ErrorCode: error)); }
    }
    private sealed class Root : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("SystemAutorun-exception-cli-").FullName;
        public Root() => File.WriteAllText(System.IO.Path.Combine(Path, "autorun.yaml"), "version: 1\nsettings:\n  default_startup_delay: 0\ntasks: []\n");
        public void Dispose()
        {
            var full = System.IO.Path.GetFullPath(Path);
            Assert.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), full, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("SystemAutorun-exception-cli-", System.IO.Path.GetFileName(full), StringComparison.Ordinal);
            Directory.Delete(full, recursive: true);
        }
    }
}
