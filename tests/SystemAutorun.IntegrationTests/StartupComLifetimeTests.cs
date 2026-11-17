using System.IO;
using System.Runtime.InteropServices;
using SystemAutorun.Windows.Startup;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Fact]
    public async Task EndOfOneComUseDoesNotDisconnectAnExistingManagedAlias()
    {
        using var fixture = new Fixture(); var path = Path.Combine(fixture.Root, "managed-alias.lnk");
        await GuiConfigurationControlTests.RunStaAsync(() =>
        {
            object? owner = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!);
            object? alias = owner; object? link = null;
            try
            {
                // Managed clients (including binding caches) need not re-enter via COM.
                // The RCW's unmanaged-entry count therefore cannot prove sole ownership.
                object Create(object value) => StartupComDispatch.Call(value, "CreateShortcut", path);
                link = Create(owner!); // Exercise this exact dispatch boundary before owner completion.
                StartupManager.KeepComObjectAlive(link); link = null;
                StartupManager.KeepComObjectAlive(owner); owner = null;
                link = Create(alias!);
                StartupComDispatch.SetText(link, "TargetPath", Path.Combine(fixture.Root, "never-run.exe"));
                StartupComDispatch.Command(link, "Save"); Assert.True(File.Exists(path));
                return Task.CompletedTask;
            }
            finally
            {
                foreach (var value in new[] { link, alias, owner }) { StartupManager.KeepComObjectAlive(value); }
            }
        });
    }

    [Fact]
    public async Task RepeatedJoinedStaCreationAndProductionReadRetainOnlyManagedMetadata()
    {
        using var fixture = new Fixture();
        var folder = fixture.Native.GetShortcutDirectory(StartupManager.UserStartupSource);
        var reader = new StartupSnapshotReader(fixture.Paths, new Dictionary<string, string> { [StartupManager.UserStartupSource] = folder });
        var configuration = new AutorunConfiguration(1, new("logs", "backups", 0), [], fixture.Root);
        for (var pass = 0; pass < 32; pass++)
        {
            var path = await fixture.CreateLinkAsync(StartupManager.UserStartupSource);
            var original = await File.ReadAllBytesAsync(path);
            var result = await reader.ReadAsync(configuration, new("user", "shortcut", "active")).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(result.SourceIssues); var entry = Assert.Single(result.Entries);
            Assert.Null(entry.Error); Assert.Equal("--owned \"two words\"", entry.Arguments);
            Assert.Equal(original, await File.ReadAllBytesAsync(path)); Assert.False(File.Exists(entry.Target));
        }
        fixture.AssertNoTransactionOrTemps();
    }

    [Fact]
    public async Task CompletingOneComUseKeepsAnotherAcquiredWrapperUsable()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "shared-wrapper.lnk");
        await GuiConfigurationControlTests.RunStaAsync(() =>
        {
            object? first = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!);
            object? acquired = null; object? link = null;
            try
            {
                // Model a second acquisition of the SAME COM identity, not a managed alias.
                var pointer = Marshal.GetIUnknownForObject(first!);
                try { acquired = Marshal.GetObjectForIUnknown(pointer); }
                finally { _ = Marshal.Release(pointer); }
                Assert.Same(first, acquired);
                StartupManager.KeepComObjectAlive(first); first = null;
                link = StartupComDispatch.Call(acquired, "CreateShortcut", path);
                StartupComDispatch.SetText(link, "TargetPath", Path.Combine(fixture.Root, "never-run.exe"));
                StartupComDispatch.Command(link, "Save");
                Assert.True(File.Exists(path));
                return Task.CompletedTask;
            }
            finally
            {
                foreach (var value in new[] { link, acquired, first }) { StartupManager.KeepComObjectAlive(value); }
            }
        });
    }
}
