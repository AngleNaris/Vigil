using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.Windows.Startup;

// No mutation API is exposed. The Agent owns the single-flight gate and retains it even
// when a native getter does not return before the caller's deadline. Never abort that thread.
public sealed class StartupSnapshotReader(ProjectPaths paths) : IStartupSnapshotReader
{
    private readonly IReadOnlyDictionary<string, string>? _shortcutDirectories;

    internal StartupSnapshotReader(ProjectPaths paths, IReadOnlyDictionary<string, string> shortcutDirectories) : this(paths)
    { _shortcutDirectories = shortcutDirectories; } // Owned-source integration tests only; never part of RPC input.

    public Task<StartupManagementSnapshot> ReadAsync(AutorunConfiguration configuration, StartupListQuery query,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<StartupManagementSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                StartupManagementSnapshot result;
                using (var manager = new StartupManager(paths))
                { result = manager.CaptureInventory(configuration, query, cancellationToken, _shortcutDirectories); }
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { completion.TrySetCanceled(cancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true, Name = "SystemAutorun startup metadata" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
