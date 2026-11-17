using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Logging;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

public sealed class LogPageReaderTests
{
    private static readonly string Stamp = "[2026-11-01 09:00:00 +08:00] ";
    private static LogSourceInfo Source(string path, string origin = "control") => new("test", "file", origin, "task", path);

    [Fact]
    public async Task TimestampedMultilineAndUnknownLevelsRemainCompleteAndUtc()
    {
        using var root = new TempDirectory();
        var path = root.Write("app.log", Stamp + "ERROR failure\nSystem.InvalidOperationException: detail\n   at Example.Run()\n" + Stamp + "opaque payload\n");
        var page = await new LogPageReader().ReadAsync(Source(path), new(), root.Path);
        Assert.True(page.Available, page.Reason);
        Assert.Equal(2, page.Entries.Count);
        Assert.Equal("error", page.Entries[0].Level);
        Assert.Contains("System.InvalidOperationException: detail\n   at Example.Run()", page.Entries[0].Message);
        Assert.Equal(TimeSpan.Zero, page.Entries[0].Timestamp!.Value.Offset);
        Assert.Null(page.Entries[1].Level);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task RealFileLogFormatProducesAConfirmedOffsetTimestamp()
    {
        using var root = new TempDirectory();
        await new SystemAutorun.Core.Infrastructure.FileLog().WriteAsync(root.Path, "agent", "INFO actual FileLog writer\n  continuation");
        var page = await new LogPageReader().ReadAsync(Source(Path.Combine(root.Path, "agent.log")), new(), root.Path);
        var entry = Assert.Single(page.Entries);
        Assert.True(page.Available, page.Reason); Assert.NotNull(entry.Timestamp); Assert.Equal("info", entry.Level);
        Assert.Contains("\n  continuation", entry.Message);
    }

    [Fact]
    public async Task CursorFindsTheSameFileAfterRotationAndContinuesIntoNewFile()
    {
        using var root = new TempDirectory();
        var path = root.Write("app.log", Stamp + "INFO first\n");
        var reader = new LogPageReader(); var source = Source(path);
        var first = await reader.ReadAsync(source, new(), root.Path);
        File.Move(path, path + ".1");
        await File.AppendAllTextAsync(path + ".1", Stamp + "ERROR archived append\n");
        root.Write("app.log", Stamp + "INFO new file\n");
        var next = await reader.ReadAsync(source, new(Cursor: first.NextCursor), root.Path);
        Assert.True(next.Available, next.Reason);
        Assert.Equal(2, next.Entries.Count);
        Assert.Contains("archived append", next.Entries[0].Message);
        Assert.Contains("new file", next.Entries[1].Message);
        Assert.NotEqual(next.Entries[0].FileIdentity, next.Entries[1].FileIdentity);
    }

    [Fact]
    public async Task LostRotationAndCopyTruncateAreExplicitUnavailableNotAnEmptySuccess()
    {
        using var root = new TempDirectory(); var reader = new LogPageReader();
        var path = root.Write("app.log", Stamp + "INFO original record\n"); var source = Source(path);
        var first = await reader.ReadAsync(source, new(), root.Path);
        root.Write("app.log", "short\n");
        var truncated = await reader.ReadAsync(source, new(Cursor: first.NextCursor), root.Path);
        Assert.False(truncated.Available); Assert.Equal("source_truncated_or_replaced", truncated.Reason);
        File.Move(path, path + ".outside"); root.Write("app.log", Stamp + "INFO replacement\n");
        var lost = await reader.ReadAsync(source, new(Cursor: first.NextCursor), root.Path);
        Assert.False(lost.Available); Assert.Equal("rotation_lost", lost.Reason);
    }

    [Fact]
    public async Task PartialRecordsWaitForNewlineWithoutDuplicatingPreviousEvents()
    {
        using var root = new TempDirectory(); var reader = new LogPageReader();
        var path = root.Write("app.log", Stamp + "INFO complete\n" + Stamp + "ERROR par"); var source = Source(path);
        var first = await reader.ReadAsync(source, new(), root.Path);
        Assert.Single(first.Entries); Assert.Equal("partial_record_pending", first.Reason);
        await File.AppendAllTextAsync(path, "tial\n  stack\n");
        var second = await reader.ReadAsync(source, new(Cursor: first.NextCursor), root.Path);
        Assert.True(second.Available, second.Reason); Assert.Single(second.Entries);
        Assert.Contains("ERROR partial\n  stack", second.Entries[0].Message);
        var third = await reader.ReadAsync(source, new(Cursor: second.NextCursor), root.Path);
        Assert.Empty(third.Entries);
    }

    [Fact]
    public async Task ForwardPagesDoNotSkipRecordsAtTheCountLimit()
    {
        using var root = new TempDirectory(); var reader = new LogPageReader();
        var path = root.Write("app.log", ""); var source = Source(path);
        var first = await reader.ReadAsync(source, new(Tail: 2), root.Path);
        await File.AppendAllTextAsync(path, string.Concat(Enumerable.Range(0, 7).Select(index => Stamp + $"INFO row-{index}\n")));
        var messages = new List<string>(); var cursor = first.NextCursor;
        for (var index = 0; index < 4; index++)
        {
            var page = await reader.ReadAsync(source, new(Tail: 2, Cursor: cursor), root.Path);
            Assert.True(page.Available, page.Reason); messages.AddRange(page.Entries.Select(entry => entry.Message)); cursor = page.NextCursor;
            Assert.Equal(index < 3, page.HasMore);
        }
        Assert.Equal(7, messages.Count); Assert.Equal(7, messages.Distinct().Count());
        for (var index = 0; index < 7; index++) { Assert.Contains($"row-{index}", messages[index]); }
    }

    [Fact]
    public async Task FromStartPagesAcrossArchivesWithoutInventingABackwardCursor()
    {
        using var root = new TempDirectory(); var reader = new LogPageReader();
        var path = root.Write("app.log", string.Concat(Enumerable.Range(3, 4).Select(index => Stamp + $"INFO row-{index}\n")));
        root.Write("app.log.1", string.Concat(Enumerable.Range(0, 3).Select(index => Stamp + $"INFO row-{index}\n")));
        var source = Source(path); var messages = new List<string>(); string? cursor = null;
        for (var index = 0; index < 4; index++)
        {
            var page = await reader.ReadAsync(source, new(Tail: 2, Cursor: cursor, FromStart: cursor is null), root.Path);
            Assert.True(page.Available, page.Reason); messages.AddRange(page.Entries.Select(entry => entry.Message)); cursor = page.NextCursor;
            Assert.Equal(index < 3, page.HasMore);
        }
        Assert.Equal(7, messages.Count);
        for (var index = 0; index < 7; index++) { Assert.Contains($"row-{index}", messages[index]); }
    }

    [Fact]
    public async Task PrefixAnchorDetectsRewriteEvenWhenLengthAndLast32BytesMatch()
    {
        using var root = new TempDirectory(); var reader = new LogPageReader();
        var original = Stamp + "INFO " + new string('a', 3000) + "\n";
        var path = root.Write("app.log", original); var source = Source(path);
        var first = await reader.ReadAsync(source, new(), root.Path);
        root.Write("app.log", original.Replace("INFO a", "INFO b", StringComparison.Ordinal));
        var changed = await reader.ReadAsync(source, new(Cursor: first.NextCursor), root.Path);
        Assert.False(changed.Available); Assert.Equal("source_truncated_or_replaced", changed.Reason);
    }

    [Fact]
    public async Task PlainTimestampFreeClrExceptionRemainsOneEventWithNullableTime()
    {
        using var root = new TempDirectory();
        var path = root.Write("app.log", "ERROR failure\nSystem.InvalidOperationException: detail\n  at Example.Run()\nINFO recovered\n");
        var page = await new LogPageReader().ReadAsync(Source(path, "business"), new(), root.Path);
        Assert.True(page.Available, page.Reason); Assert.Equal(2, page.Entries.Count);
        Assert.Contains("System.InvalidOperationException", page.Entries[0].Message);
        Assert.Null(page.Entries[0].Timestamp); Assert.Equal("error", page.Entries[0].Level);
    }

    [Fact]
    public async Task HugeFilesLongMessagesAndInvalidUtf8RemainBoundedAndVisible()
    {
        using var root = new TempDirectory();
        var path = root.Write("app.log", new string('x', 2 * 1024 * 1024) + "\n" + Stamp + "ERROR " + new string('长', 20000) + "\n" + Stamp + "INFO final\n");
        await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write))
        { await stream.WriteAsync(new byte[] { 0xff, 0x0a }); }
        var page = await new LogPageReader().ReadAsync(Source(path), new(), root.Path);
        Assert.True(page.Available, page.Reason); Assert.True(page.Truncated);
        Assert.InRange(page.ScannedBytes, 1, LogPageReader.MaximumReadBytes);
        Assert.All(page.Entries, entry => Assert.InRange(entry.Message.Length, 0, 4096));
        Assert.Contains(page.Entries, entry => entry.Truncated);
        Assert.Contains(page.Entries, entry => entry.LossyUtf8);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, AgentProtocol.JsonOptions).Length < 64 * 1024);
    }

    [Fact]
    public async Task FiltersNeverInventTimestampOrLevelAndCursorIsFilterScoped()
    {
        using var root = new TempDirectory(); var reader = new LogPageReader();
        var path = root.Write("app.log", "ordinary unknown line\n" + Stamp + "ERROR known\n"); var source = Source(path, "business");
        var page = await reader.ReadAsync(source, new(Since: DateTimeOffset.Parse("2026-11-01T00:00:00Z")), root.Path);
        Assert.Single(page.Entries); Assert.Equal(1, page.UnknownTimestampExcluded);
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(source, new(Level: "info", Cursor: page.NextCursor), root.Path));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(source with { Id = "other" }, new(Cursor: page.NextCursor), root.Path));
    }

    [Fact]
    public async Task MissingLockedAndUnsupportedSourcesAreNotEmptySuccess()
    {
        using var root = new TempDirectory(); var reader = new LogPageReader();
        var path = root.Write("app.log", "test\n");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { var locked = await reader.ReadAsync(Source(path), new(), root.Path); Assert.False(locked.Available); Assert.Equal("io_error", locked.Reason); }
        var missing = await reader.ReadAsync(Source(Path.Combine(root.Path, "missing.log")), new(), root.Path);
        Assert.False(missing.Available); Assert.Equal("source_missing", missing.Reason);
        var unsupported = await reader.ReadAsync(Source(path) with { Kind = "wsl-journal", Path = null, UnavailableReason = "provider_unsupported" }, new(), root.Path);
        Assert.False(unsupported.Available); Assert.Equal("provider_unsupported", unsupported.Reason);
    }

    [Theory]
    [InlineData("\\\\server\\share\\app.log")]
    [InlineData("\\\\.\\pipe\\test")]
    [InlineData("C:\\log.txt:secret")]
    [InlineData("C:relative.log")]
    [InlineData("..\\secret.txt")]
    [InlineData("logs\\CON.log")]
    [InlineData("logs\\COM¹.log")]
    [InlineData("logs\\LPT2 .log")]
    [InlineData("logs\\NUL .json")]
    [InlineData("logs\\app. ")]
    [InlineData("logs\\*.log")]
    public void UnsafeSourcePathsAreRejectedWithoutOpeningThem(string path)
    {
        Assert.Throws<ArgumentException>(() => LogSourcePolicy.ResolveFilePath(path, Path.GetTempPath()));
    }

    [Fact]
    public async Task SourceSettingsRoundTripUnknownFieldsAndPatchPreservesAssociations()
    {
        using var root = new TempDirectory();
        root.Write("autorun.yaml", "version: 1\nsettings:\n  default_startup_delay: 0\ntasks: []\n");
        root.Write("autorun.gui.settings.json", """
            {"future_root":{"v":1},"log_sources":[{"id":"business","kind":"file","path":"application.log","future_source":{"v":2}}]}
            """);
        var workspace = new ConfigurationWorkspace(new(root.Path)); var before = await workspace.ReadAsync();
        await workspace.PatchSettingsAsync(new(AutoStartMissingOnLaunch: true), before.Revision);
        var after = await workspace.ReadAsync();
        Assert.Single(after.Preferences.LogSources); Assert.NotEqual(before.Revision, after.Revision);
        Assert.Equal(2, after.Preferences.LogSources[0].AdditionalSettings!["future_source"].GetProperty("v").GetInt32());
        Assert.Equal(1, after.Preferences.AdditionalSettings!["future_root"].GetProperty("v").GetInt32());
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => workspace.PatchSettingsAsync(new(), before.Revision));
    }
}
