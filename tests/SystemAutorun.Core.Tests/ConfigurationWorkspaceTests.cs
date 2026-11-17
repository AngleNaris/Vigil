using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

public sealed class ConfigurationWorkspaceTests
{
    [Fact]
    public async Task ReadsStableRevisionAndDefaultPreferences()
    {
        using var root = new OwnedRoot();
        var first = await root.Workspace.ReadAsync();
        var second = await root.Workspace.ReadAsync();
        Assert.Equal(first.Revision, second.Revision);
        Assert.Equal(64, first.Revision.Length);
        Assert.Empty(first.Configuration.Tasks);
        Assert.False(first.Preferences.AutoRecoverFailedTasks);
    }

    [Fact]
    public async Task ConcurrentClientsWithSameBaseRevisionCannotOverwriteEachOther()
    {
        using var root = new OwnedRoot();
        var revision = (await root.Workspace.ReadAsync()).Revision;
        var results = await Task.WhenAll(AddAsync("first"), AddAsync("second"));
        Assert.Equal(1, results.Count(exception => exception is null));
        Assert.IsType<ConfigurationConflictException>(Assert.Single(results, exception => exception is not null));
        Assert.Single((await root.Workspace.ReadAsync()).Configuration.Tasks);
        var yaml = await File.ReadAllTextAsync(root.Paths.ConfigPath);
        Assert.Contains("# preserve-comment", yaml, StringComparison.Ordinal);
        Assert.Contains("custom_field: keep", yaml, StringComparison.Ordinal);

        async Task<Exception?> AddAsync(string name)
        {
            try
            {
                await root.Workspace.AddTaskAsync(NewTask(name), revision);
                return null;
            }
            catch (ConfigurationConflictException exception)
            {
                return exception;
            }
        }
    }

    [Fact]
    public async Task StaleSettingsCannotEraseAnotherClientsRemoteHost()
    {
        using var root = new OwnedRoot();
        var old = await root.Workspace.ReadAsync();
        var host = NewHost(root);
        var added = await root.Workspace.UpsertRemoteHostAsync(host, old.Revision);
        var conflict = await Assert.ThrowsAsync<ConfigurationConflictException>(() =>
            root.Workspace.PatchSettingsAsync(new(AutoRecoverFailedTasks: true), old.Revision));
        Assert.Equal(old.Revision, conflict.ExpectedRevision);
        Assert.Equal(added.Revision, conflict.ActualRevision);
        var updated = await root.Workspace.PatchSettingsAsync(new(AutoRecoverFailedTasks: true), added.Revision);
        Assert.True(updated.Value.AutoRecoverFailedTasks);
        Assert.Equal(host, Assert.Single(updated.Value.RemoteHosts));
        Assert.Equal(host, Assert.Single((await root.Workspace.ReadAsync()).Preferences.RemoteHosts));
    }

    [Fact]
    public async Task ContentRevisionDetectsExternalChangeWithUnchangedMtime()
    {
        using var root = new OwnedRoot();
        var old = await root.Workspace.ReadAsync();
        var timestamp = File.GetLastWriteTimeUtc(root.Paths.ConfigPath);
        await File.AppendAllTextAsync(root.Paths.ConfigPath, "\n# external-change\n");
        File.SetLastWriteTimeUtc(root.Paths.ConfigPath, timestamp);
        var latest = await root.Workspace.ReadAsync();
        Assert.NotEqual(old.Revision, latest.Revision);
        await Assert.ThrowsAsync<ConfigurationConflictException>(() =>
            root.Workspace.AddTaskAsync(NewTask("not-added"), old.Revision));
        Assert.Empty(latest.Configuration.Tasks);
    }

    [Fact]
    public async Task AddMonitorDisableAndRemovePreserveConfigurationContracts()
    {
        using var root = new OwnedRoot();
        var initial = await root.Workspace.ReadAsync();
        var added = await root.Workspace.AddTaskAsync(NewTask("fixture"), initial.Revision);
        Assert.True(File.Exists(added.Value.BackupPath));
        var monitored = await root.Workspace.SetExpectedRunningAsync("fixture", true, added.Revision);
        var disabled = await root.Workspace.SetEnabledAsync("fixture", false, monitored.Revision);
        Assert.False(disabled.Value.Enabled);
        Assert.True(disabled.Value.ExpectedRunning);
        var removed = await root.Workspace.RemoveTaskAsync("fixture", disabled.Revision);
        Assert.NotEqual(disabled.Revision, removed.Revision);
        Assert.Empty((await root.Workspace.ReadAsync()).Configuration.Tasks);
    }

    [Fact]
    public async Task RemovingLastTaskPreservesFollowingCommentsAndRootFields()
    {
        using var root = new OwnedRoot();
        var initial = await root.Workspace.ReadAsync();
        _ = await root.Workspace.AddTaskAsync(NewTask("fixture"), initial.Revision);
        await File.AppendAllTextAsync(root.Paths.ConfigPath, "\n# keep-tail\nfuture_root: keep\n");
        var revision = (await root.Workspace.ReadAsync()).Revision;
        _ = await root.Workspace.RemoveTaskAsync("fixture", revision);
        var yaml = await File.ReadAllTextAsync(root.Paths.ConfigPath);
        Assert.Contains("tasks: []", yaml, StringComparison.Ordinal);
        Assert.Contains("# keep-tail", yaml, StringComparison.Ordinal);
        Assert.Contains("future_root: keep", yaml, StringComparison.Ordinal);
        Assert.Empty((await root.Workspace.ReadAsync()).Configuration.Tasks);
    }

