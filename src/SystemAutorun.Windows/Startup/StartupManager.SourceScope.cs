using SystemAutorun.Core.Models;

namespace SystemAutorun.Windows.Startup;

public sealed partial class StartupManager
{
    // Backend only. A current source observation, not record authentication, a native
    // preparation/commit token, a new IPC schema or an upgrade of legacy backup rows.
    public StartupSourceScopeObservation ObserveShortcutSourceScope(string source, CancellationToken cancellationToken = default) =>
        StartupSourceScopeProbe.Observe(source, _native, cancellationToken);

    // Ephemeral backend input, NOT prepare/commit or an attested disable. The caller
    // must dispose it; no GUI/RPC, ledger, intent or native writer is connected to it.
    internal StartupShortcutCapture CaptureShortcutForDisable(string source, string name, CancellationToken cancellationToken = default) =>
        StartupShortcutCapture.Open(source, name, _native, cancellationToken);
}
