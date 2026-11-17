using System.IO;
using System.Text.Json;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

// Cache service I/O only; no WPF window, UI screenshot or visual-acceptance claim.
public sealed class GuiCacheSnapshotTests
{
    [Fact]
    public async Task StartupCachePublicationRetainsAnOpenOldSnapshot()
    {
        var root = Directory.CreateTempSubdirectory("SystemAutorun-cache-snapshot-").FullName;
        try
        {
            var path = Path.Combine(root, "startup.json");
            var cache = new StartupCacheStore(path);
            var initial = new StartupManagementSnapshot([], 0, 0, 0, false);
            await cache.SaveAsync(initial);
            await using var reader = SnapshotFile.OpenRead(path);
            await cache.SaveAsync(initial with { ReadableSources = 2 });
            using var old = await JsonDocument.ParseAsync(reader);
            Assert.Equal(0, old.RootElement.GetProperty("readableSources").GetInt32());
            Assert.Equal(2, (await cache.LoadAsync())!.ReadableSources);
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            var path = Path.GetFullPath(root);
            if (Path.GetDirectoryName(path) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(path).StartsWith("SystemAutorun-cache-snapshot-", StringComparison.Ordinal))
            { throw new InvalidOperationException("Refuse non-owned startup-cache-test cleanup."); }
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task CachePublicationRetainsAnOpenOldSnapshotAndLoadsTheNewEnvelope()
    {
        var root = Directory.CreateTempSubdirectory("SystemAutorun-cache-snapshot-").FullName;
        try
        {
            var path = Path.Combine(root, "cache.json");
            const string initial = "{\"nativeVersion\":2,\"tasks\":[]}";
            await File.WriteAllTextAsync(path, initial);
            var cache = new NativeCacheStore(path);
            await using (var reader = SnapshotFile.OpenRead(path))
            {
                await cache.SaveAsync(Array.Empty<TaskSnapshot>());
                using var oldReader = new StreamReader(reader, leaveOpen: true);
                Assert.Equal(initial, await oldReader.ReadToEndAsync());
                using var current = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                Assert.Equal(2, current.RootElement.GetProperty("nativeVersion").GetInt32());
                Assert.NotEqual(default, current.RootElement.GetProperty("savedAt").GetDateTimeOffset());
                Assert.Empty(await cache.LoadAsync());
                Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
            }
        }
        finally
        {
            var path = Path.GetFullPath(root);
            if (Path.GetDirectoryName(path) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(path).StartsWith("SystemAutorun-cache-snapshot-", StringComparison.Ordinal))
            { throw new InvalidOperationException("Refuse non-owned cache-test cleanup."); }
            Directory.Delete(path, recursive: true);
        }
    }
}
