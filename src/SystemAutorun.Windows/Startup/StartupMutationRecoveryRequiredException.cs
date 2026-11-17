namespace SystemAutorun.Windows.Startup;

// This is not permission to replay the mutation or remove a pending intent.
// CommitConfirmed describes an observed publication return, not crash durability,
// external native state, or a guarantee that the intent still exists after an I/O error.
public sealed class StartupMutationRecoveryRequiredException : IOException
{
    public string Operation { get; }
    public string BackupId { get; }
    public string Stage { get; }
    public bool CommitConfirmed { get; }

    internal StartupMutationRecoveryRequiredException(
        string operation, string backupId, string stage, bool commitConfirmed, Exception cause)
        : base($"Startup {operation} requires inspection at {stage}. "
            + (commitConfirmed ? "Ledger publication was confirmed; do not reapply. " : "Do not retry automatically. ")
            + $"Inspect native state, ledger and any pending intent. Backup ID: {backupId}.", cause)
    {
        Operation = operation;
        BackupId = backupId;
        Stage = stage;
        CommitConfirmed = commitConfirmed;
    }
}
