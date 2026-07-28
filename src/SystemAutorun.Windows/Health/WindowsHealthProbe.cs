using System.Net.Sockets;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;
using SystemAutorun.Windows.Execution;

namespace SystemAutorun.Windows.Health;

public sealed class WindowsHealthProbe : IHealthProbe
{
    private readonly ProcessIdentity _processIdentity = new();
    private readonly WslHealthClient _wsl = new();

    public async Task<HealthResult> CheckAsync(
        AutorunTask task,
        bool quick,
        CancellationToken cancellationToken = default) =>
        task.Health.Type switch
        {
            HealthCheckType.Process => CheckProcess(task),
            HealthCheckType.Tcp => await CheckTcpAsync(task, quick, cancellationToken).ConfigureAwait(false),
            HealthCheckType.WslRunning => await _wsl.CheckRunningAsync(task, cancellationToken).ConfigureAwait(false),
            HealthCheckType.SystemdActive => await _wsl.CheckSystemdAsync(task, quick, cancellationToken).ConfigureAwait(false),
            HealthCheckType.WslProcess => await _wsl.CheckProcessAsync(task, cancellationToken).ConfigureAwait(false),
            _ => HealthResult.Fail("unsupported-health", $"Unsupported health type: {task.Health.Type}"),
        };

    private HealthResult CheckProcess(AutorunTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Health.Pattern))
        {
            return HealthResult.Fail("missing-pattern", "Process health pattern is missing.");
        }

        var processIds = _processIdentity.FindMatchingProcessIds(task);
        return processIds.Count > 0
            ? HealthResult.Ok("process-running", $"PID {string.Join(", ", processIds)}")
            : HealthResult.Fail("process-not-found", $"Process not found: {task.Health.Pattern}");
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
