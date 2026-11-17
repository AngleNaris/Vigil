using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

public sealed class StartupInspectionTests
{
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static StartupEntry Entry => new("selected", "Startup", "owned", "literal command", @"\\unreachable.invalid\never-probe\owned.exe",
        "--literal", null, "fixed origin", null, false, null, null, false, true, null);

    [Fact]
    public void DigestIsVersionedDisplayedMetadataNotNativeAuthorityAndSurvivesWireRoundtrip()
    {
        var entry = Entry;
        var json = JsonSerializer.SerializeToElement(entry, AgentProtocol.JsonOptions);
        Assert.Equal(entry.MetadataDigest, json.GetProperty("metadataDigest").GetString());
        Assert.Equal(entry.MetadataDigest, json.Deserialize<StartupEntry>(AgentProtocol.JsonOptions)!.MetadataDigest);
        Assert.Equal(64, entry.MetadataDigest.Length);
        Assert.All(entry.MetadataDigest, value => Assert.True(char.IsAsciiHexDigit(value) && !char.IsAsciiLetterUpper(value)));
        Assert.False(AgentProtocol.IsMutation("startup-inspect"));
        Assert.False(AgentProtocol.IsConfigurationMutation("STARTUP-INSPECT"));
        Assert.Equal(new StartupListQuery("user", "shortcut", "active", "owned", 200), StartupInspectionRequest.FromEntry(entry).Validate(entry.Id));
    }

    [Theory]
    [InlineData("id")] [InlineData("source")] [InlineData("name")] [InlineData("command")]
    [InlineData("target")] [InlineData("arguments")] [InlineData("directory")] [InlineData("origin")]
    [InlineData("error")] [InlineData("state")] [InlineData("backup")] [InlineData("time")]
    [InlineData("admin")] [InlineData("modify")] [InlineData("managed")]
    public void EveryDisplayedInputParticipatesInDigest(string change)
    {
        var entry = change switch
        {
            "id" => Entry with { Id = "other" }, "source" => Entry with { Source = "HKCU" }, "name" => Entry with { Name = "other" },
            "command" => Entry with { RawCommand = "other" }, "target" => Entry with { Target = "other" },
            "arguments" => Entry with { Arguments = "other" }, "directory" => Entry with { WorkingDirectory = "other" },
            "origin" => Entry with { Origin = "other" }, "error" => Entry with { Error = "error" },
            "state" => Entry with { IsDisabled = true }, "backup" => Entry with { BackupId = "other" },
            "time" => Entry with { DisabledAt = DateTimeOffset.UnixEpoch }, "admin" => Entry with { RequiresAdministrator = true },
            "modify" => Entry with { CanModify = false }, _ => Entry with { ManagedTask = "managed" },
        };
        Assert.NotEqual(Entry.MetadataDigest, entry.MetadataDigest);
    }

    [Theory]
    [InlineData("HKCU", false, "user", "registry")] [InlineData("HKLM", false, "machine", "registry")]
    [InlineData("HKLM-WOW64", true, "machine", "registry")] [InlineData("Startup", true, "user", "shortcut")]
    [InlineData("Common Startup", true, "machine", "shortcut")]
    public void ReferenceCanOnlySelectABoundedFixedSource(string source, bool disabled, string scope, string kind)
    {
        var entry = Entry with { Source = source, IsDisabled = disabled };
        var query = StartupInspectionRequest.FromEntry(entry).Validate(entry.Id);
        Assert.Equal(scope, query.Scope); Assert.Equal(kind, query.Kind); Assert.Equal(disabled ? "disabled" : "active", query.State);
        Assert.Equal(200, query.Limit);
        var longName = StartupInspectionRequest.FromEntry(entry with { Name = new string('x', 300) });
        Assert.Null(longName.Validate(entry.Id).Search);
    }

    [Theory]
    [InlineData("source")] [InlineData("name")] [InlineData("empty-name")] [InlineData("digest")] [InlineData("uppercase")] [InlineData("id")]
    public void InvalidReferenceCannotStartAScan(string change)
    {
        var request = StartupInspectionRequest.FromEntry(Entry);
        request = change switch
        {
            "source" => request with { Source = "arbitrary" }, "name" => request with { Name = new string('x', 1025) },
            "empty-name" => request with { Name = " " }, "digest" => request with { MetadataDigest = "none" },
            "uppercase" => request with { MetadataDigest = new string('A', 64) }, _ => request,
        };
        Assert.Throws<ArgumentException>(() => request.Validate(change == "id" ? "" : Entry.Id));
    }

    [Fact]
    public void StateMustBeExplicitRatherThanDefaultingToActive()
    {
        var node = JsonSerializer.SerializeToNode(StartupInspectionRequest.FromEntry(Entry), AgentProtocol.JsonOptions)!;
        node.AsObject().Remove("isDisabled");
        Assert.Throws<JsonException>(() => node.Deserialize<StartupInspectionRequest>(AgentProtocol.JsonOptions));
    }

    [Theory]
    [InlineData("active", "reviewable", null)] [InlineData("error", "blocked", "entry_unreadable")]
    [InlineData("disabled", "blocked", "entry_disabled")] [InlineData("managed", "blocked", "already_managed")]
    [InlineData("target", "blocked", "target_metadata_unavailable")]
    public void ActionsDescribeReviewEligibilityButNeverNativeReadiness(string state, string expected, string? reason)
    {
        var entry = state switch
        {
            "error" => Entry with { Error = "unsupported_registry_value_kind" }, "disabled" => Entry with { IsDisabled = true },
            "managed" => Entry with { ManagedTask = "owned" }, "target" => Entry with { Target = null }, _ => Entry,
        };
        var actions = StartupInspectionPolicy.Describe(entry);
        Assert.Equal(expected, actions[0].State); Assert.Equal(reason, actions[0].Reason);
        Assert.DoesNotContain(actions.Skip(1), action => action.State is not ("blocked" or "not_applicable"));
        Assert.Equal(state == "disabled" ? "native_control_unavailable" : "entry_active", actions[3].Reason);
        Assert.Equal("shortcut_required", StartupInspectionPolicy.Describe(entry with { Source = "HKLM", IsDisabled = true })[3].Reason);
    }

