using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Windows.Startup;

// Ephemeral CURRENT input only, not a historical credential or permission to mutate.
// Disable capture reserves READ+DELETE; metadata projection uses READ only. Both
// deny write/delete sharing. The separate archive session may use the held object; capture itself
// never moves, deletes, publishes evidence or executes. Disposal releases/zeros input.
internal sealed class StartupShortcutCapture : IDisposable
{
    private readonly IStartupNativeAccess _native;
    private readonly StartupNativeSlotProbe.ShortcutReadLease _input;
    internal StartupSourceScopeObservation Scope { get; }
    internal string Name { get; }
    internal StartupShortcutInputSnapshot Input => _input.Snapshot;
    internal StartupNativeSlotProbe.ShortcutReadLease HeldInput => _input;

    private StartupShortcutCapture(IStartupNativeAccess native, string name, StartupSourceScopeObservation scope,
        StartupNativeSlotProbe.ShortcutReadLease input)
    { _native = native; Name = name; Scope = scope; _input = input; }

    internal static StartupShortcutCapture Open(string source, string name, IStartupNativeAccess native,
        CancellationToken cancellationToken, Action? beforeSeal = null) => OpenCore(source, name, native, cancellationToken, beforeSeal, reserveDelete: true);

    internal static StartupShortcutCapture OpenReadOnly(string source, string name, IStartupNativeAccess native,
        CancellationToken cancellationToken) => OpenCore(source, name, native, cancellationToken, null, reserveDelete: false);

    private static StartupShortcutCapture OpenCore(string source, string name, IStartupNativeAccess native,
        CancellationToken cancellationToken, Action? beforeSeal, bool reserveDelete)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A basename, not a path. Double extensions remain valid literal basenames.
        if (string.IsNullOrWhiteSpace(name) || name.Length > 251 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        { throw new ArgumentException("A bounded literal shortcut basename is required.", nameof(name)); }
        var before = StartupSourceScopeProbe.Observe(source, native, cancellationToken);
        if (before.State != "observed") { throw new InvalidOperationException("Current Startup source scope is unavailable."); }
        string origin;
        try { origin = StartupShortcutPathLease.LocalPath(Path.Combine(before.DirectoryPath!, name + ".lnk")); }
        catch (InvalidOperationException error) { throw new ArgumentException("A safe literal shortcut basename is required.", nameof(name), error); }
        if (Path.GetDirectoryName(origin) != before.DirectoryPath || Path.GetFileNameWithoutExtension(origin) != name)
        { throw new ArgumentException("Shortcut capture must stay in its fixed original source.", nameof(name)); }
        var input = reserveDelete ? StartupNativeSlotProbe.ShortcutReadLease.Open(origin, cancellationToken)
            : StartupNativeSlotProbe.ShortcutReadLease.OpenReadOnly(origin, cancellationToken);
        try
        {
            beforeSeal?.Invoke();
            var capture = new StartupShortcutCapture(native, name, before, input);
            capture.VerifyCurrent(cancellationToken);
            return capture;
        }
        catch { input.Dispose(); throw; }
    }

    // The caller owns (and must protect/zero) this sensitive clone; it is never logged.
    internal byte[] CopyOriginalBytes() => _input.CopyBytes();

    internal void VerifyCurrent(CancellationToken cancellationToken = default)
    {
        _input.ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
        var now = StartupSourceScopeProbe.Observe(Scope.Source, _native, cancellationToken);
        if (now.State != "observed" || now.ObserverSid != Scope.ObserverSid || now.SourceUserSid != Scope.SourceUserSid
            || now.KnownFolder != Scope.KnownFolder || now.SourceKind != Scope.SourceKind || now.DirectoryPath != Scope.DirectoryPath
            || now.DirectoryIdentity != Scope.DirectoryIdentity || now.CaseSensitivity != Scope.CaseSensitivity
            || Input.DirectoryIdentity != Scope.DirectoryIdentity || Input.CaseSensitivity != Scope.CaseSensitivity)
        { throw new InvalidOperationException("Current Startup source scope changed during input capture."); }
        _input.VerifyCurrent(cancellationToken);
    }

    public void Dispose() => _input.Dispose();
}

internal sealed record StartupShortcutInputSnapshot(string Origin, StartupNativeFileIdentity DirectoryIdentity,
    string CaseSensitivity, StartupNativeFileIdentity FileIdentity, int Length, uint LinkCount, string Sha256, DateTimeOffset CapturedAt);

internal static partial class StartupNativeSlotProbe
{
    internal sealed class ShortcutReadLease : IDisposable
    {
        private SshInputFileLease? _chain;
        private SafeFileHandle? _parent;
        private SafeFileHandle? _leaf;
        private FileStream? _stream;
        private byte[] _bytes = [];
        private bool _disposed;
        private bool _renameAllowed;
        internal StartupShortcutInputSnapshot Snapshot { get; private set; } = null!;
        internal SafeFileHandle RenameHandle
        {
            get
            {
                ThrowIfDisposed();
                if (!_renameAllowed) { throw new InvalidOperationException("A read-only input has no rename authority."); }
                return _leaf!;
            }
        }
        internal SafeFileHandle SourceParent { get { ThrowIfDisposed(); return _parent!; } }

