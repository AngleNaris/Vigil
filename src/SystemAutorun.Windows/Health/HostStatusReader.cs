using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;
using SystemAutorun.Windows.Native;

namespace SystemAutorun.Windows.Health;

public sealed class HostStatusReader : IDisposable
{
    private readonly SemaphoreSlim _sampleLock = new(1, 1);
    private readonly PdhDiskIoReader _diskIo = new();
    private readonly HostNetworkRateTracker _networkRates = new();
    private CpuSample? _previousCpuSample;

    public async Task<HostStatusSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        // Serialize the complete observation, not just CPU: overlapping RPCs must not
        // consume/reset one another's network or disk counter baselines.
        await _sampleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cpuUsage = await ReadCpuUsageAsync(cancellationToken).ConfigureAwait(false);
            var memory = ReadMemory();
            var (processCount, threadCount) = ReadProcessCounts();
            var drives = ReadDrives();
            IReadOnlyList<NetworkStatusSnapshot> networks;
            NetworkRateSample networkRate;
            try
            {
                networks = ReadNetworks();
                networkRate = _networkRates.Observe(DateTimeOffset.UtcNow, networks);
            }
            catch (NetworkInformationException)
            {
                networks = []; _networkRates.Reset();
                networkRate = new(null, null, "network-unavailable");
            }
            var disk = _diskIo.Read(DateTimeOffset.UtcNow);
            return new HostStatusSnapshot(
                Environment.MachineName, RuntimeInformation.OSDescription, DateTimeOffset.UtcNow,
                TimeSpan.FromMilliseconds(GetTickCount64()), cpuUsage,
                checked((long)memory.TotalPhysical), checked((long)memory.AvailablePhysical),
                drives, networks, Environment.ProcessorCount, processCount, threadCount,
                disk.ReadBytesPerSecond, disk.WriteBytesPerSecond,
                networkRate.ReceiveBytesPerSecond, networkRate.SendBytesPerSecond, disk.Error, networkRate.Error);
        }
        finally { _sampleLock.Release(); }
    }

    private async Task<double> ReadCpuUsageAsync(CancellationToken cancellationToken)
    {
        var current = ReadCpuSample();
        if (_previousCpuSample is null)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
            var next = ReadCpuSample(); _previousCpuSample = next;
            return CalculateCpuUsage(current, next);
        }
        var previous = _previousCpuSample.Value; _previousCpuSample = current;
        return CalculateCpuUsage(previous, current);
    }

    // Owners drain reads before disposing, as with the Agent sampler/server lifecycle.
    public void Dispose() { _diskIo.Dispose(); _sampleLock.Dispose(); }

    private static CpuSample ReadCpuSample()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throw new InvalidOperationException(
                $"GetSystemTimes failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return new CpuSample(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
    }

    private static double CalculateCpuUsage(CpuSample previous, CpuSample current)
    {
        var idle = current.Idle - previous.Idle;
        var kernel = current.Kernel - previous.Kernel;
        var user = current.User - previous.User;
        var total = kernel + user;
        return total == 0
            ? 0
            : Math.Clamp((total - idle) * 100d / total, 0, 100);
    }

    private static MemoryStatus ReadMemory()
    {
        var status = new MemoryStatus
        {
            Length = checked((uint)Marshal.SizeOf<MemoryStatus>()),
        };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new InvalidOperationException(
                $"GlobalMemoryStatusEx failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return status;
    }

    private static IReadOnlyList<DriveStatusSnapshot> ReadDrives()
    {
        var drives = new List<DriveStatusSnapshot>();
        foreach (var drive in DriveInfo.GetDrives()
                     .Where(item => item.DriveType == DriveType.Fixed && item.IsReady)
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            drives.Add(new DriveStatusSnapshot(
                drive.Name.TrimEnd(Path.DirectorySeparatorChar),
                string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : drive.VolumeLabel,
                drive.TotalSize,
                drive.AvailableFreeSpace));
        }

        return drives;
    }

    private static IReadOnlyList<NetworkStatusSnapshot> ReadNetworks()
    {
        var networks = new List<NetworkStatusSnapshot>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(item => item.OperationalStatus == OperationalStatus.Up
                         && item.NetworkInterfaceType is not NetworkInterfaceType.Loopback
                         && item.NetworkInterfaceType is not NetworkInterfaceType.Tunnel))
        {
            var address = network.GetIPProperties().UnicastAddresses
                .Select(item => item.Address)
                .FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork
                    && !item.ToString().StartsWith("169.254.", StringComparison.Ordinal));
            if (address is null)
            {
                continue;
            }

            var statistics = network.GetIPStatistics();
            networks.Add(new NetworkStatusSnapshot(
                network.Name,
                address.ToString(),
                statistics.BytesReceived,
                statistics.BytesSent,
                network.Id));
        }

        return networks
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static (int ProcessCount, int ThreadCount) ReadProcessCounts()
    {
        if (NativeMethods.TryReadProcessCounts(out var nativeProcesses, out var nativeThreads))
        {
            return (nativeProcesses, nativeThreads);
        }

        var processCount = 0;
        var threadCount = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                processCount++;
                try
                {
                    threadCount += process.Threads.Count;
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        return (processCount, threadCount);
    }

    private static ulong ToUInt64(FileTime value) =>
        ((ulong)value.HighDateTime << 32) | value.LowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    private readonly record struct CpuSample(ulong Idle, ulong Kernel, ulong User);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        internal readonly uint LowDateTime;
        internal readonly uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }
}