    internal static StartupInspectionSnapshot Snapshot(StartupEntry entry, string revision)
    {
        var now = DateTimeOffset.UtcNow;
        return new(1, revision, now, now, entry, entry.MetadataDigest, entry.IsDisabled ? "not_checked" : "not_applicable",
            false, false, false, false, StartupInspectionPolicy.Describe(entry));
    }

    [Fact]
    public void CompleteInspectionIsBoundToTheRequestAndEnvelope()
    {
        var data = Snapshot(Entry, Revision);
        var result = AgentStartupClient.ReadInspection(new("request", true, data, Revision: Revision), Entry.Id,
            StartupInspectionRequest.FromEntry(Entry), Revision);
        Assert.Equal(data.Entry, result.Entry); Assert.Equal(data.MetadataDigest, result.MetadataDigest);
        Assert.True(data.Actions.SequenceEqual(result.Actions)); Assert.False(result.NativeWriteReady);
    }

    [Fact]
    public void InventoryDigestCannotLieAndLegacyInventoryWithoutItStillYieldsOnlyComputedMetadata()
    {
        var now = DateTimeOffset.UtcNow;
        var observation = new StartupInventorySnapshot(1, Revision, now, now, true, null, [Entry], 1, 1, false, false, false, 1, []);
        var node = JsonSerializer.SerializeToNode(observation, AgentProtocol.JsonOptions)!;
        node["entries"]![0]!["metadataDigest"] = new string('b', 64);
        var error = Assert.Throws<AgentCommandException>(() => AgentStartupClient.ReadObservation(JsonSerializer.SerializeToElement(node), new()));
        Assert.Equal("protocol_mismatch", error.Code);
        node["entries"]![0]!.AsObject().Remove("metadataDigest");
        Assert.Equal(Entry.MetadataDigest, Assert.Single(AgentStartupClient.ReadObservation(JsonSerializer.SerializeToElement(node), new()).Entries).MetadataDigest);
    }

    [Theory]
    [InlineData("schema")] [InlineData("revision")] [InlineData("envelope-revision")] [InlineData("operation")]
    [InlineData("replayed")] [InlineData("stale")] [InlineData("future")] [InlineData("observed")]
    [InlineData("entry")] [InlineData("digest")] [InlineData("entry-digest")] [InlineData("entry-managed")]
    [InlineData("archive")] [InlineData("nativeWriteReady")] [InlineData("nativeWritePerformed")] [InlineData("executionRequested")]
    [InlineData("missing-nativeWriteReady")] [InlineData("missing-nativeWritePerformed")] [InlineData("missing-executionRequested")]
    [InlineData("missing-entry-flag")] [InlineData("missing-action-reason")] [InlineData("actions")] [InlineData("native-action")]
    public void IncompleteChangedOrFalselyAuthoritativeRepliesAreProtocolFailuresNotReceipts(string defect)
    {
        var node = JsonSerializer.SerializeToNode(Snapshot(Entry, Revision), AgentProtocol.JsonOptions)!;
        if (defect == "schema") { node["schemaVersion"] = 2; }
        else if (defect == "revision") { node["configurationRevision"] = new string('b', 64); }
        else if (defect == "stale") { node["completedAt"] = DateTimeOffset.UtcNow.AddMinutes(-1); }
        else if (defect == "future") { node["completedAt"] = DateTimeOffset.UtcNow.AddMinutes(1); }
        else if (defect == "observed") { node["observedAt"] = DateTimeOffset.UtcNow.AddSeconds(-1); }
        else if (defect == "entry") { node["entry"]!["name"] = "another"; }
        else if (defect == "digest") { node["metadataDigest"] = new string('b', 64); }
        else if (defect == "entry-digest") { node["entry"]!["metadataDigest"] = new string('b', 64); }
        else if (defect == "entry-managed") { node["entry"]!["isManaged"] = true; }
        else if (defect == "archive") { node["originalBackupState"] = "present"; }
        else if (defect.StartsWith("missing-", StringComparison.Ordinal))
        {
            if (defect == "missing-entry-flag") { node["entry"]!.AsObject().Remove("canModify"); }
            else if (defect == "missing-action-reason") { node["actions"]![0]!.AsObject().Remove("reason"); }
            else { node.AsObject().Remove(defect[8..]); }
        }
        else if (defect == "actions") { node["actions"] = new JsonArray(); }
        else if (defect == "native-action") { node["actions"]![1]!["state"] = "ready"; }
        else if (defect is "nativeWriteReady" or "nativeWritePerformed" or "executionRequested") { node[defect] = true; }
        var response = new AgentResponse("request", true, JsonSerializer.SerializeToElement(node), Revision: defect == "envelope-revision" ? new string('b', 64) : Revision,
            OperationId: defect == "operation" ? AgentProtocol.NewOperationId() : null, Replayed: defect == "replayed");
        var error = Assert.Throws<AgentCommandException>(() => AgentStartupClient.ReadInspection(response, Entry.Id,
            StartupInspectionRequest.FromEntry(Entry), Revision));
        Assert.Equal("protocol_mismatch", error.Code);
    }
}
