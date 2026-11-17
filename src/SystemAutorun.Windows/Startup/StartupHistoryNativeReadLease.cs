using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Windows.Startup;

internal static partial class StartupNativeSlotProbe
{
    // Pins current trusted namespaces and both present objects; an absent leaf cannot
    // be locked. Recheck absence at the end, never claim a native CAS/ABA fence.
    internal sealed class HistoryNativeReadLease : IDisposable
    {
        private readonly StartupHistoryBinding _binding;
        private readonly List<DirectoryPin> _directories = [];
        private DirectoryPin _source = null!;
        private DirectoryPin _archive = null!;
        private ShortcutReadLease? _original;
        private ShortcutReadLease? _backup;
        private string _origin = "";
        private string _backupPath = "";
        private bool _disposed;
        private HistoryNativeReadLease(StartupHistoryBinding binding) { _binding = binding; }

        internal static HistoryNativeReadLease Open(StartupHistoryBinding binding, string currentRoot, string currentSource, string currentArchiveBase,
            IReadOnlyList<string> startupDirectories, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); StartupHistoryFormat.ValidateBinding(binding); StartupHistoryProtection.RequireActor(binding.ActorSid);
            var root = StartupShortcutPathLease.LocalPath(currentRoot); var source = StartupShortcutPathLease.LocalPath(currentSource);
            var archiveBase = StartupShortcutPathLease.LocalPath(currentArchiveBase); var archive = Path.Combine(archiveBase, "startup");
            if (root != binding.Root.Path || source != binding.Source.Directory.Path || archiveBase != binding.ArchiveBase.Path || archive != binding.Archive.Path)
            { throw new InvalidOperationException("Current paths must match the protected context before IO."); }
            var location = StartupShortcutPathLease.Resolve(archiveBase, binding.RecordId, binding.Name, Path.Combine(source, binding.Name + ".lnk"),
                Path.Combine(archive, binding.RecordId + ".lnk"), source, startupDirectories);
            var lease = new HistoryNativeReadLease(binding) { _origin = location.Origin, _backupPath = location.BackupPath };
            try
            {
                lease.Pin(root, binding.Root); lease._source = lease.Pin(source, binding.Source.Directory);
                lease.Pin(archiveBase, binding.ArchiveBase); lease._archive = lease.Pin(archive, binding.Archive);
                lease._original = ReadSlot(lease._origin, lease._source, token);
                lease._backup = ReadSlot(lease._backupPath, lease._archive, token);
                lease.VerifyCurrent(token); return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        private DirectoryPin Pin(string path, StartupHistoryDirectory expected)
        { var pin = DirectoryPin.Open(path, expected); _directories.Add(pin); return pin; }
        private static ShortcutReadLease? ReadSlot(string path, DirectoryPin parent, CancellationToken token)
        {
            try { return ShortcutReadLease.OpenReadOnly(path, token); }
            catch (Win32Exception error) when (error.NativeErrorCode == 2)
            {
                if (parent.Expected.CaseSensitivity != "insensitive") { throw new InvalidOperationException("Missing slot case semantics are not established."); }
                return null;
            }
        }

        internal void VerifyCurrent(CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested(); StartupHistoryProtection.RequireActor(_binding.ActorSid);
            foreach (var directory in _directories) { token.ThrowIfCancellationRequested(); directory.VerifyCurrent(); }
            if (_original is not null) { _original.VerifyCurrent(token); } else { RequireStillMissing(_source, Path.GetFileName(_origin), token); }
            if (_backup is not null) { _backup.VerifyCurrent(token); } else { RequireStillMissing(_archive, Path.GetFileName(_backupPath), token); }
        }
        private static void RequireStillMissing(DirectoryPin parent, string leaf, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var current = OpenRelative(parent.Handle, leaf, out var error, caseSensitive: parent.Expected.CaseSensitivity == "sensitive");
            if (error == 0) { throw new InvalidOperationException("An absent slot reappeared."); }
            if (error != 2) { throw new Win32Exception(error); }
            if (parent.Expected.CaseSensitivity != "insensitive") { throw new InvalidOperationException("Missing slot case semantics are not established."); }
        }
        internal StartupHistoryNativeCheck Compare()
        {
            ObjectDisposedException.ThrowIf(_disposed, this); var when = DateTimeOffset.UtcNow;
            if (_original is not null && _backup is not null) { return new("unverified", "history_native_both_present", when); }
            if (_original is null && _backup is null) { return new("unverified", "history_native_both_missing", when); }
            var actual = (_original ?? _backup)!.Snapshot; var expected = _binding.Original;
            if (actual.FileIdentity != expected.Identity || actual.Length != expected.Length || actual.LinkCount != expected.LinkCount || actual.Sha256 != expected.Sha256)
            { return new("unverified", _original is not null ? "history_native_original_changed" : "history_native_archive_changed", when); }
            return _original is not null ? new("original_matches", "history_native_original_observed", when)
                : new("archive_matches", "history_native_archive_observed", when);
        }

        public void Dispose()
        {
            if (_disposed) { return; } _disposed = true;
            try { _original?.Dispose(); }
            finally
            {
                try { _backup?.Dispose(); }
                finally { for (var index = _directories.Count - 1; index >= 0; index--) { _directories[index].Dispose(); } }
            }
        }

        private sealed class DirectoryPin : IDisposable
        {
            private readonly string _path;
            private readonly SshInputFileLease _chain;
            internal SafeFileHandle Handle { get; }
            internal StartupHistoryDirectory Expected { get; }
            private DirectoryPin(string path, StartupHistoryDirectory expected, SshInputFileLease chain, SafeFileHandle handle)
            { _path = path; Expected = expected; _chain = chain; Handle = handle; }
            internal static DirectoryPin Open(string path, StartupHistoryDirectory expected)
            {
                if (path != expected.Path) { throw new InvalidOperationException("A current derived directory is required."); }
                var chain = SshInputFileLease.OpenDirectory(path); SafeFileHandle? handle = null;
                try
                {
                    handle = OpenParent(path); var pin = new DirectoryPin(path, expected, chain, handle); pin.VerifyCurrent(); return pin;
                }
                catch { handle?.Dispose(); chain.Dispose(); throw; }
            }
            internal void VerifyCurrent()
            {
                using var current = OpenParent(_path);
                if (Identity(Handle) != Expected.Identity || Identity(current) != Expected.Identity
                    || CaseSensitivity(Handle) != Expected.CaseSensitivity || CaseSensitivity(current) != Expected.CaseSensitivity)
                { throw new InvalidOperationException("A current directory identity or case context changed."); }
            }
            public void Dispose() { try { Handle.Dispose(); } finally { _chain.Dispose(); } }
        }
    }
}
