using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

public sealed class StartupInventoryTests
{
    [Theory]
    [InlineData("scope")] [InlineData("type")] [InlineData("state")] [InlineData("search-control")]
    [InlineData("search-length")] [InlineData("limit-small")] [InlineData("limit-large")] [InlineData("null-scope")]
    public void RejectsInvalidFilters(string kind)
    {
        var query = kind switch
        {
            "scope" => new StartupListQuery(Scope: "remote"),
            "type" => new StartupListQuery(Kind: "command"),
            "state" => new StartupListQuery(State: "running"),
            "search-control" => new StartupListQuery(Search: "bad\nquery"),
            "search-length" => new StartupListQuery(Search: new string('x', 257)),
            "limit-small" => new StartupListQuery(Limit: 0),
            "limit-large" => new StartupListQuery(Limit: 201),
            _ => new StartupListQuery(Scope: null!),
        };
        Assert.Throws<ArgumentException>(() => query.Validate());
    }

    [Fact]
    public void FiltersMetadataWithoutTreatingConfigurationOrBackupStateAsProcessState()
    {
        var entry = Entry();
        var query = new StartupListQuery("USER", "REGISTRY", "ACTIVE", " FIXTURE ").Validate();
        Assert.True(query.Matches(entry));
        Assert.False(query.Matches(entry with { Source = "HKLM" }));
        Assert.False(query.Matches(entry with { IsDisabled = true }));
        Assert.False(query.Matches(entry with { Source = "Startup" }));
        Assert.True(new StartupListQuery(State: "disabled").Validate().Matches(entry with { IsDisabled = true }));
        Assert.False(AgentProtocol.IsMutation("startup-list"));
        Assert.False(AgentProtocol.IsConfigurationMutation("startup-list"));
    }

    [Theory]
    [InlineData("schema")] [InlineData("missing-available")] [InlineData("revision")] [InlineData("stale")]
    [InlineData("future")] [InlineData("observed")] [InlineData("counts")] [InlineData("duplicate")]
    [InlineData("source")] [InlineData("filter")] [InlineData("hidden-error")] [InlineData("source-issue")] [InlineData("lower-bounds")]
    public void RejectsBadOrMismatchedObservations(string kind)
    {
        var snapshot = Snapshot();
        var node = JsonSerializer.SerializeToNode(snapshot, AgentProtocol.JsonOptions)!;
        var query = new StartupListQuery(Scope: "user");
        switch (kind)
        {
            case "schema": node["schemaVersion"] = 2; break;
            case "missing-available": ((JsonObject)node).Remove("available"); break;
            case "revision": node["configurationRevision"] = "bad"; break;
            case "stale": node["completedAt"] = DateTimeOffset.UtcNow.AddMinutes(-1); break;
            case "future": node["completedAt"] = DateTimeOffset.UtcNow.AddMinutes(1); break;
            case "observed": node["observedAt"] = null; break;
            case "counts": node["matchedEntries"] = 0; break;
            case "lower-bounds": node["countsAreLowerBounds"] = false; break;
            case "duplicate": node["entries"]!.AsArray().Add(node["entries"]![0]!.DeepClone()); break;
            case "source": node["entries"]![0]!["source"] = "network-command"; break;
            case "filter": node["entries"]![0]!["source"] = "HKLM"; break;
            case "hidden-error": node["entries"]![0]!["error"] = "unavailable"; break;
            case "source-issue": node["sourceIssues"]!.AsArray().Add(JsonSerializer.SerializeToNode(new StartupSourceIssue("HKLM", "permission_denied"), AgentProtocol.JsonOptions)); break;
        }
        using var document = JsonDocument.Parse(node.ToJsonString());
        var exception = Assert.Throws<AgentCommandException>(() => AgentStartupClient.ReadObservation(document.RootElement, query));
        Assert.Equal("protocol_mismatch", exception.Code);
    }

    [Fact]
    public void UnavailableCountsAndObservationStayUnknownWhilePartialIssuesRemainVisible()
    {
        var unavailable = Snapshot() with { Available = false, ObservedAt = null, Reason = "permission_denied", Entries = [],
            ScannedEntries = null, MatchedEntries = null, ReadableSources = 0 };
        var result = AgentStartupClient.ReadObservation(JsonSerializer.SerializeToElement(unavailable, AgentProtocol.JsonOptions), new());
        Assert.False(result.Available); Assert.Null(result.ObservedAt); Assert.Null(result.ScannedEntries);
        var partial = Snapshot() with { Reason = "startup_source_unavailable", SourceIssues = [new("HKCU", "permission_denied")] };
        var read = AgentStartupClient.ReadObservation(JsonSerializer.SerializeToElement(partial, AgentProtocol.JsonOptions), new());
        Assert.Single(read.Entries); Assert.Single(read.SourceIssues);
    }

    private static StartupInventorySnapshot Snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        return new(1, new string('a', 64), now, now, true, null, [Entry()], 1, 1, false, false, false, 1, []);
    }

    private static StartupEntry Entry() => new("fixture", "HKCU", "fixture", "never-run.exe --metadata", "never-run.exe",
        "--metadata", null, @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Run :: fixture", null, false,
        null, null, false, true, "fixture-task");
}
