using System.Diagnostics;
using System.Globalization;
using System.IO;
using SystemAutorun.Windows.Execution;

namespace SystemAutorun.IntegrationTests;

public sealed class HiddenProcessRunnerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CallerCancellationOrDeadlineTerminatesStartedProcess(bool bounded, bool commandDeadline)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SystemAutorun.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var pidPath = Path.Combine(directory, "pid.txt");
        int? processId = null;
        Task? pending = null;
        using var cancellation = new CancellationTokenSource();
        try
        {
            var escapedPidPath = pidPath.Replace("'", "''", StringComparison.Ordinal);
            var run = new HiddenProcessRunner().RunAsync(
                Environment.ExpandEnvironmentVariables(
                    "%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"),
                new[]
                {
                    "-NoProfile",
                    "-Command",
                    $"[IO.File]::WriteAllText('{escapedPidPath}.tmp',[string]$PID); [IO.File]::Move('{escapedPidPath}.tmp','{escapedPidPath}'); Start-Sleep -Seconds 30",
                },
                commandDeadline ? TimeSpan.FromSeconds(4) : TimeSpan.FromMinutes(1),
                cancellation.Token, maximumOutputCharacters: bounded ? 4096 : null);
            pending = run;

            processId = await ReadOwnedPidAsync(pidPath, TimeSpan.FromSeconds(10));
            Assert.True(processId is > 0, "The child process did not publish a readable PID within the fixture deadline.");
            if (commandDeadline) { await Assert.ThrowsAsync<TimeoutException>(() => run); }
            else
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            }
            await WaitUntilExitedAsync(processId.Value, TimeSpan.FromSeconds(5));
            Assert.False(IsRunning(processId.Value));
        }
        finally
        {
            cancellation.Cancel();
            if (pending is not null)
            {
                try { await pending; }
                catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException) { }
            }
            if (processId is null && File.Exists(pidPath)) { processId = await ReadOwnedPidAsync(pidPath, TimeSpan.FromSeconds(2)); }
            if (processId is int ownedProcessId && IsRunning(ownedProcessId))
            {
                using var process = Process.GetProcessById(ownedProcessId);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            var resolved = Path.GetFullPath(directory);
            if (!resolved.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "SystemAutorun.Tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _)) { throw new InvalidOperationException("Non-owned cancellation fixture."); }
            Directory.Delete(resolved, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExcessStdoutOrStderrTerminatesOnlyItsOwnedProcessWithoutReturningPartialSuccess(bool stderr)
    {
        var directory = Directory.CreateTempSubdirectory("SystemAutorun-bounded-probe-").FullName;
        var path = Path.Combine(directory, "owned-pid.txt");
        int? pid = null;
        try
        {
            var stream = stderr ? "Error" : "Out";
            var script = $"$PID | Set-Content -LiteralPath '{path.Replace("'", "''", StringComparison.Ordinal)}' -NoNewline; while($true){{[Console]::{stream}.Write(('x'*2048))}}";
            var exception = await Assert.ThrowsAsync<IOException>(() => new HiddenProcessRunner().RunAsync(
                Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"),
                ["-NoProfile", "-NonInteractive", "-Command", script], TimeSpan.FromSeconds(10), maximumOutputCharacters: 4096));
            Assert.Contains("bounded character limit", exception.Message, StringComparison.Ordinal);
            pid = await ReadOwnedPidAsync(path, TimeSpan.FromSeconds(2));
            Assert.True(pid is > 0, "The bounded child did not publish a readable PID.");
            await WaitUntilExitedAsync(pid.Value, TimeSpan.FromSeconds(5)); Assert.False(IsRunning(pid.Value));
        }
        finally
        {
            if (pid is null && File.Exists(path)) { pid = await ReadOwnedPidAsync(path, TimeSpan.FromSeconds(2)); }
            if (pid is { } owned && IsRunning(owned)) { using var process = Process.GetProcessById(owned); process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            var resolved = Path.GetFullPath(directory);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("SystemAutorun-bounded-probe-", StringComparison.Ordinal)) { throw new InvalidOperationException("Non-owned probe directory."); }
            Directory.Delete(resolved, recursive: true);
        }
    }

    [Fact]
    public async Task BoundedCaptureClosesStdinAndReturnsSmallOutputNormally()
    {
        var result = await new HiddenProcessRunner().RunAsync(
            Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write(('closed:'+([Console]::In.ReadToEnd().Length)))"],
            TimeSpan.FromSeconds(10), maximumOutputCharacters: 4096);
        Assert.Equal(0, result.ExitCode); Assert.Equal("closed:0", result.StandardOutput); Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task OwnedPidReaderBoundsSharingWaitAndRejectsMalformedPublishedData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SystemAutorun.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "pid.txt");
        try
        {
            await File.WriteAllTextAsync(path, "12345");
            using (var blocker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            { Assert.Null(await ReadOwnedPidAsync(path, TimeSpan.FromMilliseconds(100))); }
            var blockerAgain = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Task<int?> waiting;
            try
            {
                waiting = ReadOwnedPidAsync(path, TimeSpan.FromSeconds(2)); await Task.Delay(100); Assert.False(waiting.IsCompleted);
            }
            finally { blockerAgain.Dispose(); }
            Assert.Equal(12345, await waiting);
            await File.WriteAllTextAsync(path, "not-a-pid");
            await Assert.ThrowsAsync<InvalidDataException>(() => ReadOwnedPidAsync(path, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            var resolved = Path.GetFullPath(directory);
            if (!resolved.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "SystemAutorun.Tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _)) { throw new InvalidOperationException("Non-owned PID reader fixture."); }
            Directory.Delete(resolved, recursive: true);
        }
    }

    // Atomic publish does not guarantee every later reader gets a share-compatible open.
    // Only transient missing/sharing statuses may wait; malformed reports still fail.
    private static async Task<int?> ReadOwnedPidAsync(string path, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            try
            {
                var text = await File.ReadAllTextAsync(path);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
                    { throw new InvalidDataException("The owned fixture published an invalid PID."); }
                    return pid;
                }
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 2 or 3 or 32 or 33) { }
            await Task.Delay(50);
        }
        while (elapsed.Elapsed < timeout);
        return null;
    }

    private static async Task WaitUntilExitedAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline && IsRunning(processId))
        {
            await Task.Delay(50);
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
