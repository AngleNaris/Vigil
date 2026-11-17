using System.IO;
using System.Text;
using System.Text.Json;
using SystemAutorun.Cli;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.IntegrationTests;

public sealed class ConfigurationDocumentControlPlaneTests
{
    private const string Candidate = "# preserve 注释\r\nversion: 1\r\nsettings:\r\n  default_startup_delay: 0\r\ntasks:\r\n  - name: fixture\r\n    type: wsl_boot\r\n    enabled: false\r\n    distro: never-launch\r\n    health:\r\n      type: wsl_running\r\nfuture_field: {keep: true}\r\n";

    [Fact]
    public async Task RealPipeCliEditValidateApplyReplayAndConflictShareTheSameRevision()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var original = await File.ReadAllBytesAsync(fixture.Paths.ConfigPath);
        var before = await fixture.Client.SendAsync<ConfigurationDocumentSnapshot>("configuration-document");
        var raw = await Invoke(fixture.Root, ["config", "show", "--raw"]);
        Assert.Equal(0, raw.Exit); Assert.Equal(before.Utf8Base64Url, raw.Json.GetProperty("data").GetProperty("utf8Base64Url").GetString());
        using var plain = new StringWriter(); using var diagnostics = new StringWriter();
        var code = await new CliApplication().RunAsync(["--root", fixture.Root, "config", "show", "--raw"], new StringReader(""), plain, diagnostics);
        Assert.Equal(0, code); Assert.Equal(Encoding.UTF8.GetString(original), plain.ToString());
        Assert.Equal("revision: " + before.Revision + Environment.NewLine, diagnostics.ToString());
        var validated = await Invoke(fixture.Root, ["config", "validate", "--file", "-"], Candidate);
        Assert.Equal(0, validated.Exit); Assert.True(validated.Json.GetProperty("data").GetProperty("valid").GetBoolean());
        Assert.Equal("fixture", Assert.Single(validated.Json.GetProperty("data").GetProperty("changes").GetProperty("addedTasks").EnumerateArray()).GetString());
        Assert.Equal(before.Revision, validated.Json.GetProperty("revision").GetString());
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "backups")));
        var operation = AgentProtocol.NewOperationId();
        string[] applyArgs = ["config", "apply", "--file", "-", "--yes", "--expected-revision", before.Revision, "--operation-id", operation];
        var applied = await Invoke(fixture.Root, applyArgs, Candidate);
        Assert.Equal(0, applied.Exit); Assert.Empty(applied.Error);
        Assert.Equal(operation, applied.Json.GetProperty("operationId").GetString());
        var backup = applied.Json.GetProperty("data").GetProperty("value").GetProperty("backupPath").GetString()!;
        Assert.Equal(original, await File.ReadAllBytesAsync(backup));
        Assert.Equal(Candidate, await File.ReadAllTextAsync(fixture.Paths.ConfigPath));
        Assert.NotEqual(before.Revision, applied.Json.GetProperty("revision").GetString());
        var replay = await Invoke(fixture.Root, applyArgs, Candidate);
        Assert.Equal(0, replay.Exit); Assert.True(replay.Json.GetProperty("replayed").GetBoolean());
        Assert.Equal(backup, replay.Json.GetProperty("data").GetProperty("value").GetProperty("backupPath").GetString());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(backup)!));
        var changedArguments = await Invoke(fixture.Root, applyArgs, Candidate + "# different\n");
        Assert.Equal(1, changedArguments.Exit); Assert.Equal("operation_id_conflict", changedArguments.Json.GetProperty("error").GetProperty("code").GetString());
        var conflict = await Invoke(fixture.Root, ["config", "apply", "--file", "-", "--yes", "--expected-revision", before.Revision], Candidate);
        Assert.Equal(5, conflict.Exit); Assert.Equal("revision_conflict", conflict.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(applied.Json.GetProperty("revision").GetString(), conflict.Json.GetProperty("revision").GetString());
        var receipt = await Invoke(fixture.Root, ["operation", "show", operation]);
        Assert.Equal(0, receipt.Exit); Assert.False(fixture.IsStopping);
        Assert.Single((await fixture.Cache.GetAsync()).Tasks); // Same cache consumed by task/GUI control plane.
    }

    [Fact]
    public async Task ReplyLostAfterRealCommitReturnsUnknownWithoutRetryAndCanBeResolvedBySameId()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var before = await fixture.Client.SendAsync<ConfigurationDocumentSnapshot>("configuration-document");
        var operation = AgentProtocol.NewOperationId(); var transport = new LostReplyTransport(fixture.Client);
        string[] args = ["config", "apply", "--file", "-", "--yes", "--expected-revision", before.Revision, "--operation-id", operation];
        var unknown = await Invoke(fixture.Root, args, Candidate, new CliApplication(_ => transport));
        Assert.Equal(6, unknown.Exit); Assert.Equal(1, transport.Calls);
        Assert.Equal("outcome_unknown", unknown.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(operation, unknown.Json.GetProperty("operationId").GetString());
        Assert.Equal(Candidate, await File.ReadAllTextAsync(fixture.Paths.ConfigPath));
        var receipt = await Invoke(fixture.Root, ["operation", "show", operation]);
        Assert.Equal(0, receipt.Exit);
        var resolved = await Invoke(fixture.Root, args, Candidate);
        Assert.Equal(0, resolved.Exit); Assert.True(resolved.Json.GetProperty("replayed").GetBoolean());
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "backups", "config")));
    }

    [Theory]
    [InlineData("version: [")]
    [InlineData("version: 1\nsettings: {}\ntasks: []\n---\nother: value\n")]
    public async Task InvalidYamlReturnsExitTwoWithValidationDataAndNoMutation(string yaml)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var before = await fixture.Client.SendAsync<ConfigurationDocumentSnapshot>("configuration-document");
        var result = await Invoke(fixture.Root, ["config", "validate", "--file", "-"], yaml);
        Assert.Equal(2, result.Exit); Assert.Empty(result.Error); Assert.False(result.Json.GetProperty("ok").GetBoolean());
        Assert.False(result.Json.GetProperty("data").GetProperty("valid").GetBoolean());
        Assert.Equal("configuration_error", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(before.Revision, result.Json.GetProperty("revision").GetString());
        Assert.Equal(before.Utf8Base64Url, (await fixture.Client.SendAsync<ConfigurationDocumentSnapshot>("configuration-document")).Utf8Base64Url);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "backups"))); Assert.False(fixture.IsStopping);
    }

    [Fact]
    public async Task BackendRequiresConfirmationRevisionAndCanonicalUtf8EvenWithoutTheCli()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var before = await fixture.Client.SendAsync<ConfigurationDocumentSnapshot>("configuration-document");
        var input = ConfigurationDocumentRequest.FromText(Candidate);
        var unconfirmed = await fixture.Client.SendResponseAsync("configuration-apply", payload: input, expectedRevision: before.Revision);
        Assert.False(unconfirmed.Ok); Assert.Equal("authorization_required", unconfirmed.ErrorCode);
        var noRevision = await fixture.Client.SendResponseAsync("configuration-apply", payload: input with { Confirmed = true });
        Assert.False(noRevision.Ok); Assert.Equal("invalid_argument", noRevision.ErrorCode);
        foreach (var encoded in new[] { "Zh", "_w", "=" })
        {
            var malformed = await fixture.Client.SendResponseAsync("configuration-apply", payload: new ConfigurationDocumentRequest(encoded, true), expectedRevision: before.Revision);
            Assert.False(malformed.Ok); Assert.Equal("invalid_argument", malformed.ErrorCode);
        }
        Assert.Equal(before.Revision, (await fixture.Client.SendAsync<ConfigurationDocumentSnapshot>("configuration-document")).Revision);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "backups")));
    }

    [Fact]
    public async Task FileInputPreservesUtf8BomAndNoOpCreatesNoSecondBackup()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var path = Path.Combine(fixture.Root, "edit.yaml");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Candidate)).ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        var before = await fixture.Client.SendAsync<ConfigurationDocumentSnapshot>("configuration-document");
        var applied = await Invoke(fixture.Root, ["config", "apply", "--file", path, "--yes", "--expected-revision", before.Revision]);
        Assert.Equal(0, applied.Exit); Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Paths.ConfigPath));
        var unchanged = await Invoke(fixture.Root, ["config", "apply", "--file", path, "--yes", "--expected-revision", applied.Json.GetProperty("revision").GetString()!]);
        Assert.Equal(0, unchanged.Exit); Assert.False(unchanged.Json.GetProperty("data").GetProperty("value").GetProperty("changed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, unchanged.Json.GetProperty("data").GetProperty("value").GetProperty("backupPath").ValueKind);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "backups", "config")));
    }

    [Fact]
    public async Task InvalidUtf8RawSnapshotCanBeInspectedAndExplicitlyRepairedWithoutDefaulting()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        byte[] invalid = [0xff, 0x01]; await File.WriteAllBytesAsync(fixture.Paths.ConfigPath, invalid);
        var raw = await Invoke(fixture.Root, ["config", "show", "--raw"]);
        Assert.Equal(0, raw.Exit); Assert.False(raw.Json.GetProperty("data").GetProperty("utf8Valid").GetBoolean());
        Assert.Equal(ConfigurationDocumentRequest.Encode(invalid), raw.Json.GetProperty("data").GetProperty("utf8Base64Url").GetString());
        using var plain = new StringWriter(); using var error = new StringWriter();
        var failed = await new CliApplication().RunAsync(["--root", fixture.Root, "config", "show", "--raw"], new StringReader(""), plain, error);
        Assert.Equal(2, failed); Assert.Empty(plain.ToString()); Assert.Contains("not valid UTF-8", error.ToString(), StringComparison.Ordinal);
        var repaired = await Invoke(fixture.Root, ["config", "apply", "--file", "-", "--yes", "--expected-revision", raw.Json.GetProperty("revision").GetString()!], Candidate);
        Assert.Equal(0, repaired.Exit);
        var value = repaired.Json.GetProperty("data").GetProperty("value");
        Assert.False(value.GetProperty("changes").GetProperty("available").GetBoolean());
        Assert.Equal(invalid, await File.ReadAllBytesAsync(value.GetProperty("backupPath").GetString()!));
    }

    [Theory]
    [InlineData("config apply --file - --expected-revision REV", 4)]
    [InlineData("config apply --file - --yes", 2)]
    [InlineData("config validate", 2)]
    [InlineData("settings get --raw", 2)]
    [InlineData("config validate --file - --operation-id OP", 2)]
    public async Task InvalidCliOptionsNeverConnectOrStartTheAgent(string command, int expected)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var calls = 0;
        var app = new CliApplication(_ => { calls++; throw new InvalidOperationException("Must not connect"); }, (_, _) => { calls++; throw new InvalidOperationException("Must not start"); });
        var result = await Invoke(fixture.Root, command.Replace("REV", new string('a', 64), StringComparison.Ordinal).Replace("OP", AgentProtocol.NewOperationId(), StringComparison.Ordinal).Split(' '), Candidate, app);
        Assert.Equal(expected, result.Exit); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OversizedAndNonUtf8FileInputsAreRejectedBeforeConnecting()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var calls = 0;
        var app = new CliApplication(_ => { calls++; throw new InvalidOperationException("Must not connect"); });
        var oversized = await Invoke(fixture.Root, ["config", "validate", "--file", "-"], new string('x', ConfigurationDocumentRequest.MaximumBytes + 1), app);
        Assert.Equal(2, oversized.Exit);
        var path = Path.Combine(fixture.Root, "invalid.yaml"); await File.WriteAllBytesAsync(path, [0xff]);
        var invalid = await Invoke(fixture.Root, ["config", "validate", "--file", path], app: app);
        Assert.Equal(2, invalid.Exit); Assert.Equal(0, calls);
    }

    private sealed class LostReplyTransport(AgentClient client) : ICliTransport
    {
        public int Calls { get; private set; }
        public async Task<AgentResponse> SendAsync(CliRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = await client.SendResponseAsync(request.Command, request.Name, request.Payload, request.Timeout, cancellationToken, request.Revision, request.OperationId);
            Assert.True(response.Ok, response.Error);
            throw new AgentOutcomeUnknownException(request.OperationId!, new IOException("Injected reply loss after the real Agent persisted its result."));
        }
    }

    private static async Task<(int Exit, JsonElement Json, string Error)> Invoke(string root, string[] args, string input = "", CliApplication? app = null)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var code = await (app ?? new()).RunAsync(["--root", root, "--json", .. args], new StringReader(input), output, error);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Single(lines);
        return (code, JsonDocument.Parse(lines[0]).RootElement.Clone(), error.ToString());
    }
}
