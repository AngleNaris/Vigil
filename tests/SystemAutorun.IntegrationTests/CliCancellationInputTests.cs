using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SystemAutorun.Cli;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.IntegrationTests;

public sealed class CliCancellationInputTests
{
    [Theory]
    [InlineData("task list")]
    [InlineData("config show")]
    [InlineData("host status")]
    public async Task CancelledSingleReadReturnsFailureEnvelope(string command)
    {
        using var root = new TempRoot(); using var cancelled = new CancellationTokenSource();
        var transport = new CancellingTransport(cancelled);
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await new CliApplication(_ => transport).RunAsync(
            [.. command.Split(' '), "--root", root.Path, "--json"], TextReader.Null, output, error, cancelled.Token);
        Assert.Equal(6, exit); Assert.Equal(1, transport.Calls);
        var reply = Parse(output.ToString());
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("cancelled", reply.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("monitor watch")]
    [InlineData("logs --follow")]
    public async Task UserCancellationStillCleanlyEndsStreamingCommands(string command)
    {
        using var root = new TempRoot(); using var cancelled = new CancellationTokenSource();
        var transport = new CancellingTransport(cancelled);
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await new CliApplication(_ => transport).RunAsync(
            [.. command.Split(' '), "--root", root.Path, "--json"], TextReader.Null, output, error, cancelled.Token);
        Assert.Equal(0, exit); Assert.Equal(1, transport.Calls);
        Assert.Empty(output.ToString()); Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InputDeadlineReturnsBeforeNonCooperativeReaderAndNeverConnects(bool yaml, bool callerCancellation)
    {
        using var root = new TempRoot(); using var input = new BlockingReader();
        using var cancelled = new CancellationTokenSource();
        using var output = new StringWriter(); using var error = new StringWriter();
        var connections = 0;
        var app = new CliApplication(_ => { connections++; throw new InvalidOperationException("No connection before complete input."); });
        var args = yaml ? new[] { "config", "validate" }
            : new[] { "settings", "patch", "--expected-revision", new string('a', 64) };
        try
        {
            var running = app.RunAsync([.. args, "--root", root.Path, "--file", "-", "--timeout", callerCancellation ? "5" : "0.2", "--non-interactive", "--json"], input, output, error, cancelled.Token);
            await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (callerCancellation) { cancelled.Cancel(); }
            Assert.Equal(6, await running.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(input.Finished.Task.IsCompleted); Assert.Equal(0, connections);
            var reply = Parse(output.ToString());
            Assert.Equal(callerCancellation ? "cancelled" : "timeout", reply.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, reply.GetProperty("operationId").ValueKind);
            if (!callerCancellation) { Assert.Contains("no request was sent", reply.GetProperty("error").GetProperty("message").GetString()); }
        }
        finally
        {
            input.Release.Set();
            if (input.Started.Task.IsCompleted) { await input.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }

    [Fact]
    public async Task ConsoleWithOpenEmptyStdinExitsOnItsInputDeadlineWithoutAnAgent()
    {
        using var root = new TempRoot();
        var cli = typeof(CliCancellationInputTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(item => item.Key == "CliExecutablePath").Value!;
        var info = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--root", root.Path, "config", "validate", "--file", "-", "--timeout", "1", "--non-interactive", "--json" })
        { info.ArgumentList.Add(arg); }
        using var process = Process.Start(info)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            // Deliberately neither write nor close stdin: this reproduced the original hang.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(6, process.ExitCode);
            var reply = Parse(await stdout.WaitAsync(deadline.Token));
            Assert.Equal("timeout", reply.GetProperty("error").GetProperty("code").GetString());
            Assert.Empty(await stderr.WaitAsync(deadline.Token));
            Assert.False(Directory.Exists(System.IO.Path.Combine(root.Path, ".autorun-state")));
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }

    private static JsonElement Parse(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    private sealed class CancellingTransport(CancellationTokenSource source) : ICliTransport
    {
        public int Calls { get; private set; }
        public Task<AgentResponse> SendAsync(CliRequest request, CancellationToken cancellationToken)
        { Calls++; source.Cancel(); return Task.FromCanceled<AgentResponse>(cancellationToken); }
    }

    private sealed class BlockingReader : TextReader
    {
        public ManualResetEventSlim Release { get; } = new(false);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try { Release.Wait(); return ValueTask.FromResult(0); }
            finally { Finished.TrySetResult(); }
        }
        protected override void Dispose(bool disposing) { if (disposing) { Release.Dispose(); } base.Dispose(disposing); }
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("SystemAutorun-cli-input-tests-").FullName;
        public TempRoot() => File.WriteAllText(System.IO.Path.Combine(Path, "autorun.yaml"), "version: 1\nsettings:\n  default_startup_delay: 0\ntasks: []\n");
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            Assert.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), resolved, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("SystemAutorun-cli-input-tests-", System.IO.Path.GetFileName(resolved), StringComparison.Ordinal);
            Directory.Delete(resolved, true);
        }
    }
}
