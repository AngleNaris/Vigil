using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Cli;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupInspectionControlPlaneTests
{
    private static StartupEntry DisabledShortcut => Entry with
    { Source = "Startup", IsDisabled = true, BackupId = "untrusted-record", CanModify = false, Target = @"\\offline.invalid\never-use-ledger-target.exe" };
    private static StartupRebuildInput Proposed => new("c:/Apps/never-created/Tool.exe", "--background \"two words\"", "C:/Apps/Work/");

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealConsoleAndTypedPipeReviewExplicitInputsWithoutGrantingNativeAuthority(bool file)
    {
        var entry = DisabledShortcut; var source = new Reader { Entries = [entry] };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var original = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var request = StartupInspectionRequest.FromEntry(entry) with { RebuildInput = Proposed };
        var payload = JsonSerializer.Serialize(request, AgentProtocol.JsonOptions);
        var inputPath = Path.Combine(fixture.Root, "rebuild-preview.json");
        if (file) { await File.WriteAllTextAsync(inputPath, payload); }
        var before = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order().ToArray();
        var bytes = await File.ReadAllBytesAsync(fixture.Paths.ConfigPath);
        var result = await Console(fixture.Root, ["startup", "inspect", entry.Id, "--file", file ? inputPath : "-", "--expected-revision", original.Revision], payload);
        Assert.Equal(0, result.Exit); Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Text);
        var data = json.RootElement.GetProperty("data"); var preview = data.GetProperty("rebuildPreview");
        Assert.Equal(@"C:\Apps\never-created\Tool.exe", preview.GetProperty("input").GetProperty("target").GetString());
        Assert.Equal("lexical_only", preview.GetProperty("validationScope").GetString());
        Assert.Equal("not_checked", preview.GetProperty("targetAvailability").GetString());
        Assert.Equal("not_checked", data.GetProperty("originalBackupState").GetString());
        Assert.False(data.GetProperty("nativeWriteReady").GetBoolean()); Assert.False(data.GetProperty("nativeWritePerformed").GetBoolean());
        Assert.False(data.GetProperty("executionRequested").GetBoolean()); Assert.Null(json.RootElement.GetProperty("operationId").GetString());
        var typed = await new AgentStartupClient(fixture.Client).InspectAsync(entry.Id, request, original.Revision);
        Assert.Equal(StartupRebuildInputPolicy.Preview(Proposed), typed.RebuildPreview);
        Assert.All(typed.Actions.Skip(1), action => Assert.True(action.State is "blocked" or "not_applicable"));
        var output = new StringWriter(); var error = new StringWriter();
        var exit = await new CliApplication().RunAsync(["startup", "inspect", entry.Id, "--file", "-", "--expected-revision", original.Revision, "--root", fixture.Root],
            new StringReader(payload), output, error);
        Assert.Equal(0, exit); Assert.Empty(error.ToString()); Assert.Contains("rebuild input preview: lexical_only", output.ToString());
        Assert.Contains("Native rebuild remains blocked", output.ToString());
        Assert.Contains("not original backup data, eligibility or confirmation", output.ToString());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Paths.ConfigPath));
        Assert.Equal(before, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.Empty((await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration.Tasks);
    }

    [Theory]
    [InlineData("target")] [InlineData("arguments")] [InlineData("workingDirectory")] [InlineData("injected")]
    [InlineData("missing-target")] [InlineData("null-target")] [InlineData("active")] [InlineData("registry")]
    public async Task ServerRejectsInvalidProposalsBeforeScanAndNeverDefaultsFromLedger(string defect)
    {
        var entry = defect == "active" ? DisabledShortcut with { IsDisabled = false, BackupId = null }
            : defect == "registry" ? DisabledShortcut with { Source = "HKCU" } : DisabledShortcut;
        var source = new Reader { Entries = [entry] };
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        var revision = (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Revision;
        var node = JsonSerializer.SerializeToNode(StartupInspectionRequest.FromEntry(entry) with { RebuildInput = Proposed }, AgentProtocol.JsonOptions)!;
        if (defect == "missing-target") { node["rebuildInput"]!.AsObject().Remove("target"); }
        else if (defect == "null-target") { node["rebuildInput"]!["target"] = null; }
        else if (defect == "target") { node["rebuildInput"]!["target"] = @"\\offline.invalid\never-probe\tool.exe"; }
        else if (defect == "arguments") { node["rebuildInput"]!["arguments"] = "nul\0argument"; }
        else if (defect == "workingDirectory") { node["rebuildInput"]!["workingDirectory"] = "relative"; }
        else if (defect == "injected") { node["rebuildInput"]!["nativeWriteReady"] = true; }
        var response = await fixture.Client.SendResponseAsync("startup-inspect", entry.Id, node, expectedRevision: revision);
        Assert.False(response.Ok); Assert.Equal("invalid_argument", response.ErrorCode); Assert.Null(response.OperationId);
        Assert.Equal(0, source.Calls); Assert.False(File.Exists(fixture.Paths.StartupBackupPath));
    }

    [Theory]
    [InlineData("target")] [InlineData("arguments")] [InlineData("workingDirectory")]
    public async Task EachEditRequiresItsOwnPreviewAndDoesNotChangeTheSelectedRecord(string field)
    {
        var entry = DisabledShortcut;
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(startupReader: new Reader { Entries = [entry] });
        var revision = (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Revision;
        var client = new AgentStartupClient(fixture.Client);
        var request = StartupInspectionRequest.FromEntry(entry) with { RebuildInput = Proposed };
        var initial = await client.InspectAsync(entry.Id, request, revision);
        var changed = field switch { "target" => Proposed with { Target = @"C:\Other\tool.com" },
            "arguments" => Proposed with { Arguments = "--other" }, _ => Proposed with { WorkingDirectory = @"C:\Other" } };
        var next = await client.InspectAsync(entry.Id, request with { RebuildInput = changed }, revision);
        Assert.NotEqual(initial.RebuildPreview!.InputDigest, next.RebuildPreview!.InputDigest);
        Assert.Equal(initial.MetadataDigest, next.MetadataDigest); Assert.Equal(entry, next.Entry);
        Assert.False(next.NativeWriteReady); Assert.Null((await fixture.Client.SendResponseAsync("ping")).OperationId);
    }
}
