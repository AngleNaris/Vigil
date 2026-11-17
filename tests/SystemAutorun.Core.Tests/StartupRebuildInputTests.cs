using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

public sealed class StartupRebuildInputTests
{
    [Theory]
    [InlineData("c:/Apps/媒体库/Tool.EXE", "C:\\Apps\\媒体库\\Tool.EXE")]
    [InlineData("D:\\never-created\\tool.com", "D:\\never-created\\tool.com")]
    [InlineData("C:\\🖤\\tool.exe", "C:\\🖤\\tool.exe")]
    public void PreviewIsLexicalIdempotentAndNeverCertifiesAvailability(string target, string expected)
    {
        const string arguments = "--literal \"two words\"\t%NEVER_EXPAND% \\\\offline.invalid\\argument";
        var preview = StartupRebuildInputPolicy.Preview(new(target, arguments, "c:/Apps/Work/"));
        Assert.Equal(expected, preview.Input.Target); Assert.Equal(arguments, preview.Input.Arguments);
        Assert.Equal(@"C:\Apps\Work", preview.Input.WorkingDirectory);
        Assert.Equal("lexical_only", preview.ValidationScope); Assert.Equal("not_checked", preview.TargetAvailability);
        Assert.Equal(preview, StartupRebuildInputPolicy.Preview(preview.Input));
        Assert.Equal(64, preview.InputDigest.Length); Assert.All(preview.InputDigest, c => Assert.True(char.IsAsciiHexDigit(c) && !char.IsAsciiLetterUpper(c)));
        Assert.Equal(preview, JsonSerializer.Deserialize<StartupRebuildPreview>(JsonSerializer.Serialize(preview, AgentProtocol.JsonOptions), AgentProtocol.JsonOptions));
    }

    [Theory]
    [InlineData("")] [InlineData("tool.exe")] [InlineData("C:tool.exe")] [InlineData("/Apps/tool.exe")]
    [InlineData("\\\\offline.invalid\\share\\tool.exe")] [InlineData("\\\\?\\C:\\tool.exe")] [InlineData("\\\\.\\C:\\tool.exe")]
    [InlineData("%ROOT%\\tool.exe")] [InlineData("C:\\%APP%\\tool.exe")]
    [InlineData("C:\\Apps\\..\\tool.exe")] [InlineData("C:\\Apps\\.\\tool.exe")]
    [InlineData("C:\\Apps\\\\tool.exe")] [InlineData("C:\\Apps \\tool.exe")] [InlineData("C:\\Apps.\\tool.exe")]
    [InlineData("C:\\Apps\\tool.exe:stream")] [InlineData("C:\\Apps\\*.exe")] [InlineData("\"C:\\Apps\\tool.exe\"")]
    [InlineData("C:\\Apps\\tool.exe --flag")] [InlineData("C:\\Apps\\tool.cmd")] [InlineData("C:\\Apps\\tool.ps1")]
    [InlineData("C:\\Apps\\tool.bat")] [InlineData("shell:AppsFolder\\tool.exe")]
    [InlineData("C:\\NUL\\tool.exe")] [InlineData("C:\\CON.exe")] [InlineData("C:\\CON .exe")]
    [InlineData("C:\\COM1.exe")] [InlineData("C:\\LPT¹\\tool.exe")] [InlineData("C:\\CONIN$\\tool.exe")]
    [InlineData("C:\\Apps\\tool.exe\n")] [InlineData("C:\\Apps\\tool\0.exe")]
    public void UnsupportedTargetsAreRejectedNotExpandedSanitizedOrProbed(string value) =>
        Assert.Throws<ArgumentException>(() => StartupRebuildInputPolicy.Preview(new(value)));

    [Theory]
    [InlineData("C:relative")] [InlineData("..\\work")] [InlineData("\\\\offline.invalid\\work")]
    [InlineData("C:\\work\\..")][InlineData("C:\\\\")] [InlineData(" ")]
    public void WorkingDirectoryUsesTheSameOrdinaryPathBoundary(string value) =>
        Assert.Throws<ArgumentException>(() => StartupRebuildInputPolicy.Preview(new(@"C:\Apps\tool.exe", WorkingDirectory: value)));

