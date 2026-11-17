using System.Text.Json;
using SystemAutorun.Core.Infrastructure;

namespace SystemAutorun.Core.Configuration;

public sealed class OverrideStore
{
    public async Task SetEnabledAsync(
        string rootDirectory,
        string taskName,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        var path = Path.Combine(Path.GetFullPath(rootDirectory), "autorun.overrides.json");
        await using var writeLock = await AcquireWriteLockAsync(path, cancellationToken).ConfigureAwait(false);
        var values = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        values[taskName] = enabled;
        await WriteAtomicAsync(path, values, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(
        string rootDirectory,
        string taskName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        var path = Path.Combine(Path.GetFullPath(rootDirectory), "autorun.overrides.json");
        await using var writeLock = await AcquireWriteLockAsync(path, cancellationToken).ConfigureAwait(false);
        var overrides = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (!overrides.Remove(taskName))
        {
            return;
        }

        await WriteAtomicAsync(path, overrides, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FileStream> AcquireWriteLockAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Invalid overrides path: {path}");
        Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, $".{Path.GetFileName(path)}.lock");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        Exception? lastException = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"Timed out waiting to update overrides: {path}", lastException);
    }

    private static async Task<Dictionary<string, bool>> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await OverrideFileCodec.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new ConfigurationException(
                $"Failed to parse overrides file '{path}': {exception.Message}",
                exception);
        }
    }

    private static async Task WriteAtomicAsync(
        string path,
        IReadOnlyDictionary<string, bool> values,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Invalid overrides path: {path}");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await OverrideFileCodec.WriteAsync(tempPath, values, cancellationToken).ConfigureAwait(false);
            SnapshotFile.Publish(tempPath, path);
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
