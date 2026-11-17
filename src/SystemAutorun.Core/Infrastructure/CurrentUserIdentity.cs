using System.Security.Principal;

namespace SystemAutorun.Core.Infrastructure;

public static class CurrentUserIdentity
{
    public static string Key
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return (identity.User?.Value
                ?? throw new InvalidOperationException("Unable to obtain the current Windows user SID.")).Replace('-', '_');
        }
    }
}
