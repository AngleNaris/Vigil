namespace SystemAutorun.Core.Infrastructure;

public static class RootInstanceLease
{
    // A second, filesystem-enforced lifetime guard covers physical aliases that cannot
    // be unified by Windows' final-path API (for example, some network-share aliases).
    public static FileStream? TryAcquire(ProjectPaths paths)
    {
        var file = Path.Combine(paths.RootDirectory, $".autorun.agent.{CurrentUserIdentity.Key}.lock");
        try
        {
            return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                1, FileOptions.DeleteOnClose);
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
        {
            return null;
        }
    }
}
