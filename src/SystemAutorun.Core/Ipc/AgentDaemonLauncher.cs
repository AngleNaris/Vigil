using System.Diagnostics;

namespace SystemAutorun.Core.Ipc;

// Shared launch policy: this owns a process HANDLE, never the daemon/workload lifetime.
// Readers do not call this implicitly. Only GUI startup / explicit `daemon start` do.
public sealed class AgentDaemonLauncher
{
    private readonly AgentClient _client;
    private readonly Infrastructure.ProjectPaths _paths;
    private readonly Func<ProcessStartInfo, Process?> _start;

    public AgentDaemonLauncher(AgentClient client, Infrastructure.ProjectPaths paths,
        Func<ProcessStartInfo, Process?>? processStarter = null)
    { _client = client; _paths = paths; _start = processStarter ?? Process.Start; }

    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (await _client.PingAsync(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false)) { return; }
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(ResolveAgentPath(_paths.RootDirectory))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = _paths.RootDirectory,
            // A GUI-subsystem child can still inherit the CLI's redirected pipe handles.
            // Private pipes prevent `daemon start | capture` from waiting until Agent exit.
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.ArgumentList.Add("--root"); info.ArgumentList.Add(_paths.RootDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = _start(info) ?? throw new InvalidOperationException("Failed to start Vigil.Agent.");
        process.StandardInput.Close();
        using var discard = new CancellationTokenSource();
        var output = DiscardAsync(process.StandardOutput.BaseStream, discard.Token);
        var error = DiscardAsync(process.StandardError.BaseStream, discard.Token);
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            var exited = false;
            do
            {
                if (!exited && process.HasExited)
                {
                    exited = true;
                    if (process.ExitCode != 0) { throw new InvalidOperationException($"Vigil.Agent exited with code {process.ExitCode}."); }
                }
                if (await _client.PingAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false)) { return; }
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            } while (DateTimeOffset.UtcNow < deadline);
            throw new TimeoutException("Vigil.Agent did not become ready within 15 seconds; it may still be running. Inspect this root's daemon status before retrying.");
        }
        finally
        {
            discard.Cancel();
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
    }

    private static async Task DiscardAsync(Stream stream, CancellationToken cancellationToken)
    {
        try { await stream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    public static string ResolveAgentPath(string rootDirectory, string? baseDirectory = null)
    {
        var basis = baseDirectory ?? AppContext.BaseDirectory;
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("VIGIL_AGENT_PATH"),
            Path.Combine(basis, "runtime", "Vigil.Agent.exe"),
            Path.Combine(basis, "Vigil.Agent.exe"),
        };
        // A published Console has an isolated runtime in <package>/cli to avoid
        // conflicting desktop/runtime assets; its Agent stays in <package>/runtime.
        if (new DirectoryInfo(basis).Name.Equals("cli", StringComparison.OrdinalIgnoreCase))
        { candidates.Add(Path.GetFullPath(Path.Combine(basis, "..", "runtime", "Vigil.Agent.exe"))); }
        // Development binaries belong to the code checkout, not the selected config root.
        // This also permits an isolated --root without copying executable trees into it.
        var roots = new List<string> { rootDirectory };
        for (var directory = new DirectoryInfo(basis); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SystemAutorun.sln"))) { roots.Add(directory.FullName); break; }
        }
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bin = Path.Combine(root, "src", "SystemAutorun.Agent", "bin");
            if (Directory.Exists(bin))
            {
                candidates.AddRange(Directory.EnumerateFiles(bin, "Vigil.Agent.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc));
            }
        }
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? throw new FileNotFoundException("Vigil.Agent.exe was not found beside this client or in the development output.");
    }
}
