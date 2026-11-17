using System.IO;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Windows.Startup;
using Xunit;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup", false, false)] [InlineData("Common Startup", false, false)]
    [InlineData("Startup", true, false)] [InlineData("Common Startup", true, true)]
    public async Task HistoryFinalizationUsesOnlyItsLiveCommittedOwnerAndPreservesEveryOtherImage(string source, bool realShortcut, bool previousRaw)
    {
        using var store = HistoryTransactionStore(source); using var manager = store.History.Owned.Manager(); var candidate = store.History.Payload;
        if (previousRaw) { File.WriteAllBytes(store.History.Owned.Paths.StartupBackupPath, " \n[]\n"u8.ToArray()); }
        if (realShortcut)
        {
            await store.History.Owned.CreateLinkAsync(source);
            using var capture = manager.CaptureShortcutForDisable(source, "owned"); var input = capture.Input;
            candidate = candidate with { Binding = candidate.Binding with { Original = new(input.FileIdentity, input.Length, input.LinkCount, input.Sha256) } };
        }
        await using var owner = await manager.PrepareHistoryDisableAsync(candidate, store.History.Record, () => store.DirectoryPath);
        Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified); var committed = await owner.PublishCommittedHistoryAsync(); Assert.True(committed.Committed);
        var images = FinalizationImages(store); var calls = store.History.Owned.Operations.Calls.ToArray();
        var result = await owner.FinalizeCommittedAsync();
        Assert.Equal("finalized", result.State); Assert.True(result.TransactionFinalized); Assert.True(result.IntentCleanupDispatched); Assert.True(result.IntentCleanupReturned);
        AssertFinalizationNoAuthority(result); Assert.False(File.Exists(IntentPath(store))); Assert.False(File.Exists(store.History.Record.Origin)); AssertFinalizationImagesUnchanged(images);
        Assert.Equal(calls.Append(StartupMutationOperation.IntentCleanup), store.History.Owned.Operations.Calls);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Equal(result, await owner.FinalizeCommittedAsync(cancellation.Token, _ => throw new IOException("Never retry.")));
        Assert.False(committed.TransactionFinalized); Assert.Equal(committed, await owner.PublishCommittedHistoryAsync());
        await Assert.ThrowsAsync<TimeoutException>(() => RootWriteLease.AcquireAsync(store.History.Owned.Paths, timeout: TimeSpan.FromMilliseconds(30)));
        await owner.DisposeAsync();
        var query = new StartupHistoryIntentReader(store.History.Owned.Root, () => store.DirectoryPath).Inspect();
        Assert.Equal("none", query.State); AssertIntentQueryNoAuthority(query); Assert.Equal("protected_committed", store.Reader().Inspect(store.Context).State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.FinalizeCommittedAsync());
        store.History.Owned.AssertNoTransactionOrTemps(); Assert.Equal(0, store.History.Owned.Native.WritableOpens);
    }

    [Theory]
    [InlineData("candidate")] [InlineData("ledger")] [InlineData("uncertain-commit")]
    public async Task HistoryFinalizationCannotPromoteAQueryOrAnUnacknowledgedCommittedIndex(string phase)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var owner = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        if (phase != "candidate") { Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified); }
        if (phase == "uncertain-commit")
        {
            var commit = await owner.PublishCommittedHistoryAsync(checkpoint: stage => { if (stage == "commit_index_published") { throw new IOException("Lost acknowledgement."); } });
            Assert.False(commit.Committed); Assert.Equal("uncertain", commit.State); Assert.Equal("protected_committed", store.Reader().Inspect(store.Context).State);
        }
        var before = IntentDataImages(store); var result = await owner.FinalizeCommittedAsync();
        Assert.Equal("rejected", result.State); Assert.False(result.IntentCleanupDispatched); AssertFinalizationNoAuthority(result); Assert.False(result.TransactionFinalized);
        AssertFinalizationImagesUnchanged(before); Assert.DoesNotContain(StartupMutationOperation.IntentCleanup, store.History.Owned.Operations.Calls);
        await owner.DisposeAsync();
        var query = new StartupHistoryIntentReader(store.History.Owned.Root, () => store.DirectoryPath).Inspect();
        AssertIntentQueryNoAuthority(query); Assert.True(File.Exists(IntentPath(store)));
    }

    [Theory]
    [InlineData("intent")] [InlineData("ledger")] [InlineData("index-valid-new-image")] [InlineData("proof-missing")]
    [InlineData("original-occupied")] [InlineData("source-unavailable")] [InlineData("readonly-intent")] [InlineData("sharing-intent")]
    [InlineData("hardlink-intent")]
    public async Task HistoryFinalizationRevalidatesExactInputsAndDoesNotCleanOnPreflightDrift(string mode)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); await using var owner = await CommittedFinalizationOwner(store, manager);
        FileStream? blocker = null;
        try
        {
            switch (mode)
            {
                case "intent": File.AppendAllText(IntentPath(store), "\n"); break;
                case "ledger": File.AppendAllText(store.History.Owned.Paths.StartupBackupPath, "\n"); break;
                case "index-valid-new-image":
                    var index = StartupHistoryProtection.UnprotectIndex(File.ReadAllBytes(store.IndexPath));
                    store.WritePrivateFile(store.IndexPath, StartupHistoryProtection.ProtectIndex(index with { Generation = index.Generation + 1, Nonce = Guid.NewGuid().ToString("N") }));
                    Assert.Equal("protected_committed", store.Reader().Inspect(store.Context).State); break;
                case "proof-missing": DeleteNativeHistoryFile(store, CommittedHistoryPath(store)); break;
                case "original-occupied": File.WriteAllBytes(store.History.Record.Origin, new byte[] { 8, 9 }); break;
                case "source-unavailable": store.History.Owned.Native.BeforeDirectory = () => throw new IOException("Source no longer observed."); break;
                case "readonly-intent": File.SetAttributes(IntentPath(store), FileAttributes.ReadOnly); break;
                case "sharing-intent": blocker = File.Open(IntentPath(store), FileMode.Open, FileAccess.Read, FileShare.Read); break;
                case "hardlink-intent": Assert.True(EvidenceNativeCases.CreateHardLinkW(Path.Combine(store.History.Owned.Root, "intent-alias"), IntentPath(store), IntPtr.Zero)); break;
            }
            var images = FinalizationImages(store); var intent = File.ReadAllBytes(IntentPath(store)); var result = await owner.FinalizeCommittedAsync();
            Assert.Equal("rejected", result.State); Assert.False(result.IntentCleanupDispatched); Assert.False(result.TransactionFinalized); AssertFinalizationNoAuthority(result);
            Assert.Equal(intent, File.ReadAllBytes(IntentPath(store))); AssertFinalizationImagesUnchanged(images);
            Assert.DoesNotContain(StartupMutationOperation.IntentCleanup, store.History.Owned.Operations.Calls);
            Assert.Equal(result, await owner.FinalizeCommittedAsync());
        }
        finally
        {
            store.History.Owned.Native.BeforeDirectory = null; blocker?.Dispose();
            if (mode == "readonly-intent") { File.SetAttributes(IntentPath(store), FileAttributes.Normal); }
        }
    }

    [Theory]
    [InlineData("Startup", false)] [InlineData("Startup", true)] [InlineData("Common Startup", false)] [InlineData("Common Startup", true)]
    public async Task HistoryFinalizationBeforeAndAfterCleanupFaultsNeverReplayRecreateOrRollback(string source, bool after)
    {
        using var store = HistoryTransactionStore(source); using var manager = store.History.Owned.Manager(); await using var owner = await CommittedFinalizationOwner(store, manager);
        var images = FinalizationImages(store); var intent = File.ReadAllBytes(IntentPath(store));
        void Fail(StartupMutationOperation operation) { if (operation == StartupMutationOperation.IntentCleanup) { throw new IOException("Cleanup acknowledgement lost."); } }
        if (after) { store.History.Owned.Operations.After = Fail; } else { store.History.Owned.Operations.Before = Fail; }
        var result = await owner.FinalizeCommittedAsync();
        Assert.Equal("uncertain", result.State); Assert.True(result.IntentCleanupDispatched); Assert.False(result.IntentCleanupReturned); Assert.False(result.TransactionFinalized);
        AssertFinalizationNoAuthority(result); Assert.Equal(!after, File.Exists(IntentPath(store))); if (!after) { Assert.Equal(intent, File.ReadAllBytes(IntentPath(store))); }
        AssertFinalizationImagesUnchanged(images); Assert.Equal("protected_committed", store.Reader().Inspect(store.Context).State);
        store.History.Owned.Operations.Before = store.History.Owned.Operations.After = null;
        Assert.Equal(result, await owner.FinalizeCommittedAsync()); Assert.Equal(1, store.History.Owned.Operations.Calls.Count(call => call == StartupMutationOperation.IntentCleanup));
        Assert.DoesNotContain(StartupMutationOperation.NativeRollback, store.History.Owned.Operations.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HistoryFinalizationUnexpectedDispatchExceptionsStillLatchUncertaintyBeforeTheyEscape(bool after)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); await using var owner = await CommittedFinalizationOwner(store, manager);
        var images = FinalizationImages(store);
        void Fail(StartupMutationOperation operation) { if (operation == StartupMutationOperation.IntentCleanup) { throw new NotSupportedException("Unexpected fixture boundary failure."); } }
        if (after) { store.History.Owned.Operations.After = Fail; } else { store.History.Owned.Operations.Before = Fail; }
        await Assert.ThrowsAsync<NotSupportedException>(() => owner.FinalizeCommittedAsync());
        var result = await owner.FinalizeCommittedAsync(); Assert.Equal("uncertain", result.State); Assert.Equal("history_intent_cleanup_unacknowledged", result.Reason);
        Assert.True(result.IntentCleanupDispatched); Assert.False(result.IntentCleanupReturned); Assert.False(result.TransactionFinalized); AssertFinalizationNoAuthority(result);
        Assert.Equal(!after, File.Exists(IntentPath(store))); AssertFinalizationImagesUnchanged(images);
        Assert.Equal(result, await owner.FinalizeCommittedAsync()); Assert.Equal(1, store.History.Owned.Operations.Calls.Count(call => call == StartupMutationOperation.IntentCleanup));
    }

    [Theory]
    [InlineData("finalization_prepared", false)] [InlineData("intent_cleanup_returned", true)] [InlineData("finalization_verified", true)]
    public async Task HistoryFinalizationCancellationKeepsActualEvidenceAndCachesTheTerminalOutcome(string boundary, bool removed)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); await using var owner = await CommittedFinalizationOwner(store, manager);
        var images = FinalizationImages(store); using var cancellation = new CancellationTokenSource();
        var result = await owner.FinalizeCommittedAsync(cancellation.Token, stage => { if (stage == boundary) { cancellation.Cancel(); } });
        Assert.Equal(removed ? "uncertain" : "rejected", result.State); Assert.Equal(removed, result.IntentCleanupDispatched); Assert.Equal(removed, result.IntentCleanupReturned);
        Assert.False(result.TransactionFinalized); AssertFinalizationNoAuthority(result); Assert.Equal(!removed, File.Exists(IntentPath(store))); AssertFinalizationImagesUnchanged(images);
        Assert.Equal(result, await owner.FinalizeCommittedAsync());
        Assert.Equal(removed ? 1 : 0, store.History.Owned.Operations.Calls.Count(call => call == StartupMutationOperation.IntentCleanup));
    }

    [Fact]
    public async Task HistoryFinalizationPinsTheExactIntentAgainstReplacementAndWritesUntilTheDeleteHandleCloses()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); await using var owner = await CommittedFinalizationOwner(store, manager);
        var before = FinalizationImages(store); var reached = false;
        var result = await owner.FinalizeCommittedAsync(checkpoint: stage =>
        {
            if (stage != "finalization_prepared") { return; } reached = true;
            Assert.ThrowsAny<IOException>(() => File.AppendAllText(IntentPath(store), "changed"));
            Assert.ThrowsAny<IOException>(() => File.Move(IntentPath(store), Path.Combine(store.History.Owned.Root, "other-intent")));
            Assert.ThrowsAny<IOException>(() => File.Delete(IntentPath(store)));
            Assert.ThrowsAny<IOException>(() => File.AppendAllText(store.History.Owned.Paths.ConfigPath, "changed"));
        });
        Assert.True(reached); Assert.True(result.TransactionFinalized); AssertFinalizationImagesUnchanged(before); Assert.False(File.Exists(IntentPath(store)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HistoryFinalizationLateLedgerDriftAndReplacementIntentRemainUncertainWithoutASecondDelete(bool after)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); await using var owner = await CommittedFinalizationOwner(store, manager);
        var evidence = FinalizationImages(store); var foreign = "[]\n"u8.ToArray();
        if (after)
        { store.History.Owned.Operations.After = operation => { if (operation == StartupMutationOperation.IntentCleanup) { File.WriteAllBytes(IntentPath(store), foreign); } }; }
        else
        { store.History.Owned.Operations.Before = operation => { if (operation == StartupMutationOperation.IntentCleanup) { File.WriteAllBytes(store.History.Owned.Paths.StartupBackupPath, foreign); } }; }
        var result = await owner.FinalizeCommittedAsync();
        Assert.Equal("uncertain", result.State); Assert.True(result.IntentCleanupDispatched); Assert.Equal(after, result.IntentCleanupReturned); Assert.False(result.TransactionFinalized);
        Assert.True(File.Exists(IntentPath(store))); Assert.Equal(foreign, File.ReadAllBytes(after ? IntentPath(store) : store.History.Owned.Paths.StartupBackupPath));
        if (!after) { evidence.Remove(store.History.Owned.Paths.StartupBackupPath); } AssertFinalizationImagesUnchanged(evidence);
        Assert.Equal(result, await owner.FinalizeCommittedAsync()); Assert.Equal(1, store.History.Owned.Operations.Calls.Count(call => call == StartupMutationOperation.IntentCleanup));
    }

    [Fact]
    public async Task HistoryFinalizationLosingTheOwnerDoesNotConvertReadOnlyMatchingIntoCleanupAuthority()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var owner = await CommittedFinalizationOwner(store, manager);
        await owner.DisposeAsync(); var images = IntentDataImages(store);
        var query = new StartupHistoryIntentReader(store.History.Owned.Root, () => store.DirectoryPath).Inspect();
        Assert.Equal("protected_committed", query.HistoryState); AssertIntentQueryNoAuthority(query); AssertFinalizationImagesUnchanged(images);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.FinalizeCommittedAsync());
        Assert.True(File.Exists(IntentPath(store))); Assert.DoesNotContain(StartupMutationOperation.IntentCleanup, store.History.Owned.Operations.Calls);
        await using var root = await RootWriteLease.AcquireAsync(store.History.Owned.Paths, timeout: TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task HistoryFinalizationCleanupPrimitiveRejectsABorrowedLeaseFromAnotherRootBeforeOpeningTheIntent()
    {
        using var store = HistoryTransactionStore(); using var alien = new Fixture(); using var manager = store.History.Owned.Manager();
        await using var owner = await CommittedFinalizationOwner(store, manager); var intent = File.ReadAllBytes(IntentPath(store));
        await using var otherRoot = await RootWriteLease.AcquireAsync(alien.Paths);
        Assert.Throws<ArgumentException>(() => StartupNativeSlotProbe.HistoryIntentCleanupLease.Open(store.History.Owned.Paths, otherRoot, intent, default));
        Assert.Equal(intent, File.ReadAllBytes(IntentPath(store))); Assert.DoesNotContain(StartupMutationOperation.IntentCleanup, store.History.Owned.Operations.Calls);
    }

    [Fact]
    public async Task HistoryFinalizationConcurrentAndCanceledWaitersObserveOneTerminalCleanupWithoutAbandoningTheOwner()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); await using var owner = await CommittedFinalizationOwner(store, manager);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource(); Task<StartupHistoryFinalizationResult>? queued = null;
        var work = Task.Run(() => owner.FinalizeCommittedAsync(checkpoint: stage => PauseFinalization(stage, "finalization_prepared", reached, release)));
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(20)); queued = owner.FinalizeCommittedAsync(cancellation.Token); cancellation.Cancel();
            Assert.False(queued.IsCompleted); Assert.False(work.IsCompleted); Assert.True(File.Exists(IntentPath(store)));
        }
        finally { release.Set(); await Task.WhenAll(work, queued ?? work).WaitAsync(TimeSpan.FromSeconds(20)); }
        Assert.True((await work).TransactionFinalized); Assert.Equal(await work, await queued!);
        Assert.Equal(1, store.History.Owned.Operations.Calls.Count(call => call == StartupMutationOperation.IntentCleanup));
    }

    [Fact]
    public async Task HistoryFinalizationDisposeCannotReleaseTheNativeAndRootLeasesWhileCleanupIsStillBeingVerified()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); await using var owner = await CommittedFinalizationOwner(store, manager);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim(); Task? disposal = null;
        var work = Task.Run(() => owner.FinalizeCommittedAsync(checkpoint: stage => PauseFinalization(stage, "intent_cleanup_returned", reached, release)));
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(20)); disposal = owner.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted);
            Assert.False(File.Exists(IntentPath(store))); Assert.ThrowsAny<IOException>(() => File.Delete(store.History.Record.BackupPath!));
            await Assert.ThrowsAsync<TimeoutException>(() => RootWriteLease.AcquireAsync(store.History.Owned.Paths, timeout: TimeSpan.FromMilliseconds(30)));
        }
        finally { release.Set(); await Task.WhenAll(work, disposal ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(20)); }
        Assert.True((await work).TransactionFinalized); await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.FinalizeCommittedAsync());
        await using var root = await RootWriteLease.AcquireAsync(store.History.Owned.Paths, timeout: TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task HistoryFinalizationOpeningAndDisposingACleanupLeaseDoesNotArmDeletion()
    {
        using var fixture = new Fixture(); await using var root = await RootWriteLease.AcquireAsync(fixture.Paths);
        var path = fixture.Paths.StartupBackupPath + ".transaction"; var bytes = "owned untrusted metadata, not authority"u8.ToArray(); File.WriteAllBytes(path, bytes);
        using (var cleanup = StartupNativeSlotProbe.HistoryIntentCleanupLease.Open(fixture.Paths, root, bytes, default)) { cleanup.VerifyCurrent(bytes, default); }
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Empty(fixture.Operations.Calls);
    }

    [Fact]
    public async Task HistoryFinalizationCancellationBeforeDispatchCachesNoAttemptAndKeepsTheExactIntent()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); await using var owner = await CommittedFinalizationOwner(store, manager);
        var images = IntentDataImages(store); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var result = await owner.FinalizeCommittedAsync(cancellation.Token); Assert.Equal("rejected", result.State); Assert.False(result.IntentCleanupDispatched);
        Assert.False(result.TransactionFinalized); AssertFinalizationNoAuthority(result); AssertFinalizationImagesUnchanged(images);
        Assert.Equal(result, await owner.FinalizeCommittedAsync()); Assert.DoesNotContain(StartupMutationOperation.IntentCleanup, store.History.Owned.Operations.Calls);
    }

    private static void PauseFinalization(string stage, string boundary, TaskCompletionSource reached, ManualResetEventSlim release)
    { if (stage == boundary) { reached.SetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) { throw new IOException("Owned pause timed out."); } } }

    private static async Task<StartupManager.HistoryDisableTransaction> CommittedFinalizationOwner(HistoryStoreFixture store, StartupManager manager)
    {
        var owner = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        try { Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified); Assert.True((await owner.PublishCommittedHistoryAsync()).Committed); return owner; }
        catch { await owner.DisposeAsync(); throw; }
    }
    private static Dictionary<string, byte[]> FinalizationImages(HistoryStoreFixture store) => new[] { store.History.Owned.Paths.ConfigPath,
        store.History.Owned.Paths.StartupBackupPath, store.ProofPath, CommittedHistoryPath(store), store.IndexPath, store.History.Record.BackupPath! }
        .Where(File.Exists).ToDictionary(path => path, ReadFinalizationFixtureBytes);
    private static byte[] ReadFinalizationFixtureBytes(string path)
    {
        // Test snapshot only: accommodate the live capture's DELETE reservation in
        // both sharing directions. Never change the production no-write/no-delete pin.
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        Assert.InRange(stream.Length, 0, StartupHistoryIntentFormat.MaximumBytes); var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }
    private static void AssertFinalizationImagesUnchanged(Dictionary<string, byte[]> images)
    { foreach (var (path, expected) in images) { Assert.Equal(expected, ReadFinalizationFixtureBytes(path)); } }
    private static void AssertFinalizationNoAuthority(StartupHistoryFinalizationResult result)
    { Assert.False(result.NativeWriteReady); Assert.False(result.ExecutionRequested); }
}
