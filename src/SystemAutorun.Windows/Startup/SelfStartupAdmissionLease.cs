using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Windows.Startup;

internal static partial class StartupNativeSlotProbe
{
    // Cooperating writers of this ONE actor's fixed HKCU Run item, across roots,
    // packages and await continuations sharing the same OS known-folder namespace.
    // Not a Windows registry CAS, cross-profile uniqueness proof, or
    // authority to write. Never version the namespace independently of old writers.
    internal sealed class SelfStartupAdmissionLease : IDisposable
    {
        internal const string LockName = "hkcu-run-systemautorun-v1.lock";
        private readonly List<SelfTargetDirectory> _directories = [];
        private readonly Func<SelfStartupAdmissionScope> _scope;
        private readonly SelfStartupAdmissionScope _expected;
        private SafeFileHandle? _lock;
        private StartupNativeFileIdentity? _lockIdentity;
        private bool _disposed;
        internal string LockPath { get; private set; } = "";
        internal string ActorSid => _expected.ActorSid;
        internal StartupNativeFileIdentity FileIdentity => _lockIdentity!;

        private SelfStartupAdmissionLease(Func<SelfStartupAdmissionScope> scope, SelfStartupAdmissionScope expected)
        { _scope = scope; _expected = expected; }

        // No root, caller-selected SID, executable, hive, key or environment override.
        internal static Task<SelfStartupAdmissionLease> AcquireAsync(CancellationToken token = default, TimeSpan? timeout = null) =>
            AcquireAsync(CurrentScope, token, timeout);

        // INTERNAL isolated-storage seam, never a public/RPC request field.
        internal static async Task<SelfStartupAdmissionLease> AcquireAsync(Func<SelfStartupAdmissionScope> scope,
            CancellationToken token = default, TimeSpan? timeout = null)
        {
            token.ThrowIfCancellationRequested(); var limit = timeout ?? TimeSpan.FromSeconds(15);
            if (limit <= TimeSpan.Zero || limit > TimeSpan.FromMinutes(1)) { throw new ArgumentOutOfRangeException(nameof(timeout)); }
            SelfStartupAdmissionLease? lease = null;
            try
            {
                var expected = scope(); StartupHistoryProtection.RequireActor(expected.ActorSid);
                var local = StartupShortcutPathLease.LocalPath(expected.LocalAppData);
                lease = new SelfStartupAdmissionLease(scope, expected);
                lease.Pin(local, requireProtected: false, trustedContainer: true);
                using var directorySecurity = new HistoryCreationSecurity(expected.ActorSid, directory: true);
                var parent = Path.Combine(local, "Vigil");
                CreateHistoryDirectory(parent, directorySecurity); lease.Pin(parent, requireProtected: false, trustedContainer: true);
                var actorKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expected.ActorSid)));
                var directory = Path.Combine(parent, "self-startup-v1-" + actorKey);
                CreateHistoryDirectory(directory, directorySecurity); lease.Pin(directory, requireProtected: true, trustedContainer: false);
                lease.LockPath = Path.Combine(directory, LockName);
                using var fileSecurity = new HistoryCreationSecurity(expected.ActorSid, directory: false);
                var elapsed = Stopwatch.StartNew(); Win32Exception? contention = null;
                while (true)
                {
                    lease.VerifyDirectories(token);
                    // OPEN_ALWAYS, NOT truncation or DELETE_ON_CLOSE. Existing files
                    // get no ACL repair. Share=0 is an OS handle lease, not byte locking.
                    var handle = CreateFileW(lease.LockPath, 0xc0000000, 0, fileSecurity.Attributes, 4, 0x00200080, IntPtr.Zero);
                    if (!handle.IsInvalid)
                    {
                        lease._lock = handle; lease._lockIdentity = Identity(handle);
                        lease.VerifyCurrent(token); return lease;
                    }
                    var error = Marshal.GetLastWin32Error(); handle.Dispose();
                    if (error is not (32 or 33)) { throw new Win32Exception(error); }
                    contention = new Win32Exception(error); var remaining = limit - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero) { throw new SelfStartupAdmissionBusyException(contention); }
                    await Task.Delay(remaining < TimeSpan.FromMilliseconds(25) ? remaining : TimeSpan.FromMilliseconds(25), token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (AdmissionFailure(error))
            { lease?.Dispose(); throw new SelfStartupAdmissionException(error); }
            catch { lease?.Dispose(); throw; }
        }

