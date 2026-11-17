using System.Text.Json;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Core.Tests;

public sealed class AgentProtocolTests
{
    [Fact]
    public void UsesCamelCaseAndStableRequestShape()
    {
        var request = new AgentRequest("abc", "status", "worker", TimeoutMilliseconds: 12_500);

        var json = JsonSerializer.Serialize(request, AgentProtocol.JsonOptions);

        Assert.Contains("\"id\":\"abc\"", json, StringComparison.Ordinal);
        Assert.Contains("\"command\":\"status\"", json, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"worker\"", json, StringComparison.Ordinal);
        Assert.Contains("\"timeoutMilliseconds\":12500", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializesRequestsFromClientsWithoutTimeoutField()
    {
        var request = JsonSerializer.Deserialize<AgentRequest>(
            """{"id":"legacy","command":"ping","name":null,"payload":null}""",
            AgentProtocol.JsonOptions);

        Assert.NotNull(request);
        Assert.Null(request.TimeoutMilliseconds);
    }

    [Fact]
    public async Task ClientRejectsNonPositiveTimeoutBeforeConnecting()
    {
        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new AgentClient().SendAsync<JsonElement>("ping", timeout: TimeSpan.Zero));

        Assert.Equal("timeout", exception.ParamName);
    }

    [Fact]
    public async Task ClientRejectsTimeoutAboveServerLimitBeforeConnecting()
    {
        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new AgentClient().SendAsync<JsonElement>(
                "ping",
                timeout: TimeSpan.FromMilliseconds(
                    AgentProtocol.MaximumRequestTimeoutMilliseconds + 1)));

        Assert.Equal("timeout", exception.ParamName);
    }

    [Fact]
    public async Task ClientRejectsOversizedPayloadBeforeConnecting()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => new AgentClient().SendAsync<JsonElement>(
                "ping",
                payload: new { value = new string('x', AgentProtocol.MaximumRequestCharacters) }));

        Assert.Equal("payload", exception.ParamName);
    }
}
