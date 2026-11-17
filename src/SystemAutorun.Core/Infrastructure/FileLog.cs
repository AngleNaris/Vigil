using System.Collections.Concurrent;
using System.Text;

namespace SystemAutorun.Core.Infrastructure;

public sealed class FileLog
{
    private const long DefaultMaxFileBytes = 5 * 1024 * 1024;
    private const int DefaultMaxArchiveFiles = 3;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly long _maxFileBytes;
    private readonly int _maxArchiveFiles;

    public FileLog(
        long maxFileBytes = DefaultMaxFileBytes,
        int maxArchiveFiles = DefaultMaxArchiveFiles)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxArchiveFiles, 1);
        _maxFileBytes = maxFileBytes;
        _maxArchiveFiles = maxArchiveFiles;
    }

    public async Task WriteAsync(
        string logDirectory,
        string category,
        string message,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(logDirectory);
        var safeCategory = SanitizeCategory(category);
        var path = Path.Combine(logDirectory, $"{safeCategory}.log");
        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}";
            RotateIfNeeded(path, Encoding.UTF8.GetByteCount(line));
            await File.AppendAllTextAsync(
                path,
                line,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ReadTailAsync(
        string logDirectory,
        string category,
        int count,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(logDirectory, $"{SanitizeCategory(category)}.log");
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        var limit = Math.Max(0, count);
        if (limit == 0)
        {
            return Array.Empty<string>();
        }

        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tail = new Queue<string>(limit);
            for (var index = _maxArchiveFiles; index >= 1; index--)
            {
                await ReadIntoTailAsync(
                    $"{path}.{index}",
                    tail,
                    limit,
                    cancellationToken).ConfigureAwait(false);
            }

            await ReadIntoTailAsync(path, tail, limit, cancellationToken).ConfigureAwait(false);
            return tail.ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    private void RotateIfNeeded(string path, int pendingBytes)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var currentBytes = new FileInfo(path).Length;
        if (currentBytes == 0 || currentBytes + pendingBytes <= _maxFileBytes)
        {
            return;
        }

        for (var index = _maxArchiveFiles; index >= 1; index--)
        {
            var source = index == 1 ? path : $"{path}.{index - 1}";
            if (File.Exists(source))
            {
                File.Move(source, $"{path}.{index}", overwrite: true);
            }
        }
    }

    private static async Task ReadIntoTailAsync(
        string path,
        Queue<string> tail,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (tail.Count == limit)
            {
                _ = tail.Dequeue();
            }

            tail.Enqueue(line);
        }
    }

    public static string CategoryFileName(string category) => $"{SanitizeCategory(category)}.log";

    private static string SanitizeCategory(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(category.Select(character =>
            invalid.Contains(character)
                || character == Path.DirectorySeparatorChar
                || character == Path.AltDirectorySeparatorChar
                    ? '_'
                    : character));
        return safe is "." or ".." ? safe.Replace('.', '_') : safe;
    }
}
