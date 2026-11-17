using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Windows.Startup;

internal static partial class StartupNativeSlotProbe
{
    // Only the fixed external history layout calls this reader. No path from a ledger
    // or index row is opened. Both file handles remain held until the caller finishes.
    internal sealed class HistoryDirectoryReadLease : IDisposable
    {
        private SshInputFileLease? _chain;
        private SshInputFileLease? _rootChain;
        private SafeFileHandle? _parent;
        private SafeFileHandle? _root;
        private string _path = "";
        private string _actor = "";
        private StartupHistoryDirectory _expectedRoot = null!;
        private StartupNativeFileIdentity _identity = null!;
        private string _casing = "";
        internal string DirectoryPath => _path;
        internal StartupNativeFileIdentity DirectoryIdentity => _identity;

        internal static HistoryDirectoryReadLease Open(string directory, StartupHistoryDirectory root, string actor, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var lease = new HistoryDirectoryReadLease();
            try
            {
                lease._path = StartupShortcutPathLease.LocalPath(directory); lease._actor = actor; lease._expectedRoot = root;
                lease._rootChain = SshInputFileLease.OpenDirectory(root.Path); lease._root = OpenParent(root.Path);
                lease._chain = SshInputFileLease.OpenDirectory(lease._path); lease._parent = OpenParent(lease._path, readSecurity: true);
                lease._identity = Identity(lease._parent); lease._casing = CaseSensitivity(lease._parent);
                // Physical ancestry, not string prefixes, also rejects aliases into root.
                for (var current = lease._path; current is not null; current = Path.GetDirectoryName(current))
                {
                    token.ThrowIfCancellationRequested(); using var parent = OpenParent(current);
                    if (Identity(parent) == root.Identity) { throw new InvalidOperationException("History state must be outside the project root."); }
                }
                lease.VerifyCurrent(token); return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        internal HistoryFileReadLease OpenFile(string leaf, int maximumBytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (leaf != StartupHistoryStoreReader.IndexName
                && !StartupHistoryStoreReader.IsProofName(leaf) && !StartupHistoryOperationStoreReader.IsLinkName(leaf))
            { throw new ArgumentException("Only fixed history index, proof or operation-link leaves are allowed."); }
            var handle = OpenRelative(_parent!, leaf, out var error, caseSensitive: _casing == "sensitive");
            if (error != 0) { handle.Dispose(); throw new Win32Exception(error); }
            return HistoryFileReadLease.Open(handle, maximumBytes, _actor, token);
        }

        internal void VerifyCurrent(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_casing is not ("sensitive" or "insensitive") || Identity(_parent!) != _identity || CaseSensitivity(_parent!) != _casing
                || Identity(_root!) != _expectedRoot.Identity || CaseSensitivity(_root!) != _expectedRoot.CaseSensitivity)
            { throw new InvalidOperationException("History directory context changed."); }
            using var current = OpenParent(_path);
            if (Identity(current) != _identity || CaseSensitivity(current) != _casing)
            { throw new InvalidOperationException("History directory mapping changed."); }
            VerifyPrivateHistoryAcl(_parent!, _actor, requireProtected: true);
        }

        public void Dispose()
        { _parent?.Dispose(); _chain?.Dispose(); _root?.Dispose(); _rootChain?.Dispose(); }
    }

    internal sealed class HistoryFileReadLease : IDisposable
    {
        private SafeFileHandle? _handle;
        private FileStream? _stream;
        private string _actor = "";
        private int _maximum;
        private StartupNativeFileIdentity _identity = null!;
        internal byte[] Bytes { get; private set; } = [];

