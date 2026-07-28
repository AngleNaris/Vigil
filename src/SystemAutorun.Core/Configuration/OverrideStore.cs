using System.Text;
using System.Text.Json;

namespace SystemAutorun.Core.Configuration;

public sealed class OverrideStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public async Task SetEnabledAsync(
        string rootDirectory,
        string taskName,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        var path = Path.Combine(Path.GetFullPath(rootDirectory), "autorun.overrides.json");
        var values = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        values[taskName] = new OverrideValue(enabled);
        await WriteAtomicAsync(path, values, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(
        string rootDirectory,
        string taskName,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Path.GetFullPath(rootDirectory), "autorun.overrides.json");
        var overrides = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (!overrides.Remove(taskName))
        {
            return;
        }

        await WriteAtomicAsync(path, overrides, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, OverrideValue>> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, OverrideValue>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var values = await JsonSerializer.DeserializeAsync<Dictionary<string, OverrideValue>>(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return values is null
                ? new Dictionary<string, OverrideValue>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, OverrideValue>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, OverrideValue>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static async Task WriteAtomicAsync(
        string path,
        IReadOnlyDictionary<string, OverrideValue> values,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Invalid overrides path: {path}");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(values, JsonOptions);
            await File.WriteAllTextAsync(
                tempPath,
                json + Environment.NewLine,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, path, true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private sealed record OverrideValue(bool Enabled);
}
