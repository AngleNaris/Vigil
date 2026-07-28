using System.Collections.Concurrent;
using System.Text;

namespace SystemAutorun.Core.Infrastructure;

public sealed class FileLog
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task WriteAsync(
        string logDirectory,
        string category,
        string message,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(logDirectory);
        var safeCategory = string.Concat(category.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var path = Path.Combine(logDirectory, $"{safeCategory}.log");
        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}";
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
        var path = Path.Combine(logDirectory, $"{category}.log");
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        return lines.TakeLast(Math.Max(0, count)).ToArray();
    }
}
