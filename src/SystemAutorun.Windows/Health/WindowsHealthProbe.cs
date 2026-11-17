using System.ComponentModel;
using System.Net.Sockets;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;
using SystemAutorun.Windows.Execution;

namespace SystemAutorun.Windows.Health;

public sealed class WindowsHealthProbe : IHealthProbe
{
    private readonly ProcessIdentity _processIdentity = new();
    private readonly WslHealthClient _wsl = new();
    private readonly WindowsServiceController _services = new();

    public Task<HealthResult> CheckPassiveAsync(AutorunTask task, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A prior `wsl --list --running` check cannot close the stop/--exec race.
        // Do not use --exec at all on passive read paths; explicit start verification may.
        return task.Health.Type is HealthCheckType.SystemdActive or HealthCheckType.WslProcess
            ? Task.FromResult(HealthResult.Unknown("passive-wsl-probe-unavailable",
                "只读监测不执行 WSL 内部命令，以免启动已停止的发行版；服务/进程健康来源不可用。"))
            : CheckAsync(task, quick: true, cancellationToken);
    }

    public async Task<HealthResult> CheckAsync(
        AutorunTask task,
        bool quick,
        CancellationToken cancellationToken = default) =>
        task.Health.Type switch
        {
            HealthCheckType.Process => CheckProcess(task),
            HealthCheckType.Tcp => await CheckTcpAsync(task, quick, cancellationToken).ConfigureAwait(false),
            HealthCheckType.WindowsService => CheckWindowsService(task),
            HealthCheckType.WslRunning => await _wsl.CheckRunningAsync(task, cancellationToken).ConfigureAwait(false),
            HealthCheckType.SystemdActive => await _wsl.CheckSystemdAsync(task, quick, cancellationToken).ConfigureAwait(false),
            HealthCheckType.WslProcess => await _wsl.CheckProcessAsync(task, cancellationToken).ConfigureAwait(false),
            _ => HealthResult.Fail("unsupported-health", $"Unsupported health type: {task.Health.Type}"),
        };

    private HealthResult CheckProcess(AutorunTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Health.Pattern))
        {
            return HealthResult.Unknown("missing-pattern", "Process health pattern is missing.");
        }

        var presence = _processIdentity.ReadPresence(task);
        return presence.ProcessIds.Count > 0
            ? HealthResult.Ok("process-running", $"PID {string.Join(", ", presence.ProcessIds)}")
            : presence.IdentityUnavailable
                ? HealthResult.Unknown("process-identity-unavailable", "A matching process identity cannot be read; absence is not confirmed.")
                : HealthResult.Fail("process-not-found", $"Process not found: {task.Health.Pattern}");
    }

    private HealthResult CheckWindowsService(AutorunTask task)
    {
        var serviceName = task.Health.Service ?? task.Service;
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return HealthResult.Fail("missing-windows-service", "Windows service name is missing.");
        }

        try
        {
            var status = _services.GetStatus(serviceName);
            if (!status.Exists)
            {
                return HealthResult.Fail(
                    "windows-service-not-found",
                    $"Windows service was not found: {serviceName}");
            }

            return status.Running
                ? HealthResult.Ok(
                    "windows-service-running",
                    status.ProcessId is int processId
                        ? $"{serviceName} is running (PID {processId})."
                        : $"{serviceName} is running.")
                : status.State == 1 // SERVICE_STOPPED; pending/paused does not prove an absent process.
                    ? HealthResult.Fail("windows-service-stopped", $"{serviceName} is {status.StateLabel}.")
                    : HealthResult.Unknown("windows-service-transition", $"{serviceName} is {status.StateLabel}; absence is not confirmed.");
        }
        catch (Win32Exception exception)
        {
            return HealthResult.Unknown("windows-service-error", exception.Message);
        }
    }

    private static async Task<HealthResult> CheckTcpAsync(
        AutorunTask task,
        bool quick,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(task.Health.Host) || task.Health.Port is null)
        {
            return HealthResult.Fail("missing-host-or-port", "TCP health host or port is missing.");
        }

        var timeout = TimeSpan.FromSeconds(quick ? 2 : task.Health.TimeoutSeconds);
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;
        do
        {
            try
            {
                using var client = new TcpClient();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(Math.Min(2, Math.Max(1, timeout.TotalSeconds))));
                await client.ConnectAsync(task.Health.Host, task.Health.Port.Value, attempt.Token)
                    .ConfigureAwait(false);
                return HealthResult.Ok(
                    "tcp-open",
                    $"{task.Health.Host}:{task.Health.Port.Value} is reachable.");
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                lastError = exception;
            }

            if (quick || DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return HealthResult.Fail(
            "tcp-closed",
            $"{task.Health.Host}:{task.Health.Port.Value} is unavailable: {lastError?.Message}");
    }
}
