using System.Globalization;
using System.Text;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Infrastructure;

public sealed class ExceptionLogReader
{
    private const int MaximumFiles = 32;
    private const int LinesPerFile = 240;
    private const int MaximumTailBytes = 256 * 1024;

    public async Task<ExceptionLogSnapshot> ReadAsync(
        string rootDirectory,
        string logDirectory,
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var files = EnumerateLogFiles(rootDirectory, logDirectory)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .ToArray();
        var selectedFiles = files.Take(MaximumFiles).ToArray();
        var entries = new List<ExceptionLogEntry>();
        var truncated = files.Length > selectedFiles.Length;
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var path in selectedFiles)
        {
            var tail = await ReadTailLinesAsync(path, LinesPerFile, cancellationToken)
                .ConfigureAwait(false);
            truncated |= tail.Truncated;
            foreach (var record in CoalesceTimestampedRecords(tail.Lines))
            {
                if (TryParseEntry(path, record, out var entry))
                {
                    entries.Add(entry);
                }
            }
        }

        var ordered = entries
            .OrderByDescending(entry => entry.Timestamp)
            .ThenBy(entry => entry.Source, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new ExceptionLogSnapshot(
            DateTimeOffset.Now,
            ordered.Take(count).ToArray(),
            selectedFiles.Length,
            truncated || ordered.Length > count);
    }

    private static IEnumerable<string> EnumerateLogFiles(string rootDirectory, string logDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[]
                 {
                     Path.GetFullPath(logDirectory),
                     Path.GetFullPath(rootDirectory),
                 })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.log*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if ((name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                        || name.Contains(".log.", StringComparison.OrdinalIgnoreCase))
                    && seen.Add(Path.GetFullPath(path)))
                {
                    yield return Path.GetFullPath(path);
                }
            }
        }
    }

    private static async Task<(IReadOnlyList<string> Lines, bool Truncated)> ReadTailLinesAsync(
        string path,
        int count,
        CancellationToken cancellationToken)
    {
        var tail = new Queue<string>(count);
        var truncated = false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
            var length = stream.Length;
            var prefix = new byte[(int)Math.Min(4, length)];
            await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
            // Retain StreamReader's BOM-aware legacy formats even when seeking beyond the BOM.
            var (encoding, unit) = TailEncoding(prefix);
            var start = Math.Max(0, length - MaximumTailBytes);
            start += (unit - start % unit) % unit;
            stream.Position = start;
            var bytes = new byte[(int)(length - start)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(new MemoryStream(bytes, writable: false), encoding, detectEncodingFromByteOrderMarks: true);
            if (start > 0)
            {
                truncated = true;
                // Only the potentially partial first line is discarded. Complete untimestamped
                // records still belong to the legacy aggregate; do not drop them as stack noise.
                _ = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (tail.Count == count) { _ = tail.Dequeue(); truncated = true; }
                tail.Enqueue(line);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return (Array.Empty<string>(), false); }
        return (tail.ToArray(), truncated);
    }

    private static (Encoding Encoding, int Unit) TailEncoding(byte[] prefix) => prefix switch
    {
        [0xFF, 0xFE, 0x00, 0x00, ..] => (Encoding.UTF32, 4),
        [0x00, 0x00, 0xFE, 0xFF, ..] => (new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4),
        [0xFF, 0xFE, ..] => (Encoding.Unicode, 2),
        [0xFE, 0xFF, ..] => (Encoding.BigEndianUnicode, 2),
        _ => (Encoding.UTF8, 1),
    };

    private static IReadOnlyList<string> CoalesceTimestampedRecords(IReadOnlyList<string> lines)
    {
        var records = new List<string>();
        StringBuilder? timestampedRecord = null;
        foreach (var line in lines)
        {
            if (HasTimestampPrefix(line))
            {
                FlushTimestampedRecord();
                timestampedRecord = new StringBuilder(line);
                continue;
            }

            if (timestampedRecord is not null)
            {
                _ = timestampedRecord.AppendLine().Append(line);
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                records.Add(line);
            }
        }

        FlushTimestampedRecord();
        return records;

        void FlushTimestampedRecord()
        {
            if (timestampedRecord is null)
            {
                return;
            }

            records.Add(timestampedRecord.ToString());
            timestampedRecord = null;
        }
    }

    private static bool HasTimestampPrefix(string line)
    {
        var closing = line.IndexOf(']');
        if (!line.StartsWith("[", StringComparison.Ordinal)
            || closing <= 1)
        {
            return false;
        }

        return DateTimeOffset.TryParse(
            line[1..closing],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
            out _);
    }

    private static bool TryParseEntry(
        string path,
        string record,
        out ExceptionLogEntry entry)
    {
        var fileName = Path.GetFileName(path);
        var source = NormalizeSource(fileName);
        var message = record.Trim();
        var timestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        if (message.StartsWith("[", StringComparison.Ordinal)
            && message.IndexOf(']') is var closing
            && closing > 1)
        {
            var rawTimestamp = message[1..closing];
            if (DateTimeOffset.TryParse(
                    rawTimestamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                    out var parsed))
            {
                timestamp = parsed;
            }

            message = message[(closing + 1)..].Trim();
        }

        var level = Classify(source, message);
        if (level is null)
        {
            entry = null!;
            return false;
        }

        entry = new ExceptionLogEntry(timestamp, level.Value, source, message, fileName);
        return true;
    }

    private static ExceptionLogLevel? Classify(string source, string message)
    {
        var value = $"{source} {message}";
        if (ContainsAny(value, "fatal", "critical", "unhandled", "startup-error"))
        {
            return ExceptionLogLevel.Critical;
        }

        if (ContainsAny(
                value,
                "exception",
                " error",
                "error:",
                "failed",
                "failure",
                "timed out",
                "timeout",
                "access denied",
                "unauthorized",
                "unavailable"))
        {
            return ExceptionLogLevel.Error;
        }

        if (ContainsAny(value, "warning", "warn:", "retry", "degraded", "unexpectedly", "告警"))
        {
            return ExceptionLogLevel.Warning;
        }

        return null;
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeSource(string fileName)
    {
        var marker = fileName.IndexOf(".log", StringComparison.OrdinalIgnoreCase);
        return marker <= 0 ? fileName : fileName[..marker];
    }
}
