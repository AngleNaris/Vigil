using System.Text.Json;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Core.Tests;

public sealed class AgentProtocolTests
{
    [Fact]
    public void UsesCamelCaseAndStableRequestShape()
    {
        var request = new AgentRequest("abc", "status", "worker");

        var json = JsonSerializer.Serialize(request, AgentProtocol.JsonOptions);

        Assert.Contains("\"id\":\"abc\"", json, StringComparison.Ordinal);
        Assert.Contains("\"command\":\"status\"", json, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"worker\"", json, StringComparison.Ordinal);
    }
}
