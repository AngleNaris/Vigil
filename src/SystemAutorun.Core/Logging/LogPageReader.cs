using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Logging;

public sealed partial class LogPageReader
{
    public const int MaximumReadBytes = 256 * 1024;
    public const int MaximumPayloadBytes = 48 * 1024;
    private const int MaximumMessageCharacters = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private sealed record CursorState(int Version, string Scope, string File, long Offset, string Anchor);
    private sealed record Parsed(LogEntry Entry, long End);
    private sealed class ParsePosition(long offset) { public long Offset = offset; public bool Truncated; public bool Pending; }
    private sealed class ReadMeter { public int Bytes; }

    public async Task<LogPage> ReadAsync(LogSourceInfo source, LogQuery query, string root,
        CancellationToken cancellationToken = default)
    {
        Validate(query);
        if (source.UnavailableReason is not null) { return Unavailable(source.UnavailableReason); }
        if (source.Kind != "file" || source.Path is null) { return Unavailable("provider_unsupported"); }
        var scope = Hash($"{root}\n{source.Id}\n{source.Path}\n{source.TaskName}\n{query.Level?.ToLowerInvariant()}\n{query.Since?.ToUniversalTime():O}");
        var cursor = DecodeCursor(query.Cursor, scope);
        var initialTail = cursor is null && !query.FromStart;
        var meter = new ReadMeter();
        var files = new List<LogFileHandle>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        var token = budget.Token;
        try
        {
            var path = LogSourcePolicy.ResolveFilePath(source.Path, root);
            var identities = new HashSet<string>(StringComparer.Ordinal);
            for (var archive = 3; archive >= 0; archive--)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var file = LogFileHandle.Open(archive == 0 ? path : $"{path}.{archive}");
                    if (identities.Add(file.Identity)) { files.Add(file); } else { file.Dispose(); }
                }
                catch (LogReadException exception) when (exception.Reason == "source_missing") { }
            }
            if (files.Count == 0) { return Unavailable("source_missing"); }
            var startIndex = 0;
            if (cursor is not null)
            {
                startIndex = files.FindIndex(file => file.Identity == cursor.File);
                if (startIndex < 0) { return Unavailable("rotation_lost"); }
                var file = files[startIndex];
                if (cursor.Offset > file.Length || await AnchorAsync(file, cursor.Offset, meter, token).ConfigureAwait(false) != cursor.Anchor)
                { return Unavailable("source_truncated_or_replaced"); }
            }
            var windows = new List<(LogFileHandle File, long Start, int Count)>();
            var remaining = MaximumReadBytes - 4096; // Reserve both cursor-anchor reads inside the byte budget.
            var truncated = false;
            if (initialTail)
            {
                for (var index = files.Count - 1; index >= 0; index--)
                {
                    var length = files[index].Length;
                    var count = (int)Math.Min(length, remaining);
                    if (remaining == 0) { truncated |= length > 0; continue; }
                    windows.Insert(0, (files[index], length - count, count));
                    remaining -= count;
                    truncated |= count < length;
                }
            }
            else
            {
                for (var index = startIndex; index < files.Count && remaining > 0; index++)
                {
                    var start = index == startIndex ? cursor?.Offset ?? 0 : 0;
                    var count = (int)Math.Min(Math.Max(0, files[index].Length - start), remaining);
                    windows.Add((files[index], start, count)); remaining -= count;
                }
            }
            var entries = new List<LogEntry>();
            var bytes = 0; var payloadBytes = 0; var unknown = 0;
            var hasMore = false; var pending = false;
            var nextFile = files[^1]; var nextOffset = nextFile.Length;
            foreach (var window in windows)
            {
                token.ThrowIfCancellationRequested();
                var data = await window.File.ReadAsync(window.Start, window.Count, token).ConfigureAwait(false);
                bytes += data.Length;
                var cut = window.Start + data.Length < window.File.Length;
                var position = new ParsePosition(window.Start);
                var stopped = false;
                foreach (var record in Parse(data, window.Start, window.File.Identity, source.Origin == "control",
                             initialTail && window.Start > 0, cut, position, token))
                {
                    if (query.Level is { } level && !string.Equals(record.Entry.Level ?? "unknown", level, StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (query.Since is { } since)
                    {
                        if (record.Entry.Timestamp is null) { unknown++; continue; }
                        if (record.Entry.Timestamp < since) { continue; }
                    }
                    var size = JsonSerializer.SerializeToUtf8Bytes(record.Entry, AgentProtocol.JsonOptions).Length;
                    if (initialTail)
                    {
                        entries.Add(record.Entry); payloadBytes += size;
                        while (entries.Count > query.Tail || payloadBytes > MaximumPayloadBytes)
                        {
                            payloadBytes -= JsonSerializer.SerializeToUtf8Bytes(entries[0], AgentProtocol.JsonOptions).Length;
                            entries.RemoveAt(0); truncated = true;
                        }
                    }
                    else
                    {
                        if (entries.Count == query.Tail || payloadBytes + size > MaximumPayloadBytes)
                        { position.Offset = record.Entry.ByteOffset; hasMore = true; stopped = true; break; }
                        entries.Add(record.Entry); payloadBytes += size;
                    }
                }
                nextFile = window.File; nextOffset = position.Offset;
                pending = position.Pending;
                truncated |= position.Truncated || entries.Any(entry => entry.Truncated);
                if (stopped) { break; }
                if (cut) { hasMore = true; if (!initialTail) { break; } }
            }
            if (!initialTail && !hasMore && nextFile != files[^1]) { hasMore = true; }
            // Initial tail intentionally omits older records. FromStart provides bounded forward paging.
            if (initialTail) { hasMore = false; }
            var next = new CursorState(1, scope, nextFile.Identity, nextOffset,
                await AnchorAsync(nextFile, nextOffset, meter, token).ConfigureAwait(false));
            return new LogPage(1, source, true, pending ? "partial_record_pending" : unknown > 0 ? "unknown_timestamps_excluded" : null, DateTimeOffset.UtcNow,
                entries, Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(next)), hasMore, truncated, bytes + meter.Bytes, unknown);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable("read_budget_exceeded"); }
        catch (LogReadException exception) { return Unavailable(exception.Reason); }
        catch (UnauthorizedAccessException) { return Unavailable("permission_denied"); }
        catch (IOException) { return Unavailable("io_error"); }
        catch (ArgumentException) { return Unavailable("invalid_source_path"); }
        finally { foreach (var file in files) { file.Dispose(); } }

