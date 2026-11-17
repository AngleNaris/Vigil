using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Windows.Startup;

internal static partial class StartupNativeSlotProbe
{
    // Only the server-derived root intent, never a ledger-supplied path. This lease
    // reserves DELETE but does nothing on ordinary Dispose unless deletion was armed.
    internal sealed class HistoryIntentCleanupLease : IDisposable
    {
        private readonly ProjectPaths _paths;
        private readonly RootWriteLease.Held _root;
        private readonly string _directory;
        private readonly string _name;
        private SshInputFileLease? _chain;
        private SafeFileHandle? _parent;
        private SafeFileHandle? _leaf;
        private FileStream? _stream;
        private StartupNativeFileIdentity? _parentIdentity;
        private StartupNativeFileIdentity? _identity;
        private bool _disposed;
        private bool _armed;
        private HistoryIntentCleanupLease(ProjectPaths paths, RootWriteLease.Held root)
        { _paths = paths; _root = root; _directory = Path.GetDirectoryName(paths.StartupBackupPath)!; _name = Path.GetFileName(paths.StartupBackupPath) + ".transaction"; }

        internal static HistoryIntentCleanupLease Open(ProjectPaths paths, RootWriteLease.Held root, byte[] expected, CancellationToken token)
        {
            root.Require(paths); token.ThrowIfCancellationRequested();
            if (expected.Length is 0 or > StartupHistoryIntentFormat.MaximumBytes) { throw new IOException("Intent image is out of bounds."); }
            var lease = new HistoryIntentCleanupLease(paths, root);
            try
            {
                lease._chain = SshInputFileLease.OpenDirectory(lease._directory); lease._parent = OpenParent(lease._directory);
                lease._parentIdentity = Identity(lease._parent); lease.VerifyParent(token);
                lease._leaf = OpenRelative(lease._parent, lease._name, out var error, reserveDeleteAccess: true);
                if (error != 0) { throw new Win32Exception(error); }
                lease._identity = Identity(lease._leaf);
                lease._stream = new FileStream(lease._leaf, FileAccess.Read, bufferSize: 1, isAsync: false);
                lease.VerifyCurrent(expected, token); return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        private void VerifyParent(CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested(); _root.Require(_paths);
            using var current = OpenParent(_directory);
            if (Identity(current) != _parentIdentity || Identity(_parent!) != _parentIdentity
                || CaseSensitivity(current) != "insensitive" || CaseSensitivity(_parent!) != "insensitive")
            { throw new IOException("The intent parent identity or case semantics changed."); }
        }
        internal void VerifyCurrent(byte[] expected, CancellationToken token)
        {
            VerifyParent(token);
            if (_armed || _leaf is null || _stream is null) { throw new InvalidOperationException("The cleanup handle is no longer a pending intent."); }
            if (GetFileType(_leaf) != 1 || !GetFileInformationByHandle(_leaf, out var info)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            var length = ((long)info.SizeHigh << 32) | info.SizeLow;
            if ((info.Attributes & 0x411) != 0 || info.Links != 1 || length != expected.Length || Identity(_leaf) != _identity
                || _identity!.VolumeSerialNumber != _parentIdentity!.VolumeSerialNumber)
            { throw new IOException("Cleanup requires the same bounded ordinary single-link intent, without readonly/reparse attributes."); }
            var actual = new byte[expected.Length];
            try
            {
                _stream.Position = 0; var offset = 0;
                while (offset < actual.Length)
                {
                    token.ThrowIfCancellationRequested(); var read = _stream.Read(actual, offset, Math.Min(4096, actual.Length - offset));
                    if (read == 0) { throw new IOException("Intent length changed."); } offset += read;
                }
                token.ThrowIfCancellationRequested();
                if (_stream.ReadByte() != -1 || !CryptographicOperations.FixedTimeEquals(actual, expected)) { throw new IOException("Intent image changed."); }
            }
            finally { CryptographicOperations.ZeroMemory(actual); }
        }

        internal void RemoveAndClose(byte[] expected, CancellationToken token)
        {
            VerifyCurrent(expected, token); _armed = true;
            uint flags = 0x3; // DELETE | POSIX_SEMANTICS, never IGNORE_READONLY or a path fallback.
            if (!SetIntentDisposition(_leaf!, 21 /* FileDispositionInfoEx */, ref flags, 4)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            // POSIX unlink is observable only after the delete handle closes. Keep the
            // parent/ancestry held across this close and the independent namespace probe.
            _stream!.Dispose(); _stream = null; _leaf!.Dispose(); _leaf = null;
        }
        internal void VerifyRemoved(CancellationToken token)
        {
            VerifyParent(token);
            if (!_armed || _leaf is not null || _stream is not null) { throw new InvalidOperationException("Cleanup has not closed the delete handle."); }
            using var found = OpenRelative(_parent!, _name, out var error);
            if (error != 2) { throw error == 0 ? new IOException("The intent namespace is occupied.") : new Win32Exception(error); }
        }
        public void Dispose()
        {
            if (_disposed) { return; } _disposed = true;
            try { _stream?.Dispose(); }
            finally { try { _leaf?.Dispose(); } finally { try { _parent?.Dispose(); } finally { _chain?.Dispose(); } } }
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetIntentDisposition(SafeFileHandle file, int infoClass, ref uint flags, uint size);
}
