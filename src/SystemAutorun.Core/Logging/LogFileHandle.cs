using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SystemAutorun.Core.Logging;

// No directory enumeration, file creation, backup privilege, remote filesystem or path-based reads.
// Ancestors remain pinned against rename until the leaf's handle identity/path has been verified.
internal sealed class LogFileHandle : IDisposable
{
    private readonly FileStream _stream;
    public string Identity { get; }
    public long Length => _stream.Length;

    private LogFileHandle(FileStream stream, string identity) { _stream = stream; Identity = identity; }

    public static LogFileHandle Open(string path)
    {
        if (GetDriveTypeW(Path.GetPathRoot(path)!) is not (3 or 6))
        { throw new LogReadException("local_disk_required"); }
        var parents = new List<SafeFileHandle>();
        SafeFileHandle? leaf = null;
        try
        {
            var chain = new Stack<string>();
            for (var current = Path.GetDirectoryName(path); current is not null; current = Path.GetDirectoryName(current))
            { chain.Push(current); }
            foreach (var parent in chain)
            {
                var handle = OpenHandle(parent, 0x81, 3, 0x02200000); // LIST_DIRECTORY + attributes; real no-DELETE share arbitration
                parents.Add(handle);
                ValidateHandle(handle, parent, directory: true);
            }
            leaf = OpenHandle(path, 0x80000000, 7, 0x48200000); // READ, shared delete, OVERLAPPED/SEQUENTIAL/OPEN_REPARSE_POINT
            ValidateHandle(leaf, path, directory: false);
            if (!GetFileInformationByHandleEx(leaf, 18, out FileIdInfo id, (uint)Marshal.SizeOf<FileIdInfo>()))
            { throw Failure(); }
            var stream = new FileStream(leaf, FileAccess.Read, 4096, isAsync: true);
            leaf = null; // stream owns the handle
            return new LogFileHandle(stream, $"{id.VolumeSerialNumber:x16}{id.FileId:N}");
        }
        finally
        {
            leaf?.Dispose();
            foreach (var parent in parents) { parent.Dispose(); }
        }
    }

    public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken)
    {
        _stream.Position = offset;
        var bytes = new byte[count];
        var total = 0;
        while (total < count)
        {
            var read = await _stream.ReadAsync(bytes.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) { break; }
            total += read;
        }
        return total == count ? bytes : bytes[..total];
    }

    public void Dispose() => _stream.Dispose();

    private static SafeFileHandle OpenHandle(string path, uint access, uint sharing, uint flags)
    {
        var handle = CreateFileW(path, access, sharing, IntPtr.Zero, 3, flags, IntPtr.Zero);
        if (!handle.IsInvalid) { return handle; }
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw Failure(error);
    }

    private static void ValidateHandle(SafeFileHandle handle, string expected, bool directory)
    {
        if (GetFileType(handle) != 1 || !GetFileInformationByHandleEx(handle, 9, out AttributeTagInfo info, 8))
        { throw new LogReadException("not_regular_disk_file"); }
        if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory)
        { throw new LogReadException("reparse_or_nonregular_source"); }
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) { throw new LogReadException("identity_unavailable"); }
        var actual = buffer.ToString();
        if (!actual.StartsWith(@"\\?\", StringComparison.Ordinal) || actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            || !Path.TrimEndingDirectorySeparator(actual[4..]).Equals(Path.TrimEndingDirectorySeparator(expected), StringComparison.OrdinalIgnoreCase))
        { throw new LogReadException("identity_changed"); }
    }

    private static Exception Failure(int? error = null)
    {
        var code = error ?? Marshal.GetLastWin32Error();
        return code switch
        {
            2 or 3 => new LogReadException("source_missing"),
            5 => new UnauthorizedAccessException("Log source is not readable by the current identity."),
            _ => new IOException("Unable to open the configured log source.", new Win32Exception(code)),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo { public ulong VolumeSerialNumber; public Guid FileId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { public uint Attributes; public uint Tag; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileIdInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out AttributeTagInfo info, uint size);
}

internal sealed class LogReadException(string reason) : IOException(reason)
{
    public string Reason { get; } = reason;
}
