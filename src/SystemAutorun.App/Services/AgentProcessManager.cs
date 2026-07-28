using System.Diagnostics;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.App.Services;

internal sealed class AgentProcessManager : IAsyncDisposable
{
    private readonly AgentClient _client;
    private readonly ProjectPaths _paths;
    private Process? _ownedProcess;

    internal AgentProcessManager(AgentClient client, ProjectPaths paths)
    {
        _client = client;
        _paths = paths;
    }

    internal async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (await _client.PingAsync(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var agentPath = ResolveAgentPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = agentPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(_paths.RootDirectory);
        var startedProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start SystemAutorun.Agent.");
        _ownedProcess = startedProcess;

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        var duplicateExited = false;
        do
        {
            if (!duplicateExited && startedProcess.HasExited)
            {
                var exitCode = startedProcess.ExitCode;
                duplicateExited = true;
                _ownedProcess = null;
                startedProcess.Dispose();
                if (exitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"SystemAutorun.Agent exited with code {exitCode}.");
                }
            }

            if (await _client.PingAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);

        await StopOwnedProcessAsync().ConfigureAwait(false);
        throw new TimeoutException("SystemAutorun.Agent did not become ready within 15 seconds.");
    }

    public async ValueTask DisposeAsync() => await StopOwnedProcessAsync().ConfigureAwait(false);

    private string ResolveAgentPath()
    {
        var configured = Environment.GetEnvironmentVariable("SYSTEMAUTORUN_AGENT_PATH");
        var candidates = new List<string?>
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, "runtime", "SystemAutorun.Agent.exe"),
            Path.Combine(AppContext.BaseDirectory, "SystemAutorun.Agent.exe"),
        };

        var devRoot = Path.Combine(_paths.RootDirectory, "src", "SystemAutorun.Agent", "bin");
        if (Directory.Exists(devRoot))
        {
            candidates.AddRange(Directory
                .EnumerateFiles(devRoot, "SystemAutorun.Agent.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc));
        }

        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? throw new FileNotFoundException(
                "SystemAutorun.Agent.exe was not found beside the UI or in the development output.");
    }

    private async Task StopOwnedProcessAsync()
    {
        var process = _ownedProcess;
        _ownedProcess = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    _ = await _client.SendAsync<System.Text.Json.JsonElement>(
                        "shutdown",
                        timeout: TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
                {
                    // The exact owned PID is terminated below if graceful shutdown is unavailable.
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            process.Dispose();
        }
    }
}
