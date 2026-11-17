using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Cli;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Recovery;
using Host = SystemAutorun.IntegrationTests.ConfigurationControlPlaneTests.ServerFixture;

namespace SystemAutorun.IntegrationTests;

public sealed partial class RecoveryCliPendingTests
{
    [Theory]
    [InlineData("valid", false)] [InlineData("valid", true)]
    [InlineData("corrupt", false)] [InlineData("corrupt", true)]
    [InlineData("directory", false)] [InlineData("directory", true)]
    [InlineData("oversized", true)] [InlineData("wrong-root", true)]
    [InlineData("wrong-subject", true)] [InlineData("wrong-name", true)]
    public async Task StateReportsOnlyVerifiedLocalHintOnStderrWithoutChangingAgentDataOrFiles(string hint, bool json)
    {
        using var fixture = new Fixture(); var original = AgentProtocol.NewOperationId();
        _ = await fixture.Pending.ReserveAsync(original, Revision);
        if (hint == "directory") { File.Delete(fixture.PendingPath); Directory.CreateDirectory(fixture.PendingPath); }
        else if (hint == "corrupt") { File.WriteAllText(fixture.PendingPath, "{broken"); }
        else if (hint == "oversized") { File.WriteAllText(fixture.PendingPath, new string('x', RecoveryPendingStore.MaximumBytes + 1)); }
        else if (hint.StartsWith("wrong-", StringComparison.Ordinal))
        {
            var record = JsonNode.Parse(File.ReadAllText(fixture.PendingPath))!.AsObject();
            var key = hint == "wrong-root" ? "rootId" : hint == "wrong-subject" ? "subjectKey" : "taskName";
            record[key] = hint == "wrong-root" ? new string('b', 64) : "different";
            File.WriteAllText(fixture.PendingPath, record.ToJsonString(AgentProtocol.JsonOptions));
        }
        File.WriteAllText(Path.Combine(fixture.Root, "unrelated.tmp"), "Queries must not clean this file.");
        var before = StateFingerprint(fixture.Root); var calls = new List<CliRequest>();
        var wire = StateWire(fixture.Root);
        using var output = new StringWriter(); using var error = new StringWriter();
        var app = new CliApplication(_ => new Fake(request => { calls.Add(request); return wire; }));
        var exit = await app.RunAsync(["recovery", "state", "--root", fixture.Root, .. (json ? new[] { "--json" } : Array.Empty<string>())],
            TextReader.Null, output, error);
        Assert.Equal(0, exit); Assert.Equal(before, StateFingerprint(fixture.Root));
        var call = Assert.Single(calls); Assert.Equal("recovery-state", call.Command);
        Assert.Null(call.Name); Assert.Null(call.OperationId); Assert.Null(call.Revision); Assert.Null(call.Payload);
        var diagnostic = error.ToString();
        Assert.Contains("local recovery hint", diagnostic); Assert.Contains("task: \"MEDIA\"", diagnostic);
        Assert.Contains(AgentProtocol.ForRoot(fixture.Root).RootId, diagnostic);
        Assert.Contains("returned recovery task names only", diagnostic);
        if (hint == "valid")
        {
            Assert.Contains("state: Present", diagnostic); Assert.Contains("original operationId: " + original, diagnostic);
            Assert.Contains("expectedRevision: " + Revision, diagnostic); Assert.Contains("NOT admission", diagnostic);
            Assert.Contains("History/current state does not unlock", diagnostic);
        }
        else
        {
            Assert.Contains("state: Unavailable", diagnostic); Assert.Contains("no operation ID or permission is inferred", diagnostic);
            Assert.DoesNotContain(original, diagnostic); Assert.DoesNotContain("original operationId:", diagnostic);
        }
        if (json)
        {
            using var result = JsonDocument.Parse(output.ToString()); var envelope = result.RootElement;
            Assert.True(envelope.GetProperty("ok").GetBoolean()); Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
            Assert.Null(envelope.GetProperty("operationId").GetString()); Assert.Null(envelope.GetProperty("error").GetString());
            Assert.Equal(wire.Revision, envelope.GetProperty("revision").GetString());
            Assert.Equal(JsonSerializer.SerializeToElement(wire.Data, AgentProtocol.JsonOptions).GetRawText(), envelope.GetProperty("data").GetRawText());
            Assert.DoesNotContain("local recovery hint", output.ToString());
            Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        else
        {
            Assert.Contains("futureField", output.ToString()); Assert.DoesNotContain("local recovery hint", output.ToString());
        }
    }

    [Theory]
    [InlineData("missing")] [InlineData("other-name")] [InlineData("uninitialized")]
    public async Task StateWithoutAReturnedBlockedNameDoesNotInventADiagnosticOrUnlockUnlistedHints(string scope)
    {
        using var fixture = new Fixture();
        if (scope != "missing") { _ = await fixture.Pending.ReserveAsync(AgentProtocol.NewOperationId(), Revision); }
        var before = StateFingerprint(fixture.Root);
        var wire = scope == "uninitialized"
            ? new AgentResponse("state", true, new RecoveryControlSnapshot(Revision, [], false), Revision: Revision, RootId: AgentProtocol.ForRoot(fixture.Root).RootId)
            : StateWire(fixture.Root, scope == "other-name" ? "different" : "MEDIA");
        var result = await Run(new(_ => new Fake(_ => wire)), fixture.Root, ["recovery", "state"]);
        Assert.Equal(0, result.Exit); Assert.Empty(result.Error); Assert.Equal(before, StateFingerprint(fixture.Root));
        Assert.Null(result.Json.GetProperty("operationId").GetString());
    }

    [Theory]
    [InlineData("malformed")] [InlineData("wrong-root")] [InlineData("replayed")]
    public async Task InvalidAgentStateCannotSupplyNamesForLocalHintDiagnostics(string kind)
    {
        using var fixture = new Fixture(); var original = AgentProtocol.NewOperationId();
        _ = await fixture.Pending.ReserveAsync(original, Revision); var before = StateFingerprint(fixture.Root);
        var wire = StateWire(fixture.Root);
        wire = kind switch
        {
            "malformed" => wire with { Data = new { marker = "not a recovery snapshot" } },
            "wrong-root" => wire with { RootId = new string('b', 64) },
            _ => wire with { Replayed = true },
        };
        var result = await Run(new(_ => new Fake(_ => wire)), fixture.Root, ["recovery", "state"]);
        Assert.Equal(3, result.Exit); Assert.Equal("protocol_mismatch", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("data").ValueKind);
        Assert.Empty(result.Error); Assert.Equal(before, StateFingerprint(fixture.Root));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedOrOfflineAgentDoesNotBecomeSuccessfulLocalStateOrInventARecoveryId(bool offline)
    {
        using var fixture = new Fixture(); var original = AgentProtocol.NewOperationId();
        _ = await fixture.Pending.ReserveAsync(original, Revision); var before = StateFingerprint(fixture.Root);
        var wire = StateWire(fixture.Root) with { Ok = false, Data = null, Error = "Fixture denial", ErrorCode = "permission_denied" };
        var result = await Run(new(_ => new Fake(_ => offline ? throw new IOException("Fixture offline") : wire)), fixture.Root, ["recovery", "state"]);
        Assert.Equal(offline ? 3 : 4, result.Exit); Assert.False(result.Json.GetProperty("ok").GetBoolean());
        Assert.Null(result.Json.GetProperty("operationId").GetString()); Assert.Empty(result.Error);
        Assert.Equal(before, StateFingerprint(fixture.Root));
    }

    [Fact]
    public async Task IndependentConsoleReportsAnUnadmittedLocalHintAgainstRealAgentStateWithoutQueryingJournal()
    {
        await using var host = new Host(); var store = new RecoveryControlStore(host.Paths);
        var missing = await store.ReadAsync(); await store.InitializeEmptyAsync(missing.Revision);
        var current = await store.SetPausedAsync("media", true, recordIntent: true);
        var original = AgentProtocol.NewOperationId(); var pending = new RecoveryPendingStore(host.Paths, "media");
        _ = await pending.ReserveAsync(original, current.Revision);
        var sentinel = Path.Combine(host.Root, ".autorun-state", CurrentUserIdentity.Key, "operations", "unrelated.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!); File.WriteAllText(sentinel, "No Journal query or orphan cleanup authorized.");
        var before = StateFingerprint(host.Root);
        var result = await Console(host.Root, ["recovery", "state"]);
        Assert.Equal(0, result.Exit); Assert.Contains("state: Present", result.Error);
        Assert.Contains("original operationId: " + original, result.Error); Assert.Contains("NOT admission", result.Error);
        Assert.Equal(current.Revision, result.Json.GetProperty("revision").GetString());
        Assert.Null(result.Json.GetProperty("operationId").GetString());
        Assert.True(Assert.Single(result.Json.GetProperty("data").GetProperty("tasks").EnumerateArray()).GetProperty("paused").GetBoolean());
        Assert.Equal(before, StateFingerprint(host.Root)); Assert.Empty(host.ObservedErrors);
        Assert.Empty(Directory.EnumerateFiles(host.Root, "*.operation.json", SearchOption.AllDirectories));
        Assert.Equal(original, (await pending.ReadAsync()).Operation!.OperationId);
        if (Environment.GetEnvironmentVariable("SA_RECOVERY_STATE_EVIDENCE") is { Length: > 0 } output)
        {
            var directory = new DirectoryInfo(Path.GetFullPath(output));
            if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) { throw new IOException("Expected an ordinary existing evidence directory."); }
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "console-state.ndjson"), result.Json.GetRawText() + "\n");
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "console-state.stderr.txt"), result.Error);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "live-state.json"), JsonSerializer.Serialize(new
            { host.Root, host.Endpoint.RootId, OriginalOperationId = original, current.Revision, Initialized = true, Paused = true, JournalReceipts = 0, WorkloadExecutor = "NeverRunExecutor", FilesUnchanged = true }, AgentProtocol.JsonOptions));
        }
    }

    private static AgentResponse StateWire(string root, string name = "MEDIA") => new("state", true,
        new { revision = new string('b', 64), initialized = true, tasks = new[] { new RecoveryTaskState(name, IntentId: AgentProtocol.NewOperationId()) }, futureField = "preserved" },
        Revision: new string('b', 64), RootId: AgentProtocol.ForRoot(root).RootId);
    private static string[] StateFingerprint(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + (Directory.Exists(path) ? "directory" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();
}
