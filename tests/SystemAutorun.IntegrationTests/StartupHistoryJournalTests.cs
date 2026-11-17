using System.IO;
using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;
using Xunit;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    // This file directly composes existing primitives inside owned fixtures. It does
    // NOT install a dispatcher, native mutation command, consent token or public API.
    [Theory]
    [InlineData("Startup")] [InlineData("Common Startup")]
    public async Task HistoryJournalReleasesRootForTheOwnerAndReplaysFinalizedResultsWithoutExecutingAgain(string source)
    {
        using var store = HistoryTransactionStore(source); using var manager = store.History.Owned.Manager();
        var entry = await DraftEntry(store, manager, source); var request = await HistoryJournalRequest(store, entry); var journal = new OperationJournal(store.History.Owned.Paths);
        StartupHistoryDisableDraft? draft = null; var calls = 0; var stages = new List<string>();
        Task<AgentResponse> Execute(CancellationToken token) { calls++; return ExecuteHistoryForJournalTest(store, manager, request, token, value => draft = value, stages.Add); }
        var first = await journal.RunAsync(request, Execute); Assert.True(first.Ok); Assert.False(first.Replayed); Assert.Equal(1, calls);
        var result = HistoryJournalResult(first); Assert.True(result.NativeWritePerformed); Assert.True(result.LedgerVerified); Assert.True(result.Committed);
        Assert.True(result.TransactionFinalized); Assert.False(result.NativeWriteReady); Assert.False(result.ExecutionRequested);
        Assert.NotNull(draft); Assert.Equal(draft.Record.Id, result.RecordId); Assert.False(File.Exists(entry.Origin)); Assert.True(File.Exists(draft.Record.BackupPath));
        var receipt = await journal.GetAsync(request.OperationId!); Assert.NotNull(receipt); Assert.Equal(OperationState.Completed, receipt.State);
        var images = DraftDataImages(store); var side = HistoryJournalSideImages(store); var replayRequest = request with { Id = Guid.NewGuid().ToString("N") };
        var replay = await journal.RunAsync(replayRequest, Execute); Assert.True(replay.Ok); Assert.True(replay.Replayed); Assert.Equal(replayRequest.Id, replay.Id);
        Assert.Equal(result, HistoryJournalResult(replay)); Assert.Equal(1, calls); AssertDraftDataUnchanged(store, images); AssertHistoryJournalSideUnchanged(store, side);
        var conflict = await Assert.ThrowsAsync<AgentCommandException>(() => journal.RunAsync(request with { ExpectedRevision = new string('0', 64) }, Execute));
        Assert.Equal("operation_id_conflict", conflict.Code); Assert.Equal(1, calls); AssertDraftDataUnchanged(store, images); AssertHistoryJournalSideUnchanged(store, side);
        Assert.Equal(1, stages.Count(stage => stage == "native_renamed"));
        store.History.Owned.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData("Startup", "before-native")] [InlineData("Common Startup", "before-native")]
    [InlineData("Startup", "after-native")] [InlineData("Common Startup", "after-native")]
    [InlineData("Startup", "after-ledger")] [InlineData("Common Startup", "after-ledger")]
    [InlineData("Startup", "after-commit")] [InlineData("Common Startup", "after-commit")]
    [InlineData("Startup", "after-finalize")] [InlineData("Common Startup", "after-finalize")]
    [InlineData("Startup", "after-owner-dispose")] [InlineData("Common Startup", "after-owner-dispose")]
    [InlineData("Startup", "cancel-after-native")] [InlineData("Common Startup", "cancel-after-native")]
    public async Task HistoryJournalKeepsEveryDispatchedFailureUnknownAndNeverReplaysNativeOrCompensates(string source, string fault)
    {
        using var store = HistoryTransactionStore(source); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager, source);
        var request = await HistoryJournalRequest(store, entry); var journal = new OperationJournal(store.History.Owned.Paths); using var cancellation = new CancellationTokenSource();
        StartupHistoryDisableDraft? draft = null; var calls = 0; var stages = new List<string>();
        Task<AgentResponse> Execute(CancellationToken token)
        {
            calls++;
            return ExecuteHistoryForJournalTest(store, manager, request, token, value => draft = value, stage =>
            {
                stages.Add(stage);
                if (fault == "before-native" && stage == "before_native" || fault == "after-native" && stage == "native_renamed" || stage == fault)
                { throw new IOException("Owned journal boundary fault."); }
                if (fault == "cancel-after-native" && stage == "native_renamed") { cancellation.Cancel(); }
            });
        }
        var response = await journal.RunAsync(request, Execute, cancellation.Token); Assert.False(response.Ok); Assert.Equal("outcome_unknown", response.ErrorCode);
        Assert.NotNull(draft); Assert.Equal(1, calls); Assert.Equal(OperationState.Unknown, (await journal.GetAsync(request.OperationId!))!.State);
        var images = DraftDataImages(store); var side = HistoryJournalSideImages(store); var nativeStages = stages.ToArray(); var operations = store.History.Owned.Operations.Calls.ToArray();
        var restarted = new OperationJournal(store.History.Owned.Paths);
        foreach (var reader in new[] { journal, restarted })
        {
            var replay = await reader.RunAsync(request with { Id = Guid.NewGuid().ToString("N") }, Execute);
            Assert.False(replay.Ok); Assert.True(replay.Replayed); Assert.Equal("outcome_unknown", replay.ErrorCode); Assert.Equal(1, calls);
        }
        AssertDraftDataUnchanged(store, images); AssertHistoryJournalSideUnchanged(store, side); Assert.Equal(operations, store.History.Owned.Operations.Calls); Assert.Equal(nativeStages, stages);
        Assert.Equal(fault == "before-native", File.Exists(entry.Origin)); Assert.Equal(fault != "before-native", File.Exists(draft.Record.BackupPath));
        Assert.Equal(fault is "after-finalize" or "after-owner-dispose", !File.Exists(store.History.Owned.Paths.StartupBackupPath + ".transaction"));
        if (fault is "after-commit" or "after-finalize" or "after-owner-dispose")
        { Assert.Equal("protected_committed", new StartupHistoryStoreReader(store.History.Owned.Root, () => store.DirectoryPath).Inspect(draft.Candidate.Binding.Context).State); }
        Assert.Equal(fault == "before-native" ? 0 : 1, stages.Count(stage => stage == "native_renamed")); await ProbeHistoryJournalRoot(store);
    }

    [Theory]
    [InlineData("Startup")] [InlineData("Common Startup")]
    public async Task HistoryJournalReceiptPublicationFailureAfterFinalizationCannotBecomeASecondNativeAttempt(string source)
    {
        using var store = HistoryTransactionStore(source); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager, source);
        var request = await HistoryJournalRequest(store, entry); var journal = new OperationJournal(store.History.Owned.Paths);
        FileStream? blocker = null; StartupHistoryDisableDraft? draft = null; var calls = 0; var stages = new List<string>();
        async Task<AgentResponse> Execute(CancellationToken token)
        {
            calls++; var response = await ExecuteHistoryForJournalTest(store, manager, request, token, value => draft = value, stages.Add);
            var path = Path.Combine(store.History.Owned.Root, ".autorun-state", CurrentUserIdentity.Key, "operations", request.OperationId + ".operation.json");
            Assert.Equal(path, Assert.Single(Directory.GetFiles(store.History.Owned.Root, request.OperationId + ".operation.json", SearchOption.AllDirectories)));
            blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return response;
        }
        try
        {
            var unknown = await Assert.ThrowsAsync<AgentOutcomeUnknownException>(() => journal.RunAsync(request, Execute));
            Assert.Equal(request.OperationId, unknown.OperationId); Assert.NotNull(blocker);
        }
        finally { blocker?.Dispose(); }
        Assert.NotNull(draft); Assert.False(File.Exists(entry.Origin)); Assert.True(File.Exists(draft.Record.BackupPath)); store.History.Owned.AssertNoTransactionOrTemps();
        var pendingPath = Assert.Single(Directory.GetFiles(store.History.Owned.Root, request.OperationId + ".operation.json", SearchOption.AllDirectories));
        var pending = JsonSerializer.Deserialize<OperationReceipt>(File.ReadAllBytes(pendingPath), AgentProtocol.JsonOptions)!;
        Assert.Equal(OperationState.Running, pending.State); Assert.Null(pending.Response); // Disk is pending; observation is UNKNOWN, not completed.
        var restarted = new OperationJournal(store.History.Owned.Paths); Assert.Equal(OperationState.Unknown, (await restarted.GetAsync(request.OperationId!))!.State);
        var images = DraftDataImages(store); var side = HistoryJournalSideImages(store);
        var replay = await restarted.RunAsync(request, _ => { calls++; throw new InvalidOperationException("Pending receipt must not enter another callback."); });
        Assert.False(replay.Ok); Assert.True(replay.Replayed); Assert.Equal("outcome_unknown", replay.ErrorCode);
        Assert.Equal(1, calls); AssertDraftDataUnchanged(store, images); AssertHistoryJournalSideUnchanged(store, side);
        Assert.Equal(1, stages.Count(stage => stage == "native_renamed")); Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(pendingPath)!, "*.tmp")); await ProbeHistoryJournalRoot(store);
    }

    [Theory]
    [InlineData("Startup")] [InlineData("Common Startup")]
    public async Task HistoryJournalOmittedResultsStayCompletedButNeverReexecuteToRebuildTheResponse(string source)
    {
        using var store = HistoryTransactionStore(source); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager, source);
        var request = await HistoryJournalRequest(store, entry); var journal = new OperationJournal(store.History.Owned.Paths, limits: new(MaximumResultBytes: 1)); var calls = 0;
        Task<AgentResponse> Execute(CancellationToken token) { calls++; return ExecuteHistoryForJournalTest(store, manager, request, token); }
        var response = await journal.RunAsync(request, Execute); Assert.True(response.Ok); Assert.True(HistoryJournalResult(response).TransactionFinalized);
        var receipt = (await journal.GetAsync(request.OperationId!))!; Assert.Equal(OperationState.Completed, receipt.State); Assert.True(receipt.ResultOmitted); Assert.Null(receipt.Response!.Data);
        var images = DraftDataImages(store); var side = HistoryJournalSideImages(store); var replay = await journal.RunAsync(request, Execute);
        Assert.False(replay.Ok); Assert.True(replay.Replayed); Assert.Equal("operation_result_unavailable", replay.ErrorCode); Assert.Equal(1, calls); AssertDraftDataUnchanged(store, images); AssertHistoryJournalSideUnchanged(store, side);
        store.History.Owned.AssertNoTransactionOrTemps();
    }

    [Fact]
    public async Task HistoryJournalConcurrentSameIdObservesTheRunningOwnerWithoutEnteringAnotherCallback()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var request = await HistoryJournalRequest(store, entry); var journal = new OperationJournal(store.History.Owned.Paths); var calls = 0; var stages = new List<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = journal.RunAsync(request, async token => { calls++; entered.TrySetResult(); await release.Task.WaitAsync(token); return await ExecuteHistoryForJournalTest(store, manager, request, token, checkpoint: stages.Add); }, deadline.Token);
        AgentResponse? first = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var concurrent = await journal.RunAsync(request, _ => { calls++; throw new InvalidOperationException("Duplicate callback."); });
            Assert.False(concurrent.Ok); Assert.True(concurrent.Replayed); Assert.Equal("operation_in_progress", concurrent.ErrorCode); Assert.Equal(1, calls);
        }
        finally
        {
            release.TrySetResult(); if (!entered.Task.IsCompleted) { deadline.Cancel(); }
            first = await original; // Cancellation never substitutes for draining the actual workflow before fixture cleanup.
        }
        Assert.True(first.Ok); Assert.True(HistoryJournalResult(first).TransactionFinalized); Assert.Equal(1, calls); Assert.Equal(1, stages.Count(stage => stage == "native_renamed"));
        store.History.Owned.AssertNoTransactionOrTemps(); await ProbeHistoryJournalRoot(store);
    }

    [Fact]
    public async Task HistoryJournalCanPersistAKnownRevisionConflictOnlyBeforeHistoryPreparationBegins()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var request = await HistoryJournalRequest(store, entry); File.AppendAllText(store.History.Owned.Paths.ConfigPath, "\n");
        var journal = new OperationJournal(store.History.Owned.Paths); var calls = 0;
        Task<AgentResponse> Execute(CancellationToken token) { calls++; return ExecuteHistoryForJournalTest(store, manager, request, token); }
        var response = await journal.RunAsync(request, Execute); Assert.False(response.Ok); Assert.Equal("revision_conflict", response.ErrorCode);
        Assert.Equal(OperationState.Completed, (await journal.GetAsync(request.OperationId!))!.State); Assert.False(Directory.Exists(store.DirectoryPath));
        Assert.Empty(store.History.Owned.Operations.Calls); Assert.True(File.Exists(entry.Origin)); store.History.Owned.AssertNoTransactionOrTemps();
        var replay = await journal.RunAsync(request, Execute); Assert.Equal("revision_conflict", replay.ErrorCode); Assert.True(replay.Replayed); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task HistoryJournalDoesNotBorrowTheAdoptOnlyNoWriteErrorWhitelistForANativeCommandLabel()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var request = await HistoryJournalRequest(store, entry); request = request with { Payload = JsonSerializer.SerializeToElement(StartupInspectionRequest.FromEntry(entry) with { MetadataDigest = new string('0', 64) }, AgentProtocol.JsonOptions) };
        var journal = new OperationJournal(store.History.Owned.Paths); StartupHistoryDisableDraft? draft = null; var calls = 0;
        async Task<AgentResponse> Execute(CancellationToken token)
        {
            calls++;
            try { return await ExecuteHistoryForJournalTest(store, manager, request, token, value => draft = value); }
            catch (InvalidOperationException) when (draft is null) { return new(request.Id, false, Error: "Displayed entry changed before prepare.", ErrorCode: "startup_entry_changed"); }
        }
        var response = await journal.RunAsync(request, Execute); Assert.Equal("outcome_unknown", response.ErrorCode); Assert.Null(draft);
        Assert.Equal(OperationState.Unknown, (await journal.GetAsync(request.OperationId!))!.State); Assert.False(Directory.Exists(store.DirectoryPath));
        Assert.Empty(store.History.Owned.Operations.Calls); store.History.Owned.AssertNoTransactionOrTemps();
        var images = DraftDataImages(store); var replay = await journal.RunAsync(request, Execute);
        Assert.Equal("outcome_unknown", replay.ErrorCode); Assert.True(replay.Replayed); Assert.Equal(1, calls);
        AssertDraftDataUnchanged(store, images); Assert.False(Directory.Exists(store.DirectoryPath)); Assert.Empty(store.History.Owned.Operations.Calls);
    }

    [Fact]
    public async Task HistoryJournalCancellationAfterCallbackEntryIsConservativelyUnknownEvenWithoutNativeEffects()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var request = await HistoryJournalRequest(store, entry); var journal = new OperationJournal(store.History.Owned.Paths); using var cancellation = new CancellationTokenSource(); var calls = 0;
        var response = await journal.RunAsync(request, token => { calls++; cancellation.Cancel(); return ExecuteHistoryForJournalTest(store, manager, request, token); }, cancellation.Token);
        Assert.False(response.Ok); Assert.Equal("outcome_unknown", response.ErrorCode); Assert.Equal(OperationState.Unknown, (await journal.GetAsync(request.OperationId!))!.State);
        Assert.False(Directory.Exists(store.DirectoryPath)); Assert.Empty(store.History.Owned.Operations.Calls); store.History.Owned.AssertNoTransactionOrTemps();
        var images = DraftDataImages(store); var replay = await journal.RunAsync(request, _ => { calls++; throw new InvalidOperationException("Cancelled callback replayed."); });
        Assert.True(replay.Replayed); Assert.Equal("outcome_unknown", replay.ErrorCode); Assert.Equal(1, calls);
        AssertDraftDataUnchanged(store, images); Assert.False(Directory.Exists(store.DirectoryPath)); Assert.Empty(store.History.Owned.Operations.Calls); await ProbeHistoryJournalRoot(store);
    }

    private static async Task<AgentRequest> HistoryJournalRequest(HistoryStoreFixture store, StartupEntry entry)
    {
        const string command = "startup-disable";
        Assert.False(AgentProtocol.IsMutation(command)); Assert.False(AgentProtocol.IsConfigurationMutation(command)); // This composition is NOT a registered transport route.
        return new(Guid.NewGuid().ToString("N"), command, entry.Id, JsonSerializer.SerializeToElement(StartupInspectionRequest.FromEntry(entry), AgentProtocol.JsonOptions),
            ExpectedRevision: await ConfigurationRevision.ReadAsync(store.History.Owned.Paths), OperationId: AgentProtocol.NewOperationId());
    }

    private static async Task<AgentResponse> ExecuteHistoryForJournalTest(HistoryStoreFixture store, StartupManager manager, AgentRequest request,
        CancellationToken token, Action<StartupHistoryDisableDraft>? observed = null, Action<string>? checkpoint = null)
    {
        await ProbeHistoryJournalRoot(store); // Journal must have released Root BEFORE executing this callback.
        StartupHistoryDisableDraft draft;
        try { draft = await manager.CaptureHistoryDisableDraftAsync(request.Name!, request.Payload!.Value.Deserialize<StartupInspectionRequest>(AgentProtocol.JsonOptions)!, request.ExpectedRevision!, token); }
        catch (ConfigurationConflictException) { return new(request.Id, false, Error: "Workspace changed before history preparation.", ErrorCode: "revision_conflict"); }
        observed?.Invoke(draft); HistoryJournalTestResult result;
        await using (var owner = await manager.PrepareHistoryDisableAsync(draft, () => store.DirectoryPath, token))
        {
            var ledger = await owner.ArchiveAndPublishLedgerAsync(token, checkpoint); if (!ledger.LedgerVerified) { throw new IOException("Internal archive/ledger stage was not verified: " + ledger.Reason); }
            checkpoint?.Invoke("after-ledger");
            var committed = await owner.PublishCommittedHistoryAsync(token); if (!committed.Committed) { throw new IOException("Internal committed publication was not verified: " + committed.Reason); }
            checkpoint?.Invoke("after-commit");
            var finalized = await owner.FinalizeCommittedAsync(token); if (!finalized.TransactionFinalized) { throw new IOException("Internal finalization was not verified: " + finalized.Reason); }
            checkpoint?.Invoke("after-finalize");
            result = new(draft.Record.Id, ledger.NativeWritePerformed, ledger.LedgerVerified, committed.Committed, finalized.TransactionFinalized,
                draft.NativeWriteReady || ledger.NativeWriteReady || committed.NativeWriteReady || finalized.NativeWriteReady,
                draft.ExecutionRequested || ledger.ExecutionRequested || committed.ExecutionRequested || finalized.ExecutionRequested);
        }
        await ProbeHistoryJournalRoot(store); // Owner is disposed BEFORE callback completion / Journal receipt publication.
        checkpoint?.Invoke("after-owner-dispose");
        return new(request.Id, true, Data: result, Revision: draft.ConfigurationRevision);
    }
    private static async Task ProbeHistoryJournalRoot(HistoryStoreFixture store)
    { await using var probe = await RootWriteLease.AcquireAsync(store.History.Owned.Paths, timeout: TimeSpan.FromMilliseconds(250)); probe.Require(store.History.Owned.Paths); }
    private sealed record HistoryJournalTestResult(string RecordId, bool NativeWritePerformed, bool LedgerVerified, bool Committed, bool TransactionFinalized,
        bool NativeWriteReady, bool ExecutionRequested);
    private static HistoryJournalTestResult HistoryJournalResult(AgentResponse response) => JsonSerializer.SerializeToElement(response.Data, AgentProtocol.JsonOptions).Deserialize<HistoryJournalTestResult>(AgentProtocol.JsonOptions)!;
    private static Dictionary<string, byte[]> HistoryJournalSideImages(HistoryStoreFixture store) => Directory.GetFiles(store.DirectoryPath).ToDictionary(path => path, File.ReadAllBytes);
    private static void AssertHistoryJournalSideUnchanged(HistoryStoreFixture store, Dictionary<string, byte[]> before)
    { var after = HistoryJournalSideImages(store); Assert.Equal(before.Keys.Order(), after.Keys.Order()); foreach (var (path, bytes) in before) { Assert.Equal(bytes, after[path]); } }
}
