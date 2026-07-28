using System.Collections.Concurrent;
using System.Diagnostics;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Windows.Execution;

internal sealed class WslController : IDisposable
{
    private readonly HiddenProcessRunner _runner = new();
    private readonly ConcurrentDictionary<string, Process> _keepalives =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<CommandResult> EnsureRunningAsync(
        string distro,
        CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(
            "wsl.exe",
            new[] { "-d", distro, "--exec", "/bin/true" },
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return new CommandResult(false, result.CombinedOutput);
        }

        if (!_keepalives.TryGetValue(distro, out var process) || process.HasExited)
        {
            process?.Dispose();
            var keepalive = _runner.StartDetached(
                "wsl.exe",
                new[] { "-d", distro, "--exec", "/bin/sleep", "infinity" });
            _keepalives[distro] = keepalive;
        }

        return new CommandResult(true, $"WSL distro is running: {distro}", _keepalives[distro].Id);
    }

    public async Task<CommandResult> StartSystemdAsync(
        string distro,
        string service,
        CancellationToken cancellationToken = default)
    {
        var ready = await EnsureRunningAsync(distro, cancellationToken).ConfigureAwait(false);
        if (!ready.Succeeded)
        {
            return ready;
        }

        return await RunWslAsync(
            new[] { "-d", distro, "-u", "root", "--exec", "systemctl", "start", service },
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommandResult> StopSystemdAsync(
        string distro,
        string service,
        CancellationToken cancellationToken = default) =>
        await RunWslAsync(
            new[] { "-d", distro, "-u", "root", "--exec", "systemctl", "stop", service },
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);

    public async Task<CommandResult> StartCommandAsync(
        string distro,
        string command,
        CancellationToken cancellationToken = default)
    {
        var ready = await EnsureRunningAsync(distro, cancellationToken).ConfigureAwait(false);
        if (!ready.Succeeded)
        {
            return ready;
        }

        var process = _runner.StartDetached(
            "wsl.exe",
            new[] { "-d", distro, "--exec", "bash", "-lc", $"nohup setsid bash -lc {ShellQuote(command)} >/dev/null 2>&1 &" });
        return new CommandResult(true, $"WSL command started in {distro}.", process.Id);
    }

    public async Task<CommandResult> StopCommandAsync(
        string distro,
        string pattern,
        CancellationToken cancellationToken = default) =>
        await RunWslAsync(
            new[] { "-d", distro, "-u", "root", "--exec", "pkill", "-TERM", "-f", "--", pattern },
            TimeSpan.FromSeconds(15),
            cancellationToken,
            successExitCodes: new[] { 0, 1 }).ConfigureAwait(false);

    public async Task<CommandResult> TerminateAsync(
        string distro,
        CancellationToken cancellationToken = default)
    {
        if (_keepalives.TryRemove(distro, out var keepalive))
        {
            if (!keepalive.HasExited)
            {
                keepalive.Kill(entireProcessTree: true);
            }

            keepalive.Dispose();
        }

        return await RunWslAsync(
            new[] { "--terminate", distro },
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProcessRunResult> QueryAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        await _runner.RunAsync("wsl.exe", arguments, timeout, cancellationToken).ConfigureAwait(false);

    public IReadOnlyList<int> GetKeepaliveProcessIds(string distro) =>
        _keepalives.TryGetValue(distro, out var process) && !process.HasExited
            ? new[] { process.Id }
            : Array.Empty<int>();

    public void Dispose()
    {
        foreach (var process in _keepalives.Values)
        {
            process.Dispose();
        }

        _keepalives.Clear();
    }

    private async Task<CommandResult> RunWslAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? successExitCodes = null)
    {
        var result = await _runner.RunAsync(
            "wsl.exe",
            arguments,
            timeout,
            cancellationToken).ConfigureAwait(false);
        var success = (successExitCodes ?? new[] { 0 }).Contains(result.ExitCode);
        return new CommandResult(
            success,
            string.IsNullOrWhiteSpace(result.CombinedOutput)
                ? $"wsl.exe exited with code {result.ExitCode}."
                : result.CombinedOutput);
    }

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
