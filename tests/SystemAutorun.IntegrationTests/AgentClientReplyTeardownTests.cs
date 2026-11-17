using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.IntegrationTests;

public sealed class AgentClientReplyTeardownTests
{
    [Theory]
    [InlineData(1024, true)] [InlineData(2048, true)] [InlineData(4096, true)] [InlineData(4095, true)]
    [InlineData(1024, false)] [InlineData(2048, false)] [InlineData(4096, false)] [InlineData(4095, false)]
    public async Task CompleteReplyFollowedByImmediatePeerCloseRetainsItsSuccessOrBusinessFailure(int frameBytes, bool success)
    {
        var root = Directory.CreateTempSubdirectory("SystemAutorun-reply-teardown-").FullName;
        File.WriteAllText(Path.Combine(root, "autorun.yaml"), "version: 1\ntasks: []\n");
        var endpoint = AgentProtocol.ForRoot(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var pipe = new NamedPipeServerStream(endpoint.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serving = ServeAsync();
        try
        {
            // This is a synthetic peer, not a real configuration commit or workload action.
            var request = new AgentClient(root).SendAsync<JsonElement>("settings-patch", payload: new { autoStartMissingOnLaunch = true },
                timeout: TimeSpan.FromSeconds(8), expectedRevision: new string('a', 64));
            if (success) { Assert.True((await request).TryGetProperty("padding", out _)); }
            else { Assert.Equal("fixture_error", (await Assert.ThrowsAsync<AgentCommandException>(() => request)).Code); }
            await serving;
        }
        finally
        {
            deadline.Cancel(); await pipe.DisposeAsync();
            try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or IOException) { }
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("SystemAutorun-reply-teardown-", StringComparison.Ordinal))
            { throw new InvalidOperationException("Non-owned pipe fixture."); }
            Directory.Delete(resolved, recursive: true);
        }

        async Task ServeAsync()
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var request = JsonSerializer.Deserialize<AgentRequest>((await reader.ReadLineAsync(deadline.Token))!, AgentProtocol.JsonOptions)!;
            var response = new AgentResponse(request.Id, success, new { padding = "" }, Error: success ? null : "synthetic business failure",
                RootId: endpoint.RootId, ErrorCode: success ? null : "fixture_error", OperationId: request.OperationId);
            var empty = JsonSerializer.Serialize(response, AgentProtocol.JsonOptions);
            var padding = frameBytes - Encoding.UTF8.GetByteCount(empty) - 1;
            Assert.True(padding >= 0);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response with { Data = new { padding = new string('x', padding) } }, AgentProtocol.JsonOptions) + "\n");
            Assert.Equal(frameBytes, bytes.Length);
            await pipe.WriteAsync(bytes, deadline.Token); await pipe.FlushAsync(deadline.Token);
            await pipe.DisposeAsync();
        }
    }
}
