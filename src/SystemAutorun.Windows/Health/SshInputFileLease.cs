using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Windows.Health;

// Read-only handles, held for the complete SSH lifetime; no contents are read here. Never follows reparse
// points, accesses SMB/devices, reads key contents, or permits file replacement/writes.
internal sealed class SshInputFileLease : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];

    internal static SshInputFileLease Open(string value, long maximumBytes)
    {
        var path = RemoteHostDefinitionValidator.ResolveLocalFilePath(value);
        if (GetDriveTypeW(Path.GetPathRoot(path)!) is not (2 or 3 or 6))
        { throw new IOException("SSH input files require a local disk."); }
        var lease = new SshInputFileLease();
        try
        {
            var chain = new Stack<string>();
            for (var current = Path.GetDirectoryName(path); current is not null; current = Path.GetDirectoryName(current)) { chain.Push(current); }
            foreach (var parent in chain) { lease.Pin(parent, directory: true); }
            var leaf = lease.Pin(path, directory: false);
            if (!GetFileSizeEx(leaf, out var length) || length < 0 || length > maximumBytes)
            { throw new IOException("Configured SSH input file exceeds its bounded size or is unreadable."); }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    // The same tested local-only pin is also used by read-only startup metadata enumeration.
    internal static SshInputFileLease OpenDirectory(string value)
    {
        var path = RemoteHostDefinitionValidator.ResolveLocalFilePath(value);
        if (GetDriveTypeW(Path.GetPathRoot(path)!) is not (2 or 3 or 6))
        { throw new IOException("A local disk directory is required."); }
        var lease = new SshInputFileLease();
        try
        {
            var chain = new Stack<string>();
            for (var current = path; current is not null; current = Path.GetDirectoryName(current)) { chain.Push(current); }
            foreach (var directory in chain) { lease.Pin(directory, directory: true); }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static SshInputFileLease? OpenOptional(string value, long maximumBytes)
    {
        try { return Open(value, maximumBytes); }
        catch (IOException exception) when (exception.InnerException is Win32Exception { NativeErrorCode: 2 or 3 }) { return null; }
    }

    private SafeFileHandle Pin(string path, bool directory)
    {
        // FILE_READ_ATTRIBUTES alone does not participate in Windows share arbitration.
        // Request actual read access (but do not read) to enforce no-write/no-delete sharing.
        var handle = CreateFileW(path, directory ? 0x81u : 0x80000000u, directory ? 3u : 1u, IntPtr.Zero, 3, directory ? 0x02200000u : 0x00200000u, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new IOException("Configured SSH input file or parent is missing, occupied or inaccessible.", new Win32Exception(error));
        }
        _handles.Add(handle);
        if (GetFileType(handle) != 1 || !GetFileInformationByHandleEx(handle, 9, out AttributeTagInfo info, 8)
            || (info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory)
        { throw new IOException("Reparse points and non-regular SSH input files are rejected."); }
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity || !buffer.ToString().StartsWith(@"\\?\", StringComparison.Ordinal)
            || buffer.ToString().StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            || !Path.TrimEndingDirectorySeparator(buffer.ToString()[4..]).Equals(Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))
        { throw new IOException("Configured SSH file identity or final path changed."); }
        return handle;
    }

    public void Dispose() { for (var index = _handles.Count - 1; index >= 0; index--) { _handles[index].Dispose(); } _handles.Clear(); }

    [StructLayout(LayoutKind.Sequential)] private struct AttributeTagInfo { public uint Attributes; public uint Tag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle file);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out AttributeTagInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSizeEx(SafeFileHandle file, out long size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}
