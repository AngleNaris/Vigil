using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SystemAutorun.Windows.Native;

internal static class NativeMethods
{
    internal const uint CreateNoWindow = 0x08000000;
    internal const uint CreateUnicodeEnvironment = 0x00000400;
    internal const uint StartfUseShowWindow = 0x00000001;
    internal const short SwHide = 0;
    internal const short SwShowMinNoActive = 7;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    private const int SystemProcessInformationClass = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int ErrorInsufficientBuffer = 122;

    internal readonly record struct NativeProcessMetric(
        int ProcessId,
        string ProcessName,
        long CreateFileTimeUtc,
        long TotalProcessorTimeTicks,
        long WorkingSetBytes,
        long PrivateBytes,
        int ThreadCount);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        [Out] char[] executableName,
        ref uint size);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        uint systemInformationLength,
        out uint returnLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    internal static string? TryGetProcessImagePath(int processId)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
        {
            return null;
        }

        var buffer = ArrayPool<char>.Shared.Rent(1024);
        try
        {
            var capacity = (uint)buffer.Length;
            if (QueryFullProcessImageName(process, 0, buffer, ref capacity))
            {
                return new string(buffer, 0, (int)capacity);
            }

            if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
            {
                return null;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }

        var largeBuffer = ArrayPool<char>.Shared.Rent(32_768);
        try
        {
            var capacity = (uint)largeBuffer.Length;
            return QueryFullProcessImageName(process, 0, largeBuffer, ref capacity)
                ? new string(largeBuffer, 0, (int)capacity)
                : null;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(largeBuffer);
        }
    }

    internal static bool TryReadProcessCounts(out int processCount, out int threadCount)
    {
        processCount = 0;
        threadCount = 0;
        if (!TryQuerySystemProcesses(out var buffer, out var validBytes))
        {
            return false;
        }

        try
        {
            var span = buffer.AsSpan(0, validBytes);
            var offset = 0;
            while (offset + 8 <= span.Length)
            {
                var nextOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
                var threads = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 4, 4));
                processCount++;
                threadCount += checked((int)threads);
                if (nextOffset == 0)
                {
                    break;
                }
                if (nextOffset < 8 || offset + (long)nextOffset > span.Length)
                {
                    return false;
                }
                offset += (int)nextOffset;
            }

            return processCount > 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static Dictionary<int, NativeProcessMetric>? TryReadProcessMetrics()
    {
        if (IntPtr.Size != 8 || !TryQuerySystemProcesses(out var buffer, out var validBytes))
        {
            return null;
        }

        try
        {
            var result = new Dictionary<int, NativeProcessMetric>(256);
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var baseAddress = handle.AddrOfPinnedObject().ToInt64();
                var span = buffer.AsSpan(0, validBytes);
                var offset = 0;
                // SYSTEM_PROCESS_INFORMATION x64 offsets (Win10+):
                // 0: NextEntryOffset (u32), 4: NumberOfThreads (u32), 32: CreateTime (i64),
                // 40: UserTime (i64), 48: KernelTime (i64), 56: ImageName.Length (u16),
                // 64: ImageName.Buffer (ptr), 80: UniqueProcessId (ptr),
                // 144: WorkingSetSize (usize), 184: PagefileUsage (usize)
                while (offset + 192 <= span.Length)
                {
                    var entry = span.Slice(offset);
                    var nextOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(0, 4));
                    var threads = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4, 4));
                    var createTime = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(32, 8));
                    var userTime = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(40, 8));
                    var kernelTime = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(48, 8));
                    var imageLength = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(56, 2));
                    var imagePtr = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(64, 8));
                    var pid64 = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(80, 8));
                    var workingSet = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(144, 8));
                    var privateBytes = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(184, 8));

                    if (pid64 >= 0 && pid64 <= int.MaxValue)
                    {
                        var processName = "Idle";
                        if (imageLength > 0 && (imageLength & 1) == 0 && imagePtr >= baseAddress)
                        {
                            var rel = imagePtr - baseAddress;
                            if (rel >= 0 && rel + imageLength <= validBytes)
                            {
                                var chars = MemoryMarshal.Cast<byte, char>(span.Slice((int)rel, imageLength));
                                var fileName = Path.GetFileName(chars);
                                if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    fileName = fileName[..^4];
                                }
                                if (fileName.Length > 0)
                                {
                                    processName = fileName.ToString();
                                }
                            }
                        }

                        var pid = (int)pid64;
                        result[pid] = new NativeProcessMetric(
                            pid,
                            processName,
                            createTime,
                            userTime + kernelTime,
                            workingSet,
                            privateBytes,
                            checked((int)threads));
                    }

                    if (nextOffset == 0)
                    {
                        break;
                    }
                    if (nextOffset < 192 || offset + (long)nextOffset > span.Length)
                    {
                        break;
                    }
                    offset += (int)nextOffset;
                }
            }
            finally
            {
                handle.Free();
            }

            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryQuerySystemProcesses(out byte[] buffer, out int validBytes)
    {
        var size = 256 * 1024;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = ArrayPool<byte>.Shared.Rent(size);
            var handle = GCHandle.Alloc(candidate, GCHandleType.Pinned);
            int status;
            uint returnLength;
            try
            {
                status = NtQuerySystemInformation(
                    SystemProcessInformationClass,
                    handle.AddrOfPinnedObject(),
                    (uint)candidate.Length,
                    out returnLength);
            }
            finally
            {
                handle.Free();
            }

            if (status >= 0)
            {
                buffer = candidate;
                validBytes = returnLength > 0 && returnLength <= candidate.Length
                    ? (int)returnLength
                    : candidate.Length;
                return true;
            }

            ArrayPool<byte>.Shared.Return(candidate);
            if (status != StatusInfoLengthMismatch)
            {
                break;
            }

            var needed = returnLength > 0 && returnLength < 32 * 1024 * 1024
                ? (int)returnLength + 32 * 1024
                : size * 2;
            size = Math.Max(size * 2, needed);
            if (size > 32 * 1024 * 1024)
            {
                break;
            }
        }

        buffer = [];
        validBytes = 0;
        return false;
    }

    internal static Win32Exception LastError(string operation) =>
        new(Marshal.GetLastPInvokeError(), operation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        internal int Size;
        internal string? Reserved;
        internal string? Desktop;
        internal string? Title;
        internal int X;
        internal int Y;
        internal int XSize;
        internal int YSize;
        internal int XCountChars;
        internal int YCountChars;
        internal int FillAttribute;
        internal uint Flags;
        internal short ShowWindow;
        internal short Reserved2Size;
        internal IntPtr Reserved2;
        internal IntPtr StandardInput;
        internal IntPtr StandardOutput;
        internal IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal int ProcessId;
        internal int ThreadId;
    }
}
