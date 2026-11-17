using System.ComponentModel;
using System.Security;
using System.Security.Principal;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Windows.Startup;

// Only fixed internal sources, not a ledger path, RPC path or historical attestation.
// This primitive is synchronous and creates no workers; callers retain their admission
// slot for its real lifetime. No target, shortcut bytes, registry key or ledger is read.
internal static class StartupSourceScopeProbe
{
    internal static StartupSourceScopeObservation Observe(string source, IStartupNativeAccess native,
        CancellationToken cancellationToken, Func<string?>? readActorSid = null, Action? beforeSeal = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (kind, folder) = source switch
        {
            StartupManager.UserStartupSource => ("user", "Startup"),
            StartupManager.CommonStartupSource => ("machine", "CommonStartup"),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "A fixed Startup known-folder source is required."),
        };
        var actor = readActorSid ?? CurrentActorSid;
        StartupSourceScopeObservation Unknown(string reason, int? error = null) => new(1, source, kind, folder,
            "unknown", "source_scope_" + reason, error, null, null, null, "unknown", null, DateTimeOffset.UtcNow,
            NativeWriteReady: false, NativeWritePerformed: false, ExecutionRequested: false);
        try
        {
            var sid = CanonicalSid(actor());
            cancellationToken.ThrowIfCancellationRequested();
            if (sid is null) { return Unknown("identity_unavailable"); }
            var directory = StartupShortcutPathLease.LocalPath(native.GetShortcutDirectory(source));
            var capture = StartupNativeSlotProbe.ObserveDirectory(directory, cancellationToken, () =>
            {
                beforeSeal?.Invoke();
                // Strict ordinal mapping comparison also detects case-only remapping in
                // case-sensitive namespaces. It does not follow the changed destination.
                return CanonicalSid(actor()) == sid
                    && StartupShortcutPathLease.LocalPath(native.GetShortcutDirectory(source)) == directory;
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (capture.State != "observed") { return Unknown(capture.Reason!, capture.NativeErrorCode); }
            return new(1, source, kind, folder, "observed", null, null, sid, kind == "user" ? sid : null,
                directory, capture.CaseSensitivity, capture.DirectoryIdentity, capture.ObservedAt,
                NativeWriteReady: false, NativeWritePerformed: false, ExecutionRequested: false);
        }
        catch (UnauthorizedAccessException) { return Unknown("permission_denied", 5); }
        catch (SecurityException) { return Unknown("permission_denied", 5); }
        catch (Win32Exception error) { return Unknown("identity_unavailable", error.NativeErrorCode); }
        catch (ArgumentException) { return Unknown("context_invalid"); }
        catch (InvalidOperationException) { return Unknown("context_invalid"); }
        catch (IOException) { return Unknown("directory_unavailable"); }
    }

    private static string? CurrentActorSid()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        // Known-folder resolution is not an impersonation API. Do not silently mix an
        // impersonated thread SID with process/profile-derived folder locations.
        return identity.ImpersonationLevel == TokenImpersonationLevel.None ? identity.User?.Value : null;
    }

    private static string? CanonicalSid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return null; }
        try { return new SecurityIdentifier(value).Value; }
        catch (ArgumentException) { return null; }
    }
}
