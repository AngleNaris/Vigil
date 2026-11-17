using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

public sealed class StartupBackupLedgerTests
{
    internal static StartupBackupRecord Record => new() { Id = "record", Source = "HKCU", Name = "owned", Origin = "fixed metadata",
        RawCommand = "literal", RegistryKind = RegistryValueKind.ExpandString, DisabledAt = DateTimeOffset.UnixEpoch };

    [Theory]
    [InlineData("legacy-disabled")] [InlineData("legacy-restored")] [InlineData("disabled")]
    [InlineData("restored")] [InlineData("rebuilt")]
    public void LegacyAndExplicitDispositionsRoundtripWithoutInventingRestoration(string state)
    {
        var record = state switch
        {
            "disabled" => Record with { Disposition = "disabled" },
            "restored" => Record with { Disposition = "restored", RestoredAt = DateTimeOffset.UtcNow },
            "legacy-restored" => Record with { RestoredAt = DateTimeOffset.UtcNow },
            "rebuilt" => Record with { Source = "Startup", RegistryKind = null, Disposition = "rebuilt", RebuiltAt = DateTimeOffset.UtcNow,
                Rebuild = new(@"\\never-probe.invalid\untrusted-historical-summary.exe", "--literal", null) },
            _ => Record,
        };
        var bytes = StartupBackupLedger.Write([record]);
        var result = StartupBackupLedger.Read(bytes);
        Assert.False(result.InvalidRecords); Assert.False(result.Truncated);
        Assert.Equal(record, Assert.Single(result.Records));
        Assert.Equal(state.Replace("legacy-", "", StringComparison.Ordinal), result.Records[0].EffectiveDisposition);
        using var json = JsonDocument.Parse(bytes);
        Assert.False(json.RootElement[0].TryGetProperty("effectiveDisposition", out _));
        if (state.StartsWith("legacy-", StringComparison.Ordinal)) { Assert.False(json.RootElement[0].TryGetProperty("disposition", out _)); }
        if (state == "rebuilt") { Assert.Null(result.Records[0].RestoredAt); }
    }

    [Theory]
    [InlineData("state")] [InlineData("disabled-time")] [InlineData("restored-no-time")]
    [InlineData("disabled-rebuild")] [InlineData("restored-rebuild")] [InlineData("orphan-rebuild")]
    [InlineData("rebuilt-no-time")] [InlineData("rebuilt-no-summary")] [InlineData("rebuilt-registry")]
    [InlineData("rebuilt-restored")] [InlineData("empty-audit")] [InlineData("action")]
    [InlineData("source")] [InlineData("id")] [InlineData("name")] [InlineData("kind")]
    [InlineData("typed-null")] [InlineData("shortcut-kind")] [InlineData("large-field")]
    public void ContradictoryOrMalformedMetadataCannotBePublished(string defect)
    {
        var rebuilt = Record with { Source = "Startup", RegistryKind = null, Disposition = "rebuilt", RebuiltAt = DateTimeOffset.UtcNow, Rebuild = new("untrusted-summary") };
        var record = defect switch
        {
            "state" => Record with { Disposition = "ready" }, "disabled-time" => Record with { Disposition = "disabled", RestoredAt = DateTimeOffset.UtcNow },
            "restored-no-time" => Record with { Disposition = "restored" },
            "disabled-rebuild" => Record with { Disposition = "disabled", Rebuild = new("metadata") },
            "restored-rebuild" => Record with { RestoredAt = DateTimeOffset.UtcNow, RebuiltAt = DateTimeOffset.UtcNow },
            "orphan-rebuild" => Record with { RebuiltAt = DateTimeOffset.UtcNow },
            "rebuilt-no-time" => rebuilt with { RebuiltAt = null }, "rebuilt-no-summary" => rebuilt with { Rebuild = null },
            "rebuilt-registry" => rebuilt with { Source = "HKCU" }, "rebuilt-restored" => rebuilt with { RestoredAt = DateTimeOffset.UtcNow },
            "empty-audit" => rebuilt with { Rebuild = new(" ") }, "action" => Record with { Action = "restore" },
            "source" => Record with { Source = "arbitrary" }, "id" => Record with { Id = "" }, "name" => Record with { Name = "" },
            "kind" => Record with { RegistryKind = RegistryValueKind.Binary }, "typed-null" => Record with { RawCommand = null },
            "shortcut-kind" => Record with { Source = "Startup" }, _ => Record with { Target = new string('x', 32769) },
        };
        Assert.False(StartupBackupLedger.Valid(record));
        Assert.Throws<JsonException>(() => StartupBackupLedger.Write([record]));
        var read = StartupBackupLedger.Read(JsonSerializer.SerializeToUtf8Bytes(new[] { record }));
        Assert.True(read.InvalidRecords); Assert.Empty(read.Records);
    }

