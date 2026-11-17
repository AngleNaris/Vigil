using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace SystemAutorun.Windows.Execution;

internal sealed class HiddenProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        int? maximumOutputCharacters = null)
    {
        if (maximumOutputCharacters is { } maximum)
        { return await RunBoundedAsync(executable, arguments, timeout, maximum, cancellationToken).ConfigureAwait(false); }
        var startInfo = CreateStartInfo(executable, arguments, redirect: true);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start: {executable}");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TerminateProcessTreeAsync(process).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"Process timed out after {timeout.TotalSeconds:0}s: {executable}");
        }

        return new ProcessRunResult(
            process.ExitCode,
            SanitizeOutput(await outputTask.ConfigureAwait(false)),
            SanitizeOutput(await errorTask.ConfigureAwait(false)));
    }

    private static async Task<ProcessRunResult> RunBoundedAsync(string executable, IEnumerable<string> arguments,
        TimeSpan timeout, int maximum, CancellationToken callerToken)
    {
        if (maximum is < 1 or > 1024 * 1024 || timeout <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException(nameof(maximum)); }
        callerToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadline.CancelAfter(timeout);
        var start = CreateStartInfo(executable, arguments, redirect: true);
        start.RedirectStandardInput = true;
        using var process = new Process { StartInfo = start };
        if (!process.Start()) { throw new InvalidOperationException("Failed to start the bounded probe process."); }
        var exceeded = 0;
        var outputTask = CaptureAsync(process.StandardOutput);
        var errorTask = CaptureAsync(process.StandardError);
        try
        {
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).WaitAsync(deadline.Token).ConfigureAwait(false);
            return new(process.ExitCode, SanitizeOutput(await outputTask.ConfigureAwait(false)), SanitizeOutput(await errorTask.ConfigureAwait(false)));
        }
        catch
        {
            var expired = deadline.IsCancellationRequested;
            await TerminateProcessTreeAsync(process).ConfigureAwait(false);
            deadline.Cancel();
            try { await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception) { /* Observe failed/cancelled captures; process/pipe cleanup remains bounded. */ }
            callerToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref exceeded) != 0) { throw new IOException("Probe output exceeded its bounded character limit."); }
            if (expired) { throw new TimeoutException("Probe process or pipe capture exceeded its time budget."); }
            throw;
        }
        async Task<string> CaptureAsync(StreamReader reader)
        {
            var builder = new StringBuilder(Math.Min(maximum, 4096));
            var buffer = new char[4096];
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
                if (read == 0) { return builder.ToString(); }
                if (builder.Length + read > maximum)
                { Interlocked.Exchange(ref exceeded, 1); deadline.Cancel(); return builder.ToString(); }
                builder.Append(buffer, 0, read);
            }
        }
    }

    public Process StartDetached(string executable, IEnumerable<string> arguments)
    {
        var process = new Process
        {
            StartInfo = CreateStartInfo(executable, arguments, redirect: false),
            EnableRaisingEvents = true,
        };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start: {executable}");
        }

        return process;
    }

    public Process StartStreaming(string executable, IEnumerable<string> arguments)
    {
        var start = CreateStartInfo(executable, arguments, redirect: true);
        start.RedirectStandardInput = true;
        var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start streaming process: {executable}");
        }

        return process;
    }

    internal static async Task TerminateProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }

        try
        {
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
        {
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        IEnumerable<string> arguments,
        bool redirect)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            StandardOutputEncoding = redirect ? Encoding.UTF8 : null,
            StandardErrorEncoding = redirect ? Encoding.UTF8 : null,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string SanitizeOutput(string value) => value.Replace("\0", string.Empty).Trim();
}

internal sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => string.Join(
        Environment.NewLine,
        new[] { StandardOutput, StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
