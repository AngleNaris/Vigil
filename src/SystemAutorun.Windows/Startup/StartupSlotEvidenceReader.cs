using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.Windows.Startup;

// The Agent shares one admission slot with inventory/inspection and retains it until this
// task REALLY completes. Do not signal cancellation early or create replacement workers.
public sealed class StartupSlotEvidenceReader(ProjectPaths paths) : IStartupSlotEvidenceReader
{
    private readonly IStartupNativeAccess _native = WindowsStartupNativeAccess.Instance;
    internal StartupSlotEvidenceReader(ProjectPaths paths, IStartupNativeAccess native) : this(paths)
    { _native = native; } // Owned test mapping, never accepted from configuration/RPC.

    public Task<StartupSlotEvidenceRead> ReadAsync(string backupId, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<StartupSlotEvidenceRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                StartupSlotEvidenceRead result;
                using (var manager = new StartupManager(paths, _native))
                { result = manager.ReadSlotEvidenceAsync(backupId, cancellationToken).GetAwaiter().GetResult(); }
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { completion.TrySetCanceled(cancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true, Name = "SystemAutorun startup slot evidence" };
        // No COM or shortcut parser is used by this adapter; a dedicated MTA suffices.
        thread.Start();
        return completion.Task;
    }
}
