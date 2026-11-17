using System.IO;
using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Windows.Startup;
using Xunit;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    // Direct owned composition only; no dispatcher, consent or durable operation link.
    [Theory]
    [InlineData("Startup")] [InlineData("Common Startup")]
    public async Task HistoryExecutionContextFeedsItsOwnedSnapshotThroughTheEntireOwnerBeforeEnding(string source)
    {
        using var store = HistoryTransactionStore(source); using var manager = store.History.Owned.Manager();
        var entry = await DraftEntry(store, manager, source); var request = await HistoryJournalRequest(store, entry);
        using var document = JsonDocument.Parse(request.Payload!.Value.GetRawText()); request = request with { Payload = document.RootElement };
        var journal = new OperationJournal(store.History.Owned.Paths); OperationExecutionContext? captured = null;
        StartupHistoryDisableDraft? draft = null; var calls = 0; var stages = new List<string>();
        var first = await journal.RunWithContextAsync(request, async (context, token) =>
        {
            captured = context; calls++; document.Dispose(); context.RequireActiveFor(store.History.Owned.Paths);
            var response = await ExecuteHistoryForJournalTest(store, manager, context.Request, token, value => draft = value, stages.Add);
            context.RequireActiveFor(store.History.Owned.Paths); return response; // Helper has awaited owner Dispose and Root probe.
        });
        Assert.True(first.Ok); Assert.NotNull(captured); Assert.False(captured.IsActive);
        Assert.Throws<InvalidOperationException>(() => captured.RequireActiveFor(store.History.Owned.Paths));
        var result = HistoryJournalResult(first); Assert.True(result.NativeWritePerformed); Assert.True(result.LedgerVerified);
        Assert.True(result.Committed); Assert.True(result.TransactionFinalized); Assert.False(result.NativeWriteReady); Assert.False(result.ExecutionRequested);
        Assert.NotNull(draft); Assert.Equal(draft.Record.Id, result.RecordId); Assert.NotEqual(captured.OperationId, result.RecordId); // IDs remain independent, not durably linked.
        Assert.False(File.Exists(entry.Origin)); Assert.True(File.Exists(draft.Record.BackupPath)); Assert.Equal(1, stages.Count(stage => stage == "native_renamed"));
        var receipt = await journal.GetAsync(captured.OperationId); Assert.Equal(OperationState.Completed, receipt!.State);
        Assert.Equal(receipt.Fingerprint, captured.Fingerprint); Assert.Equal(receipt.AcceptedAt, captured.AcceptedAt);
        var images = DraftDataImages(store); var side = HistoryJournalSideImages(store); var frozenStages = stages.ToArray();
        var replay = await new OperationJournal(store.History.Owned.Paths).RunWithContextAsync(captured.Request with { Id = Guid.NewGuid().ToString("N") },
            (_, _) => { calls++; throw new InvalidOperationException("Completed work must not issue a second scope."); });
        Assert.True(replay.Ok); Assert.True(replay.Replayed); Assert.Equal(result, HistoryJournalResult(replay)); Assert.Equal(1, calls);
        AssertDraftDataUnchanged(store, images); AssertHistoryJournalSideUnchanged(store, side); Assert.Equal(frozenStages, stages);
        store.History.Owned.AssertNoTransactionOrTemps(); await ProbeHistoryJournalRoot(store);
    }

    [Theory]
    [InlineData("Startup")] [InlineData("Common Startup")]
    public async Task HistoryExecutionContextEndingAfterNativeFinalizationStillLeavesAnUnknownReceiptWithoutReplay(string source)
    {
        using var store = HistoryTransactionStore(source); using var manager = store.History.Owned.Manager();
        var entry = await DraftEntry(store, manager, source); var request = await HistoryJournalRequest(store, entry);
        var journal = new OperationJournal(store.History.Owned.Paths); OperationExecutionContext? captured = null;
        StartupHistoryDisableDraft? draft = null; var calls = 0; var stages = new List<string>();
        var response = await journal.RunWithContextAsync(request, async (context, token) =>
        {
            captured = context; calls++; context.RequireActiveFor(store.History.Owned.Paths);
            var actual = await ExecuteHistoryForJournalTest(store, manager, context.Request, token, value => draft = value, stages.Add);
            Assert.True(HistoryJournalResult(actual).TransactionFinalized); context.RequireActiveFor(store.History.Owned.Paths);
            throw new IOException("Owned failure after the fully drained native workflow.");
        });
        Assert.False(response.Ok); Assert.Equal("outcome_unknown", response.ErrorCode); Assert.NotNull(captured); Assert.False(captured.IsActive);
        Assert.Throws<InvalidOperationException>(() => captured.RequireActiveFor(store.History.Owned.Paths));
        Assert.NotNull(draft); Assert.False(File.Exists(entry.Origin)); Assert.True(File.Exists(draft.Record.BackupPath));
        Assert.Equal(OperationState.Unknown, (await journal.GetAsync(captured.OperationId))!.State); Assert.Equal(1, stages.Count(stage => stage == "native_renamed"));
        var images = DraftDataImages(store); var side = HistoryJournalSideImages(store); var frozenStages = stages.ToArray();
        var replay = await journal.RunWithContextAsync(captured.Request, (_, _) => { calls++; throw new InvalidOperationException("Unknown must not issue another native scope."); });
        Assert.True(replay.Replayed); Assert.Equal("outcome_unknown", replay.ErrorCode); Assert.Equal(1, calls);
        AssertDraftDataUnchanged(store, images); AssertHistoryJournalSideUnchanged(store, side); Assert.Equal(frozenStages, stages);
        store.History.Owned.AssertNoTransactionOrTemps(); await ProbeHistoryJournalRoot(store);
    }
}
