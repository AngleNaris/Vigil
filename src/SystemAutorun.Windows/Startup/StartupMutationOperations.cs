namespace SystemAutorun.Windows.Startup;

// Internal fault boundary, never selectable through config/RPC or a public constructor.
// Production invokes the real operation once. Owned fixtures may throw before/after it;
// an exception does not prove whether the underlying effect occurred.
internal enum StartupMutationOperation
{
    IntentPublication,
    NativeMutation,
    ShortcutPreparation,
    LedgerPublication,
    NativeRollback,
    IntentCleanup,
    TemporaryCleanup,
}

internal interface IStartupMutationOperations
{
    void Execute(StartupMutationOperation operation, Action action);
}

internal sealed class StartupMutationOperations : IStartupMutationOperations
{
    internal static StartupMutationOperations Instance { get; } = new();
    private StartupMutationOperations() { }
    public void Execute(StartupMutationOperation operation, Action action) => action();
}
