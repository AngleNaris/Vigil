using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Windows.Execution;

public sealed class WindowsServiceController
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x00000001;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceContinuePending = 0x00000005;
    private const uint ServicePausePending = 0x00000006;
    private const uint ServicePaused = 0x00000007;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;

    public WindowsServiceStatus GetStatus(string serviceName)
    {
        serviceName = Required(serviceName);
        using var manager = OpenManager();
        using var service = OpenServiceHandle(manager, serviceName, ServiceQueryStatus);
        if (service is null)
        {
            return WindowsServiceStatus.Missing(serviceName);
        }

        return QueryStatus(serviceName, service);
    }

    public async Task<CommandResult> StartAsync(
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        serviceName = Required(serviceName);
        try
        {
            using var manager = OpenManager();
            using var service = OpenServiceHandle(
                manager,
                serviceName,
                ServiceQueryStatus | ServiceStart);
            if (service is null)
            {
                return new CommandResult(false, $"Windows service was not found: {serviceName}");
            }

            var status = QueryStatus(serviceName, service);
            if (status.Running)
            {
                return new CommandResult(
                    true,
                    $"Windows service is already running: {serviceName}",
                    status.ProcessId);
            }

            if (!StartService(service, 0, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceAlreadyRunning)
                {
                    return new CommandResult(false, new Win32Exception(error).Message);
                }
            }

            return await WaitForStateAsync(
                serviceName,
                service,
                ServiceRunning,
                TimeSpan.FromSeconds(60),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception exception)
        {
            return new CommandResult(false, exception.Message);
        }
    }

    public async Task<CommandResult> StopAsync(
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        serviceName = Required(serviceName);
        try
        {
            using var manager = OpenManager();
            using var service = OpenServiceHandle(
                manager,
                serviceName,
                ServiceQueryStatus | ServiceStop);
            if (service is null)
            {
                return new CommandResult(false, $"Windows service was not found: {serviceName}");
            }

            var status = QueryStatus(serviceName, service);
            if (status.State == ServiceStopped)
            {
                return new CommandResult(true, $"Windows service is already stopped: {serviceName}");
            }

            if (!ControlService(service, ServiceControlStop, out _))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceNotActive)
                {
                    return new CommandResult(false, new Win32Exception(error).Message);
                }
            }

            return await WaitForStateAsync(
                serviceName,
                service,
                ServiceStopped,
                TimeSpan.FromSeconds(60),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception exception)
        {
            return new CommandResult(false, exception.Message);
        }
    }

    internal static string StateLabel(uint state) => state switch
    {
        ServiceStopped => "stopped",
        ServiceStartPending => "start pending",
        ServiceStopPending => "stop pending",
        ServiceRunning => "running",
        ServiceContinuePending => "continue pending",
        ServicePausePending => "pause pending",
        ServicePaused => "paused",
        _ => $"unknown ({state})",
    };

    private static async Task<CommandResult> WaitForStateAsync(
        string serviceName,
        SafeServiceHandle service,
        uint expectedState,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        WindowsServiceStatus status;
        do
        {
            status = QueryStatus(serviceName, service);
            if (status.State == expectedState)
            {
                return new CommandResult(
                    true,
                    $"Windows service is {StateLabel(expectedState)}: {serviceName}",
                    status.ProcessId);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return new CommandResult(
            false,
            $"Timed out waiting for Windows service '{serviceName}' to become "
            + $"{StateLabel(expectedState)}; current state is {status.StateLabel}.");
    }

    private static SafeServiceHandle OpenManager()
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (!manager.IsInvalid)
        {
            return manager;
        }

        var error = Marshal.GetLastWin32Error();
        manager.Dispose();
        throw new Win32Exception(error);
    }

    private static SafeServiceHandle? OpenServiceHandle(
        SafeServiceHandle manager,
        string serviceName,
        uint access)
    {
        var service = OpenService(manager, serviceName, access);
        if (!service.IsInvalid)
        {
            return service;
        }

        var error = Marshal.GetLastWin32Error();
        service.Dispose();
        return error == ErrorServiceDoesNotExist
            ? null
            : throw new Win32Exception(error);
    }

    private static WindowsServiceStatus QueryStatus(
        string serviceName,
        SafeServiceHandle service)
    {
        var buffer = new byte[Marshal.SizeOf<ServiceStatusProcess>()];
        if (!QueryServiceStatusEx(
                service,
                ScStatusProcessInfo,
                buffer,
                buffer.Length,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var native = MemoryMarshal.Read<ServiceStatusProcess>(buffer);
        return new WindowsServiceStatus(
            serviceName,
            true,
            native.CurrentState == ServiceRunning,
            native.CurrentState,
            native.ProcessId == 0 ? null : checked((int)native.ProcessId),
            StateLabel(native.CurrentState));
    }

    private static string Required(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Windows service name is required.", nameof(value))
            : value.Trim();

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeServiceHandle OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeServiceHandle OpenService(
        SafeServiceHandle serviceManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(
        SafeServiceHandle service,
        int argumentCount,
        IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(
        SafeServiceHandle service,
        uint control,
        out ServiceStatus serviceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        SafeServiceHandle service,
        int infoLevel,
        [Out] byte[] buffer,
        int bufferSize,
        out int bytesNeeded);

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ServiceStatusProcess
    {
        internal readonly uint ServiceType;
        internal readonly uint CurrentState;
        internal readonly uint ControlsAccepted;
        internal readonly uint Win32ExitCode;
        internal readonly uint ServiceSpecificExitCode;
        internal readonly uint CheckPoint;
        internal readonly uint WaitHint;
        internal readonly uint ProcessId;
        internal readonly uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ServiceStatus
    {
        internal readonly uint ServiceType;
        internal readonly uint CurrentState;
        internal readonly uint ControlsAccepted;
        internal readonly uint Win32ExitCode;
        internal readonly uint ServiceSpecificExitCode;
        internal readonly uint CheckPoint;
        internal readonly uint WaitHint;
    }
}

public sealed record WindowsServiceStatus(
    string ServiceName,
    bool Exists,
    bool Running,
    uint State,
    int? ProcessId,
    string StateLabel)
{
    internal static WindowsServiceStatus Missing(string serviceName) =>
        new(serviceName, false, false, 0, null, "not found");
}
