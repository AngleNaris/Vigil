using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Windows.Startup;
using Xunit;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup", "prepared", "original_matches")] [InlineData("Common Startup", "prepared", "original_matches")]
    [InlineData("Startup", "native", "archive_matches")] [InlineData("Common Startup", "native", "archive_matches")]
    [InlineData("Startup", "ledger", "archive_matches")] [InlineData("Common Startup", "ledger", "archive_matches")]
    [InlineData("Startup", "committed", "archive_matches")] [InlineData("Common Startup", "committed", "archive_matches")]
    public async Task HistoryOperationEvidenceJoinsActualIssuanceAfterLogicalRestartWithoutChangingUnknown(string source, string phase, string nativeState)
    {
        using var store = HistoryTransactionStore(source); var fixture = await PrepareOperationEvidence(store, phase, source);
        var before = OperationEvidenceImages(store); var access = new ReconciliationAccess(store.History.Owned.Native);
        var reader = new StartupHistoryIntentReader(store.History.Owned.Root, () => store.DirectoryPath);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await reader.InspectOperationAsync(fixture.Receipt, access);
            Assert.Equal("observed", result.State); Assert.Equal("unknown", result.ReceiptSnapshotState); Assert.Equal(nativeState, result.Native.State);
            Assert.Equal(phase == "committed" ? "associated_committed" : "associated_candidate", result.Association.State);
            Assert.Equal(fixture.Candidate.Binding.RecordId, result.Association.RecordId); Assert.Equal(fixture.Reference.TransactionId, result.Association.TransactionId);
            Assert.Equal("observed_pending", result.Metadata.State); AssertOperationEvidenceNoAuthority(result); AssertOperationEvidenceImages(store, before);
            reader = new(store.History.Owned.Root, () => store.DirectoryPath); // Reader recreation, not an OS/process restart claim.
        }
        Assert.True(access.DirectoryCalls > 0); Assert.Equal(0, store.History.Owned.Native.WritableOpens);
        var disk = await new OperationJournal(store.History.Owned.Paths).GetAsync(fixture.Receipt.OperationId);
        Assert.Equal(OperationState.Unknown, disk!.State); Assert.Equal(fixture.Receipt, disk); AssertOperationEvidenceImages(store, before);
    }

    [Theory]
    [InlineData(OperationState.Running)] [InlineData(OperationState.Completed)]
    public async Task HistoryOperationEvidenceLabelsTheGivenReceiptSnapshotRatherThanInferringItsLiveState(OperationState state)
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed"); var before = OperationEvidenceImages(store);
        var result = await OperationEvidenceQuery(store, fixture.Receipt with { State = state }, new(store.History.Owned.Native));
        Assert.Equal("observed", result.State); Assert.Equal(state.ToString().ToLowerInvariant(), result.ReceiptSnapshotState);
        AssertOperationEvidenceNoAuthority(result); AssertOperationEvidenceImages(store, before);
    }

    [Theory]
    [InlineData("finalized")] [InlineData("never-prepared")]
    public async Task HistoryOperationEvidenceWithoutIntentNeverScansOrGuessesARecordOrCreatesCoordination(string phase)
    {
        using var store = HistoryTransactionStore(); OperationReceipt receipt;
        if (phase == "finalized") { receipt = (await PrepareOperationEvidence(store, phase)).Receipt; }
        else
        {
            // Only an input shape fixture; no claim of issuance or Journal identity.
            receipt = new(1, AgentProtocol.NewOperationId(), "startup-disable", "owned-selection", new string('a', 64), "fixture", OperationState.Unknown, DateTimeOffset.UtcNow);
        }
        var before = OperationEvidenceImages(store); var directories = Directory.GetDirectories(store.History.Owned.Root, "*", SearchOption.AllDirectories);
        var access = new ReconciliationAccess(store.History.Owned.Native); var result = await OperationEvidenceQuery(store, receipt, access);
        Assert.Equal("none", result.State); Assert.Equal("unobserved", result.Association.State); Assert.Equal("unobserved", result.Native.State);
        Assert.Null(result.Association.RecordId); Assert.Equal(0, access.DirectoryCalls); AssertOperationEvidenceNoAuthority(result); AssertOperationEvidenceImages(store, before);
        Assert.Equal(directories, Directory.GetDirectories(store.History.Owned.Root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("opid")] [InlineData("fingerprint")] [InlineData("intent-transaction")]
    [InlineData("link-missing")] [InlineData("link-invalid")] [InlineData("candidate-changed")]
    [InlineData("configuration-changed")] [InlineData("ledger-changed")] [InlineData("index-missing")]
    [InlineData("parent-missing")] [InlineData("coordination-missing")]
    public async Task HistoryOperationEvidenceRejectsMismatchOrMissingEvidenceBeforeResolvingNativeFolders(string mode)
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed"); var receipt = fixture.Receipt;
        var id = fixture.Candidate.Binding.RecordId; var path = Path.Combine(store.DirectoryPath, StartupHistoryOperationStoreReader.LinkName(id));
        if (mode == "opid") { receipt = receipt with { OperationId = AgentProtocol.NewOperationId() }; }
        else if (mode == "fingerprint") { receipt = receipt with { Fingerprint = new string('0', 64) }; }
        else if (mode == "intent-transaction" || mode == "candidate-changed")
        {
            var intent = StartupHistoryIntentFormat.Read(File.ReadAllBytes(IntentPath(store)));
            intent = mode == "intent-transaction" ? intent with { TransactionId = Guid.NewGuid().ToString("N") }
                : intent with { Candidate = intent.Candidate with { Nonce = Guid.NewGuid().ToString("N") } };
            File.WriteAllBytes(IntentPath(store), StartupHistoryIntentFormat.Write(intent));
        }
        else if (mode == "link-missing") { DeleteNativeHistoryFile(store, path); }
        else if (mode == "link-invalid") { var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; store.WritePrivateFile(path, bytes); }
        else if (mode == "configuration-changed") { File.AppendAllText(store.History.Owned.Paths.ConfigPath, "\n"); }
        else if (mode == "ledger-changed") { File.AppendAllText(store.History.Owned.Paths.StartupBackupPath, "\n"); }
        else if (mode == "index-missing") { DeleteNativeHistoryFile(store, store.IndexPath); }
        else if (mode == "parent-missing") { DeleteNativeHistoryFile(store, Path.Combine(store.DirectoryPath, StartupHistoryStoreReader.ProofName(id, "candidate"))); }
        else { DeleteNativeOwnedFile(store, Path.Combine(store.History.Owned.Root, RootWriteLease.FileName)); }
        var before = OperationEvidenceImages(store); var access = new ReconciliationAccess(store.History.Owned.Native);
        var result = await OperationEvidenceQuery(store, receipt, access);
        Assert.NotEqual("observed", result.State); Assert.Null(result.Association.RecordId); Assert.Null(result.Association.TransactionId);
        Assert.Equal(mode == "link-missing" ? "unlinked" : "unverified", result.Association.State); Assert.Equal(0, access.DirectoryCalls);
        AssertOperationEvidenceNoAuthority(result); AssertOperationEvidenceImages(store, before);
    }

    [Theory]
    [InlineData("schema")] [InlineData("operation-id")] [InlineData("fingerprint")] [InlineData("command")] [InlineData("name")] [InlineData("state")]
    public async Task HistoryOperationEvidenceRejectsMalformedReceiptSnapshotsWithoutReadingOrWritingJournal(string changed)
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed"); var receipt = fixture.Receipt;
        receipt = changed switch
        {
            "schema" => receipt with { SchemaVersion = 99 }, "operation-id" => receipt with { OperationId = "not-an-operation" },
            "fingerprint" => receipt with { Fingerprint = new string('A', 64) }, "command" => receipt with { Command = "startup-restore" },
            "name" => receipt with { Name = null }, _ => receipt with { State = (OperationState)99 },
        };
        var before = OperationEvidenceImages(store); var access = new ReconciliationAccess(store.History.Owned.Native); var result = await OperationEvidenceQuery(store, receipt, access);
        Assert.Equal("unverified", result.State); Assert.Equal("history_operation_receipt_invalid", result.Reason); Assert.Null(result.Association.RecordId);
        Assert.Equal(0, access.DirectoryCalls); AssertOperationEvidenceImages(store, before);
    }

    [Theory]
    [InlineData("original-changed")] [InlineData("both-present")] [InlineData("source-mapping")]
    public async Task HistoryOperationEvidenceDoesNotRetainAssociatedIdsWhenNativeVerificationFails(string mode)
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, mode == "original-changed" ? "prepared" : "committed");
        var source = Path.Combine(fixture.Candidate.Binding.Source.Directory.Path, fixture.Candidate.Binding.Name + ".lnk");
        var archive = Path.Combine(fixture.Candidate.Binding.Archive.Path, fixture.Candidate.Binding.RecordId + ".lnk");
        var access = new ReconciliationAccess(store.History.Owned.Native);
        if (mode == "original-changed") { File.WriteAllBytes(source, [3, 2, 1]); }
        else if (mode == "both-present") { File.Copy(archive, source); }
        else { access.Map = kind => store.History.Owned.Native.GetShortcutDirectory(kind == "Startup" ? "Common Startup" : "Startup"); }
        var before = OperationEvidenceImages(store); var result = await OperationEvidenceQuery(store, fixture.Receipt, access);
        Assert.Equal("unverified", result.State); Assert.Equal("unverified", result.Association.State); Assert.Null(result.Association.RecordId); Assert.Null(result.Association.TransactionId);
        Assert.Equal("unverified", result.Native.State); Assert.True(access.DirectoryCalls > 0); AssertOperationEvidenceNoAuthority(result); AssertOperationEvidenceImages(store, before);
    }

    [Fact]
    public async Task HistoryOperationEvidencePinsAllExistingRootSideAndNativeInputsUntilFinalSeal()
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed"); var before = OperationEvidenceImages(store); var seen = false;
        var files = new[] { IntentPath(store), store.History.Owned.Paths.ConfigPath, store.History.Owned.Paths.StartupBackupPath,
            Path.Combine(fixture.Candidate.Binding.Archive.Path, fixture.Candidate.Binding.RecordId + ".lnk") }.Concat(Directory.GetFiles(store.DirectoryPath).Where(path => path.EndsWith(".dpapi", StringComparison.Ordinal))).ToArray();
        var result = await OperationEvidenceQuery(store, fixture.Receipt, new(store.History.Owned.Native), checkpoint: step =>
        {
            if (step != "operation_evidence_sealing") { return; } seen = true;
            foreach (var path in files)
            { Assert.ThrowsAny<IOException>(() => { using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }); Assert.ThrowsAny<IOException>(() => File.Delete(path)); }
            Assert.ThrowsAny<IOException>(() => Directory.Move(store.DirectoryPath, store.DirectoryPath + ".not-moved"));
        });
        Assert.True(seen); Assert.Equal("observed", result.State); AssertOperationEvidenceImages(store, before);
        foreach (var path in files) { using var available = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
    }

    [Theory]
    [InlineData("operation_association_sealing")] [InlineData("operation_evidence_sealing")]
    public async Task HistoryOperationEvidenceLateAclFailureErasesAssociatedIdsAndRestoresTheExactDescriptor(string boundary)
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed");
        var path = Path.Combine(store.DirectoryPath, StartupHistoryOperationStoreReader.LinkName(fixture.Candidate.Binding.RecordId)); byte[]? original = null; var reached = false;
        try
        {
            var result = await OperationEvidenceQuery(store, fixture.Receipt, new(store.History.Owned.Native), checkpoint: step =>
            {
                if (step != boundary) { return; } reached = true; var file = new FileInfo(path); var acl = file.GetAccessControl();
                acl.SetSecurityDescriptorBinaryForm(acl.GetSecurityDescriptorBinaryForm()); file.SetAccessControl(acl); acl = file.GetAccessControl(); original = acl.GetSecurityDescriptorBinaryForm();
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.WriteData, AccessControlType.Allow)); file.SetAccessControl(acl);
            });
            Assert.True(reached); Assert.Equal("unverified", result.State); Assert.Null(result.Association.RecordId); Assert.Null(result.Association.TransactionId); AssertOperationEvidenceNoAuthority(result);
        }
        finally
        {
            if (original is not null) { var file = new FileInfo(path); var acl = file.GetAccessControl(); acl.SetSecurityDescriptorBinaryForm(original); file.SetAccessControl(acl); Assert.Equal(original, file.GetAccessControl().GetSecurityDescriptorBinaryForm()); }
        }
    }

    [Theory]
    [InlineData("operation_association_sealing")] [InlineData("native_slots_read")] [InlineData("operation_evidence_sealing")]
    public async Task HistoryOperationEvidenceCancellationReleasesAllReadPinsWithoutChangingAnyEvidence(string boundary)
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed"); var before = OperationEvidenceImages(store); var reached = false;
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OperationEvidenceQuery(store, fixture.Receipt, new(store.History.Owned.Native), cancellation.Token, step =>
        { if (step == boundary) { reached = true; cancellation.Cancel(); } }));
        Assert.True(reached); AssertOperationEvidenceImages(store, before);
        foreach (var path in Directory.GetFiles(store.DirectoryPath).Append(IntentPath(store))) { using var available = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
    }

    [Fact]
    public async Task HistoryOperationEvidenceActiveWriterIsBusyWithoutCreatingOrWaitingOnAnotherLease()
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed"); var before = OperationEvidenceImages(store); var access = new ReconciliationAccess(store.History.Owned.Native);
        await using var writer = await RootWriteLease.AcquireAsync(store.History.Owned.Paths); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await OperationEvidenceQuery(store, fixture.Receipt, access, deadline.Token);
        Assert.Equal("busy", result.State); Assert.Null(result.Association.RecordId); Assert.Equal(0, access.DirectoryCalls); AssertOperationEvidenceImages(store, before);
    }

    [Fact]
    public async Task HistoryOperationEvidenceLateStoreMappingChangeCannotReturnPreviouslyAssociatedIds()
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed"); var before = OperationEvidenceImages(store); var changed = false;
        var reader = new StartupHistoryIntentReader(store.History.Owned.Root, () => changed ? store.DirectoryPath + ".not-the-store" : store.DirectoryPath);
        var result = await reader.InspectOperationAsync(fixture.Receipt, new ReconciliationAccess(store.History.Owned.Native), checkpoint: step => { if (step == "operation_evidence_sealing") { changed = true; } });
        Assert.True(changed); Assert.Equal("unverified", result.State); Assert.Null(result.Association.RecordId); AssertOperationEvidenceImages(store, before);
    }

    [Fact]
    public async Task HistoryOperationEvidenceDoesNotOpenReceiptFilesOrCleanJournalOrphans()
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed");
        var receiptPath = Assert.Single(Directory.GetFiles(store.History.Owned.Root, "*.operation.json", SearchOption.AllDirectories));
        var orphan = Path.Combine(Path.GetDirectoryName(receiptPath)!, "r55-owned-query.tmp"); VerifyEvidenceOwnedPath(store.History.Owned, orphan); File.WriteAllBytes(orphan, [9, 8, 7]);
        var before = OperationEvidenceImages(store); StartupHistoryOperationEvidence result;
        using (var exclusiveReceipt = new FileStream(receiptPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { result = await OperationEvidenceQuery(store, fixture.Receipt, new(store.History.Owned.Native)); }
        Assert.Equal("observed", result.State); Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(orphan)); AssertOperationEvidenceImages(store, before);
    }

    [Fact]
    public async Task HistoryOperationEvidenceLateNativeScopeFailureCannotLeakPreviouslyAssociatedIds()
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed"); var before = OperationEvidenceImages(store); var late = false;
        var access = new ReconciliationAccess(store.History.Owned.Native)
        { Map = source => store.History.Owned.Native.GetShortcutDirectory(late ? (source == "Startup" ? "Common Startup" : "Startup") : source) };
        var result = await OperationEvidenceQuery(store, fixture.Receipt, access, checkpoint: step => { if (step == "operation_evidence_sealing") { late = true; } });
        Assert.True(late); Assert.Equal("unverified", result.State); Assert.Equal("unverified", result.Native.State); Assert.Null(result.Association.RecordId); Assert.Null(result.Association.TransactionId);
        AssertOperationEvidenceNoAuthority(result); AssertOperationEvidenceImages(store, before);
    }

    [Fact]
    public async Task HistoryOperationEvidenceDoesNotAdoptALinkAppearingAfterItsMissingLinkObservation()
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed");
        var path = Path.Combine(store.DirectoryPath, StartupHistoryOperationStoreReader.LinkName(fixture.Candidate.Binding.RecordId)); var issued = File.ReadAllBytes(path); DeleteNativeHistoryFile(store, path);
        var reads = 0; var restored = false; var access = new ReconciliationAccess(store.History.Owned.Native);
        var result = await OperationEvidenceQuery(store, fixture.Receipt, access, checkpoint: step =>
        {
            if (step == "history_read" && ++reads == 2) { store.WritePrivateFile(path, issued); restored = true; }
        });
        Assert.True(restored); Assert.Equal("unlinked", result.State); Assert.Null(result.Association.RecordId); Assert.Equal(0, access.DirectoryCalls);
        Assert.Equal(issued, File.ReadAllBytes(path)); AssertOperationEvidenceNoAuthority(result);
    }

    private static async Task<(OperationReceipt Receipt, StartupHistoryPayload Candidate, StartupHistoryOperationReference Reference)> PrepareOperationEvidence(HistoryStoreFixture store, string phase, string source = "Startup")
    {
        using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager, source); var request = await HistoryOperationRequest(store, entry);
        var journal = new OperationJournal(store.History.Owned.Paths); StartupHistoryPayload? candidate = null; StartupHistoryOperationReference? reference = null;
        await RunHistoryOwnerContext(journal, request, async (context, token) =>
        {
            await using var owner = await manager.PrepareHistoryDisableAsync(context, () => store.DirectoryPath, token); candidate = owner.Candidate; reference = owner.OperationReference;
            if (phase == "native")
            { var result = await owner.ArchiveAndPublishLedgerAsync(token, step => { if (step == "native_verified") { throw new IOException("Owned native outcome fixture."); } }); Assert.True(result.NativeWritePerformed); Assert.False(result.LedgerVerified); }
            else if (phase != "prepared") { Assert.True((await owner.ArchiveAndPublishLedgerAsync(token)).LedgerVerified); }
            if (phase is "committed" or "finalized") { Assert.True((await owner.PublishCommittedHistoryAsync(token)).Committed); }
            if (phase == "finalized") { Assert.True((await owner.FinalizeCommittedAsync(token)).TransactionFinalized); }
            // Deliberately synthetic unknown callback result, not a production adapter.
            return new(context.Request.Id, false, ErrorCode: "fixture_outcome_unknown");
        });
        var receipt = await new OperationJournal(store.History.Owned.Paths).GetAsync(request.OperationId!); Assert.NotNull(receipt); Assert.Equal(OperationState.Unknown, receipt.State);
        Assert.NotNull(candidate); Assert.NotNull(reference); return (receipt, candidate, reference);
    }
    private static Task<StartupHistoryOperationEvidence> OperationEvidenceQuery(HistoryStoreFixture store, OperationReceipt receipt, ReconciliationAccess access,
        CancellationToken token = default, Action<string>? checkpoint = null) =>
        new StartupHistoryIntentReader(store.History.Owned.Root, () => store.DirectoryPath).InspectOperationAsync(receipt, access, token, checkpoint);
    private static Dictionary<string, byte[]> OperationEvidenceImages(HistoryStoreFixture store) => DraftDataImages(store)
        .Concat(Directory.Exists(store.DirectoryPath) ? Directory.GetFiles(store.DirectoryPath).ToDictionary(path => path, File.ReadAllBytes) : new Dictionary<string, byte[]>()).ToDictionary(value => value.Key, value => value.Value);
    private static void AssertOperationEvidenceImages(HistoryStoreFixture store, Dictionary<string, byte[]> expected)
    { var actual = OperationEvidenceImages(store); Assert.Equal(expected.Keys.Order(), actual.Keys.Order()); foreach (var pair in expected) { Assert.Equal(pair.Value, actual[pair.Key]); } }
    private static void AssertOperationEvidenceNoAuthority(StartupHistoryOperationEvidence result)
    {
        Assert.False(result.NativeWriteReady); Assert.False(result.ExecutionRequested); Assert.False(result.TransactionFinalized);
        Assert.False(result.MayFinalize); Assert.False(result.ReceiptUpdated); Assert.False(result.ReplayRequested);
        AssertIntentQueryNoAuthority(result.Metadata); Assert.False(result.Native.NativeWriteReady); Assert.False(result.Native.ExecutionRequested); Assert.False(result.Native.MayFinalize);
    }
}
