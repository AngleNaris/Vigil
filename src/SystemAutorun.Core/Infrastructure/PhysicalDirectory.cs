using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SystemAutorun.Core.Infrastructure;

public static class PhysicalDirectory
{
    public static string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        // Open the directory itself, following reparse points, without locking its contents.
        using var handle = CreateFileW(fullPath, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException($"Unable to resolve configuration directory: {fullPath}",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        }

        if (length == 0 || length >= buffer.Capacity)
        {
            throw new IOException($"Unable to obtain final configuration directory: {fullPath}",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var resolved = buffer.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            resolved = @"\\" + resolved[8..];
        }
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)
            && resolved.Length > 5 && resolved[5] == ':')
        {
            resolved = resolved[4..];
        }

        return Path.TrimEndingDirectorySeparator(resolved);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}