    [Fact]
    public void DefaultsAreExplicitAndCaseAndEachConfirmedFieldRemainSignificant()
    {
        var input = new StartupRebuildInput(@"C:\Apps\tool.exe");
        Assert.Equal("ea49289d367c860ff38bda3926f0cd67859a64480f8504f7a071c9af80a110ec",
            StartupRebuildInputPolicy.Preview(input with { Arguments = "--safe" }).InputDigest);
        var original = StartupRebuildInputPolicy.Preview(input);
        Assert.Equal("", original.Input.Arguments); Assert.Null(original.Input.WorkingDirectory);
        Assert.Equal(original, StartupRebuildInputPolicy.Preview(input with { Arguments = "", WorkingDirectory = "" }));
        Assert.Equal(@"C:\", StartupRebuildInputPolicy.Preview(input with { WorkingDirectory = @"C:\" }).Input.WorkingDirectory);
        Assert.NotEqual(original.InputDigest, StartupRebuildInputPolicy.Preview(input with { Target = @"C:\apps\tool.exe" }).InputDigest);
        Assert.NotEqual(original.InputDigest, StartupRebuildInputPolicy.Preview(input with { Arguments = " " }).InputDigest);
        Assert.NotEqual(original.InputDigest, StartupRebuildInputPolicy.Preview(input with { WorkingDirectory = @"C:\" }).InputDigest);
    }

    [Theory]
    [InlineData("target")] [InlineData("args")] [InlineData("workdir")]
    public void OversizeControlsAndMalformedUnicodeCannotCollideViaSerialization(string field)
    {
        foreach (var bad in new[] { new string('x', 8193), "bad\nvalue", "bad\uD800value", "bad\uDC00value" })
        {
            var input = new StartupRebuildInput(@"C:\Apps\tool.exe");
            input = field switch { "target" => input with { Target = bad }, "args" => input with { Arguments = bad }, _ => input with { WorkingDirectory = bad } };
            Assert.Throws<ArgumentException>(() => StartupRebuildInputPolicy.Preview(input));
        }
    }

    [Theory]
    [InlineData("Startup", false)] [InlineData("HKCU", true)] [InlineData("HKLM", true)]
    public void InputPreviewCannotBeAttachedToAnActiveOrRegistrySelection(string source, bool disabled) =>
        Assert.Throws<ArgumentException>(() => new StartupInspectionRequest(source, "owned", disabled, new string('a', 64), new(@"C:\tool.exe")).Validate("selected"));

    [Theory]
    [InlineData("input")] [InlineData("digest")] [InlineData("scope")] [InlineData("availability")]
    [InlineData("missing")] [InlineData("unrequested")] [InlineData("missing-field")]
    public void TypedInspectionRejectsChangedInventedOrFalselyCertifiedPreview(string defect)
    {
        var entry = new StartupEntry("selected", "Startup", "owned", null, null, null, null, "untrusted origin", null, true,
            "record", DateTimeOffset.UnixEpoch, false, false, null);
        var request = StartupInspectionRequest.FromEntry(entry) with { RebuildInput = new(@"C:\Apps\tool.exe") };
        var revision = new string('a', 64);
        var snapshot = StartupInspectionTests.Snapshot(entry, revision) with { RebuildPreview = StartupRebuildInputPolicy.Preview(request.RebuildInput!) };
        var response = new AgentResponse("request", true, snapshot, Revision: revision);
        Assert.Equal(snapshot.RebuildPreview, AgentStartupClient.ReadInspection(response, entry.Id, request, revision).RebuildPreview);
        var node = JsonSerializer.SerializeToNode(snapshot, AgentProtocol.JsonOptions)!;
        if (defect == "input") { node["rebuildPreview"]!["input"]!["target"] = @"C:\other.exe"; }
        else if (defect == "digest") { node["rebuildPreview"]!["inputDigest"] = new string('b', 64); }
        else if (defect == "scope") { node["rebuildPreview"]!["validationScope"] = "native_ready"; }
        else if (defect == "availability") { node["rebuildPreview"]!["targetAvailability"] = "verified"; }
        else if (defect == "missing") { node.AsObject().Remove("rebuildPreview"); }
        else if (defect == "missing-field") { node["rebuildPreview"]!.AsObject().Remove("targetAvailability"); }
        else { request = request with { RebuildInput = null }; }
        var failure = Assert.Throws<AgentCommandException>(() => AgentStartupClient.ReadInspection(response with { Data = JsonSerializer.SerializeToElement(node) }, entry.Id, request, revision));
        Assert.Equal("protocol_mismatch", failure.Code);
    }

    [Fact]
    public void AbsentInputAndPreviewKeepTheOldWireShape()
    {
        var request = new StartupInspectionRequest("Startup", "owned", true, new string('a', 64));
        Assert.False(JsonSerializer.SerializeToElement(request, AgentProtocol.JsonOptions).TryGetProperty("rebuildInput", out _));
        Assert.True(StartupRebuildInputPolicy.Matches(null, null));
        Assert.False(StartupRebuildInputPolicy.Matches(StartupRebuildInputPolicy.Preview(new(@"C:\tool.exe")), null));
    }
}
