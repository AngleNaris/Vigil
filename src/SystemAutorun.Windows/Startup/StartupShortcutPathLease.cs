using System.ComponentModel;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Windows.Startup;

// Constrains locations and pins directory namespaces, not file-byte authenticity or
// a native leaf CAS. The moving file itself is NOT opened with a no-delete lease.
internal sealed class StartupShortcutPathLease : IDisposable
{
    private readonly List<SshInputFileLease> _leases = [];
    private readonly Dictionary<string, Stack<string>> _missing = new(StringComparer.OrdinalIgnoreCase);
    internal string Origin { get; }
    internal string BackupPath { get; }
    private string OriginDirectory { get; }
    private string ArchiveDirectory { get; }

    private StartupShortcutPathLease(string origin, string backup)
    {
        Origin = origin; BackupPath = backup;
        OriginDirectory = Path.GetDirectoryName(origin)!;
        ArchiveDirectory = Path.GetDirectoryName(backup)!;
    }

    internal static StartupShortcutPathLease Open(
        string backupDirectory, string id, string name, string origin, string? backupPath,
        string sourceDirectory, IReadOnlyList<string> startupDirectories, bool restoring)
    {
        var location = Resolve(backupDirectory, id, name, origin, backupPath, sourceDirectory, startupDirectories);
        var lease = new StartupShortcutPathLease(location.Origin, location.BackupPath);
        try
        {
            lease.PinExistingBranch(lease.OriginDirectory);
            lease.PinExistingBranch(lease.ArchiveDirectory);
            lease.CheckState(restoring);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    // Pure layout validation, also used by read-only archive evidence. Never probes origin.
    internal static (string Origin, string BackupPath) Resolve(
        string backupDirectory, string id, string name, string origin, string? backupPath,
        string sourceDirectory, IReadOnlyList<string> startupDirectories)
    {
        // Complete lexical checks BEFORE probing any path supplied by backup metadata.
        if (!Guid.TryParseExact(id, "N", out var guid) || guid == Guid.Empty)
        { throw new InvalidOperationException("A shortcut backup requires its original N-format backup ID."); }
        var source = LocalPath(sourceDirectory);
        var original = LocalPath(origin);
        var archive = LocalPath(Path.Combine(LocalPath(backupDirectory), "startup"));
        var expectedBackup = Path.Combine(archive, guid.ToString("N") + ".lnk");
        if (!string.Equals(Path.GetDirectoryName(original), source, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(original), ".lnk", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileNameWithoutExtension(original), name, StringComparison.Ordinal))
        { throw new InvalidOperationException("Shortcut identity does not match the fixed Startup source."); }
        if (backupPath is null || !string.Equals(LocalPath(backupPath), expectedBackup, StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException("Shortcut backup does not match the current configured archive and backup ID; explicit migration is required."); }
        foreach (var directory in startupDirectories)
        {
            var fixedDirectory = LocalPath(directory);
            if (archive.Equals(fixedDirectory, StringComparison.OrdinalIgnoreCase)
                || archive.StartsWith(fixedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidOperationException("A startup backup archive cannot be inside either Startup folder."); }
        }
        // Derive the operated namespace from the fixed source, not the spelling of an
        // editable origin parent (which may alias differently in case-sensitive folders).
        var expectedOrigin = Path.Combine(source, Path.GetFileName(original));
        return (expectedOrigin, expectedBackup);
    }

    // No environment expansion, namespace aliases, streams, dot traversal, or Win32
    // trailing-name normalization. Config paths have already been resolved by the loader.
    internal static string LocalPath(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 32767 || value.Length < 3
            || !char.IsAsciiLetter(value[0]) || value[1] != ':' || value[2] is not ('\\' or '/')
            || value.Contains('%') || value.Any(char.IsControl))
        { throw new InvalidOperationException("Startup mutation paths must be ordinary local absolute drive paths without expansion tokens."); }
        var path = Path.TrimEndingDirectorySeparator(value);
        if (path.Length > 3)
        {
            foreach (var part in path[3..].Split(['\\', '/']))
            {
                if (part.Length is 0 or > 255 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                    || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ReservedName(part))
                { throw new InvalidOperationException("Startup mutation path contains an ambiguous or unsupported component."); }
            }
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool ReservedName(string part)
    {
        var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && "123456789¹²³".Contains(stem[3]));
    }

    private void PinExistingBranch(string directory)
    {
        if (_missing.ContainsKey(directory)) { return; }
        var pending = new Stack<string>();
        var existing = directory;
        while (true)
        {
            try { _leases.Add(SshInputFileLease.OpenDirectory(existing)); break; }
            catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: 2 or 3 })
            {
                pending.Push(existing);
                existing = Path.GetDirectoryName(existing)
                    ?? throw new IOException("The startup path has no accessible local ancestor.", error);
            }
        }
        _missing.Add(directory, pending);
    }

    internal void EnsureOriginDirectory() => EnsureDirectory(OriginDirectory);
    internal void EnsureArchiveDirectory() => EnsureDirectory(ArchiveDirectory);

    private void EnsureDirectory(string directory)
    {
        var pending = _missing[directory];
        while (pending.TryPeek(out var child))
        {
            // The parent is pinned. Create only ONE child, then pin/verify it without
            // following reparses before any deeper creation or native file move.
            Directory.CreateDirectory(child);
            _leases.Add(SshInputFileLease.OpenDirectory(child));
            pending.Pop();
        }
    }

    internal void CheckState(bool restoring)
    {
        var original = RegularFileExists(Origin);
        var backup = RegularFileExists(BackupPath);
        if (restoring)
        {
            if (original) { throw new IOException("The startup restore destination already exists."); }
            if (!backup) { throw new FileNotFoundException("The original shortcut backup is missing; metadata-only reconstruction is refused."); }
        }
        else
        {
            if (!original) { throw new FileNotFoundException("The selected startup shortcut no longer exists."); }
            if (backup) { throw new IOException("The shortcut backup destination already exists."); }
        }
    }

    private static bool RegularFileExists(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        { throw new InvalidOperationException("Startup mutation file leaves must be ordinary files, not directories or reparse points."); }
        return true;
    }

    public void Dispose()
    {
        for (var index = _leases.Count - 1; index >= 0; index--) { _leases[index].Dispose(); }
        _leases.Clear(); _missing.Clear();
    }
}
