using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Windows.Startup;

internal static partial class StartupNativeSlotProbe
{
    // Held original object -> held same-volume archive parent, no shell/path move,
    // copy/delete fallback, replacement, rollback or historical authentication.
    internal sealed class ShortcutArchiveLease : IDisposable
    {
        private readonly StartupHistoryBinding _binding;
        private SshInputFileLease? _baseChain;
        private SshInputFileLease? _archiveChain;
        private SafeFileHandle? _base;
        private SafeFileHandle? _parent;
        private bool _disposed;
        internal bool Attempted { get; private set; }
        internal bool Moved { get; private set; }
        private string Leaf => _binding.RecordId + ".lnk";
        private ShortcutArchiveLease(StartupHistoryBinding binding) { _binding = binding; }

        internal static ShortcutArchiveLease Open(StartupHistoryBinding binding, CancellationToken token)
        {
            StartupHistoryFormat.ValidateBinding(binding); token.ThrowIfCancellationRequested();
            var lease = new ShortcutArchiveLease(binding);
            try
            {
                lease._baseChain = SshInputFileLease.OpenDirectory(binding.ArchiveBase.Path);
                lease._base = OpenParent(binding.ArchiveBase.Path);
                lease._archiveChain = SshInputFileLease.OpenDirectory(binding.Archive.Path);
                lease._parent = CreateFileW(binding.Archive.Path, 0x120083, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (lease._parent.IsInvalid) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                lease.VerifyCurrent(token); return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        internal void VerifyCurrent(CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
            StartupHistoryProtection.RequireActor(_binding.ActorSid);
            VerifyDirectory(_base!, _binding.ArchiveBase); VerifyDirectory(_parent!, _binding.Archive);
            if (Identity(_parent!).VolumeSerialNumber != _binding.Original.Identity.VolumeSerialNumber)
            { throw new InvalidOperationException("Archive must remain on the original volume."); }
        }

        private static void VerifyDirectory(SafeFileHandle held, StartupHistoryDirectory expected)
        {
            using var current = OpenParent(expected.Path);
            if (GetFileType(held) != 1 || !GetFileInformationByHandle(held, out var info) || (info.Attributes & 0x410) != 0x10
                || Identity(held) != expected.Identity || Identity(current) != expected.Identity
                || CaseSensitivity(held) != expected.CaseSensitivity || CaseSensitivity(current) != expected.CaseSensitivity)
            { throw new InvalidOperationException("Archive directory identity or semantics changed."); }
        }

        internal void RequireMissingDestination(CancellationToken token)
        {
            VerifyCurrent(token);
            using var target = OpenRelative(_parent!, Leaf, out var error, caseSensitive: _binding.Archive.CaseSensitivity == "sensitive", share: 7);
            if (error == 0) { throw new InvalidOperationException("Archive destination already exists."); }
            if (error != 2) { throw new Win32Exception(error); }
        }

        internal void Move(ShortcutReadLease input, CancellationToken token)
        {
            VerifyCurrent(token); input.VerifyCurrent(token);
            if (Attempted || input.Snapshot.FileIdentity != _binding.Original.Identity
                || input.Snapshot.DirectoryIdentity != _binding.Source.Directory.Identity || input.Snapshot.CaseSensitivity != _binding.Source.Directory.CaseSensitivity
                || input.Snapshot.Origin != Path.Combine(_binding.Source.Directory.Path, _binding.Name + ".lnk")
                || input.Snapshot.Length != _binding.Original.Length || input.Snapshot.LinkCount != _binding.Original.LinkCount
                || input.Snapshot.Sha256 != _binding.Original.Sha256)
            { throw new InvalidOperationException("Archive input is not the bound original."); }
            var name = Encoding.Unicode.GetBytes(Leaf); var offset = Marshal.OffsetOf<HistoryRenameHeader>(nameof(HistoryRenameHeader.Name)).ToInt32();
            var length = checked(offset + name.Length); var buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.Copy(new byte[length], 0, buffer, length); // ReplaceIfExists=false, legacy class 10 only.
                Marshal.WriteIntPtr(buffer, Marshal.OffsetOf<HistoryRenameHeader>(nameof(HistoryRenameHeader.Root)).ToInt32(), _parent!.DangerousGetHandle());
                Marshal.WriteInt32(buffer, Marshal.OffsetOf<HistoryRenameHeader>(nameof(HistoryRenameHeader.Length)).ToInt32(), name.Length);
                Marshal.Copy(name, 0, buffer + offset, name.Length); token.ThrowIfCancellationRequested();
                Attempted = true;
                var status = SetHistoryFileInformation(input.RenameHandle, out _, buffer, (uint)length, 10);
                if (status != 0) { throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status))); }
                Moved = true;
            }
            finally { Marshal.FreeHGlobal(buffer); GC.KeepAlive(_parent); }
        }

        internal void VerifyResult(ShortcutReadLease input, CancellationToken token)
        {
            VerifyCurrent(token); input.VerifyCurrent(token);
            if (!Moved) { throw new InvalidOperationException("No same-handle native result is available."); }
            // This observational lookup shares DELETE only to coexist with our own held
            // DELETE-capable original. The original still denies ALL write/delete opens.
            using var target = OpenRelative(_parent!, Leaf, out var targetError,
                caseSensitive: _binding.Archive.CaseSensitivity == "sensitive", share: 7);
            if (targetError != 0) { throw new Win32Exception(targetError); }
            if (GetFileType(target) != 1 || !GetFileInformationByHandle(target, out var info) || (info.Attributes & 0x410) != 0
                || info.Links != 1 || (((long)info.SizeHigh << 32) | info.SizeLow) != _binding.Original.Length
                || Identity(target) != input.Snapshot.FileIdentity)
            { throw new InvalidOperationException("Archive slot is not the held original object."); }
            using var origin = OpenRelative(input.SourceParent, Path.GetFileName(input.Snapshot.Origin), out var sourceError,
                caseSensitive: _binding.Source.Directory.CaseSensitivity == "sensitive", share: 7);
            if (sourceError == 0) { throw new InvalidOperationException("Original slot is no longer missing."); }
            if (sourceError != 2) { throw new Win32Exception(sourceError); }
            token.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            if (_disposed) { return; } _disposed = true;
            try { _parent?.Dispose(); }
            finally { try { _base?.Dispose(); } finally { _archiveChain?.Dispose(); _baseChain?.Dispose(); } }
        }
    }
}
