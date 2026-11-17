using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Windows.Startup;

internal static partial class StartupNativeSlotProbe
{
    internal sealed class HistoryStoreWriteLease : IDisposable
    {
        internal const string LockName = "writer-v1.lock";
        private HistoryDirectoryReadLease? _directory;
        private SafeFileHandle? _parent;
        private SafeFileHandle? _lock;
        private string _actor = "";
        internal HistoryDirectoryReadLease Directory => _directory!;

        internal static HistoryStoreWriteLease Open(string path, StartupHistoryDirectory root, string actor, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); StartupHistoryProtection.RequireActor(actor); var lease = new HistoryStoreWriteLease { _actor = actor };
            try
            {
                path = StartupShortcutPathLease.LocalPath(path);
                // Pin and verify root before creating even private side-store state.
                using var rootChain = SshInputFileLease.OpenDirectory(root.Path); using var rootHandle = OpenParent(root.Path);
                if (Identity(rootHandle) != root.Identity || CaseSensitivity(rootHandle) != root.CaseSensitivity)
                { throw new InvalidOperationException("Project root changed before history creation."); }
                using var security = new HistoryCreationSecurity(actor, directory: true);
                var parentPath = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("A private history parent is required.");
                SshInputFileLease parentChain;
                try { parentChain = SshInputFileLease.OpenDirectory(parentPath); }
                catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: 2 or 3 })
                {
                    // Default layout has at most two missing levels. Never recursively
                    // manufacture a profile, a drive root or an arbitrary supplied chain.
                    var grand = Path.GetDirectoryName(parentPath) ?? throw new InvalidOperationException("History profile is unavailable.");
                    using var grandChain = SshInputFileLease.OpenDirectory(grand); using var grandHandle = OpenParent(grand, readSecurity: true);
                    CheckHistoryOutsideRoot(grand, root, token); VerifyPrivateHistoryAcl(grandHandle, actor, false, trustedContainer: true);
                    CreateHistoryDirectory(parentPath, security);
                    parentChain = SshInputFileLease.OpenDirectory(parentPath);
                }
                using (parentChain)
                using (var parent = OpenParent(parentPath, readSecurity: true))
                {
                    CheckHistoryOutsideRoot(parentPath, root, token); VerifyPrivateHistoryAcl(parent, actor, false, trustedContainer: true);
                    CreateHistoryDirectory(path, security);
                    lease._directory = HistoryDirectoryReadLease.Open(path, root, actor, token);
                }
                lease._parent = CreateFileW(path, 0x120083, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero); // READ_CONTROL, read/ADD_FILE, synchronize
                RequireHistoryHandle(lease._parent);
                if (Identity(lease._parent) != lease._directory.DirectoryIdentity) { throw new IOException("History parent changed."); }
                using var fileSecurity = new HistoryCreationSecurity(actor, directory: false);
                lease._lock = CreateFileW(Path.Combine(path, LockName), 0xc0000000, 0, fileSecurity.Attributes, 4, 0x00200080, IntPtr.Zero);
                RequireHistoryHandle(lease._lock); CheckHistoryOrdinaryFile(lease._lock, 0, actor);
                lease.VerifyCurrent(token); return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        internal HistoryStageFile CreateStage(byte[] bytes, CancellationToken token)
        { VerifyCurrent(token); return HistoryStageFile.Create(this, _parent!, _actor, bytes, token); }

