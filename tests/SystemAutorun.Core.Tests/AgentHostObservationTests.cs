using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

public sealed class AgentHostObservationTests
{
    [Theory]
    [InlineData("version")]
    [InlineData("source")]
    [InlineData("missing-header")]
    [InlineData("wrong-id")]
    [InlineData("unavailable-observed")]
    [InlineData("availability-mismatch")]
    [InlineData("bad-revision")]
    public void RejectsInvalidOrMismatchedWireObservationsAsProtocolFailures(string kind)
    {
        var data = JsonSerializer.SerializeToNode(Observation(), AgentProtocol.JsonOptions)!;
        switch (kind)
        {
            case "version": data["schemaVersion"] = 2; break;
            case "source": data["source"] = "local-cache"; break;
            case "missing-header": ((JsonObject)data).Remove("source"); break;
            case "wrong-id": data["snapshot"]!["id"] = "other-host"; break;
            case "unavailable-observed": data["observedAt"] = DateTimeOffset.UtcNow; break;
            case "availability-mismatch": data["snapshot"]!["online"] = true; break;
            case "bad-revision": data["revision"] = "not-a-revision"; break;
        }
        using var document = JsonDocument.Parse(data.ToJsonString());
        var exception = Assert.Throws<AgentCommandException>(() => AgentHostClient.ReadObservation(document.RootElement, "fixture"));
        Assert.Equal("protocol_mismatch", exception.Code);
    }

    [Fact]
    public void ReadOnlyUnavailableObservationPreservesReasonAndNeverClaimsAnObservedTime()
    {
        var original = Observation();
        var result = AgentHostClient.ReadObservation(JsonSerializer.SerializeToElement(original, AgentProtocol.JsonOptions), "FIXTURE");
        Assert.False(result.Available); Assert.Null(result.ObservedAt); Assert.Equal("host_disabled", result.Reason);
        Assert.Equal(original.Revision, result.Revision);
    }

    private static RemoteHostProbeResult Observation()
    {
        var host = new RemoteHostDefinition { Id = "fixture", Name = "fixture", Host = "127.0.0.1" };
        return new(new string('a', 64), false, "host_disabled", DateTimeOffset.UtcNow, RemoteHostStatusSnapshot.Offline(host, "disabled"));
    }
}
