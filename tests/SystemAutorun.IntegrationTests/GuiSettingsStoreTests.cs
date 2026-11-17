using System.IO;
using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

public sealed class GuiSettingsStoreTests
{
    [Fact]
    public async Task ConcurrentSavesRemainOrderedAndAtomic()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SystemAutorun.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "gui.settings.json");
            var store = new ApplicationSettingsStore(path);
            var saves = Enumerable.Range(0, 40)
                .Select(index => store.SaveAsync(new ApplicationSettings
                {
                    AutoStartMissingOnLaunch = index % 2 != 0,
                    AutoRecoverFailedTasks = index % 3 == 0,
                }))
                .ToArray();

            await Task.WhenAll(saves);

            var loaded = await store.LoadAsync();
            Assert.True(loaded.AutoStartMissingOnLaunch);
            Assert.True(loaded.AutoRecoverFailedTasks);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.True(document.RootElement.GetProperty("auto_start_missing_on_launch").GetBoolean());
            Assert.True(document.RootElement.GetProperty("auto_recover_failed_tasks").GetBoolean());
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UpdatingLocalPreferencesPreservesRemoteHosts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SystemAutorun.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "gui.settings.json");
            var store = new ApplicationSettingsStore(path);
            var remoteHost = new RemoteHostDefinition
            {
                Id = "storage-node",
                Name = "Storage Node",
                Platform = RemoteHostPlatform.Linux,
                Host = "storage.home.arpa",
                UserName = "systemautorun",
                Authentication = RemoteAuthenticationMode.SshAgent,
                KnownHostsFile = Path.Combine(directory, "known_hosts"),
            };

            await store.SaveAsync(new ApplicationSettings
            {
                RemoteHosts = [remoteHost],
            });

            var loaded = await store.LoadAsync();
            await store.SaveAsync(loaded with
            {
                AutoStartMissingOnLaunch = true,
                AutoRecoverFailedTasks = true,
            });

            var updated = await store.LoadAsync();
            Assert.True(updated.AutoStartMissingOnLaunch);
            Assert.True(updated.AutoRecoverFailedTasks);
            var savedHost = Assert.Single(updated.RemoteHosts);
            Assert.Equal(remoteHost, savedHost);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
