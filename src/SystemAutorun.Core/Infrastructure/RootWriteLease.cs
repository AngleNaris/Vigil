using System.Diagnostics;

namespace SystemAutorun.Core.Infrastructure;

// All Agent configuration writers take this lease before reading a revision. Unlike a
// process-local semaphore this also serializes separate processes and directory aliases.
public static class RootWriteLease
{
    public const string FileName = ".autorun.workspace.lock";
    public sealed class Held : IDisposable, IAsyncDisposable
    {
        private FileStream? _stream;
        private readonly string _root;
        internal Held(FileStream stream, string root) { _stream = stream; _root = root; }
        public void Require(ProjectPaths paths)
        {
            ObjectDisposedException.ThrowIf(_stream is null, this);
            if (!string.Equals(_root, PhysicalDirectory.Resolve(paths.RootDirectory), StringComparison.Ordinal))
            { throw new ArgumentException("The held root lease belongs to a different physical workspace.", nameof(paths)); }
        }
        public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
        public async ValueTask DisposeAsync()
        {
            var stream = Interlocked.Exchange(ref _stream, null);
            if (stream is not null) { await stream.DisposeAsync().ConfigureAwait(false); }
        }
    }

    public static async Task<Held> AcquireAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var root = PhysicalDirectory.Resolve(paths.RootDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Configuration root does not exist: {root}");
        }

        var limit = timeout ?? TimeSpan.FromSeconds(15);
        if (limit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var lockPath = Path.Combine(root, FileName);
        var elapsed = Stopwatch.StartNew();
        IOException? lastFailure = null;
        while (elapsed.Elapsed < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new Held(new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    // Keep the empty lock file: DELETE_ON_CLOSE creates a Windows
                    // delete-pending window where another writer gets ACCESS_DENIED
                    // instead of the sharing violation used for bounded admission.
                    1, FileOptions.Asynchronous), root);
            }
            catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
            {
                lastFailure = exception;
                var remaining = limit - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(remaining < TimeSpan.FromMilliseconds(25)
                    ? remaining : TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException("Another client is updating this configuration root.", lastFailure);
    }
}
