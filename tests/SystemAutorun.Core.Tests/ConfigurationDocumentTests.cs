using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Core.Tests;

public sealed class ConfigurationDocumentTests
{
    private const string Empty = "# keep 注释\r\nversion: 1\r\nsettings:\r\n  default_startup_delay: 0\r\ntasks: []\r\nfuture_field: keep\r\n";
    private const string TaskYaml = "version: 1\nsettings:\n  default_startup_delay: 0\ntasks:\n  - name: fixture\n    type: wsl_boot\n    enabled: true\n    distro: never-launch\n    health:\n      type: wsl_running\n";

    [Fact]
    public void WireEncodingIsCanonicalStrictAndFitsTheExistingRequestBudget()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('~', ConfigurationDocumentRequest.MaximumBytes));
        var input = ConfigurationDocumentRequest.FromBytes(bytes);
        Assert.Equal(bytes, input.Decode());
        var request = new AgentRequest(new string('i', 128), "configuration-apply", Payload: JsonSerializer.SerializeToElement(input, AgentProtocol.JsonOptions),
            RootId: new string('a', 64), ExpectedRevision: new string('b', 64), OperationId: AgentProtocol.NewOperationId());
        Assert.True(JsonSerializer.Serialize(request, AgentProtocol.JsonOptions).Length < AgentProtocol.MaximumRequestCharacters);
        Assert.Throws<ArgumentException>(() => ConfigurationDocumentRequest.FromText(new string('x', ConfigurationDocumentRequest.MaximumBytes + 1)));
        Assert.Throws<DecoderFallbackException>(() => ConfigurationDocumentRequest.FromBytes([0xff]));
        Assert.Throws<EncoderFallbackException>(() => ConfigurationDocumentRequest.FromText("\uD800"));
    }

    [Theory]
    [InlineData("=")]
    [InlineData("a")]
    [InlineData("Zg==")]
    [InlineData("Zh")]
    [InlineData("+/8")]
    [InlineData("_w")]
    public void InvalidOrAlternateEncodingsAreRejected(string value) => Assert.ThrowsAny<ArgumentException>(() => new ConfigurationDocumentRequest(value).Decode());

    [Fact]
    public async Task RawSnapshotPreservesBomCrLfAndUnknownFieldsWithoutParsingTheYaml()
    {
        using var root = new Root();
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Empty)).ToArray();
        await File.WriteAllBytesAsync(root.Paths.ConfigPath, bytes);
        var snapshot = await root.Workspace.ReadDocumentAsync();
        Assert.Equal(bytes, new ConfigurationDocumentRequest(snapshot.Utf8Base64Url).Decode());
        Assert.Equal(ConfigurationDocumentRequest.Hash(bytes), snapshot.Sha256);
        Assert.Equal(await ConfigurationRevision.ReadAsync(root.Paths), snapshot.Revision);
        await File.WriteAllBytesAsync(root.Paths.ConfigPath, [0xff, 0x01]);
        snapshot = await root.Workspace.ReadDocumentAsync();
        Assert.False(snapshot.Utf8Valid); Assert.Equal(ConfigurationDocumentRequest.Encode([0xff, 0x01]), snapshot.Utf8Base64Url);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "backups")));
    }

    [Theory]
    [InlineData("version: [")]
    [InlineData("version: 1\nsettings: {}\ntasks: []\nversion: 2\n")]
    [InlineData("version: 1\nsettings: {}\ntasks: []\n---\nversion: 1\nsettings: {}\ntasks: []\n")]
    [InlineData("version: 1\nsettings: {}\ntasks:\n  - name: broken\n    type: nonexistent\n")]
    public async Task ValidationAndApplyShareSemanticFailuresWithoutWriting(string yaml)
    {
        using var root = new Root();
        var before = await root.Workspace.ReadDocumentAsync();
        var request = ConfigurationDocumentRequest.FromText(yaml) with { Confirmed = true };
        var result = await root.Workspace.ValidateDocumentAsync(request);
        Assert.False(result.Valid); Assert.NotNull(result.Error); Assert.Equal(before.Revision, result.Revision);
        await Assert.ThrowsAnyAsync<Exception>(() => root.Workspace.ApplyDocumentAsync(request, before.Revision));
        Assert.Equal(before.Utf8Base64Url, (await root.Workspace.ReadDocumentAsync()).Utf8Base64Url);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "backups")));
        Assert.Empty(Directory.GetFiles(root.Path, "*.tmp"));
    }

    [Fact]
    public async Task ParserRejectsDeepUnknownNodesAndExcessiveAliasesBeforeTypedDeserialization()
    {
        using var root = new Root();
        var deep = Empty + "nested: " + new string('[', 70) + "0" + new string(']', 70) + "\n";
        var aliases = Empty + "anchor: &a value\nother: [" + string.Join(',', Enumerable.Repeat("*a", 129)) + "]\n";
        foreach (var yaml in new[] { deep, aliases })
        {
            var result = await root.Workspace.ValidateDocumentAsync(ConfigurationDocumentRequest.FromText(yaml));
            Assert.False(result.Valid); Assert.NotNull(result.Error);
        }
    }

    [Fact]
    public async Task ApplyPreservesSubmittedBytesOriginalBackupAclAndSeparateDocuments()
    {
        using var root = new Root();
        const string overrides = "{\"fixture\":{\"enabled\":false}}";
        const string preferences = "{\"remote_hosts\":[],\"future_setting\":true}";
        await File.WriteAllTextAsync(root.Paths.OverridesPath, overrides);
        await File.WriteAllTextAsync(root.Paths.GuiSettingsPath, preferences);
        var before = await root.Workspace.ReadDocumentAsync();
        var original = await File.ReadAllBytesAsync(root.Paths.ConfigPath);
        var privateAcl = new FileSecurity();
        privateAcl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        privateAcl.AddAccessRule(new FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(root.Paths.ConfigPath).SetAccessControl(privateAcl);
        var acl = new FileInfo(root.Paths.ConfigPath).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var candidate = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(TaskYaml + "future_map: {keep: true}\n")).ToArray();
        var request = ConfigurationDocumentRequest.FromBytes(candidate) with { Confirmed = true };
        var validated = await root.Workspace.ValidateDocumentAsync(request);
        Assert.True(validated.Valid, validated.Error); Assert.Equal("fixture", Assert.Single(validated.Changes!.AddedTasks));
        Assert.Equal(before.Revision, (await root.Workspace.ReadDocumentAsync()).Revision);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "backups")));
        var applied = await root.Workspace.ApplyDocumentAsync(request, before.Revision);
        Assert.True(applied.Value.Succeeded); Assert.True(applied.Value.Changed);
        Assert.Equal(candidate, await File.ReadAllBytesAsync(root.Paths.ConfigPath));
        Assert.Equal(original, await File.ReadAllBytesAsync(applied.Value.BackupPath!));
        Assert.Equal(acl, new FileInfo(applied.Value.BackupPath!).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.Equal(Path.Combine(root.Path, "backups", "config"), System.IO.Path.GetDirectoryName(applied.Value.BackupPath));
        Assert.Equal(acl, new FileInfo(root.Paths.ConfigPath).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.Equal(overrides, await File.ReadAllTextAsync(root.Paths.OverridesPath));
        Assert.Equal(preferences, await File.ReadAllTextAsync(root.Paths.GuiSettingsPath));
        Assert.False((await root.Workspace.ReadAsync()).Configuration.GetTask("fixture").Enabled);
        Assert.NotEqual(before.Revision, applied.Revision); Assert.Empty(Directory.GetFiles(root.Path, "*.tmp"));
    }

    [Fact]
    public async Task IdenticalBytesAreANoOpWithoutBackupAndConfirmationIsRequired()
    {
        using var root = new Root(); var before = await root.Workspace.ReadDocumentAsync();
        var input = new ConfigurationDocumentRequest(before.Utf8Base64Url);
        await Assert.ThrowsAsync<ConfigurationConfirmationRequiredException>(() => root.Workspace.ApplyDocumentAsync(input, before.Revision));
        var result = await root.Workspace.ApplyDocumentAsync(input with { Confirmed = true }, before.Revision);
        Assert.False(result.Value.Changed); Assert.Null(result.Value.BackupPath); Assert.Equal(before.Revision, result.Revision);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "backups")));
    }

    [Fact]
    public async Task CorruptYamlCanBeExplicitlyRepairedButNeverTreatedAsAnEmptyDiff()
    {
        using var root = new Root(); await File.WriteAllTextAsync(root.Paths.ConfigPath, "version: [");
        var before = await root.Workspace.ReadDocumentAsync(); var request = ConfigurationDocumentRequest.FromText(Empty) with { Confirmed = true };
        var validation = await root.Workspace.ValidateDocumentAsync(request);
        Assert.True(validation.Valid); Assert.False(validation.Changes!.Available);
        var repaired = await root.Workspace.ApplyDocumentAsync(request, before.Revision);
        Assert.False(repaired.Value.Changes.Available);
        Assert.Equal("version: [", await File.ReadAllTextAsync(repaired.Value.BackupPath!));
        Assert.Empty((await root.Workspace.ReadAsync()).Configuration.Tasks);
    }

    [Fact]
    public async Task ConcurrentStaleAppliesHaveOneWinnerAndDoNotOverwriteOtherSettings()
    {
        using var root = new Root(); var before = await root.Workspace.ReadDocumentAsync();
        var outcomes = await Task.WhenAll(Change("first"), Change("second"));
        Assert.Single(outcomes, result => result is null);
        Assert.IsType<ConfigurationConflictException>(Assert.Single(outcomes, result => result is not null));
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(root.Path, "backups", "config")));
        var revision = (await root.Workspace.ReadDocumentAsync()).Revision;
        await root.Workspace.PatchSettingsAsync(new(AutoStartMissingOnLaunch: true), revision);
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => root.Workspace.ApplyDocumentAsync(ConfigurationDocumentRequest.FromText(Empty) with { Confirmed = true }, revision));
        async Task<Exception?> Change(string suffix)
        {
            try { await root.Workspace.ApplyDocumentAsync(ConfigurationDocumentRequest.FromText(Empty + "# " + suffix) with { Confirmed = true }, before.Revision); return null; }
            catch (ConfigurationConflictException exception) { return exception; }
        }
    }

    [Fact]
    public async Task CandidateBackupDirectoryCannotSteerTheCommitAndRemovedTasksAreReported()
    {
        using var root = new Root(); using var outside = new Root();
        await File.WriteAllTextAsync(root.Paths.ConfigPath, TaskYaml);
        var before = await root.Workspace.ReadDocumentAsync();
        var yaml = Empty.Replace("  default_startup_delay: 0", "  backup_dir: " + outside.Path.Replace('\\', '/') + "\r\n  default_startup_delay: 0", StringComparison.Ordinal);
        var result = await root.Workspace.ApplyDocumentAsync(ConfigurationDocumentRequest.FromText(yaml) with { Confirmed = true }, before.Revision);
        Assert.Equal("fixture", Assert.Single(result.Value.Changes.RemovedTasks));
        Assert.Equal(System.IO.Path.Combine(root.Path, "backups", "config"), System.IO.Path.GetDirectoryName(result.Value.BackupPath));
        Assert.Equal(["autorun.yaml"], Directory.GetFiles(outside.Path).Select(System.IO.Path.GetFileName));
    }

    [Fact]
    public async Task ReparseBackupIsRejectedBeforeAnyOutsideChildIsCreated()
    {
        using var root = new Root(); using var outside = new Root();
        var link = System.IO.Path.Combine(root.Path, "backups");
        var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var item in new[] { "/d", "/c", "mklink", "/J", link, outside.Path }) { start.ArgumentList.Add(item); }
        using var process = Process.Start(start)!;
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, process.ExitCode);
            var before = await root.Workspace.ReadDocumentAsync();
            await Assert.ThrowsAsync<ConfigurationException>(() => root.Workspace.ApplyDocumentAsync(ConfigurationDocumentRequest.FromText(TaskYaml) with { Confirmed = true }, before.Revision));
            Assert.False(Directory.Exists(System.IO.Path.Combine(outside.Path, "config")));
            Assert.Equal(before.Utf8Base64Url, (await root.Workspace.ReadDocumentAsync()).Utf8Base64Url);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } if (Directory.Exists(link)) { Directory.Delete(link); } }
    }

    [Fact]
    public async Task HardLinkedConfigurationAndPreCancelledApplyAreRejected()
    {
        using var root = new Root(); var before = await root.Workspace.ReadDocumentAsync();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => root.Workspace.ApplyDocumentAsync(ConfigurationDocumentRequest.FromText(TaskYaml) with { Confirmed = true }, before.Revision, cancellation.Token));
        var link = System.IO.Path.Combine(root.Path, "extra-link.yaml");
        Assert.True(CreateHardLinkW(link, root.Paths.ConfigPath, IntPtr.Zero));
        await Assert.ThrowsAsync<ConfigurationException>(() => root.Workspace.ReadDocumentAsync());
        Assert.False(Directory.Exists(System.IO.Path.Combine(root.Path, "backups")));
    }

    private sealed class Root : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("SystemAutorun-document-tests-").FullName;
        public ProjectPaths Paths { get; }
        public ConfigurationWorkspace Workspace { get; }
        public Root() { Paths = new(Path); File.WriteAllText(Paths.ConfigPath, Empty); Workspace = new(Paths); }
        public void Dispose() { Directory.Delete(Path, recursive: true); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);
}
