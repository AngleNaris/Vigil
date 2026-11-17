using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;

namespace SystemAutorun.App.Services;

internal sealed class StartupCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;

    internal StartupCacheStore(string path) => _path = path;

    internal async Task<StartupManagementSnapshot?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            await using var stream = SnapshotFile.OpenRead(_path);
            // Display-only compatibility cache: reject unbounded or malformed rows before
            // constructing WPF bindings. No cached field can enable an action.
            if (stream.Length > 4 * 1024 * 1024) { return null; }
            var snapshot = await JsonSerializer.DeserializeAsync<StartupManagementSnapshot>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return snapshot is { Entries: not null, SourceIssues: not null, ReadableSources: >= 0 and <= 6 }
                && snapshot.Entries.Count <= 4096 && snapshot.SourceIssues.Count <= 6
                && snapshot.Entries.All(entry => entry is not null && StartupListQuery.ValidEntry(entry))
                && snapshot.Entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() == snapshot.Entries.Count
                && snapshot.SourceIssues.All(StartupListQuery.ValidIssue) ? snapshot : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal async Task SaveAsync(
        StartupManagementSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException($"Invalid startup cache path: {_path}");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(
                tempPath,
                json,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            SnapshotFile.Publish(tempPath, _path);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