        LogPage Unavailable(string reason) => new(1, source, false, reason, DateTimeOffset.UtcNow, [], null, false, false, 0);
    }

    public static void Validate(LogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Tail is < 1 or > 200 || query.Source?.Length > 128 || query.Task?.Length > 512
            || query.Cursor?.Length > 2048 || query.Source is { Length: 0 } || query.Task is { Length: 0 })
        { throw new ArgumentException("Log query limits: tail 1..200, source 1..128, task 1..512, cursor <=2048."); }
        if (query.Level is { } level && !new[] { "trace", "debug", "info", "warning", "error", "critical", "unknown" }.Contains(level, StringComparer.OrdinalIgnoreCase))
        { throw new ArgumentException("Unknown log level."); }
    }

    private static CursorState? DecodeCursor(string? value, string scope)
    {
        if (value is null) { return null; }
        try
        {
            var cursor = JsonSerializer.Deserialize<CursorState>(Convert.FromBase64String(value));
            if (cursor is null || cursor.Version != 1 || cursor.Scope != scope || cursor.File.Length != 48
                || !cursor.File.All(char.IsAsciiHexDigit) || cursor.Offset < 0 || cursor.Anchor.Length != 64 || !cursor.Anchor.All(char.IsAsciiHexDigit))
            { throw new ArgumentException("Cursor does not match this root, source or filter."); }
            return cursor;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or NullReferenceException)
        { throw new ArgumentException("Invalid log cursor.", exception); }
    }

    private static async Task<string> AnchorAsync(LogFileHandle file, long offset, ReadMeter meter, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var prefix = await file.ReadAsync(0, (int)Math.Min(1024, offset), token).ConfigureAwait(false);
        var start = Math.Max(prefix.Length, offset - 1024);
        var edge = await file.ReadAsync(start, (int)(offset - start), token).ConfigureAwait(false);
        meter.Bytes += prefix.Length + edge.Length;
        hash.AppendData(prefix); hash.AppendData(edge);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static IEnumerable<Parsed> Parse(byte[] bytes, long offset, string identity, bool timestamped,
        bool skipPrefix, bool cut, ParsePosition position, CancellationToken token)
    {
        LogEntry? current = null;
        var lineStart = 0; var skipControl = skipPrefix && timestamped;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != 10) { continue; }
            token.ThrowIfCancellationRequested();
            var text = Decode(bytes, lineStart, index - lineStart, out var lossy).TrimEnd('\r');
            if (offset + lineStart == 0) { text = text.TrimStart('\uFEFF'); }
            var lineOffset = offset + lineStart;
            lineStart = index + 1;
            if (skipPrefix) { skipPrefix = false; position.Truncated = true; position.Offset = offset + lineStart; continue; }
            var stamp = Timestamp(text, out var body);
            if (skipControl && stamp is null) { position.Offset = offset + lineStart; continue; }
            skipControl = false;
            var starts = current is null || stamp is not null || !timestamped && !IsContinuation(text);
            if (starts)
            {
                if (current is not null) { position.Offset = lineOffset; yield return new Parsed(current, lineOffset); }
                timestamped |= stamp is not null;
                current = new LogEntry(identity, lineOffset, stamp, Level(body), Clip(text), text.Length > MaximumMessageCharacters, lossy);
            }
            else
            {
                var combined = current!.Message + "\n" + Clip(text);
                current = current with { Message = Clip(combined), Truncated = current.Truncated || combined.Length > MaximumMessageCharacters || text.Length > MaximumMessageCharacters, LossyUtf8 = current.LossyUtf8 || lossy };
            }
        }
        if (lineStart < bytes.Length)
        {
            var tail = Decode(bytes, lineStart, bytes.Length - lineStart, out var lossy);
            var stamp = Timestamp(tail, out _);
            var newRecord = stamp is not null || !timestamped && !IsContinuation(tail);
            if (newRecord && current is not null)
            { position.Offset = offset + lineStart; yield return new Parsed(current, position.Offset); current = null; }
            if (cut && (current is null || current.ByteOffset == offset))
            {
                var fragment = current is null ? tail : current.Message + "\n" + tail;
                position.Offset = offset + bytes.Length; position.Truncated = true;
                yield return new Parsed(new LogEntry(identity, current?.ByteOffset ?? offset + lineStart,
                    current?.Timestamp ?? stamp, current?.Level, Clip(fragment), true, lossy || current?.LossyUtf8 == true), position.Offset);
                yield break;
            }
            position.Pending = true;
            // A pending continuation belongs to the whole last timestamped event, not a fake new event.
            if (current is not null) { position.Offset = current.ByteOffset; }
            yield break;
        }
        if (current is not null)
        {
            if (cut && current.ByteOffset > offset) { position.Offset = current.ByteOffset; yield break; }
            position.Offset = offset + bytes.Length;
            if (cut) { position.Truncated = true; current = current with { Truncated = true }; }
            yield return new Parsed(current, position.Offset);
        }
    }

    private static string Decode(byte[] bytes, int start, int count, out bool lossy)
    {
        try { lossy = false; return StrictUtf8.GetString(bytes, start, count); }
        catch (DecoderFallbackException) { lossy = true; return Encoding.UTF8.GetString(bytes, start, count); }
    }
    private static string Clip(string text)
    {
        if (text.Length <= MaximumMessageCharacters) { return text; }
        var end = char.IsHighSurrogate(text[MaximumMessageCharacters - 1]) ? MaximumMessageCharacters - 1 : MaximumMessageCharacters;
        return text[..end];
    }
    private static bool IsContinuation(string text) => text.Length == 0 || char.IsWhiteSpace(text[0]) || text.StartsWith("--->", StringComparison.Ordinal) || ExceptionType().IsMatch(text);
    private static DateTimeOffset? Timestamp(string text, out string body)
    {
        body = text;
        if (text.StartsWith('[') && text.IndexOf(']') is > 0 and < 48 and var closing)
        {
            var value = text[1..closing];
            // Require an explicit UTC/offset suffix; never infer a timestamp from file mtime or local DST.
            if (OffsetSuffix().IsMatch(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
            { body = text[(closing + 1)..].TrimStart(); return stamp.ToUniversalTime(); }
        }
        return null;
    }
    private static string? Level(string body)
    {
        var match = LevelPrefix().Match(body);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() switch { "warn" => "warning", "fatal" => "critical", var value => value } : null;
    }
    [GeneratedRegex("^(?:[\\w+`]+\\.)*[\\w+`]*Exception(?:[ :]|$)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ExceptionType();
    [GeneratedRegex("(?:Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetSuffix();
    [GeneratedRegex("^\\[?(TRACE|DEBUG|INFO|WARN|WARNING|ERROR|CRITICAL|FATAL)\\]?(?:[ :]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelPrefix();
}
