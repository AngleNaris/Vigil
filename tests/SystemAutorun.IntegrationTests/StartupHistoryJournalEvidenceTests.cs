using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Windows.Startup;
using Xunit;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup", "prepared")] [InlineData("Common Startup", "prepared")]
    [InlineData("Startup", "native")] [InlineData("Common Startup", "native")]
    [InlineData("Startup", "committed")] [InlineData("Common Startup", "committed")]
    [InlineData("Startup", "finalized")] [InlineData("Common Startup", "finalized")]
    public async Task HistoryJournalEvidenceReadsActualReceiptAndIssuerWithoutWritingOrUpgradingUnknown(string source, string phase)
    {
        using var store = HistoryTransactionStore(source); var fixture = await PrepareOperationEvidence(store, phase, source); var paths = JournalEvidencePaths(store, fixture.Receipt.OperationId);
        // Exact cleaner-recognized names, not arbitrary *.tmp files which GetAsync ignores.
        var receiptOrphan = paths.Receipt + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var metadataOrphan = paths.Metadata + "." + Guid.NewGuid().ToString("N") + ".tmp";
        VerifyEvidenceOwnedPath(store.History.Owned, receiptOrphan); VerifyEvidenceOwnedPath(store.History.Owned, metadataOrphan);
        File.WriteAllBytes(receiptOrphan, [8, 7, 6]); File.WriteAllBytes(metadataOrphan, [5, 4, 3]);
        var before = OperationEvidenceImages(store); var access = new ReconciliationAccess(store.History.Owned.Native);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await JournalEvidenceQuery(store, fixture.Receipt.OperationId, access);
            Assert.Equal("observed", result.JournalState); Assert.Equal("unknown", result.PersistedReceiptState); Assert.Equal("unknown", result.ObservedReceiptState);
            Assert.NotNull(result.Evidence); Assert.Equal(phase == "finalized" ? "none" : "observed", result.Evidence.State);
            Assert.Equal(phase == "finalized" ? null : fixture.Candidate.Binding.RecordId, result.Evidence.Association.RecordId);
            AssertJournalEvidenceNoAuthority(result); AssertOperationEvidenceImages(store, before);
        }
        Assert.Equal(new byte[] { 8, 7, 6 }, File.ReadAllBytes(receiptOrphan)); Assert.Equal(new byte[] { 5, 4, 3 }, File.ReadAllBytes(metadataOrphan));
        if (phase == "finalized") { Assert.Equal(0, access.DirectoryCalls); Assert.Equal("unobserved", (await JournalEvidenceQuery(store, fixture.Receipt.OperationId, access)).Evidence!.Association.State); }
    }

    [Fact]
    public async Task HistoryJournalEvidencePersistedRunningIsConservativelyUnknownEvenForAnActiveCallback()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var request = await HistoryOperationRequest(store, await DraftEntry(store, manager));
        var journal = new OperationJournal(store.History.Owned.Paths); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = RunHistoryOwnerContext(journal, request, async (context, token) =>
        { entered.SetResult(); await finish.Task.WaitAsync(token); return new(context.Request.Id, true); });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var before = OperationEvidenceImages(store); var access = new ReconciliationAccess(store.History.Owned.Native);
            var result = await JournalEvidenceQuery(store, request.OperationId!, access);
            Assert.Equal("observed", result.JournalState); Assert.Equal("running", result.PersistedReceiptState); Assert.Equal("unknown", result.ObservedReceiptState);
            Assert.Equal("history_journal_running_unconfirmed", result.Reason); Assert.Equal("none", result.Evidence!.State); Assert.Equal(0, access.DirectoryCalls);
            Assert.False(workflow.IsCompleted); AssertJournalEvidenceNoAuthority(result); AssertOperationEvidenceImages(store, before);
        }
        finally { finish.TrySetResult(); await workflow; }
    }

    [Fact]
    public async Task HistoryJournalEvidenceCompletedReceiptIsOnlyItsPersistedStateNotAFinalizationProof()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var request = await HistoryOperationRequest(store, await DraftEntry(store, manager));
        var journal = new OperationJournal(store.History.Owned.Paths);
        await RunHistoryOwnerContext(journal, request, async (context, token) =>
        {
            await using var owner = await manager.PrepareHistoryDisableAsync(context, () => store.DirectoryPath, token);
            // A synthetic successful callback leaves only prepared evidence. Completed
            // must not turn this into committed/finalized/native-write authority.
            return new(context.Request.Id, true);
        });
        var before = OperationEvidenceImages(store); var result = await JournalEvidenceQuery(store, request.OperationId!, new(store.History.Owned.Native));
        Assert.Equal("completed", result.PersistedReceiptState); Assert.Equal("completed", result.ObservedReceiptState);
        Assert.Equal("associated_candidate", result.Evidence!.Association.State); Assert.Equal("original_matches", result.Evidence.Native.State);
        AssertJournalEvidenceNoAuthority(result); AssertOperationEvidenceImages(store, before);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HistoryJournalEvidenceMissingSelectedReceiptDoesNotScanOtherReceiptsOrCreateAnyPaths(bool otherExists)
    {
        using var store = HistoryTransactionStore(); if (otherExists) { await PrepareOperationEvidence(store, "committed"); }
        var before = OperationEvidenceImages(store); var folders = Directory.GetDirectories(store.History.Owned.Root, "*", SearchOption.AllDirectories);
        var access = new ReconciliationAccess(store.History.Owned.Native); var result = await JournalEvidenceQuery(store, AgentProtocol.NewOperationId(), access);
        Assert.Equal("missing", result.JournalState); Assert.Null(result.Evidence); Assert.Equal(0, access.DirectoryCalls);
        AssertJournalEvidenceNoAuthority(result); AssertOperationEvidenceImages(store, before); Assert.Equal(folders, Directory.GetDirectories(store.History.Owned.Root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("identity")] [InlineData("fingerprint")] [InlineData("schema")] [InlineData("state")]
    [InlineData("duplicate")] [InlineData("unknown-field")] [InlineData("cased-field")] [InlineData("missing-field")]
    [InlineData("command")] [InlineData("owner")] [InlineData("oversize")] [InlineData("metadata-invalid")]
    [InlineData("metadata-missing")] [InlineData("coordination-missing")]
    [InlineData("completed-without-result")] [InlineData("running-with-result")]
    public async Task HistoryJournalEvidenceRejectsMalformedOrMissingInputsBeforeAnyNativeObservation(string mode)
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed"); var paths = JournalEvidencePaths(store, fixture.Receipt.OperationId);
        var json = JsonNode.Parse(File.ReadAllBytes(paths.Receipt))!.AsObject();
        if (mode == "identity") { json["operationId"] = AgentProtocol.NewOperationId(); }
        else if (mode == "fingerprint") { json["fingerprint"] = new string('A', 64); }
        else if (mode == "schema") { json["schemaVersion"] = 2; }
        else if (mode == "state") { json["state"] = 2; }
        else if (mode == "command") { json["command"] = "startup-restore"; }
        else if (mode == "owner") { json["ownerInstanceId"] = ""; }
        else if (mode == "unknown-field") { json["unexpected"] = true; }
        else if (mode == "cased-field") { json["Fingerprint"] = json["fingerprint"]!.DeepClone(); json.Remove("fingerprint"); }
        else if (mode == "missing-field") { json.Remove("acceptedAt"); }
        else if (mode == "completed-without-result") { json["state"] = "completed"; json["completedAt"] = null; json["response"] = null; json["resultOmitted"] = false; }
        else if (mode == "running-with-result") { json["state"] = "running"; }
        if (mode == "duplicate") { var text = Encoding.UTF8.GetString(File.ReadAllBytes(paths.Receipt)); File.WriteAllText(paths.Receipt, text[..^1] + ",\"state\":\"completed\"}"); }
        else if (mode == "oversize") { File.WriteAllBytes(paths.Receipt, new byte[256 * 1024 + 1]); }
        else if (mode == "metadata-invalid") { File.WriteAllText(paths.Metadata, "{\"schemaVersion\":2,\"minimumTimestamp\":0}"); }
        else if (mode == "metadata-missing") { DeleteNativeOwnedFile(store, paths.Metadata); }
        else if (mode == "coordination-missing") { DeleteNativeOwnedFile(store, Path.Combine(store.History.Owned.Root, RootWriteLease.FileName)); }
        else { File.WriteAllText(paths.Receipt, json.ToJsonString()); }
        var before = OperationEvidenceImages(store); var access = new ReconciliationAccess(store.History.Owned.Native); var result = await JournalEvidenceQuery(store, fixture.Receipt.OperationId, access);
        Assert.NotEqual("observed", result.JournalState); Assert.Null(result.Evidence); Assert.Equal(0, access.DirectoryCalls);
        AssertJournalEvidenceNoAuthority(result); AssertOperationEvidenceImages(store, before);
    }

    [Fact]
    public async Task HistoryJournalEvidenceKeepsReceiptAndMetadataPinnedWhileR55AndOuterSealRun()
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed"); var paths = JournalEvidencePaths(store, fixture.Receipt.OperationId);
        var before = OperationEvidenceImages(store); var reached = new HashSet<string>();
        var result = await JournalEvidenceQuery(store, fixture.Receipt.OperationId, new(store.History.Owned.Native), checkpoint: step =>
        {
            if (step is not ("operation_evidence_sealing" or "journal_evidence_sealing")) { return; } reached.Add(step);
            foreach (var path in new[] { paths.Receipt, paths.Metadata })
            {
                Assert.ThrowsAny<IOException>(() => { using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
                Assert.ThrowsAny<IOException>(() => File.Delete(path));
            }
        });
        Assert.Equal(2, reached.Count); Assert.Equal("observed", result.Evidence!.State); AssertOperationEvidenceImages(store, before);
        foreach (var path in new[] { paths.Receipt, paths.Metadata }) { using var available = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
    }

    [Theory]
    [InlineData("journal_images_read")] [InlineData("journal_snapshot_read")] [InlineData("journal_evidence_sealing")]
    public async Task HistoryJournalEvidenceCancellationReleasesReceiptMetadataAndExistingCoordinationPins(string boundary)
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed"); var paths = JournalEvidencePaths(store, fixture.Receipt.OperationId); var before = OperationEvidenceImages(store);
        using var cancellation = new CancellationTokenSource(); var reached = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JournalEvidenceQuery(store, fixture.Receipt.OperationId, new(store.History.Owned.Native), cancellation.Token, step =>
        { if (step == boundary) { reached = true; cancellation.Cancel(); } }));
        Assert.True(reached); AssertOperationEvidenceImages(store, before);
        foreach (var path in new[] { paths.Receipt, paths.Metadata, Path.Combine(store.History.Owned.Root, RootWriteLease.FileName) })
        { using var available = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
    }

    [Fact]
    public async Task HistoryJournalEvidenceWriterAdmissionIsBusyRatherThanWaitingOrCleaning()
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed"); var before = OperationEvidenceImages(store); var access = new ReconciliationAccess(store.History.Owned.Native);
        await using var writer = await RootWriteLease.AcquireAsync(store.History.Owned.Paths); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await JournalEvidenceQuery(store, fixture.Receipt.OperationId, access, deadline.Token);
        Assert.Equal("busy", result.JournalState); Assert.Null(result.Evidence); Assert.Equal(0, access.DirectoryCalls); AssertOperationEvidenceImages(store, before);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HistoryJournalEvidenceInputIdIsCanonicalizedOrRejectedWithoutPathFallback(bool upperCase)
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed"); var before = OperationEvidenceImages(store); var access = new ReconciliationAccess(store.History.Owned.Native);
        var result = await JournalEvidenceQuery(store, upperCase ? fixture.Receipt.OperationId.ToUpperInvariant() : "../metadata", access);
        Assert.Equal(upperCase ? "observed" : "unverified", result.JournalState);
        if (!upperCase) { Assert.Null(result.Evidence); Assert.Equal(0, access.DirectoryCalls); }
        AssertJournalEvidenceNoAuthority(result); AssertOperationEvidenceImages(store, before);
    }

    [Fact]
    public async Task HistoryJournalEvidenceWellShapedButEditedFingerprintDoesNotAuthenticateAssociation()
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed"); var paths = JournalEvidencePaths(store, fixture.Receipt.OperationId);
        var json = JsonNode.Parse(File.ReadAllBytes(paths.Receipt))!.AsObject(); json["fingerprint"] = new string('0', 64); File.WriteAllText(paths.Receipt, json.ToJsonString());
        var before = OperationEvidenceImages(store); var access = new ReconciliationAccess(store.History.Owned.Native); var result = await JournalEvidenceQuery(store, fixture.Receipt.OperationId, access);
        Assert.Equal("observed", result.JournalState); Assert.Equal("unverified", result.Evidence!.State); Assert.Null(result.Evidence.Association.RecordId);
        Assert.Equal(0, access.DirectoryCalls); AssertJournalEvidenceNoAuthority(result); AssertOperationEvidenceImages(store, before);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HistoryJournalEvidenceMissingInputRecheckRejectsNewLeafOrParentReparseBeforeOpeningItsTarget(bool parent)
    {
        using var store = HistoryTransactionStore(); var id = AgentProtocol.NewOperationId(); var paths = JournalEvidencePaths(store, id);
        var targetDirectory = Path.Combine(store.History.Owned.Root, "r56-redirect-target"); Directory.CreateDirectory(targetDirectory);
        var target = Path.Combine(targetDirectory, "metadata.json"); File.WriteAllBytes(target, [8, 9, 10]); var seen = false; var operationDirectory = Path.GetDirectoryName(paths.Receipt)!;
        using var exclusiveTarget = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None); var access = new ReconciliationAccess(store.History.Owned.Native);
        try
        {
            var result = await JournalEvidenceQuery(store, id, access, checkpoint: step =>
            {
                if (step != "journal_receipt_absent") { return; } seen = true;
                VerifyEvidenceOwnedPath(store.History.Owned, operationDirectory); VerifyEvidenceOwnedPath(store.History.Owned, paths.Receipt);
                if (parent) { Directory.CreateDirectory(Path.GetDirectoryName(operationDirectory)!); Directory.CreateSymbolicLink(operationDirectory, targetDirectory); }
                else { Directory.CreateDirectory(operationDirectory); File.CreateSymbolicLink(paths.Receipt, target); }
            });
            // Following the redirect would hit the exclusive target and report busy.
            Assert.True(seen); Assert.Equal("unverified", result.JournalState); Assert.Null(result.Evidence); Assert.Equal(0, access.DirectoryCalls); AssertJournalEvidenceNoAuthority(result);
        }
        finally
        {
            if (seen)
            {
                var redirected = parent ? operationDirectory : paths.Receipt; VerifyEvidenceOwnedPath(store.History.Owned, redirected);
                if (parent) { Assert.NotNull(new DirectoryInfo(redirected).LinkTarget); Directory.Delete(redirected); }
                else { Assert.NotNull(new FileInfo(redirected).LinkTarget); File.Delete(redirected); }
            }
        }
    }

    [Fact]
    public async Task HistoryJournalEvidenceDoesNotAdoptAReceiptAppearingAfterItsMissingObservation()
    {
        using var store = HistoryTransactionStore(); var id = AgentProtocol.NewOperationId(); var paths = JournalEvidencePaths(store, id); var seen = false;
        var result = await JournalEvidenceQuery(store, id, new(store.History.Owned.Native), checkpoint: step =>
        {
            if (step != "journal_receipt_absent") { return; } seen = true;
            VerifyEvidenceOwnedPath(store.History.Owned, paths.Receipt); Directory.CreateDirectory(Path.GetDirectoryName(paths.Receipt)!); File.WriteAllBytes(paths.Receipt, [7, 8, 9]);
        });
        Assert.True(seen); Assert.Equal("unverified", result.JournalState); Assert.Equal("history_journal_images_changed", result.Reason); Assert.Null(result.Evidence);
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(paths.Receipt)); AssertJournalEvidenceNoAuthority(result);
    }

    private static (string Receipt, string Metadata) JournalEvidencePaths(HistoryStoreFixture store, string id)
    {
        var directory = Path.Combine(store.History.Owned.Root, ".autorun-state", CurrentUserIdentity.Key, "operations");
        return (Path.Combine(directory, id + ".operation.json"), Path.Combine(directory, "metadata.json"));
    }
    private static Task<StartupHistoryJournalEvidence> JournalEvidenceQuery(HistoryStoreFixture store, string operationId, ReconciliationAccess access,
        CancellationToken token = default, Action<string>? checkpoint = null) =>
        new StartupHistoryIntentReader(store.History.Owned.Root, () => store.DirectoryPath).InspectJournalOperationAsync(operationId, access, token, checkpoint);
    private static void AssertJournalEvidenceNoAuthority(StartupHistoryJournalEvidence result)
    {
        Assert.False(result.NativeWriteReady); Assert.False(result.ExecutionRequested); Assert.False(result.TransactionFinalized);
        Assert.False(result.MayFinalize); Assert.False(result.ReceiptUpdated); Assert.False(result.ReplayRequested);
        if (result.Evidence is not null) { AssertOperationEvidenceNoAuthority(result.Evidence); }
    }
}
