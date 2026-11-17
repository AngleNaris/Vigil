using SystemAutorun.Core.Infrastructure;

namespace SystemAutorun.Core.Tests;

public sealed class RootWriteLeaseTests
{
    [Fact]
    public async Task ContendingWritersSerializeAndLeaveOneReusableEmptyLockFile()
    {
        var root = Directory.CreateTempSubdirectory("SystemAutorun-root-lease-").FullName;
        var paths = new ProjectPaths(root);
        var lockPath = Path.Combine(root, ".autorun.workspace.lock");
        try
        {
            await using (var held = await RootWriteLease.AcquireAsync(paths))
            {
                Assert.True(File.Exists(lockPath));
                await Assert.ThrowsAsync<TimeoutException>(() => RootWriteLease.AcquireAsync(paths, timeout: TimeSpan.FromMilliseconds(60)));
            }
            Assert.True(File.Exists(lockPath));
            var active = 0;
            await Task.WhenAll(Enumerable.Range(0, 40).Select(async _ =>
            {
                await using var held = await RootWriteLease.AcquireAsync(paths);
                try { Assert.Equal(1, Interlocked.Increment(ref active)); await Task.Yield(); }
                finally { Interlocked.Decrement(ref active); }
            }));
            Assert.Equal(0, active);
            Assert.Equal(0, new FileInfo(lockPath).Length);
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("SystemAutorun-root-lease-", StringComparison.Ordinal))
            { throw new InvalidOperationException("Non-owned root lease fixture."); }
            Directory.Delete(resolved, recursive: true);
        }
    }
}