        internal static ShortcutReadLease Open(string origin, CancellationToken cancellationToken) => OpenCore(origin, cancellationToken, reserveDeleteAccess: true);
        // Only server-derived, layout-validated slots may use this entry. No DELETE access.
        internal static ShortcutReadLease OpenReadOnly(string derivedPath, CancellationToken cancellationToken) => OpenCore(derivedPath, cancellationToken, reserveDeleteAccess: false);
        private static ShortcutReadLease OpenCore(string origin, CancellationToken cancellationToken, bool reserveDeleteAccess)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = new ShortcutReadLease { _renameAllowed = reserveDeleteAccess };
            try
            {
                var directory = Path.GetDirectoryName(origin)!;
                lease._chain = SshInputFileLease.OpenDirectory(directory);
                lease._parent = OpenParent(directory);
                var parent = Identity(lease._parent); var casing = CaseSensitivity(lease._parent);
                if (casing == "unknown") { throw new InvalidOperationException("Shortcut directory case semantics are unavailable."); }
                lease._leaf = OpenRelative(lease._parent, Path.GetFileName(origin), out var error,
                    reserveDeleteAccess: reserveDeleteAccess, caseSensitive: casing == "sensitive");
                if (error != 0) { throw new Win32Exception(error); }
                var (identity, length, links) = lease.Metadata();
                if (identity.VolumeSerialNumber != parent.VolumeSerialNumber)
                { throw new InvalidOperationException("Shortcut file and parent must share a volume."); }
                // Disable managed read buffering so revalidation re-reads the held object.
                lease._stream = new FileStream(lease._leaf, FileAccess.Read, bufferSize: 1, isAsync: false);
                lease._bytes = lease.ReadBytes(length, cancellationToken);
                lease.Snapshot = new(origin, parent, casing, identity, length, links,
                    Convert.ToHexStringLower(SHA256.HashData(lease._bytes)), DateTimeOffset.UtcNow);
                lease.VerifyCurrent(cancellationToken);
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        private (StartupNativeFileIdentity Identity, int Length, uint Links) Metadata()
        {
            if (GetFileType(_leaf!) != 1) { throw new InvalidOperationException("A regular local shortcut file is required."); }
            if (!GetFileInformationByHandle(_leaf!, out var info)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            var length = ((long)info.SizeHigh << 32) | info.SizeLow;
            if ((info.Attributes & 0x410) != 0 || length is < 0 or > 65536 || info.Links != 1)
            { throw new InvalidOperationException("Shortcut capture requires a bounded ordinary file with one link."); }
            return (Identity(_leaf!), (int)length, info.Links);
        }

        private byte[] ReadBytes(int length, CancellationToken cancellationToken)
        {
            var bytes = new byte[length];
            try
            {
                _stream!.Position = 0; var offset = 0;
                while (offset < length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = _stream.Read(bytes, offset, Math.Min(4096, length - offset));
                    if (read == 0) { throw new IOException("Shortcut bytes changed during capture."); }
                    offset += read;
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (_stream.ReadByte() != -1) { throw new IOException("Shortcut length changed during capture."); }
                return bytes;
            }
            catch { CryptographicOperations.ZeroMemory(bytes); throw; }
        }

        internal void VerifyCurrent(CancellationToken cancellationToken)
        {
            ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
            using var currentParent = OpenParent(Path.GetDirectoryName(Snapshot.Origin)!);
            var metadata = Metadata();
            if (Identity(currentParent) != Snapshot.DirectoryIdentity || Identity(_parent!) != Snapshot.DirectoryIdentity
                || CaseSensitivity(currentParent) != Snapshot.CaseSensitivity || CaseSensitivity(_parent!) != Snapshot.CaseSensitivity
                || metadata.Identity != Snapshot.FileIdentity || metadata.Length != Snapshot.Length || metadata.Links != Snapshot.LinkCount)
            { throw new InvalidOperationException("Shortcut identity changed during capture."); }
            var current = ReadBytes(Snapshot.Length, cancellationToken);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(current, _bytes))
                { throw new InvalidOperationException("Shortcut bytes changed during capture."); }
            }
            finally { CryptographicOperations.ZeroMemory(current); }
        }

        internal byte[] CopyBytes() { ThrowIfDisposed(); return (byte[])_bytes.Clone(); }
        internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
        public void Dispose()
        {
            if (_disposed) { return; } _disposed = true;
            try { _stream?.Dispose(); }
            finally
            {
                _leaf?.Dispose(); _parent?.Dispose(); _chain?.Dispose(); CryptographicOperations.ZeroMemory(_bytes);
            }
        }
    }
}