        internal static HistoryFileReadLease Open(SafeFileHandle handle, int maximumBytes, string actor, CancellationToken token)
        {
            var lease = new HistoryFileReadLease { _handle = handle, _actor = actor, _maximum = maximumBytes };
            try
            {
                var (identity, length) = lease.Metadata(); lease._identity = identity;
                VerifyPrivateHistoryAcl(handle, actor, requireProtected: false);
                lease._stream = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: false);
                lease.Bytes = new byte[length]; var offset = 0;
                while (offset < length)
                {
                    token.ThrowIfCancellationRequested(); var read = lease._stream.Read(lease.Bytes, offset, Math.Min(4096, length - offset));
                    if (read == 0) { throw new IOException("History file length changed."); } offset += read;
                }
                if (lease._stream.ReadByte() != -1) { throw new IOException("History file length changed."); }
                lease.VerifyCurrent(token); return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        private (StartupNativeFileIdentity Identity, int Length) Metadata()
        {
            if (GetFileType(_handle!) != 1 || !GetFileInformationByHandle(_handle!, out var info))
            { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            var length = ((long)info.SizeHigh << 32) | info.SizeLow;
            if ((info.Attributes & 0x410) != 0 || info.Links != 1 || length < 0 || length > _maximum)
            { throw new InvalidOperationException("History requires a bounded ordinary single-link file."); }
            return (Identity(_handle!), (int)length);
        }

        internal void VerifyCurrent(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var metadata = Metadata();
            if (metadata.Identity != _identity || metadata.Length != Bytes.Length) { throw new IOException("History file identity changed."); }
            VerifyPrivateHistoryAcl(_handle!, _actor, requireProtected: false);
        }
        public void Dispose()
        {
            try { _stream?.Dispose(); }
            finally { _handle?.Dispose(); CryptographicOperations.ZeroMemory(Bytes); }
        }
    }

    // Read owner/DACL from the SAME held object, not a second pathname lookup. A
    // conservative allow-list is not a Windows effective-access evaluator or ACL repair.
    private static void VerifyPrivateHistoryAcl(SafeFileHandle handle, string actor, bool requireProtected, bool trustedContainer = false)
    {
        var descriptor = IntPtr.Zero;
        try
        {
            var error = GetHistorySecurityInfo(handle, 1, 5, out _, out _, out _, out _, out descriptor);
            if (error != 0) { throw new Win32Exception(unchecked((int)error)); }
            if (descriptor == IntPtr.Zero) { throw new UnauthorizedAccessException("History security descriptor is missing."); }
            var length = GetHistorySecurityDescriptorLength(descriptor);
            if (length is 0 or > 65536) { throw new UnauthorizedAccessException("History security descriptor is invalid."); }
            var bytes = new byte[length]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var security = new RawSecurityDescriptor(bytes, 0);
            var owner = security.Owner?.Value;
            if ((owner != actor && (!trustedContainer || owner is not ("S-1-5-18" or "S-1-5-32-544"))) || security.DiscretionaryAcl is null
                || requireProtected && (security.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
            { throw new UnauthorizedAccessException("History owner or DACL is not private."); }
            const uint writing = 0x500d0156; // generic write/all, write data/EA/attrs, delete/child-delete, WRITE_DAC/OWNER
            foreach (GenericAce item in security.DiscretionaryAcl)
            {
                if (item is not CommonAce { IsCallback: false } ace || ace.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
                { throw new UnauthorizedAccessException("History DACL contains unsupported rules."); }
                if (ace.AceQualifier == AceQualifier.AccessDenied || trustedContainer && (ace.AceFlags & AceFlags.InheritOnly) != 0) { continue; }
                var sid = ace.SecurityIdentifier.Value;
                if (sid != actor && sid is not ("S-1-5-18" or "S-1-5-32-544") && (unchecked((uint)ace.AccessMask) & writing) != 0)
                { throw new UnauthorizedAccessException("History DACL permits an untrusted writer."); }
            }
        }
        finally { if (descriptor != IntPtr.Zero) { _ = FreeHistorySecurityDescriptor(descriptor); } }
    }

    [DllImport("advapi32.dll", EntryPoint = "GetSecurityInfo", ExactSpelling = true)]
    private static extern uint GetHistorySecurityInfo(SafeFileHandle handle, int objectType, uint information,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorLength", ExactSpelling = true)]
    private static extern uint GetHistorySecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll", EntryPoint = "LocalFree", ExactSpelling = true)]
    private static extern IntPtr FreeHistorySecurityDescriptor(IntPtr descriptor);
}
