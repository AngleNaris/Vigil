namespace SystemAutorun.Core.Infrastructure;

// Command support only, not authorization. The protected startup mutation workflow
// is not complete; legacy disable/restore must not bypass it through the Helper.
public static class ElevatedCommandPolicy
{
    public const int RejectedExitCode = 64;

    public static bool IsSupported(string? command) => command is
        "install-boot" or "uninstall-boot" or "start-boot"
        or "install-maintenance" or "uninstall-maintenance" or "run-maintenance";
}
