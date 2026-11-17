using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

// Exercises real Windows handles/renames in a tiny owned root; no timing races or ACL edits.
public sealed class WindowsSnapshotFileTests
{
    [Fact]
    public async Task LegacyReadSharingDeterministicallyBlocksPublicationButDoesNotPoisonLaterSaves()
    {
        using var root = new OwnedRoot();
        var store = new ApplicationSettingsStore(root.Paths.GuiSettingsPath);
        await using (var legacy = new FileStream(root.Paths.GuiSettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var exception = await Assert.ThrowsAsync<IOException>(() =>
                store.SaveAsync(new ApplicationSettings { AutoRecoverFailedTasks = true }));
            Assert.Equal(32, exception.HResult & 0xFFFF);
            Assert.False((await store.LoadAsync()).AutoRecoverFailedTasks);
            Assert.Empty(Directory.EnumerateFiles(root.Root, "*.tmp"));
        }
        await store.SaveAsync(new ApplicationSettings { AutoRecoverFailedTasks = true });
        Assert.True((await store.LoadAsync()).AutoRecoverFailedTasks);
    }

    [Fact]
    public async Task HeldSnapshotAllowsRevisionedPreferenceCommitAndRetainsTheOldFileObject()
    {
        using var root = new OwnedRoot();
        var before = await root.Workspace.ReadAsync();
        var oldBytes = await File.ReadAllBytesAsync(root.Paths.GuiSettingsPath);
        await using var reader = SnapshotFile.OpenRead(root.Paths.GuiSettingsPath);
        var updated = await root.Workspace.PatchSettingsAsync(new(AutoRecoverFailedTasks: true), before.Revision);
        Assert.NotEqual(before.Revision, updated.Revision);
        Assert.True(updated.Value.AutoRecoverFailedTasks);
        Assert.True(updated.Value.AdditionalSettings!["future_option"].GetProperty("keep").GetBoolean());
        Assert.True((await root.Workspace.ReadAsync()).Preferences.AutoRecoverFailedTasks);
        using var copy = new MemoryStream();
        await reader.CopyToAsync(copy);
        Assert.Equal(oldBytes, copy.ToArray());
        using var old = JsonDocument.Parse(copy.ToArray());
        Assert.False(old.RootElement.GetProperty("auto_recover_failed_tasks").GetBoolean());
        Assert.Empty(Directory.EnumerateFiles(root.Root, "*.tmp"));
    }

    [Theory]
    [InlineData("yaml")]
    [InlineData("overrides")]
    [InlineData("preferences")]
    public async Task RevisionInputsCanBeAtomicallyReplacedWhileTheOldSnapshotIsOpen(string kind)
    {
        using var root = new OwnedRoot();
        var path = kind switch
        {
            "yaml" => root.Paths.ConfigPath,
            "overrides" => root.Paths.OverridesPath,
            _ => root.Paths.GuiSettingsPath,
        };
        if (!File.Exists(path)) { await File.WriteAllTextAsync(path, "old fixture bytes"); }
        var before = await ConfigurationRevision.ReadAsync(root.Paths);
        var oldBytes = await File.ReadAllBytesAsync(path);
        await using var reader = SnapshotFile.OpenRead(path);
        var temporary = Path.Combine(root.Root, "replacement.tmp");
        await File.WriteAllTextAsync(temporary, "new fixture bytes", new UTF8Encoding(false));
        SnapshotFile.Publish(temporary, path);
        Assert.NotEqual(before, await ConfigurationRevision.ReadAsync(root.Paths));
        Assert.Equal("new fixture bytes", await File.ReadAllTextAsync(path));
        using var copy = new MemoryStream();
        await reader.CopyToAsync(copy);
        Assert.Equal(oldBytes, copy.ToArray());
    }

    [Fact]
    public void SnapshotSharingDoesNotPermitInPlaceWrites()
    {
        using var root = new OwnedRoot();
        using var reader = SnapshotFile.OpenRead(root.Paths.GuiSettingsPath);
        var exception = Assert.Throws<IOException>(() =>
        {
            using var writer = new FileStream(root.Paths.GuiSettingsPath, FileMode.Open,
                FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        });
        Assert.Equal(32, exception.HResult & 0xFFFF);
    }

    [Fact]
    public async Task RootWriterLeaseRemainsExclusiveAndCannotBeRenamed()
    {
        using var root = new OwnedRoot();
        await using (var lease = await RootWriteLease.AcquireAsync(root.Paths))
        {
            var lockPath = Assert.Single(Directory.EnumerateFiles(root.Root, "*.lock"));
            var exception = Record.Exception(() => File.Move(lockPath, lockPath + ".moved"));
            Assert.True(exception is IOException or UnauthorizedAccessException);
            Assert.Contains(exception!.HResult & 0xFFFF, new[] { 5, 32 });
            await Assert.ThrowsAsync<TimeoutException>(() =>
                RootWriteLease.AcquireAsync(root.Paths, timeout: TimeSpan.FromMilliseconds(60)));
            Assert.False(File.Exists(lockPath + ".moved"));
            var candidate = Path.Combine(root.Root, ".lease-probe.tmp");
            await File.WriteAllTextAsync(candidate, "not a lease");
            var publishError = Assert.Throws<IOException>(() => SnapshotFile.Publish(candidate, lockPath));
            Assert.Equal(32, publishError.HResult & 0xFFFF);
            Assert.True(File.Exists(candidate));
            Assert.Equal(0, new FileInfo(lockPath).Length);
        }
        await using var reacquired = await RootWriteLease.AcquireAsync(root.Paths);
    }

    [Fact]
    public async Task AccessDeniedIsNotConvertedToMissingPreferences()
    {
        using var root = new OwnedRoot();
        var directory = Path.Combine(root.Root, "directory-not-a-file");
        Directory.CreateDirectory(directory);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ApplicationSettingsStore(directory).LoadAsync());
    }

    [Theory]
    [InlineData(5)] // READ | DELETE
    [InlineData(7)] // READ | WRITE | DELETE is still insufficient for legacy MoveFileEx.
    public void LegacyMoveStillRejectsDeleteSharedDestination(int share)
    {
        using var root = new OwnedRoot();
        var candidate = Path.Combine(root.Root, "legacy.tmp");
        File.WriteAllText(candidate, "new");
        var old = File.ReadAllBytes(root.Paths.GuiSettingsPath);
        using var reader = new FileStream(root.Paths.GuiSettingsPath, FileMode.Open, FileAccess.Read, (FileShare)share);
        var exception = Assert.Throws<UnauthorizedAccessException>(() => File.Move(candidate, root.Paths.GuiSettingsPath, true));
        Assert.Equal(5, exception.HResult & 0xFFFF);
        Assert.True(File.Exists(candidate));
        Assert.Equal(old, File.ReadAllBytes(root.Paths.GuiSettingsPath));
    }

    [Fact]
    public async Task HeldYamlAndOverrideSnapshotsAllowRealConfigurationWriters()
    {
        using var root = new OwnedRoot();
        var before = await root.Workspace.ReadAsync();
        var oldYaml = await File.ReadAllTextAsync(root.Paths.ConfigPath);
        await using var yaml = SnapshotFile.OpenRead(root.Paths.ConfigPath);
        await root.Workspace.AddTaskAsync(new("snapshot-fixture", AutorunTaskType.WslBoot,
            Distro: "never-probed-test-fixture", HealthType: HealthCheckType.WslRunning), before.Revision);
        using var yamlText = new StreamReader(yaml, leaveOpen: true);
        Assert.Equal(oldYaml, await yamlText.ReadToEndAsync());
        await File.WriteAllTextAsync(root.Paths.OverridesPath, "{}");
        var current = await root.Workspace.ReadAsync();
        await using var overrides = SnapshotFile.OpenRead(root.Paths.OverridesPath);
        await root.Workspace.SetEnabledAsync("snapshot-fixture", false, current.Revision);
        Assert.False((await root.Workspace.ReadAsync()).Configuration.GetTask("snapshot-fixture").Enabled);
        using var overrideText = new StreamReader(overrides, leaveOpen: true);
        Assert.Equal("{}", await overrideText.ReadToEndAsync());
        Assert.Empty(Directory.EnumerateFiles(root.Root, "*.tmp"));
    }

    [Fact]
    public void SourceSharingFailureDoesNotReplaceTheDestination()
    {
        using var root = new OwnedRoot();
        var candidate = Path.Combine(root.Root, "locked-candidate.tmp");
        File.WriteAllText(candidate, "new");
        var old = File.ReadAllBytes(root.Paths.GuiSettingsPath);
        using var source = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
        var exception = Assert.Throws<IOException>(() => SnapshotFile.Publish(candidate, root.Paths.GuiSettingsPath));
        Assert.Equal(32, exception.HResult & 0xFFFF);
        Assert.Contains("open snapshot candidate", exception.Message, StringComparison.Ordinal);
        Assert.Equal(old, File.ReadAllBytes(root.Paths.GuiSettingsPath));
        Assert.True(File.Exists(candidate));
    }

    [Fact]
    public void SiblingAndReadOnlyGuardsLeaveBothFilesUntouched()
    {
        using var root = new OwnedRoot();
        var child = Directory.CreateDirectory(Path.Combine(root.Root, "child")).FullName;
        var candidate = Path.Combine(child, "new.tmp");
        File.WriteAllText(candidate, "new");
        var old = File.ReadAllBytes(root.Paths.GuiSettingsPath);
        Assert.Throws<ArgumentException>(() => SnapshotFile.Publish(candidate, root.Paths.GuiSettingsPath));
        Assert.Throws<ArgumentException>(() => SnapshotFile.Publish(candidate, candidate));
        var sibling = Path.Combine(root.Root, "new.tmp");
        File.Move(candidate, sibling);
        File.SetAttributes(root.Paths.GuiSettingsPath, FileAttributes.ReadOnly);
        try
        {
            var exception = Assert.Throws<UnauthorizedAccessException>(() => SnapshotFile.Publish(sibling, root.Paths.GuiSettingsPath));
            Assert.Equal(5, exception.HResult & 0xFFFF);
            Assert.Equal(old, File.ReadAllBytes(root.Paths.GuiSettingsPath));
            Assert.Equal("new", File.ReadAllText(sibling));
        }
        finally { File.SetAttributes(root.Paths.GuiSettingsPath, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnicodeAndLongSiblingNamesPublishWithoutTornOrMisencodedNames(bool longPath)
    {
        using var root = new OwnedRoot();
        var directory = root.Root;
        if (longPath)
        {
            for (var index = 0; index < 4; index++) { directory = Path.Combine(directory, new string('a', 60)); }
            Directory.CreateDirectory(directory);
        }
        var path = Path.Combine(directory, "设置-月-🌙.json");
        var candidate = Path.Combine(directory, ".临时-🌙.tmp");
        await File.WriteAllTextAsync(path, "old-月");
        await using (var old = SnapshotFile.OpenRead(path))
        {
            await File.WriteAllTextAsync(candidate, "new-🌙");
            SnapshotFile.Publish(candidate, path);
            using var text = new StreamReader(old, leaveOpen: true);
            Assert.Equal("old-月", await text.ReadToEndAsync());
            Assert.Equal("new-🌙", await SnapshotFile.ReadAllTextAsync(path));
            Assert.False(File.Exists(candidate));
        }
    }

    [Fact]
    public async Task RepeatedPublicationKeepsEachScopedOldHandleStableWithoutExtraDirectoryEntries()
    {
        using var root = new OwnedRoot();
        var path = Path.Combine(root.Root, "sequence.json");
        var readers = new List<FileStream>();
        try
        {
            for (var index = 0; index < 24; index++)
            {
                var candidate = Path.Combine(root.Root, ".sequence.tmp");
                await File.WriteAllTextAsync(candidate, $"snapshot-{index}");
                SnapshotFile.Publish(candidate, path);
                readers.Add(SnapshotFile.OpenRead(path));
            }
            for (var index = 0; index < readers.Count; index++)
            {
                using var text = new StreamReader(readers[index], leaveOpen: true);
                Assert.Equal($"snapshot-{index}", await text.ReadToEndAsync());
            }
            Assert.Equal(3, Directory.EnumerateFiles(root.Root).Count()); // YAML, preferences, current sequence.
            Assert.Empty(Directory.EnumerateFiles(root.Root, "*.tmp"));
        }
        finally { foreach (var reader in readers) { await reader.DisposeAsync(); } }
    }

    [Fact]
    public async Task SnapshotTextReaderPreservesBomDetectionAndCancellation()
    {
        using var root = new OwnedRoot();
        var path = Path.Combine(root.Root, "utf16.yaml");
        await File.WriteAllTextAsync(path, "月-🌙", Encoding.Unicode);
        Assert.Equal("月-🌙", await SnapshotFile.ReadAllTextAsync(path));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SnapshotFile.ReadAllTextAsync(path, cancellation.Token));
    }

    private sealed class OwnedRoot : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("SystemAutorun-snapshot-tests-").FullName;
        public ProjectPaths Paths => new(Root);
        public ConfigurationWorkspace Workspace => new(Paths);
        public OwnedRoot()
        {
            File.WriteAllText(Paths.ConfigPath, "version: 1\nsettings:\n  default_startup_delay: 0\ntasks: []\n");
            File.WriteAllText(Paths.GuiSettingsPath,
                "{\"auto_recover_failed_tasks\":false,\"future_option\":{\"keep\":true},\"remote_hosts\":[]}");
        }
        public void Dispose()
        {
            var path = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(path) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(path).StartsWith("SystemAutorun-snapshot-tests-", StringComparison.Ordinal))
            { throw new InvalidOperationException("Refuse non-owned test-root cleanup."); }
            Directory.Delete(path, recursive: true);
        }
    }
}
