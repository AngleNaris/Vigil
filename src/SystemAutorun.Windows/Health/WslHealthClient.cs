using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Execution;

namespace SystemAutorun.Windows.Health;

internal sealed class WslHealthClient
{
    private readonly HiddenProcessRunner _runner = new();

    public async Task<HealthResult> CheckRunningAsync(
        AutorunTask task,
        CancellationToken cancellationToken)
    {
        var distro = task.Health.Distro ?? task.Distro;
        if (string.IsNullOrWhiteSpace(distro))
        {
            return HealthResult.Fail("missing-distro", "WSL distro is missing.");
        }

        var result = await _runner.RunAsync(
            "wsl.exe",
            new[] { "--list", "--running", "--quiet" },
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);
        var running = result.ExitCode == 0
            && result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(value => string.Equals(value, distro, StringComparison.OrdinalIgnoreCase));
        return running
            ? HealthResult.Ok("wsl-running", $"WSL distro is running: {distro}")
            : HealthResult.Fail("wsl-stopped", $"WSL distro is not running: {distro}");
    }

    public async Task<HealthResult> CheckSystemdAsync(
        AutorunTask task,
        bool quick,
        CancellationToken cancellationToken)
    {
        var distro = task.Health.Distro ?? task.Distro;
        var service = task.Health.Service ?? task.Service;
        if (string.IsNullOrWhiteSpace(distro) || string.IsNullOrWhiteSpace(service))
        {
            return HealthResult.Fail("missing-distro-or-service", "WSL distro or service is missing.");
        }

        var timeout = TimeSpan.FromSeconds(quick ? 5 : task.Health.TimeoutSeconds);
        var result = await _runner.RunAsync(
            "wsl.exe",
            new[] { "-d", distro, "-u", "root", "--exec", "systemctl", "is-active", service },
            timeout,
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && string.Equals(
            result.StandardOutput.Trim(),
            "active",
            StringComparison.OrdinalIgnoreCase)
            ? HealthResult.Ok("systemd-active", $"{service} is active in {distro}.")
            : HealthResult.Fail(
                "systemd-not-active",
                string.IsNullOrWhiteSpace(result.CombinedOutput)
                    ? $"{service} is not active in {distro}."
                    : result.CombinedOutput);
    }

    public async Task<HealthResult> CheckProcessAsync(
        AutorunTask task,
        CancellationToken cancellationToken)
    {
        var distro = task.Health.Distro ?? task.Distro;
        var pattern = task.Health.Pattern;
        if (string.IsNullOrWhiteSpace(distro) || string.IsNullOrWhiteSpace(pattern))
        {
            return HealthResult.Fail("missing-distro-or-pattern", "WSL distro or process pattern is missing.");
        }

        var result = await _runner.RunAsync(
            "wsl.exe",
            new[] { "-d", distro, "--exec", "pgrep", "-f", "--", pattern },
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            ? HealthResult.Ok("wsl-process-running", result.StandardOutput)
            : HealthResult.Fail("wsl-process-not-found", $"Process not found in {distro}: {pattern}");
    }
}
