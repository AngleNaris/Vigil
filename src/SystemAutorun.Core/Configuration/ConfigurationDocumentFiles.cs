using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SystemAutorun.Core.Infrastructure;

namespace SystemAutorun.Core.Configuration;

// New complete-document exchanges pin local, non-reparse directory chains. No candidate paths.
internal sealed class ConfigurationDocumentFiles : IDisposable
{
    private readonly List<SafeFileHandle> _directories = [];
    private readonly ProjectPaths _paths;

    internal ConfigurationDocumentFiles(ProjectPaths paths)
    {
        _paths = paths;
        if (GetDriveTypeW(Path.GetPathRoot(paths.RootDirectory)!) is not (3 or 6))
        { throw new ConfigurationException("Configuration document exchange requires a local disk root."); }
        try
        {
            var chain = new Stack<string>();
            for (var path = paths.RootDirectory; path is not null; path = Path.GetDirectoryName(path)) { chain.Push(path); }
            foreach (var path in chain) { Pin(path); }
        }
        catch { Dispose(); throw; }
    }

    internal string PinBackupDirectory()
    {
        var path = _paths.RootDirectory;
        foreach (var component in new[] { "backups", "config" })
        {
            path = Path.Combine(path, component);
            // Its parent is already pinned; never create a descendant through an unchecked child.
            if (!Directory.Exists(path)) { Directory.CreateDirectory(path); }
            Pin(path);
        }
        return path;
    }

    internal async Task<byte[]> ReadAsync(CancellationToken token)
    {
        using var handle = Open(_paths.ConfigPath, 0x80000000, 7, 0x48200000);
        Validate(handle, _paths.ConfigPath, directory: false);
        await using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: true);
        if (stream.Length > ConfigurationDocumentRequest.MaximumBytes)
        { throw new ArgumentException("Current YAML exceeds the 40 KiB document exchange limit."); }
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) { return bytes.ToArray(); }
            if (bytes.Length + read > ConfigurationDocumentRequest.MaximumBytes)
            { throw new ArgumentException("Current YAML exceeds the 40 KiB document exchange limit."); }
            bytes.Write(buffer, 0, read);
        }
    }

    private void Pin(string path)
    {
        var handle = Open(path, 0x81, 3, 0x02200000); // LIST_DIRECTORY + attributes; attribute-only access does not enforce sharing
        _directories.Add(handle);
        Validate(handle, path, directory: true);
    }

    private static SafeFileHandle Open(string path, uint access, uint share, uint flags)
    {
        var handle = CreateFileW(path, access, share, IntPtr.Zero, 3, flags, IntPtr.Zero);
        if (!handle.IsInvalid) { return handle; }
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        if (error == 5) { throw new UnauthorizedAccessException("Configuration path is not accessible to the current identity."); }
        throw new IOException("Unable to open configuration document path.", new Win32Exception(error));
    }

    private static void Validate(SafeFileHandle handle, string expected, bool directory)
    {
        if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var info))
        { throw new IOException("Configuration document identity is unavailable."); }
        if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory
            || (!directory && info.Links != 1))
        { throw new ConfigurationException("Configuration exchange rejects reparse points, nonregular files and hard-linked documents."); }
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        var actual = buffer.ToString();
        if (length == 0 || length >= buffer.Capacity || !actual.StartsWith(@"\\?\", StringComparison.Ordinal)
            || actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            || !Path.TrimEndingDirectorySeparator(actual[4..]).Equals(Path.TrimEndingDirectorySeparator(expected), StringComparison.OrdinalIgnoreCase))
        { throw new ConfigurationException("Configuration document path identity changed."); }
    }

    public void Dispose() { foreach (var handle in _directories.AsEnumerable().Reverse()) { handle.Dispose(); } }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out NativeFileInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}
