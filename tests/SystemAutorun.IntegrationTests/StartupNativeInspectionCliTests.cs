using System.IO;
using System.Text.Json;
using SystemAutorun.Cli;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using static SystemAutorun.IntegrationTests.StartupNativeInspectionControlPlaneTests;

namespace SystemAutorun.IntegrationTests;

// Real CLI/Agent Pipe, with explicitly simulated source data and getter schedules.
public sealed class StartupNativeInspectionCliTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExplicitModeKeepsDefaultSchemaAndOneReadOnlyRpcWithoutDaemonLaunch(bool native)
    {
        var metadata = new MetadataReader(); var slots = new SlotReader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        var revision = await Revision(fixture); var starts = 0;
        var result = await Run(fixture.Root, Entry, revision, native: native, application: new(startDaemon: (_, _) => { starts++; throw new InvalidOperationException("No launch allowed."); }));
        Assert.Equal(0, result.Exit); Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output); var root = json.RootElement; var data = root.GetProperty("data");
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32()); Assert.Null(root.GetProperty("operationId").GetString());
        Assert.Equal(native ? 2 : 1, data.GetProperty("schemaVersion").GetInt32()); Assert.Equal(revision, root.GetProperty("revision").GetString());
        Assert.NotEqual(JsonValueKind.Null, root.GetProperty("observedAt").ValueKind);
        Assert.Equal(native ? 1 : 0, slots.Calls); Assert.Equal(native ? 2 : 1, metadata.Calls); Assert.Equal(0, starts);
        Assert.False(data.GetProperty("nativeWriteReady").GetBoolean()); Assert.False(data.GetProperty("executionRequested").GetBoolean());
    }

    [Theory]
    [InlineData("active")] [InlineData("registry")]
    public async Task UnsupportedNativeSelectorIsRejectedBeforeConnectingOrScanning(string defect)
    {
        var slots = new SlotReader(); var metadata = new MetadataReader(); var connections = 0;
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        var entry = defect == "active" ? Entry with { IsDisabled = false } : Entry with { Source = "HKCU" };
        var result = await Run(fixture.Root, entry, await Revision(fixture), application: new(_ => { connections++; throw new InvalidOperationException("No connect allowed."); }));
        Assert.Equal(2, result.Exit); Assert.Equal(0, connections); Assert.Equal(0, metadata.Calls); Assert.Equal(0, slots.Calls);
    }

    [Theory]
    [InlineData("confirm")] [InlineData("operation")] [InlineData("valued")] [InlineData("duplicate")]
    [InlineData("wrong-command")] [InlineData("scope")]
    public async Task NativeFlagCannotBecomeAWriteConfirmationOrAnImplicitModeForOtherCommands(string defect)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); var connections = 0; var revision = await Revision(fixture);
        var arguments = Arguments(fixture.Root, Entry.Id, revision, true, true).ToList();
        if (defect == "confirm") { arguments.Add("--yes"); }
        else if (defect == "operation") { arguments.AddRange(["--operation-id", AgentProtocol.NewOperationId()]); }
        else if (defect == "valued") { arguments[arguments.IndexOf("--native")] = "--native=true"; }
        else if (defect == "duplicate") { arguments.Add("--native"); }
        else if (defect == "scope") { arguments.AddRange(["--scope", "user"]); }
        else { arguments = ["task", "list", "--native", "--json", "--root", fixture.Root]; }
        var output = new StringWriter(); var error = new StringWriter();
        var exit = await new CliApplication(_ => { connections++; throw new InvalidOperationException("No connect allowed."); })
            .RunAsync(arguments.ToArray(), new StringReader("{}"), output, error);
        Assert.Equal(2, exit); Assert.Equal(0, connections); Assert.Empty(error.ToString());
        using var json = JsonDocument.Parse(output.ToString()); Assert.Equal("invalid_argument", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("occupied", true)] [InlineData("occupied", false)]
    [InlineData("permission", true)] [InlineData("permission", false)]
    [InlineData("context", true)] [InlineData("context", false)]
    public async Task UnknownSlotsPreserveDataButNeverClaimSuccessfulCompleteObservation(string cause, bool json)
    {
        var slots = new SlotReader { Factory = () => Task.FromResult(Unknown(cause)) };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: new MetadataReader(), startupSlotReader: slots);
        var result = await Run(fixture.Root, Entry, await Revision(fixture), json: json);
        Assert.Equal(cause == "permission" ? 4 : 1, result.Exit); Assert.Equal(1, slots.Calls);
        if (json)
        {
            Assert.Empty(result.Error); using var document = JsonDocument.Parse(result.Output); var root = document.RootElement;
            Assert.False(root.GetProperty("ok").GetBoolean()); Assert.Null(root.GetProperty("operationId").GetString());
            Assert.Equal("unknown", root.GetProperty("data").GetProperty("slots").GetProperty("original").GetProperty("state").GetString());
            Assert.False(root.GetProperty("data").GetProperty("nativeWriteReady").GetBoolean());
        }
        else
        {
            Assert.Contains(cause == "permission" ? "startup_native_permission_denied" : "startup_native_observation_unknown", result.Error);
            Assert.Contains("original: unknown", result.Output); Assert.Contains("historical identity: unverified", result.Output);
            Assert.Contains("No restore/rebuild eligibility", result.Output); Assert.DoesNotContain("metadata-only", result.Output);
        }
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task ExplicitLexicalPreviewSurvivesNativeModeWithoutBecomingAnEligibilityCheck(bool json)
    {
        var slots = new SlotReader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: new MetadataReader(), startupSlotReader: slots);
        var preview = new StartupRebuildInput(@"C:\never-created\program.exe", "--review only", @"C:\never-created");
        var result = await Run(fixture.Root, Entry, await Revision(fixture), json: json, request: StartupInspectionRequest.FromEntry(Entry) with { RebuildInput = preview });
        Assert.Equal(0, result.Exit); Assert.Empty(result.Error); Assert.Equal(1, slots.Calls);
        if (json)
        {
            using var document = JsonDocument.Parse(result.Output); var data = document.RootElement.GetProperty("data");
            Assert.Equal(preview.Target, data.GetProperty("metadata").GetProperty("rebuildPreview").GetProperty("input").GetProperty("target").GetString());
            Assert.Equal("not_checked", data.GetProperty("metadata").GetProperty("originalBackupState").GetString());
        }
        else { Assert.Contains("rebuild input preview: lexical_only", result.Output); Assert.Contains("Native rebuild remains blocked", result.Output); }
    }

    [Theory]
    [InlineData("metadata", "startup_entry_changed")] [InlineData("record", "startup_backup_changed")] [InlineData("configuration", "revision_conflict")]
    public async Task ChangesBeforeThePostcheckReturnConflictWithoutPartialEvidenceOrAutomaticRetry(string change, string code)
    {
        var metadata = new MetadataReader(); var slots = new SlotReader();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        var revision = await Revision(fixture);
        if (change == "metadata") { metadata.Factory = call => Inventory(call == 1 ? Entry : Entry with { Name = "changed" }); }
        else if (change == "record") { slots.Factory = () => Task.FromResult(Evidence() with { RecordMetadataDigestAfter = new string('b', 64) }); }
        else { slots.Factory = async () => { _ = await fixture.Client.SendResponseAsync("settings-patch", payload: new { autoStartMissingOnLaunch = true }, expectedRevision: revision); return Evidence(); }; }
        var result = await Run(fixture.Root, Entry, revision); Assert.Equal(5, result.Exit); Assert.Equal(1, slots.Calls);
        using var document = JsonDocument.Parse(result.Output); var root = document.RootElement;
        Assert.Equal(code, root.GetProperty("error").GetProperty("code").GetString()); Assert.Equal(JsonValueKind.Null, root.GetProperty("data").ValueKind);
        Assert.Null(root.GetProperty("operationId").GetString());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancelOrServerDeadlineIsExplicitAndNeverReleasesANonCooperativeReader(bool callerCancel)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StartupSlotEvidenceRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slots = new SlotReader { Factory = () => { entered.TrySetResult(); return release.Task; } };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: new MetadataReader(), startupSlotReader: slots);
        var revision = await Revision(fixture); using var cancellation = new CancellationTokenSource();
        var pending = Run(fixture.Root, Entry, revision, cancellationToken: cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(4)); if (callerCancel) { cancellation.Cancel(); }
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(8)); Assert.Equal(6, result.Exit);
            using var document = JsonDocument.Parse(result.Output); var root = document.RootElement;
            Assert.Equal(callerCancel ? "cancelled" : "startup_native_observation_timeout", root.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("data").ValueKind); Assert.Null(root.GetProperty("operationId").GetString());
            var busy = await Run(fixture.Root, Entry, revision); Assert.Equal(1, busy.Exit);
            using var busyJson = JsonDocument.Parse(busy.Output); Assert.Equal("startup_scan_busy", busyJson.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(1, slots.Calls);
        }
        finally { release.TrySetResult(Evidence()); await pending; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)); var client = new AgentStartupClient(fixture.Client);
        while ((await client.ReadAsync(new(), deadline.Token)).Reason == "startup_scan_busy") { await Task.Delay(10, deadline.Token); }
    }

    [Fact]
    public void HelpDocumentsBothSchemasPartialUnknownAndTheStillClosedNativeWrites()
    {
        Assert.Contains("[--native]", CliApplication.HelpText); Assert.Contains("Default: schema 1", CliApplication.HelpText);
        Assert.Contains("schema 2 read-only", CliApplication.HelpText); Assert.Contains("Both missing still grants", CliApplication.HelpText);
        Assert.Contains("Unknown slots retain data with exit 1 (permission: 4)", CliApplication.HelpText);
    }

    private static StartupSlotEvidenceRead Unknown(string cause)
    {
        var read = Evidence(); var original = read.Evidence.Original;
        if (cause == "context")
        {
            return read with { Evidence = read.Evidence with
            {
                Archive = read.Evidence.Archive with { State = "unknown", Reason = "archive_context_invalid", NativeErrorCode = null, ArchiveDirectory = null, DirectoryIdentity = null, CaseSensitivity = "unknown" },
                Original = original with { State = "unknown", Reason = "original_context_invalid", NativeErrorCode = null, SourceDirectory = null, DirectoryIdentity = null, CaseSensitivity = "unknown" },
            } };
        }
        return read with { Evidence = read.Evidence with { Original = original with { State = "unknown", Reason = cause == "permission" ? "original_permission_denied" : "original_occupied", NativeErrorCode = cause == "permission" ? 5 : 32 } } };
    }

    internal static string[] Arguments(string root, string id, string revision, bool native, bool json) =>
        ["startup", "inspect", id, "--file", "-", "--expected-revision", revision, "--root", root, .. native ? new[] { "--native" } : [], .. json ? new[] { "--json" } : []];
    internal static async Task<(int Exit, string Output, string Error)> Run(string root, StartupEntry entry, string revision,
        bool native = true, bool json = true, StartupInspectionRequest? request = null, CliApplication? application = null, CancellationToken cancellationToken = default)
    {
        var output = new StringWriter(); var error = new StringWriter();
        var exit = await (application ?? new()).RunAsync(Arguments(root, entry.Id, revision, native, json),
            new StringReader(JsonSerializer.Serialize(request ?? StartupInspectionRequest.FromEntry(entry), AgentProtocol.JsonOptions)), output, error, cancellationToken);
        return (exit, output.ToString(), error.ToString());
    }
}
