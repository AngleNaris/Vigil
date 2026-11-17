using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Cli;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.IntegrationTests;

public sealed class CliConfigurationTextTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
    private static ConfigurationValidationResult Result() => new(1, true, new string('a', 64), At, new string('b', 64), 300, 3,
        new(true, ["新增 服务"], ["removed"], ["changed"], true));
    private static CliEnvelope Envelope(object data, bool ok = true) => new(1, "fixture-request", null, ok, data,
        ok ? null : new("configuration_error", "fixture error"), "fixture-envelope-revision", At, "fixture-root", true);
    private static async Task<(string Output, string Error)> Text(object data, bool ok = true)
    { using var output = new StringWriter(); using var error = new StringWriter(); await CliOutput.WriteAsync(output, error, false, Envelope(data, ok)); return (output.ToString(), error.ToString()); }
    private static JsonObject Node(ConfigurationValidationResult? result = null) => JsonSerializer.SerializeToNode(result ?? Result(), AgentProtocol.JsonOptions)!.AsObject();

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task HumanDiffListsEachAgentCategoryAndIdentifiesCandidateVersusObservedRevision(bool wire)
    {
        var result = Result(); var text = await Text(wire ? JsonSerializer.SerializeToElement(result, AgentProtocol.JsonOptions) : result);
        Assert.Empty(text.Error); Assert.Contains("configuration validation: valid", text.Output);
        Assert.Contains("current revision: " + result.Revision, text.Output); Assert.Contains($"capturedAt: {At:O}", text.Output);
        Assert.Contains("candidate sha256: " + result.Sha256, text.Output); Assert.Contains("bytes: 300 | tasks: 3", text.Output);
        Assert.Contains("known-field diff: available", text.Output); Assert.Contains("added tasks: 1", text.Output); Assert.Contains("+ 新增 服务", text.Output);
        Assert.Contains("removed tasks: 1", text.Output); Assert.Contains("- removed", text.Output); Assert.Contains("changed tasks: 1", text.Output); Assert.Contains("~ changed", text.Output);
        Assert.Contains("settings changed: True", text.Output); Assert.Contains(result.Scope, text.Output);
        Assert.Contains("not a byte/text diff", text.Output); Assert.Contains("not a reservation", text.Output); Assert.Contains("does not apply", text.Output);
    }

    [Fact]
    public async Task EmptyKnownDiffDoesNotClaimIdenticalBytesFullValidationOrApplySafety()
    {
        var result = Result() with { Changes = new(true, [], [], [], false) }; var text = await Text(result);
        Assert.Contains("added tasks: 0", text.Output); Assert.Contains("removed tasks: 0", text.Output); Assert.Contains("changed tasks: 0", text.Output);
        Assert.Contains("settings changed: False", text.Output); Assert.Contains("no listed known-field changes", text.Output);
        Assert.Contains("unknown properties", text.Output); Assert.DoesNotContain("identical bytes", text.Output); Assert.DoesNotContain("safe to apply", text.Output);
    }

    [Fact]
    public async Task SettingsOnlyChangeCannotBeReportedAsNoListedKnownChanges()
    {
        var text = await Text(Result() with { Changes = new(true, [], [], [], true) });
        Assert.Contains("added tasks: 0", text.Output); Assert.Contains("removed tasks: 0", text.Output); Assert.Contains("changed tasks: 0", text.Output);
        Assert.Contains("settings changed: True", text.Output); Assert.DoesNotContain("no listed known-field changes", text.Output);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task UnavailableComparisonIsUnknownNotEmptyAndDoesNotListCachedArrays(bool missing)
    {
        var result = Result() with { Changes = missing ? null : new(false, ["cached-added"], ["cached-removed"], ["cached-changed"], false, "current_yaml_invalid; fixture") };
        var text = await Text(result); Assert.Contains("configuration validation: valid", text.Output); Assert.Contains("known-field diff: unavailable", text.Output);
        Assert.DoesNotContain("added tasks: 0", text.Output); Assert.DoesNotContain("no listed known-field changes", text.Output); Assert.DoesNotContain("cached-added", text.Output); Assert.DoesNotContain("settings changed:", text.Output);
        if (!missing) { Assert.Contains("current_yaml_invalid", text.Output); }
        Assert.Contains("not a reservation", text.Output);
    }

    [Fact]
    public async Task InvalidCandidateKeepsOriginalErrorAndCandidateIdentityWithoutAnInventedDiff()
    {
        var result = Result() with { Valid = false, TaskCount = 0, Changes = null, Error = "invalid YAML fixture" }; var text = await Text(result, false);
        Assert.Contains("configuration_error: fixture error", text.Error); Assert.Contains("configuration validation: invalid", text.Output);
        Assert.Contains("validation error: invalid YAML fixture", text.Output); Assert.Contains("candidate sha256: " + result.Sha256, text.Output);
        Assert.Contains("known-field diff: unavailable", text.Output); Assert.DoesNotContain("added tasks: 0", text.Output);
        Assert.Contains("tasks: unknown (candidate invalid)", text.Output); Assert.DoesNotContain("tasks: 0", text.Output);
        Assert.DoesNotContain("no listed known-field changes", text.Output); Assert.DoesNotContain("settings changed:", text.Output);
    }

    [Fact]
    public async Task AvailableReasonAndDataScopeAreShownInsteadOfSubstitutingTheLocalComputedScope()
    {
        var node = Node(); node["scope"] = "fixture source scope, not local substituted policy"; node["changes"]!["reason"] = "fixture comparison warning";
        var text = await Text(node); Assert.Contains("scope: fixture source scope, not local substituted policy", text.Output);
        Assert.Contains("comparison reason: fixture comparison warning", text.Output);
    }

    [Fact]
    public async Task AllReportedNamesAreListedBeyondTheOldTwoThousandCharacterNestedDump()
    {
        var names = Enumerable.Range(0, 160).Select(index => $"fixture-task-{index:000}-long-name").ToArray();
        var text = await Text(Result() with { TaskCount = names.Length, Changes = new(true, names, [], [], false) });
        Assert.Contains("added tasks: 160", text.Output); foreach (var name in names) { Assert.Contains("+ " + name, text.Output); }
    }

    [Fact]
    public async Task DynamicHumanStringsCannotInjectTerminalLinesOrBidiAndTruncationIsExplicit()
    {
        var node = Node(); node["revision"] = "data-rev\r\nINJECTED\u001b[31m\u202E";
        node["scope"] = "scope\t\n\u2028\u202E safe"; node["error"] = new string('z', 1200) + " error-tail";
        node["changes"]!["addedTasks"] = new JsonArray("name\n\t\u001b\u202E\u2028\u200B safe"); node["changes"]!["reason"] = "reason\n\u202E safe";
        var text = await Text(node); Assert.DoesNotContain('\r', text.Output.Replace(Environment.NewLine, ""));
        foreach (var value in new[] { '\u001b', '\u202E', '\u2028', '\u200B', '\t' }) { Assert.DoesNotContain(value, text.Output); }
        Assert.DoesNotContain("\nINJECTED", text.Output); Assert.DoesNotContain("error-tail", text.Output); Assert.Contains("use --json for full data", text.Output);
    }

    [Theory]
    [InlineData("schema")][InlineData("missing")][InlineData("validType")][InlineData("badChanges")]
    [InlineData("missingAvailable")][InlineData("missingSettings")][InlineData("nullRows")][InlineData("nullName")][InlineData("scopeType")][InlineData("missingMetadata")]
    public async Task UnknownOrPartialShapesRetainRawFallbackWithoutThrowingOrManufacturingATypedSummary(string kind)
    {
        var node = Node(); Corrupt(node, kind); var text = await Text(node);
        Assert.DoesNotContain("configuration validation:", text.Output); Assert.Contains("sha256:", text.Output); Assert.Empty(text.Error);
    }

    [Theory]
    [InlineData("valid")][InlineData("invalid")][InlineData("schema")][InlineData("missing")][InlineData("nullRows")][InlineData("scopeType")]
    public async Task JsonEnvelopeRemainsExactlyOriginalBeforeAnyPresentationParsing(string kind)
    {
        var node = Node(); if (kind == "invalid") { node["valid"] = false; node["changes"] = null; node["error"] = "fixture invalid"; }
        else if (kind != "valid") { Corrupt(node, kind); }
        node["future"] = new JsonObject { ["opaque"] = "retain\u202E\nmetadata" };
        var envelope = Envelope(node, kind != "invalid"); using var output = new StringWriter(); using var error = new StringWriter();
        await CliOutput.WriteAsync(output, error, true, envelope);
        Assert.Equal(JsonSerializer.Serialize(envelope, AgentProtocol.JsonOptions) + Environment.NewLine, output.ToString()); Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task UnrelatedFailureWithValidationLookingDataDoesNotShowAValidSummary()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var envelope = Envelope(Result()) with { Ok = false, Error = new("permission_denied", "fixture refusal") };
        await CliOutput.WriteAsync(output, error, false, envelope);
        Assert.Contains("permission_denied: fixture refusal", error.ToString()); Assert.DoesNotContain("configuration validation:", output.ToString());
    }

    private static void Corrupt(JsonObject node, string kind)
    {
        switch (kind)
        {
            case "schema": node["schemaVersion"] = 2; break;
            case "missing": node.Remove("taskCount"); break;
            case "validType": node["valid"] = "maybe"; break;
            case "badChanges": node["changes"] = "bad"; break;
            case "missingAvailable": node["changes"]!.AsObject().Remove("available"); break;
            case "missingSettings": node["changes"]!.AsObject().Remove("settingsChanged"); break;
            case "nullRows": node["changes"]!["addedTasks"] = null; break;
            case "nullName": node["changes"]!["addedTasks"]![0] = null; break;
            case "scopeType": node["scope"] = 123; break;
            case "missingMetadata": node.Remove("capturedAt"); break;
        }
    }

    [Fact]
    public async Task RealConsoleReadsAgentDiffFromFileWithoutApplyingOrRunningWorkloads()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var original = Yaml([("Alpha", "before"), ("removed", "before"), ("same", "same")]); await File.WriteAllTextAsync(fixture.Paths.ConfigPath, original);
        var candidate = Yaml([("Alpha", "after"), ("新增 服务", "new"), ("same", "same")], 5) + "future_field: {keep: candidate}\r\n";
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(candidate)).ToArray(); var path = Path.Combine(fixture.Root, "candidate.yaml"); await File.WriteAllBytesAsync(path, bytes);
        var snapshot = await fixture.Client.SendAsync<ConfigurationDocumentSnapshot>("configuration-document"); var before = Fingerprint(fixture.Root);
        var human = await ProcessCli(fixture.Root, ["config", "validate", "--file", path]); Assert.Equal(0, human.Exit); Assert.Empty(human.Error);
        Assert.Contains("+ 新增 服务", human.Text); Assert.Contains("- removed", human.Text); Assert.Contains("~ Alpha", human.Text); Assert.Contains("settings changed: True", human.Text);
        Assert.Contains("candidate sha256: " + ConfigurationDocumentRequest.Hash(bytes), human.Text); Assert.Contains("current revision: " + snapshot.Revision, human.Text);
        var json = await ProcessCli(fixture.Root, ["config", "validate", "--file", path, "--json"]); Assert.Equal(0, json.Exit); var envelope = Parse(json.Text); var data = envelope.GetProperty("data");
        Assert.True(envelope.GetProperty("ok").GetBoolean()); Assert.Equal(snapshot.Revision, data.GetProperty("revision").GetString());
        Assert.Equal(ConfigurationDocumentRequest.Hash(bytes), data.GetProperty("sha256").GetString()); Assert.Equal(bytes.Length, data.GetProperty("byteCount").GetInt32());
        Assert.Equal("新增 服务", Assert.Single(data.GetProperty("changes").GetProperty("addedTasks").EnumerateArray()).GetString());
        Assert.Equal(before, Fingerprint(fixture.Root)); Assert.Empty(fixture.ObservedErrors); Assert.False(fixture.IsStopping);
        Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RealConsoleStdinReportsNoListedChangesForCommentsUnknownFieldsReorderingAndMaskedOverride()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        await File.WriteAllTextAsync(fixture.Paths.ConfigPath, Yaml([("Alpha", "same"), ("Beta", "same")]));
        await File.WriteAllTextAsync(fixture.Paths.OverridesPath, "{\"Alpha\":{\"enabled\":false}}");
        var candidate = "# retained fixture comment\n" + Yaml([("Beta", "same"), ("Alpha", "same")]).Replace("    enabled: false", "    enabled: true", StringComparison.Ordinal) + "future_field: {not_semantically_validated: true}\n";
        // Only Alpha is overridden; leave Beta unchanged so the candidate really has no listed known changes.
        candidate = candidate.Replace("  - name: 'Beta'\n    type: wsl_boot\n    enabled: true", "  - name: 'Beta'\n    type: wsl_boot\n    enabled: false", StringComparison.Ordinal);
        var snapshot = await fixture.Client.SendAsync<ConfigurationDocumentSnapshot>("configuration-document"); var before = Fingerprint(fixture.Root);
        var human = await ProcessCli(fixture.Root, ["config", "validate", "--file", "-"], candidate); Assert.Equal(0, human.Exit); Assert.Empty(human.Error);
        Assert.Contains("no listed known-field changes", human.Text); Assert.Contains("not a byte/text diff", human.Text); Assert.Contains("Existing overrides are applied and retained", human.Text);
        var json = await ProcessCli(fixture.Root, ["config", "validate", "--file", "-", "--json"], candidate); Assert.Equal(0, json.Exit);
        var data = Parse(json.Text).GetProperty("data"); Assert.Empty(data.GetProperty("changes").GetProperty("changedTasks").EnumerateArray());
        Assert.NotEqual(snapshot.Sha256, data.GetProperty("sha256").GetString()); Assert.Equal(before, Fingerprint(fixture.Root)); Assert.Empty(fixture.ObservedErrors);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task RealConsoleInvalidCandidateOrCurrentYamlPreservesFilesAndDoesNotClaimAnEmptyComparison(bool badCurrent)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        if (badCurrent) { await File.WriteAllTextAsync(fixture.Paths.ConfigPath, "version: ["); }
        var snapshot = await fixture.Client.SendAsync<ConfigurationDocumentSnapshot>("configuration-document"); var before = Fingerprint(fixture.Root);
        var candidate = badCurrent ? Yaml([]) : "version: [";
        var human = await ProcessCli(fixture.Root, ["config", "validate", "--file", "-"], candidate); Assert.Equal(badCurrent ? 0 : 2, human.Exit);
        Assert.Contains("configuration validation: " + (badCurrent ? "valid" : "invalid"), human.Text); Assert.Contains("known-field diff: unavailable", human.Text);
        Assert.DoesNotContain("added tasks: 0", human.Text);
        if (badCurrent) { Assert.Contains("current_yaml_invalid", human.Text); Assert.Empty(human.Error); }
        else { Assert.Contains("configuration_error:", human.Error); Assert.Contains("validation error:", human.Text); Assert.Contains("tasks: unknown (candidate invalid)", human.Text); Assert.DoesNotContain("tasks: 0", human.Text); }
        var json = await ProcessCli(fixture.Root, ["config", "validate", "--file", "-", "--json"], candidate); Assert.Equal(badCurrent ? 0 : 2, json.Exit);
        var envelope = Parse(json.Text); Assert.Equal(badCurrent, envelope.GetProperty("ok").GetBoolean()); Assert.Equal(snapshot.Revision, envelope.GetProperty("revision").GetString());
        Assert.Equal(before, Fingerprint(fixture.Root)); Assert.Empty(fixture.ObservedErrors);
    }

    private static string Yaml((string Name, string Description)[] tasks, int delay = 0) => $"version: 1\nsettings:\n  default_startup_delay: {delay}\ntasks:\n" +
        (tasks.Length == 0 ? "  []\n" : string.Concat(tasks.Select(task => $"  - name: '{task.Name}'\n    type: wsl_boot\n    enabled: false\n    description: '{task.Description}'\n    distro: never-launch\n    health:\n      type: wsl_running\n")));
    private static string[] Fingerprint(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => path + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    private static JsonElement Parse(string text) { using var json = JsonDocument.Parse(text); return json.RootElement.Clone(); }
    private static async Task<(int Exit, string Text, string Error)> ProcessCli(string root, string[] arguments, string input = "")
    {
        var path = typeof(CliConfigurationTextTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == "CliExecutablePath").Value!;
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false, true), StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true) };
        foreach (var arg in arguments.Concat(["--root", root, "--timeout", "5", "--non-interactive"])) { info.ArgumentList.Add(arg); }
        using var process = Process.Start(info)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(input); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); return (process.ExitCode, await stdout, await stderr);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
}