    [Theory]
    [InlineData("unknown")] [InlineData("ignored")] [InlineData("duplicate")]
    [InlineData("nested-duplicate")] [InlineData("null")]
    public void InvalidRowsAreVisibleAsPartialNotSilentlyDroppedByANativeWriter(string defect)
    {
        var valid = Encoding.UTF8.GetString(StartupBackupLedger.Write([Record])).Trim()[1..^1];
        string invalid;
        if (defect == "null") { invalid = "null"; }
        else if (defect == "duplicate") { invalid = valid.Replace("\"source\":", "\"SOURCE\": \"Startup\", \"source\":", StringComparison.Ordinal); }
        else if (defect == "nested-duplicate")
        {
            var node = JsonSerializer.SerializeToNode(Record with { Source = "Startup", RegistryKind = null, Disposition = "rebuilt",
                RebuiltAt = DateTimeOffset.UtcNow, Rebuild = new("metadata") })!;
            invalid = node.ToJsonString().Replace("\"target\":\"metadata\"", "\"target\":\"metadata\",\"TARGET\":\"other\"", StringComparison.Ordinal);
        }
        else { invalid = valid.Insert(valid.IndexOf('{') + 1, defect == "ignored" ? "\"EffectiveDisposition\":\"disabled\"," : "\"future\":true,"); }
        var read = StartupBackupLedger.Read(Encoding.UTF8.GetBytes("[" + invalid + "," + valid + "]"));
        Assert.True(read.InvalidRecords); Assert.Single(read.Records);
    }

    [Fact]
    public void MissingKindRemainsLegacyMetadataNotRecoveredTypeFidelity()
    {
        var legacy = Record with { RegistryKind = null, RawCommand = null };
        Assert.Equal(legacy, Assert.Single(StartupBackupLedger.Read(StartupBackupLedger.Write([legacy])).Records));
    }

    [Fact]
    public void DuplicateIdsAreCaseInsensitiveAndMakeTheWholeInputUnsafeForMutation()
    {
        var records = new[] { Record, Record with { Id = Record.Id.ToUpperInvariant(), Name = "other" } };
        var result = StartupBackupLedger.Read(JsonSerializer.SerializeToUtf8Bytes(records));
        Assert.True(result.InvalidRecords); Assert.Single(result.Records);
        Assert.Throws<JsonException>(() => StartupBackupLedger.Write(records));
    }

    [Theory]
    [InlineData("null")] [InlineData("{}")] [InlineData("0")] [InlineData("{bad")]
    public void NonArrayDocumentsAreNotReliableEmptyLedgers(string json) =>
        Assert.ThrowsAny<JsonException>(() => StartupBackupLedger.Read(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void ReadAndPublicationLimitsRemainBounded()
    {
        var records = Enumerable.Range(0, 513).Select(index => Record with { Id = index.ToString() }).ToArray();
        var read = StartupBackupLedger.Read(JsonSerializer.SerializeToUtf8Bytes(records));
        Assert.True(read.Truncated); Assert.Equal(512, read.Records.Count);
        Assert.Throws<JsonException>(() => StartupBackupLedger.Write(records));
        Assert.Throws<JsonException>(() => StartupBackupLedger.Read(new byte[StartupBackupLedger.MaximumBytes + 1]));
        var large = Enumerable.Range(0, 20).Select(index => Record with { Id = index.ToString(), RawCommand = new string('x', 32768) }).ToArray();
        Assert.Throws<JsonException>(() => StartupBackupLedger.Write(large));
    }
}