    [Fact]
    public async Task EmptyRevisionIsRejectedBeforeWritingAnything()
    {
        using var root = new OwnedRoot();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            root.Workspace.PatchSettingsAsync(new(AutoRecoverFailedTasks: true), ""));
        Assert.False(File.Exists(root.Paths.GuiSettingsPath));
        Assert.Empty(Directory.EnumerateFiles(root.Root, "*.lock"));
    }

    [Fact]
    public async Task WaitingForAnotherWriterCanBeCancelledWithoutLeavingALock()
    {
        using var root = new OwnedRoot();
        await using (var lease = await RootWriteLease.AcquireAsync(root.Paths))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                root.Workspace.ReadAsync(cancellation.Token));
        }

        // A cancelled waiter must not retain ownership; the empty lock file is
        // intentionally reusable, not a stale-owner indicator.
        _ = await root.Workspace.ReadAsync();
        Assert.Equal(0, new FileInfo(Assert.Single(Directory.EnumerateFiles(root.Root, "*.lock"))).Length);
        Assert.Empty(Directory.EnumerateFiles(root.Root, "*.tmp"));
    }

    [Fact]
    public async Task RemoteHostUpdateAndRemovalUseTheSameRevision()
    {
        using var root = new OwnedRoot();
        var initial = await root.Workspace.ReadAsync();
        var first = await root.Workspace.UpsertRemoteHostAsync(NewHost(root), initial.Revision);
        var changed = await root.Workspace.UpsertRemoteHostAsync(NewHost(root) with { Name = "Renamed" }, first.Revision);
        Assert.Equal("Renamed", Assert.Single(changed.Value.RemoteHosts).Name);
        var removed = await root.Workspace.RemoveRemoteHostAsync("fixture-host", changed.Revision);
        Assert.Empty(removed.Value.RemoteHosts);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            root.Workspace.RemoveRemoteHostAsync("missing", removed.Revision));
    }

    [Fact]
    public async Task SettingsKeepUnknownFieldsAndDoNotResetCorruption()
    {
        using var root = new OwnedRoot();
        await File.WriteAllTextAsync(root.Paths.GuiSettingsPath, """{"future_option":{"keep":true},"remote_hosts":[]}""");
        var snapshot = await root.Workspace.ReadAsync();
        var saved = await root.Workspace.PatchSettingsAsync(new(AutoStartMissingOnLaunch: true), snapshot.Revision);
        Assert.True(saved.Value.AdditionalSettings!["future_option"].GetProperty("keep").GetBoolean());
        const string corrupt = "{\"remote_hosts\":[";
        await File.WriteAllTextAsync(root.Paths.GuiSettingsPath, corrupt);
        await Assert.ThrowsAsync<ConfigurationException>(() => root.Workspace.ReadAsync());
        Assert.Equal(corrupt, await File.ReadAllTextAsync(root.Paths.GuiSettingsPath));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"remote_hosts\":null}")]
    [InlineData("[]")]
    public async Task InvalidSettingsAreNotTreatedAsDefaults(string json)
    {
        using var root = new OwnedRoot();
        await File.WriteAllTextAsync(root.Paths.GuiSettingsPath, json);
        await Assert.ThrowsAsync<ConfigurationException>(() => new ApplicationSettingsStore(root.Paths.GuiSettingsPath).LoadAsync());
        Assert.Equal(json, await File.ReadAllTextAsync(root.Paths.GuiSettingsPath));
    }

    [Fact]
    public void LaunchArgumentsRejectMissingRootValueAndUnknownArguments()
    {
        string[][] invalid = [["--root"], ["--root", ""], ["--root", "--boot"],
            ["--root", "first", "--root", "second"], ["--unknown"]];
        foreach (var arguments in invalid)
        {
            Assert.Throws<ArgumentException>(() => ProcessLaunchArguments.Parse(arguments, allowBoot: true));
        }

        var app = ProcessLaunchArguments.Parse(["--background", "--root", "C:\\fixture path"], allowBackground: true);
        Assert.True(app.Background);
        Assert.Equal("C:\\fixture path", app.RootDirectory);
        var agent = ProcessLaunchArguments.Parse(["--root", "C:\\fixture", "--boot"], allowBoot: true);
        Assert.True(agent.BootMode);
        Assert.Throws<ArgumentException>(() => ProcessLaunchArguments.Parse(["--background"], allowBoot: true));
    }

    private static AddTaskRequest NewTask(string name) => new(
        name, AutorunTaskType.WslBoot, Distro: "test-fixture", HealthType: HealthCheckType.WslRunning);

    private static RemoteHostDefinition NewHost(OwnedRoot root) => new()
    {
        Id = "fixture-host", Name = "Fixture Host", Host = "test.invalid", UserName = "fixture",
        Platform = RemoteHostPlatform.Linux, KnownHostsFile = Path.Combine(root.Root, "known_hosts"),
    };

    private sealed class OwnedRoot : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("SystemAutorun-workspace-tests-").FullName;
        public ProjectPaths Paths => new(Root);
        public ConfigurationWorkspace Workspace => new(Paths);

        public OwnedRoot() => File.WriteAllText(Paths.ConfigPath, """
            # preserve-comment
            version: 1
            settings:
              log_dir: logs
              backup_dir: backups
              custom_field: keep
            tasks: []
            """);

        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("SystemAutorun-workspace-tests-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to delete a non-owned test root.");
            }

            Directory.Delete(resolved, recursive: true);
        }
    }
}
