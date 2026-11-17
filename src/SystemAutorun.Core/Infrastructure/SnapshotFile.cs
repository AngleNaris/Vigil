using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SystemAutorun.Core.Infrastructure;

// Single-file snapshots only; root leases/CAS, durable writes and cancellation belong to
// callers. Never use this for lock files or the pinned complete-document/backup workflow.
public static class SnapshotFile
{
    public static FileStream OpenRead(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);

    public static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    // Publish an already-written, closed, private sibling. NTFS directory-entry replacement
    // keeps old DELETE-shared readers valid without allowing in-place writes. This is not
    // an additional crash-durability guarantee, nor protection from hostile parent swaps.
    public static void Publish(string temporaryPath, string destinationPath)
    {
        var source = Path.GetFullPath(temporaryPath);
        var destination = Path.GetFullPath(destinationPath);
        // Exact lexical siblings are a private-caller requirement, not an alias/identity
        // security check. Existing root protections remain the caller's responsibility.
        if (!string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(destination), StringComparison.Ordinal)
            || string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        { throw new ArgumentException("Snapshot publication requires distinct exact-directory siblings."); }

        using (var file = CreateFileW(ExtendedPath(source), 0x10080, 5, IntPtr.Zero, 3, 0x200000, IntPtr.Zero))
        {
            // DELETE + READ_ATTRIBUTES; READ|DELETE sharing deliberately excludes WRITE.
            if (file.IsInvalid) { ThrowIoError(Marshal.GetLastWin32Error(), "open snapshot candidate"); }
            if (!GetFileInformationByHandleEx(file, 9, out var info, 8))
            { ThrowIoError(Marshal.GetLastWin32Error(), "inspect snapshot candidate"); }
            if ((info.Attributes & 0x410) != 0)
            { throw new IOException("Snapshot candidates must be regular, non-reparse files."); }

            if (IsLocalNtfs(file))
            {
                var name = Encoding.Unicode.GetBytes(ExtendedPath(destination));
                if (name.Length > 65534) { throw new PathTooLongException(); }
                var lengthOffset = IntPtr.Size == 8 ? 16 : 8;
                var nameOffset = lengthOffset + 4;
                var buffer = new byte[nameOffset + name.Length];
                BitConverter.GetBytes(3u).CopyTo(buffer, 0); // REPLACE_IF_EXISTS | POSIX_SEMANTICS
                BitConverter.GetBytes((uint)name.Length).CopyTo(buffer, lengthOffset);
                name.CopyTo(buffer, nameOffset); // FileNameLength is bytes; no NUL is required.
                if (!SetFileInformationByHandle(file, 22, buffer, (uint)buffer.Length))
                { ThrowIoError(Marshal.GetLastWin32Error(), "publish NTFS snapshot"); }
                return;
            }
        }
        // Compatibility outside proven local NTFS: retain the prior publication behavior.
        // No fallback on a failed native rename (including 5/32/24/50/87), and no retries.
        File.Move(source, destination, overwrite: true);
    }

    private static bool IsLocalNtfs(SafeFileHandle file)
    {
        var format = new StringBuilder(32);
        if (!GetVolumeInformationByHandleW(file, IntPtr.Zero, 0, out _, out _, out _, format, (uint)format.Capacity)
            || !format.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        { return false; }
        var path = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(file, path, (uint)path.Capacity, 0);
        if (length >= path.Capacity && length < 32768)
        {
            path = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandleW(file, path, (uint)path.Capacity, 0);
        }
        var value = path.ToString();
        if (length == 0 || length >= path.Capacity || !value.StartsWith(@"\\?\", StringComparison.Ordinal)
            || value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) { return false; }
        var volumeRoot = Path.GetPathRoot(value);
        return volumeRoot is not null && GetDriveTypeW(volumeRoot) is 2 or 3 or 6;
    }

    private static string ExtendedPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    private static void ThrowIoError(int error, string stage)
    {
        var cause = new Win32Exception(error);
        if (error == 5) { throw new UnauthorizedAccessException($"Unable to {stage}.", cause); }
        throw new IOException($"Unable to {stage}: {cause.Message}", unchecked((int)(0x80070000u | (uint)error)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { public uint Attributes, ReparseTag; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out AttributeTagInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, byte[] data, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle file, IntPtr name, uint nameSize, out uint serial, out uint componentLength, out uint flags, StringBuilder format, uint formatSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}
