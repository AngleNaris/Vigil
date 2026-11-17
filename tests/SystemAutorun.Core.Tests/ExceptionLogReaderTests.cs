using System.Text;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

public sealed class ExceptionLogReaderTests
{
    [Theory]
    [InlineData("utf8", "\n")]
    [InlineData("utf8", "\r\n")]
    [InlineData("utf8-bom", "\r")]
    [InlineData("utf16", "\n")]
    [InlineData("utf16", "\r\n")]
    [InlineData("utf16-be", "\r\n")]
    [InlineData("utf32", "\n")]
    [InlineData("utf32-be", "\r\n")]
    public async Task LargeTailPreservesUnicodeAndCompleteUntimestampedRecords(string format, string newline)
    {
        using var temp = new TempDirectory();
        var encoding = format switch
        {
            "utf8-bom" => new UTF8Encoding(true), "utf16" => Encoding.Unicode,
            "utf16-be" => Encoding.BigEndianUnicode, "utf32" => Encoding.UTF32,
            "utf32-be" => new UTF32Encoding(true, true), _ => new UTF8Encoding(false),
        };
        var path = Path.Combine(temp.Path, "legacy.log");
        var text = string.Join(newline, Enumerable.Range(0, 12000).Select(index => $"Warning: 中文记录 {index:D5} 尾声🧪"));
        await File.WriteAllTextAsync(path, text, encoding);
        Assert.True(new FileInfo(path).Length > 256 * 1024);
        var result = await new ExceptionLogReader().ReadAsync(temp.Path, temp.Path, 300);
        Assert.True(result.Truncated); Assert.Equal(240, result.Entries.Count);
        Assert.Equal("Warning: 中文记录 11999 尾声🧪", result.Entries[^1].Message);
        Assert.All(result.Entries, entry => Assert.DoesNotContain("\uFFFD", entry.Message));
        Assert.DoesNotContain(result.Entries, entry => entry.Message.Contains("00000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OversizedBoundaryLineIsNotInventedAsACompleteError()
    {
        using var temp = new TempDirectory();
        temp.Write("legacy.log", new string('x', 256 * 1024 + 100)
            + " ERROR: partial boundary must be omitted\nWarning: complete retained record\n");
        var result = await new ExceptionLogReader().ReadAsync(temp.Path, temp.Path, 200);
        Assert.True(result.Truncated);
        Assert.Equal("Warning: complete retained record", Assert.Single(result.Entries).Message);
    }

    [Fact]
    public async Task LineLimitAlsoReportsTruncationWhenTheByteWindowFits()
    {
        using var temp = new TempDirectory();
        temp.Write("legacy.log", string.Join('\n', Enumerable.Range(0, 241).Select(index => $"Warning: {index}")));
        var result = await new ExceptionLogReader().ReadAsync(temp.Path, temp.Path, 300);
        Assert.True(result.Truncated); Assert.Equal(240, result.Entries.Count);
        Assert.DoesNotContain(result.Entries, entry => entry.Message == "Warning: 0");
    }

    [Fact]
    public async Task UnreadableAttemptRetainsLegacySourceCountWithoutInventingAvailability()
    {
        using var temp = new TempDirectory(); var path = temp.Write("locked.log", "Warning: inaccessible");
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await new ExceptionLogReader().ReadAsync(temp.Path, temp.Path, 200);
        // Legacy Truncated is a retention cap, not a source-availability contract.
        Assert.False(result.Truncated); Assert.Empty(result.Entries); Assert.Equal(1, result.SourceCount);
    }

    [Fact]
    public async Task CancellationIsObservedEvenForAnEmptyDirectory()
    {
        using var temp = new TempDirectory(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExceptionLogReader().ReadAsync(temp.Path, temp.Path, 200, cancelled.Token));
    }

    [Fact]
    public async Task ReturnsOnlyClassifiedEntriesAcrossRootAndLogDirectories()
    {
        using var temp = new TempDirectory();
        var logs = Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(
            Path.Combine(logs, "worker.log"),
            """
            [2026-08-12T08:00:00+08:00] Worker started.
            [2026-08-12T08:01:00+08:00] Warning: retrying health probe.
            [2026-08-12T08:02:00+08:00] Start failed: timed out.
            """);
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "agent-fatal.log"),
            "[2026-08-12T08:03:00+08:00] Unhandled exception in agent.");

        var snapshot = await new ExceptionLogReader().ReadAsync(
            temp.Path,
            logs,
            20);

        Assert.Equal(3, snapshot.Entries.Count);
        Assert.Equal(ExceptionLogLevel.Critical, snapshot.Entries[0].Level);
        Assert.Equal("agent-fatal", snapshot.Entries[0].Source);
        Assert.Equal(ExceptionLogLevel.Error, snapshot.Entries[1].Level);
        Assert.Equal(ExceptionLogLevel.Warning, snapshot.Entries[2].Level);
        Assert.Equal(2, snapshot.SourceCount);
        Assert.False(snapshot.Truncated);
    }

    [Fact]
    public async Task AppliesCountLimitAndMarksSnapshotAsTruncated()
    {
        using var temp = new TempDirectory();
        var logPath = temp.Write(
            "agent.log",
            """
            [2026-08-12T08:00:00+08:00] Warning: first.
            [2026-08-12T08:01:00+08:00] Warning: second.
            [2026-08-12T08:02:00+08:00] Warning: third.
            """);

        var snapshot = await new ExceptionLogReader().ReadAsync(
            temp.Path,
            Path.GetDirectoryName(logPath)!,
            2);

        Assert.Equal(2, snapshot.Entries.Count);
        Assert.True(snapshot.Truncated);
        Assert.Contains("third", snapshot.Entries[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoalescesTimestampedExceptionAndStackTraceIntoOneEntry()
    {
        using var temp = new TempDirectory();
        var logPath = temp.Write(
            "app-startup-error.log",
            """
            [2026-08-12T08:00:00+08:00] System.InvalidOperationException: Agent exited.
             ---> System.IO.IOException: Pipe is broken.
               at SystemAutorun.Core.Ipc.AgentClient.SendAsync()
               at SystemAutorun.App.App.OnStartup()
            [2026-08-12T08:01:00+08:00] Warning: retrying startup.
            """);

        var snapshot = await new ExceptionLogReader().ReadAsync(
            temp.Path,
            Path.GetDirectoryName(logPath)!,
            20);

        Assert.Equal(2, snapshot.Entries.Count);
        var exception = Assert.Single(
            snapshot.Entries,
            entry => entry.Message.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.Equal(ExceptionLogLevel.Critical, exception.Level);
        Assert.Contains("IOException: Pipe is broken.", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "at SystemAutorun.Core.Ipc.AgentClient.SendAsync()",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(Environment.NewLine, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreservesStandaloneUntimestampedLogEntries()
    {
        using var temp = new TempDirectory();
        var logPath = temp.Write(
            "legacy.log",
            """
            Warning: first standalone record.
            Start failed: second standalone record.
            """);

        var snapshot = await new ExceptionLogReader().ReadAsync(
            temp.Path,
            Path.GetDirectoryName(logPath)!,
            20);

        Assert.Equal(2, snapshot.Entries.Count);
        Assert.Contains(
            snapshot.Entries,
            entry => entry.Message == "Warning: first standalone record.");
        Assert.Contains(
            snapshot.Entries,
            entry => entry.Message == "Start failed: second standalone record.");
    }
}
