using System.Diagnostics;
using SystemAutorun.Core.Infrastructure;

namespace SystemAutorun.Windows.Startup;

// Same-physical-root backup/transaction exclusion, NOT a global Windows startup lock.
// A file handle can be released after await on another thread and is closed on process exit.
// Keep this separate from RootWriteLease: legacy native scanning may block inside OS/COM calls.
internal static class StartupMutationLease
{
    internal const string FileName = ".autorun.startup.mutation.lock";

    internal static async Task<IDisposable> AcquireAsync(ProjectPaths paths,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var root = PhysicalDirectory.Resolve(paths.RootDirectory);
        var limit = timeout ?? TimeSpan.FromSeconds(15);
        if (limit <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException(nameof(timeout)); }
        var elapsed = Stopwatch.StartNew();
        IOException? contention = null;
        while (elapsed.Elapsed < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // No DELETE_ON_CLOSE: avoid delete-pending ACCESS_DENIED between contenders.
                return new FileStream(Path.Combine(root, FileName), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
            }
            catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
            {
                contention = exception;
                var remaining = limit - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) { break; }
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(25)
                    ? remaining : TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new TimeoutException("Another startup-item operation is still running for this configuration root.", contention);
    }
}