        internal static SelfStartupAdmissionScope CurrentScope()
        {
            var actor = StartupHistoryProtection.Actor(); var folder = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
            IntPtr value = IntPtr.Zero;
            try
            {
                // Current non-impersonating user, DONT_VERIFY only; no CREATE and no
                // LOCALAPPDATA/current-directory/project-root fallback.
                Marshal.ThrowExceptionForHR(GetSelfAdmissionKnownFolder(in folder, 0x4000, IntPtr.Zero, out value));
                var path = StartupShortcutPathLease.LocalPath(Marshal.PtrToStringUni(value) ?? "");
                StartupHistoryProtection.RequireActor(actor); return new(actor, path);
            }
            finally { if (value != IntPtr.Zero) { Marshal.FreeCoTaskMem(value); } }
        }

        private void Pin(string path, bool requireProtected, bool trustedContainer)
        {
            var directory = SelfTargetDirectory.Open(path);
            try
            {
                using var security = OpenParent(path, readSecurity: true);
                if (Identity(security) != directory.FileIdentity) { throw new IOException("Admission directory changed."); }
                VerifyPrivateHistoryAcl(security, _expected.ActorSid, requireProtected, trustedContainer);
                _directories.Add(directory);
            }
            catch { directory.Dispose(); throw; }
        }

        private void VerifyDirectories(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); StartupHistoryProtection.RequireActor(_expected.ActorSid);
            if (_scope() != _expected) { throw new InvalidOperationException("Admission actor or known-folder mapping changed."); }
            for (var index = 0; index < _directories.Count; index++)
            {
                token.ThrowIfCancellationRequested(); var directory = _directories[index]; directory.VerifyCurrent();
                using var security = OpenParent(directory.Path, readSecurity: true);
                if (Identity(security) != directory.FileIdentity) { throw new IOException("Admission directory changed."); }
                VerifyPrivateHistoryAcl(security, _expected.ActorSid, requireProtected: index == 2, trustedContainer: index != 2);
            }
            StartupHistoryProtection.RequireActor(_expected.ActorSid);
        }

        internal void VerifyCurrent(CancellationToken token = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
            try
            {
                VerifyDirectories(token); CheckHistoryOrdinaryFile(_lock!, 0, _expected.ActorSid);
                VerifyPrivateHistoryAcl(_lock!, _expected.ActorSid, requireProtected: true);
                if (Identity(_lock!) != _lockIdentity) { throw new IOException("Admission lock object changed."); }
                // Attribute/security-only opens do not reserve data sharing. Compare
                // fresh namespace identity without releasing our exclusive data handle.
                using var current = CreateFileW(LockPath, 0x20080, 7, IntPtr.Zero, 3, 0x00200080, IntPtr.Zero);
                RequireHistoryHandle(current);
                if (Identity(current) != _lockIdentity) { throw new IOException("Admission lock namespace changed."); }
                StartupHistoryProtection.RequireActor(_expected.ActorSid);
            }
            catch (Exception error) when (AdmissionFailure(error)) { throw new SelfStartupAdmissionException(error); }
        }

        public void Dispose()
        {
            if (_disposed) { return; } _disposed = true;
            try { _lock?.Dispose(); }
            finally { for (var index = _directories.Count - 1; index >= 0; index--) { _directories[index].Dispose(); } }
        }

        private static bool AdmissionFailure(Exception error) => error is IOException or InvalidDataException or Win32Exception
            or UnauthorizedAccessException or CryptographicException or System.Security.SecurityException or ArgumentException
            or InvalidOperationException or COMException;
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetKnownFolderPath", ExactSpelling = true)]
    private static extern int GetSelfAdmissionKnownFolder(in Guid id, uint flags, IntPtr token, out IntPtr path);
}

internal sealed record SelfStartupAdmissionScope(string ActorSid, string LocalAppData);
// Neither exception says whether an earlier/future native call mutated Windows.
internal sealed class SelfStartupAdmissionException(Exception inner) : Exception("Self startup coordination could not be verified.", inner);
internal sealed class SelfStartupAdmissionBusyException(Exception inner) : TimeoutException("Another cooperating self startup operation is still running.", inner);