        internal void RequireEmptyBootstrap()
        {
            // Missing index is not permission to discard orphaned or unknown evidence.
            foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(Directory.DirectoryPath))
            { if (Path.GetFileName(path) != LockName) { throw new InvalidOperationException("Missing history index requires reconciliation."); } }
        }

        internal void VerifyCurrent(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Directory.VerifyCurrent(token);
            if (Identity(_parent!) != Directory.DirectoryIdentity) { throw new IOException("History writer parent changed."); }
            CheckHistoryOrdinaryFile(_lock!, 0, _actor); StartupHistoryProtection.RequireActor(_actor);
        }
        public void Dispose() { _lock?.Dispose(); _parent?.Dispose(); _directory?.Dispose(); }
    }

    internal sealed class HistoryStageFile : IDisposable
    {
        private SafeFileHandle? _handle;
        private FileStream? _stream;
        private SafeFileHandle _parent = null!;
        private HistoryStoreWriteLease _lease = null!;
        private bool _published;

        internal static HistoryStageFile Create(HistoryStoreWriteLease lease, SafeFileHandle parent, string actor, byte[] bytes, CancellationToken token)
        {
            if (bytes.Length is 0 or > StartupHistoryProtection.MaximumProtectedIndexBytes) { throw new ArgumentException("History stage size is invalid."); }
            var stage = new HistoryStageFile { _parent = parent, _lease = lease };
            try
            {
                using var security = new HistoryCreationSecurity(actor, directory: false);
                var path = Path.Combine(lease.Directory.DirectoryPath, "stage-" + Guid.NewGuid().ToString("N") + ".tmp");
                stage._handle = CreateFileW(path, 0xc0010000, 1, security.Attributes, 1, 0x00200080, IntPtr.Zero); // CREATE_NEW, READ/WRITE/DELETE, share READ
                RequireHistoryHandle(stage._handle); CheckHistoryOrdinaryFile(stage._handle, 0, actor);
                stage._stream = new FileStream(stage._handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
                for (var offset = 0; offset < bytes.Length; offset += 4096)
                { token.ThrowIfCancellationRequested(); stage._stream.Write(bytes, offset, Math.Min(4096, bytes.Length - offset)); }
                stage._stream.Flush(flushToDisk: true); stage._stream.Position = 0;
                var buffer = new byte[4096];
                try
                {
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        token.ThrowIfCancellationRequested(); var count = stage._stream.Read(buffer, 0, Math.Min(buffer.Length, bytes.Length - offset));
                        if (count == 0 || !buffer.AsSpan(0, count).SequenceEqual(bytes.AsSpan(offset, count))) { throw new IOException("History stage read-back differs."); }
                        offset += count;
                    }
                    if (stage._stream.ReadByte() != -1) { throw new IOException("History stage grew."); }
                }
                finally { CryptographicOperations.ZeroMemory(buffer); }
                CheckHistoryOrdinaryFile(stage._handle, bytes.Length, actor); return stage;
            }
            catch { stage.Dispose(); throw; }
        }

        internal void Publish(string leaf, bool replaceIndex, CancellationToken token, bool initialIndex = false) =>
            PublishCore(leaf, replaceIndex, token, initialIndex, operationLink: false);

        internal void PublishOperationLink(string recordId, CancellationToken token) =>
            PublishCore(StartupHistoryOperationStoreReader.LinkName(recordId), replaceIndex: false, token, initialIndex: false, operationLink: true);

        private void PublishCore(string leaf, bool replaceIndex, CancellationToken token, bool initialIndex, bool operationLink)
        {
            _lease.VerifyCurrent(token);
            if (operationLink ? replaceIndex || initialIndex || !StartupHistoryOperationStoreReader.IsLinkName(leaf)
                : replaceIndex ? leaf != StartupHistoryStoreReader.IndexName : !StartupHistoryStoreReader.IsProofName(leaf))
            { throw new ArgumentException("Invalid history publication leaf."); }
            if (_published) { throw new InvalidOperationException("History stage was already published."); }
            var name = Encoding.Unicode.GetBytes(leaf); var offset = Marshal.OffsetOf<HistoryRenameHeader>(nameof(HistoryRenameHeader.Name)).ToInt32();
            var length = checked(offset + name.Length); var buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.Copy(new byte[length], 0, buffer, length);
                Marshal.WriteInt32(buffer, replaceIndex && !initialIndex ? 1 : 0); // legacy FileRenameInformation, no POSIX/ignore-readonly flags
                Marshal.WriteIntPtr(buffer, Marshal.OffsetOf<HistoryRenameHeader>(nameof(HistoryRenameHeader.Root)).ToInt32(), _parent.DangerousGetHandle());
                Marshal.WriteInt32(buffer, Marshal.OffsetOf<HistoryRenameHeader>(nameof(HistoryRenameHeader.Length)).ToInt32(), name.Length);
                Marshal.Copy(name, 0, buffer + offset, name.Length);
                var status = SetHistoryFileInformation(_handle!, out _, buffer, (uint)length, 10);
                if (status != 0) { throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status))); }
                _published = true;
            }
            finally { Marshal.FreeHGlobal(buffer); GC.KeepAlive(_parent); }
        }

        public void Dispose()
        {
            try
            {
                if (!_published && _handle is { IsInvalid: false, IsClosed: false })
                {
                    var buffer = Marshal.AllocHGlobal(1);
                    try
                    {
                        Marshal.WriteByte(buffer, 1); var status = SetHistoryFileInformation(_handle, out _, buffer, 1, 13);
                        if (status != 0) { throw new IOException("Unpublished owned history stage cleanup failed.", new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)))); }
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }
            finally { try { _stream?.Dispose(); } finally { _handle?.Dispose(); } }
        }
    }

    private static void CheckHistoryOutsideRoot(string path, StartupHistoryDirectory root, CancellationToken token)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            token.ThrowIfCancellationRequested(); using var directory = OpenParent(current);
            if (Identity(directory) == root.Identity) { throw new InvalidOperationException("History state cannot be created inside root."); }
        }
    }
    private static void CreateHistoryDirectory(string path, HistoryCreationSecurity security)
    {
        StartupHistoryProtection.RequireActor(security.Actor);
        if (!CreatePrivateHistoryDirectory(path, security.Attributes))
        { var error = Marshal.GetLastWin32Error(); if (error != 183) { throw new Win32Exception(error); } }
    }
    private static void RequireHistoryHandle(SafeFileHandle handle)
    { if (handle.IsInvalid) { throw new Win32Exception(Marshal.GetLastWin32Error()); } }
    private static void CheckHistoryOrdinaryFile(SafeFileHandle handle, int length, string actor)
    {
        if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var info)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        if ((info.Attributes & 0x410) != 0 || info.Links != 1 || (((long)info.SizeHigh << 32) | info.SizeLow) != length)
        { throw new InvalidOperationException("History writer requires an ordinary single-link exact-length file."); }
        VerifyPrivateHistoryAcl(handle, actor, requireProtected: false);
    }

    private sealed class HistoryCreationSecurity : IDisposable
    {
        private GCHandle _pinned;
        internal IntPtr Attributes { get; private set; }
        internal string Actor { get; }
        internal HistoryCreationSecurity(string actor, bool directory)
        {
            StartupHistoryProtection.RequireActor(actor); Actor = actor;
            FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
            security.SetOwner(new SecurityIdentifier(actor)); security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { actor, "S-1-5-18", "S-1-5-32-544" }.Distinct(StringComparer.Ordinal))
            {
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                    directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            }
            var bytes = security.GetSecurityDescriptorBinaryForm();
            try
            {
                _pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned); Attributes = Marshal.AllocHGlobal(Marshal.SizeOf<HistorySecurityAttributes>());
                Marshal.StructureToPtr(new HistorySecurityAttributes { Length = Marshal.SizeOf<HistorySecurityAttributes>(), Descriptor = _pinned.AddrOfPinnedObject() }, Attributes, false);
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { if (Attributes != IntPtr.Zero) { Marshal.FreeHGlobal(Attributes); Attributes = IntPtr.Zero; } if (_pinned.IsAllocated) { _pinned.Free(); } }
    }
    [StructLayout(LayoutKind.Sequential)] private struct HistorySecurityAttributes { public int Length; public IntPtr Descriptor; public int InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct HistoryRenameHeader { public uint Replace; public IntPtr Root; public uint Length; public char Name; }
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePrivateHistoryDirectory(string path, IntPtr security);
    [DllImport("ntdll.dll", EntryPoint = "NtSetInformationFile", ExactSpelling = true)]
    private static extern int SetHistoryFileInformation(SafeFileHandle file, out IoStatus status, IntPtr information, uint length, int informationClass);
}
