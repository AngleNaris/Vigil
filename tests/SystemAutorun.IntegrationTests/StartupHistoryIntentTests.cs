using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;
using Xunit;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup")] [InlineData("Common Startup")]
    public async Task HistoryIntentTypedCodecPreservesTheExactR42DefaultSerializationWire(string source)
    {
        using var store = HistoryTransactionStore(source); using var manager = store.History.Owned.Manager();
        await using var owner = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        var raw = File.ReadAllBytes(IntentPath(store)); var typed = StartupHistoryIntentFormat.Read(raw);
        var oldWire = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, operation = "history-disable", transactionId = owner.TransactionId, record = store.History.Record, candidate = owner.Candidate,
            previousLedgerMissing = true, previousLedgerSha256 = (string?)null,
            ledgerSha256 = StartupHistoryIntentFormat.Hash(StartupBackupLedger.Write(new[] { store.History.Record })),
            configurationSha256 = StartupHistoryIntentFormat.Hash(File.ReadAllBytes(store.History.Owned.Paths.ConfigPath)),
        });
        Assert.Equal(oldWire, raw); Assert.Equal(raw, StartupHistoryIntentFormat.Write(typed)); Assert.Equal(owner.Candidate, typed.Candidate);
    }

    [Theory]
    [InlineData("schema")] [InlineData("operation")] [InlineData("transactionId")] [InlineData("unknown")]
    [InlineData("missing-bool")] [InlineData("missing-hash")] [InlineData("null-record")] [InlineData("missing-candidate")]
    [InlineData("previous-contradiction")] [InlineData("previous-null")] [InlineData("same-hash")] [InlineData("upper-hash")]
    [InlineData("record-id")] [InlineData("record-source")] [InlineData("record-name")] [InlineData("origin")] [InlineData("backup")]
    [InlineData("time")] [InlineData("digest")] [InlineData("phase")] [InlineData("sequence")] [InlineData("nested-case")]
    [InlineData("nested-unknown")] [InlineData("duplicate-outer")] [InlineData("duplicate-inner")]
    public void HistoryIntentCodecRejectsIncompleteAmbiguousOrMismatchedEditableMetadata(string mode)
    {
        using var store = HistoryTransactionStore(); var raw = StartupHistoryIntentFormat.Write(IntentFor(store)); var json = JsonNode.Parse(raw)!.AsObject();
        switch (mode)
        {
            case "schema": json["schemaVersion"] = 2; break;
            case "operation": json["operation"] = "disable"; break;
            case "transactionId": json["transactionId"] = "../not-an-id"; break;
            case "unknown": json["mayFinalize"] = true; break;
            case "missing-bool": json.Remove("previousLedgerMissing"); break;
            case "missing-hash": json.Remove("ledgerSha256"); break;
            case "null-record": json["record"] = null; break;
            case "missing-candidate": json.Remove("candidate"); break;
            case "previous-contradiction": json["previousLedgerSha256"] = new string('b', 64); break;
            case "previous-null": json["previousLedgerMissing"] = false; break;
            case "same-hash": json["previousLedgerMissing"] = false; json["previousLedgerSha256"] = json["ledgerSha256"]!.GetValue<string>(); break;
            case "upper-hash": json["configurationSha256"] = new string('A', 64); break;
            case "record-id": json["record"]!["id"] = Guid.NewGuid().ToString("N"); break;
            case "record-source": json["record"]!["source"] = "HKCU Run"; break;
            case "record-name": json["record"]!["name"] = "other"; break;
            case "origin": json["record"]!["origin"] = "C:\\other.lnk"; break;
            case "backup": json["record"]!["backup_path"] = "C:\\other.lnk"; break;
            case "time": json["record"]!["disabled_at"] = store.History.Record.DisabledAt.AddSeconds(1); break;
            case "digest": json["candidate"]!["Binding"]!["RecordDigest"] = new string('c', 64); break;
            case "phase": json["candidate"]!["Phase"] = "committed"; json["candidate"]!["CommittedAt"] = store.History.Payload.CandidateAt; break;
            case "sequence": json["candidate"]!["Sequence"] = 2; break;
            case "nested-case": json["candidate"]!["phase"] = "candidate"; json["candidate"]!.AsObject().Remove("Phase"); break;
            case "nested-unknown": json["candidate"]!["Binding"]!["authorized"] = true; break;
        }
        var text = json.ToJsonString();
        if (mode == "duplicate-outer") { text = text.Insert(1, "\"schemaVersion\":1,"); }
        if (mode == "duplicate-inner") { text = text.Replace("\"Sequence\":1", "\"Sequence\":1,\"Sequence\":1", StringComparison.Ordinal); }
        Assert.Throws<JsonException>(() => StartupHistoryIntentFormat.Read(Encoding.UTF8.GetBytes(text)));
    }

    [Theory]
    [InlineData("empty")] [InlineData("over")] [InlineData("array")] [InlineData("null")] [InlineData("deep")]
    public void HistoryIntentCodecBoundsTheWholeEnvelopeBeforeAcceptingAMetadataTuple(string mode)
    {
        byte[] bytes = mode switch { "empty" => [], "over" => new byte[StartupHistoryIntentFormat.MaximumBytes + 1], "array" => "[]"u8.ToArray(),
            "null" => "null"u8.ToArray(), _ => Encoding.UTF8.GetBytes(new string('[', 20) + "0" + new string(']', 20)) };
        Assert.Throws<JsonException>(() => StartupHistoryIntentFormat.Read(bytes));
    }

    [Theory]
    [InlineData("prepared", "declared_empty", "candidate")]
    [InlineData("native-unknown", "declared_empty", "candidate")]
    [InlineData("ledger", "proposed_image", "candidate")]
    [InlineData("committed", "proposed_image", "protected_committed")]
    public async Task HistoryIntentQueryReportsOnlyImagesAndProtectedPhaseAfterTheLiveOwnerHasReleased(string phase, string ledger, string history)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var owner = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        if (phase == "native-unknown") { Assert.False((await owner.ArchiveAndPublishLedgerAsync(checkpoint: stage => { if (stage == "native_verified") { throw new IOException("Unknown native."); } })).LedgerVerified); }
        else if (phase != "prepared") { Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified); }
        if (phase == "committed") { Assert.True((await owner.PublishCommittedHistoryAsync()).Committed); }
        await owner.DisposeAsync(); var before = IntentDataImages(store);
        var result = new StartupHistoryIntentReader(store.History.Owned.Root, () => store.DirectoryPath).Inspect(checkpoint: stage =>
        {
            if (stage != "images_read") { return; }
            Assert.ThrowsAny<IOException>(() => File.Open(IntentPath(store), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
            Assert.ThrowsAny<IOException>(() => File.Open(store.History.Owned.Paths.ConfigPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
        });
        Assert.Equal("observed_pending", result.State); Assert.Equal(owner.TransactionId, result.TransactionId); Assert.Equal(store.History.Record.Id, result.RecordId);
        Assert.Equal("matched_image", result.ConfigurationImage); Assert.Equal(ledger, result.LedgerImage); Assert.Equal(history, result.HistoryState);
        AssertIntentQueryNoAuthority(result); AssertIntentImagesUnchanged(before); Assert.True(File.Exists(IntentPath(store)));
        Assert.DoesNotContain(StartupMutationOperation.IntentCleanup, store.History.Owned.Operations.Calls);
    }

    [Fact]
    public async Task HistoryIntentQueryReturnsBusyBeforeReadingAnyLiveOwnersMetadataOrProtectedState()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var owner = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        var result = new StartupHistoryIntentReader(store.History.Owned.Root, () => throw new InvalidOperationException("No side lookup while busy.")).Inspect();
        Assert.Equal("busy", result.State); Assert.Null(result.TransactionId); AssertIntentQueryNoAuthority(result);
        Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified); Assert.True((await owner.PublishCommittedHistoryAsync()).Committed);
    }

    [Fact]
    public async Task HistoryIntentQueryAlsoReturnsBusyForAnIndependentConfigurationWriter()
    {
        using var store = HistoryTransactionStore(); await using var writer = await RootWriteLease.AcquireAsync(store.History.Owned.Paths);
        Assert.Equal("busy", new StartupHistoryIntentReader(store.History.Owned.Root, () => throw new InvalidOperationException("No lookup.")).Inspect().State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HistoryIntentAbsentQueryCreatesNoLocksOrHistoryAndDetectsALateIntent(bool late)
    {
        using var store = HistoryTransactionStore(); var before = Directory.GetFiles(store.History.Owned.Root).Order().ToArray();
        var result = new StartupHistoryIntentReader(store.History.Owned.Root, () => throw new InvalidOperationException("No lookup.")).Inspect(checkpoint: stage =>
        { if (late && stage == "intent_absent") { File.WriteAllBytes(IntentPath(store), "{}"u8.ToArray()); } });
        Assert.Equal(late ? "unavailable" : "none", result.State); Assert.False(Directory.Exists(store.DirectoryPath));
        Assert.False(File.Exists(Path.Combine(store.History.Owned.Root, RootWriteLease.FileName))); Assert.False(File.Exists(Path.Combine(store.History.Owned.Root, StartupMutationLease.FileName)));
        if (!late) { Assert.Equal(before, Directory.GetFiles(store.History.Owned.Root).Order().ToArray()); } AssertIntentQueryNoAuthority(result);
    }

    [Theory]
    [InlineData("legacy")] [InlineData("malformed")] [InlineData("newer")]
    public void HistoryIntentQueryPreservesUnsupportedOrInvalidPendingFilesWithoutInventingAnIdentity(string mode)
    {
        using var store = HistoryTransactionStore(); byte[] bytes = mode switch
        { "legacy" => JsonSerializer.SerializeToUtf8Bytes(new { operation = "disable", record = store.History.Record }), "malformed" => "{"u8.ToArray(),
            _ => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(StartupHistoryIntentFormat.Write(IntentFor(store))).Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal)) };
        File.WriteAllBytes(IntentPath(store), bytes);
        var result = new StartupHistoryIntentReader(store.History.Owned.Root, () => throw new InvalidOperationException("No lookup.")).Inspect();
        Assert.Equal("unverified_pending", result.State); Assert.Null(result.RecordId); Assert.Equal(bytes, File.ReadAllBytes(IntentPath(store))); AssertIntentQueryNoAuthority(result);
    }

    [Theory]
    [InlineData("absent-previous", "missing_after_declared_previous")]
    [InlineData("old-image", "previous_image")] [InlineData("old-contains-draft", "unverified")]
    [InlineData("bad-json", "unverified")] [InlineData("noncanonical", "unverified")]
    [InlineData("extra-declared-empty", "unverified")] [InlineData("not-last", "unverified")]
    [InlineData("coherent-not-proven", "proposed_image")] [InlineData("different", "changed")]
    public void HistoryIntentLedgerComparisonsRejectFalseImagesAndNeverAuthenticateSelfDeclaredHashes(string mode, string expected)
    {
        using var store = HistoryTransactionStore(); var intent = IntentFor(store); var record = store.History.Record;
        var other = record with { Id = Guid.NewGuid().ToString("N"), Name = "old-unrelated" }; byte[]? bytes;
        switch (mode)
        {
            case "absent-previous": bytes = null; intent = intent with { PreviousLedgerMissing = false, PreviousLedgerSha256 = StartupHistoryIntentFormat.Hash("[]"u8) }; break;
            case "old-image": bytes = "[ ]"u8.ToArray(); intent = intent with { PreviousLedgerMissing = false, PreviousLedgerSha256 = StartupHistoryIntentFormat.Hash(bytes) }; break;
            case "old-contains-draft": bytes = StartupBackupLedger.Write(new[] { record }); intent = intent with { PreviousLedgerMissing = false,
                PreviousLedgerSha256 = StartupHistoryIntentFormat.Hash(bytes), LedgerSha256 = new string('b', 64) }; break;
            case "bad-json": bytes = "not-json"u8.ToArray(); intent = intent with { LedgerSha256 = StartupHistoryIntentFormat.Hash(bytes) }; break;
            case "noncanonical": bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { record }); intent = intent with { LedgerSha256 = StartupHistoryIntentFormat.Hash(bytes) }; break;
            case "not-last": bytes = StartupBackupLedger.Write(new[] { record, other }); intent = intent with { PreviousLedgerMissing = false,
                PreviousLedgerSha256 = new string('b', 64), LedgerSha256 = StartupHistoryIntentFormat.Hash(bytes) }; break;
            case "extra-declared-empty": bytes = StartupBackupLedger.Write(new[] { other, record }); intent = intent with { LedgerSha256 = StartupHistoryIntentFormat.Hash(bytes) }; break;
            case "coherent-not-proven": bytes = StartupBackupLedger.Write(new[] { other, record }); intent = intent with { PreviousLedgerMissing = false,
                PreviousLedgerSha256 = new string('b', 64), LedgerSha256 = StartupHistoryIntentFormat.Hash(bytes) }; break;
            default: bytes = "[]"u8.ToArray(); break;
        }
        WriteIntentForQuery(store, intent); if (bytes is not null) { File.WriteAllBytes(store.History.Owned.Paths.StartupBackupPath, bytes); }
        var before = File.ReadAllBytes(IntentPath(store)); var result = new StartupHistoryIntentReader(store.History.Owned.Root, () => store.DirectoryPath).Inspect();
        Assert.Equal("observed_pending", result.State); Assert.Equal(expected, result.LedgerImage); Assert.Equal(before, File.ReadAllBytes(IntentPath(store))); AssertIntentQueryNoAuthority(result);
        Assert.False(Directory.Exists(store.DirectoryPath));
    }

    [Theory]
    [InlineData("nonce")] [InlineData("original")]
    public async Task HistoryIntentCannotBorrowAProtectedCandidateSharingOnlyItsEditableContext(string field)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var owner = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        await owner.DisposeAsync(); var intent = StartupHistoryIntentFormat.Read(File.ReadAllBytes(IntentPath(store)));
        var changed = field == "nonce" ? intent.Candidate with { Nonce = Guid.NewGuid().ToString("N") }
            : intent.Candidate with { Binding = intent.Candidate.Binding with { Original = intent.Candidate.Binding.Original with { Sha256 = new string('d', 64) } } };
        File.WriteAllBytes(IntentPath(store), StartupHistoryIntentFormat.Write(intent with { Candidate = changed }));
        var result = new StartupHistoryIntentReader(store.History.Owned.Root, () => store.DirectoryPath).Inspect();
        Assert.Equal("unverified", result.HistoryState); Assert.Equal("history_store_intent_changed", result.HistoryReason); AssertIntentQueryNoAuthority(result);
    }

    [Fact]
    public async Task HistoryCommittedIntentQueryRequiresItsExactPreservedCandidateForCorrelationNotForIssuingPermissions()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var owner = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified); Assert.True((await owner.PublishCommittedHistoryAsync()).Committed); await owner.DisposeAsync();
        Assert.Equal(Path.GetFullPath(store.ProofPath), store.ProofPath); Assert.Equal(store.DirectoryPath, Path.GetDirectoryName(store.ProofPath)); File.Delete(store.ProofPath);
        Assert.Equal("protected_committed", store.Reader().Inspect(store.Context).State);
        var result = new StartupHistoryIntentReader(store.History.Owned.Root, () => store.DirectoryPath).Inspect();
        Assert.Equal("unverified", result.HistoryState); Assert.False(File.Exists(store.ProofPath)); Assert.True(File.Exists(IntentPath(store))); AssertIntentQueryNoAuthority(result);
    }

    [Fact]
    public void HistoryIntentQueryDoesNotCreateMissingCoordinationOrSideStateForATypedPendingFile()
    {
        using var store = HistoryTransactionStore(); File.WriteAllBytes(IntentPath(store), StartupHistoryIntentFormat.Write(IntentFor(store)));
        var result = new StartupHistoryIntentReader(store.History.Owned.Root, () => throw new InvalidOperationException("No lookup.")).Inspect();
        Assert.Equal("history_query_coordination_missing", result.Reason); Assert.False(Directory.Exists(store.DirectoryPath)); AssertIntentQueryNoAuthority(result);
    }

    [Theory]
    [InlineData("images_read")] [InlineData("history_read")]
    public void HistoryIntentQueryCancellationReleasesEveryReadPinAndPreservesTheIntent(string boundary)
    {
        using var store = HistoryTransactionStore(); WriteIntentForQuery(store, IntentFor(store)); using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => new StartupHistoryIntentReader(store.History.Owned.Root, () => store.DirectoryPath).Inspect(cancellation.Token,
            stage => { if (stage == boundary) { cancellation.Cancel(); } }));
        using var exclusive = File.Open(IntentPath(store), FileMode.Open, FileAccess.ReadWrite, FileShare.None); Assert.True(exclusive.Length > 0);
    }

    [Fact]
    public void HistoryIntentQueryRejectsAnAlreadyOpenWriteHandleEvenWhenThatWriterSharesEverything()
    {
        using var store = HistoryTransactionStore(); WriteIntentForQuery(store, IntentFor(store));
        using var writer = File.Open(store.History.Owned.Paths.ConfigPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var result = new StartupHistoryIntentReader(store.History.Owned.Root, () => throw new InvalidOperationException("No side read.")).Inspect();
        Assert.Equal("busy", result.State); AssertIntentQueryNoAuthority(result);
    }

    [Theory]
    [InlineData("root")] [InlineData("actor")]
    public void HistoryIntentQueryRejectsForeignRootOrActorBeforeAnyIndependentStoreLookup(string field)
    {
        using var store = HistoryTransactionStore(); var intent = IntentFor(store); var binding = intent.Candidate.Binding;
        var sid = binding.ActorSid == "S-1-5-18" ? "S-1-5-19" : "S-1-5-18";
        binding = field == "root" ? binding with { Root = binding.Root with { Path = Path.Combine(store.History.Owned.Root, "different") } }
            : binding with { ActorSid = sid, Source = binding.Source with { UserSid = sid } };
        WriteIntentForQuery(store, intent with { Candidate = intent.Candidate with { Binding = binding } });
        var result = new StartupHistoryIntentReader(store.History.Owned.Root, () => throw new InvalidOperationException("No lookup.")).Inspect();
        Assert.Equal("unverified_pending", result.State); Assert.Equal("history_intent_context_changed", result.Reason); AssertIntentQueryNoAuthority(result);
    }

    [Theory]
    [InlineData("changed", "changed")] [InlineData("missing", "missing")] [InlineData("self-declared", "matched_image")]
    public void HistoryIntentConfigurationImageMatchIsNotCurrentArchiveConfigurationOrProvenance(string mode, string expected)
    {
        using var store = HistoryTransactionStore(); var intent = IntentFor(store); var path = store.History.Owned.Paths.ConfigPath;
        Assert.Equal(Path.GetFullPath(path), path); Assert.Equal(store.History.Owned.Root, Path.GetDirectoryName(path));
        if (mode == "missing") { File.Delete(path); }
        else
        {
            File.WriteAllText(path, "version: 1\nsettings:\n  backup_dir: completely-different-context\ntasks: []\n");
            if (mode == "self-declared") { intent = intent with { ConfigurationSha256 = StartupHistoryIntentFormat.Hash(File.ReadAllBytes(path)) }; }
        }
        WriteIntentForQuery(store, intent); var before = File.ReadAllBytes(IntentPath(store));
        var result = new StartupHistoryIntentReader(store.History.Owned.Root, () => store.DirectoryPath).Inspect();
        Assert.Equal("observed_pending", result.State); Assert.Equal(expected, result.ConfigurationImage); Assert.Equal(before, File.ReadAllBytes(IntentPath(store)));
        AssertIntentQueryNoAuthority(result); Assert.False(Directory.Exists(store.DirectoryPath));
    }

    private static StartupHistoryDisableIntent IntentFor(HistoryStoreFixture store) => new(1, "history-disable", Guid.CreateVersion7().ToString("N"),
        store.History.Record, store.History.Payload, true, null, StartupHistoryIntentFormat.Hash(StartupBackupLedger.Write(new[] { store.History.Record })),
        StartupHistoryIntentFormat.Hash(File.ReadAllBytes(store.History.Owned.Paths.ConfigPath)));
    private static string IntentPath(HistoryStoreFixture store) => store.History.Owned.Paths.StartupBackupPath + ".transaction";
    private static void WriteIntentForQuery(HistoryStoreFixture store, StartupHistoryDisableIntent intent)
    {
        File.WriteAllBytes(Path.Combine(store.History.Owned.Root, StartupMutationLease.FileName), []);
        File.WriteAllBytes(Path.Combine(store.History.Owned.Root, RootWriteLease.FileName), []);
        File.WriteAllBytes(IntentPath(store), StartupHistoryIntentFormat.Write(intent));
    }
    private static Dictionary<string, byte[]> IntentDataImages(HistoryStoreFixture store) => new[] { store.History.Owned.Paths.ConfigPath, IntentPath(store),
        store.History.Owned.Paths.StartupBackupPath, store.ProofPath, store.IndexPath, CommittedHistoryPath(store) }
        .Where(File.Exists).ToDictionary(path => path, File.ReadAllBytes);
    private static void AssertIntentImagesUnchanged(Dictionary<string, byte[]> images)
    { foreach (var image in images) { Assert.Equal(image.Value, File.ReadAllBytes(image.Key)); } }
    private static void AssertIntentQueryNoAuthority(StartupHistoryIntentObservation result)
    {
        Assert.Equal("unobserved", result.NativeState); Assert.False(result.NativeWriteReady); Assert.False(result.ExecutionRequested);
        Assert.False(result.TransactionFinalized); Assert.False(result.MayFinalize);
    }
}
