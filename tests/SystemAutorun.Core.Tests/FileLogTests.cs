using SystemAutorun.Core.Infrastructure;

namespace SystemAutorun.Core.Tests;

public sealed class FileLogTests
{
    [Fact]
    public async Task SanitizesCategoryForBothWriteAndTailRead()
    {
        using var temp = new TempDirectory();
        var log = new FileLog();

        await log.WriteAsync(temp.Path, "../server/status", "line-1");
        await log.WriteAsync(temp.Path, "../server/status", "line-2");
        var lines = await log.ReadTailAsync(temp.Path, "../server/status", 1);

        Assert.Equal(new[] { "[", "line-2" }, new[] { lines[0][..1], lines[0].Split("] ", 2)[1] });
        Assert.Single(Directory.EnumerateFiles(temp.Path, "*.log", SearchOption.TopDirectoryOnly));
        Assert.False(File.Exists(Path.Combine(temp.Path, "..", "server", "status.log")));
    }

    [Fact]
    public async Task TailReadReturnsOnlyRequestedLines()
    {
        using var temp = new TempDirectory();
        var log = new FileLog();
        for (var index = 0; index < 20; index++)
        {
            await log.WriteAsync(temp.Path, "agent", $"line-{index}");
        }

        var lines = await log.ReadTailAsync(temp.Path, "agent", 3);

        Assert.Equal(3, lines.Count);
        Assert.Contains("line-17", lines[0], StringComparison.Ordinal);
        Assert.Contains("line-19", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotatesAndReadsAcrossBoundedArchives()
    {
        using var temp = new TempDirectory();
        var log = new FileLog(maxFileBytes: 64, maxArchiveFiles: 2);
        for (var index = 0; index < 4; index++)
        {
            await log.WriteAsync(temp.Path, "agent", $"line-{index}:{new string('x', 96)}");
        }

        var lines = await log.ReadTailAsync(temp.Path, "agent", 10);

        Assert.Equal(3, lines.Count);
        Assert.Contains("line-1", lines[0], StringComparison.Ordinal);
        Assert.Contains("line-3", lines[2], StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(temp.Path, "agent.log.1")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "agent.log.2")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "agent.log.3")));
    }
}
