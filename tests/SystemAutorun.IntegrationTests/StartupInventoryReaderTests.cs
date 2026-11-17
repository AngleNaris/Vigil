using System.IO;
using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Health;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed class StartupInventoryReaderTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\missing.exe\" --flag", @"C:\Program Files\missing.exe", "--flag")]
    [InlineData("C:\\Program Files\\missing.exe --flag", null, "C:\\Program Files\\missing.exe --flag")]
    [InlineData("\\\\never-probe.invalid\\share\\missing.exe --flag", @"\\never-probe.invalid\share\missing.exe", "--flag")]
    [InlineData("%NEVER_EXPAND%\\tool.exe --flag", @"%NEVER_EXPAND%\tool.exe", "--flag")]
    public void InventoryCommandParsingIsLexicalNotExecutableOrFilesystemDiscovery(string value, string? target, string args)
    {
        var result = StartupManager.SplitInventoryCommand(value);
        Assert.Equal(target, result.Target); Assert.Equal(args, result.Arguments);
    }

    [Fact]
    public async Task NativeDisabledBackupReadKeepsUnverifiedTargetsAsMetadataWithoutProbingOrModifyingThem()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { new { id = "fixture", source = "Startup", name = "metadata-only",
            disabled_at = DateTimeOffset.UtcNow, target = @"\\never-probe.invalid\share\never-run.exe",
            origin = @"\\never-probe.invalid\share\never-open.lnk", backup_path = @"\\never-probe.invalid\share\never-open-backup.lnk" } });
        await File.WriteAllBytesAsync(fixture.Paths.StartupBackupPath, bytes);
        var result = await new AgentStartupClient(fixture.Client).ReadAsync(new("user", "shortcut", "disabled"));
        Assert.True(result.Available); Assert.Empty(result.SourceIssues);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(@"\\never-probe.invalid\share\never-run.exe", entry.Target);
        Assert.True(entry.IsDisabled); Assert.False(entry.CanModify); Assert.Equal("fixture", entry.BackupId);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath));
        Assert.False(File.Exists(fixture.Paths.StartupCachePath));
    }

    [Theory]
    [InlineData("json", "backup_invalid")]
    [InlineData("size", "source_unavailable")]
    [InlineData("transaction", "startup_transaction_unresolved")]
    [InlineData("reparse", "source_unavailable")]
    public async Task NativeBackupFailuresStaySourceFailuresWithoutDestructiveRecovery(string mode, string reason)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var path = fixture.Paths.StartupBackupPath;
        if (mode == "transaction") { await File.WriteAllTextAsync(path + ".transaction", "{}"); }
        else if (mode == "size") { await File.WriteAllBytesAsync(path, new byte[512 * 1024 + 1]); }
        else if (mode == "reparse")
        {
            var target = Path.Combine(fixture.Root, "owned-target.json"); await File.WriteAllTextAsync(target, "[]");
            File.CreateSymbolicLink(path, target);
        }
        else { await File.WriteAllTextAsync(path, "{bad-json"); }
        var result = await new AgentStartupClient(fixture.Client).ReadAsync(new(State: "disabled"));
        var issue = Assert.Single(result.SourceIssues); Assert.Equal("backups", issue.Source); Assert.Equal(reason, issue.Reason);
        Assert.False(result.Available); Assert.Null(result.ObservedAt);
        if (mode == "transaction") { Assert.True(File.Exists(path + ".transaction")); }
        Assert.Empty(result.Entries); Assert.False(File.Exists(fixture.Paths.StartupCachePath));
    }

    [Fact]
    public async Task OwnedShortcutUsesRealComReadOnStaAndNeitherRunsNorRewritesTheLink()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var configuration = (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration;
        var folder = Path.Combine(fixture.Root, "owned-startup"); Directory.CreateDirectory(folder);
        var linkPath = Path.Combine(folder, "metadata.lnk");
        await GuiConfigurationControlTests.RunStaAsync(() =>
        {
            var type = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
            object shell = Activator.CreateInstance(type)!; object? link = null;
            try
            {
                link = StartupComDispatch.Call(shell, "CreateShortcut", linkPath);
                StartupComDispatch.SetText(link, "TargetPath", Path.Combine(fixture.Root, "never-created-or-run.exe"));
                StartupComDispatch.SetText(link, "Arguments", "--metadata-only"); StartupComDispatch.Command(link, "Save");
            }
            finally { StartupManager.KeepComObjectAlive(link); StartupManager.KeepComObjectAlive(shell); }
            return Task.CompletedTask;
        });
        var before = await File.ReadAllBytesAsync(linkPath);
        var reader = new StartupSnapshotReader(fixture.Paths,
            new Dictionary<string, string> { [StartupManager.UserStartupSource] = folder });
        // This is the production dedicated-STA implementation, not the test WPF dispatcher.
        var read = await reader.ReadAsync(configuration, new("user", "shortcut", "active")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(read.SourceIssues); var entry = Assert.Single(read.Entries);
        Assert.Null(entry.Error); Assert.Equal("--metadata-only", entry.Arguments);
        Assert.Equal(before, await File.ReadAllBytesAsync(linkPath)); Assert.False(File.Exists(entry.Target));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingFolderIsEmptyButAnUnreadableEnumeratedLeafIsNotCleanSuccess(bool reparseLeaf)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var configuration = (await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration;
        var folder = Path.Combine(fixture.Root, "source-folder");
        if (reparseLeaf)
        {
            Directory.CreateDirectory(folder);
            File.CreateSymbolicLink(Path.Combine(folder, "bad.lnk"), Path.Combine(fixture.Root, "absent.lnk"));
        }
        await GuiConfigurationControlTests.RunStaAsync(() =>
        {
            using var manager = new StartupManager(fixture.Paths);
            var read = manager.CaptureInventory(configuration, new("user", "shortcut", "active"), CancellationToken.None,
                new Dictionary<string, string> { [StartupManager.UserStartupSource] = folder });
            Assert.Empty(read.Entries); Assert.Equal(reparseLeaf ? 0 : 1, read.ReadableSources);
            if (reparseLeaf) { Assert.Equal("source_unavailable", Assert.Single(read.SourceIssues).Reason); }
            else { Assert.Empty(read.SourceIssues); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task StartupDirectoryPinActuallyRejectsAncestorRenameUntilReleased()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var folder = Path.Combine(fixture.Root, "pinned-folder"); Directory.CreateDirectory(folder);
        var moved = Path.Combine(fixture.Root, "renamed-folder");
        using (var pin = SshInputFileLease.OpenDirectory(folder)) { Assert.Throws<IOException>(() => Directory.Move(folder, moved)); }
        Directory.Move(folder, moved); Assert.True(Directory.Exists(moved));
    }
}
