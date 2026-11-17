using System.IO;
using System.Text.Json;
using SystemAutorun.Windows.Startup;
using Xunit;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup", false)] [InlineData("Common Startup", false)] [InlineData("Startup", true)]
    public async Task HistoryCommitRequiresTheSameLiveNativeAndLedgerSessionAndKeepsBothImmutablePhases(string source, bool realShortcut)
    {
        using var store = HistoryTransactionStore(source); using var manager = store.History.Owned.Manager(); var candidate = store.History.Payload;
        byte[] original = [1, 2, 3];
        if (realShortcut)
        {
            await store.History.Owned.CreateLinkAsync(source);
            using var input = manager.CaptureShortcutForDisable(source, "owned"); original = input.CopyOriginalBytes(); var facts = input.Input;
            candidate = candidate with { Binding = candidate.Binding with { Original = new(facts.FileIdentity, facts.Length, facts.LinkCount, facts.Sha256) } };
        }
        await using var owner = await manager.PrepareHistoryDisableAsync(candidate, store.History.Record, () => store.DirectoryPath);
        Assert.False((await owner.PublishCommittedHistoryAsync()).Committed); Assert.False(File.Exists(CommittedHistoryPath(store)));
        Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified);
        var before = StartupHistoryProtection.UnprotectIndex(File.ReadAllBytes(store.IndexPath)); var candidateBytes = File.ReadAllBytes(store.ProofPath);
        var ledgerBytes = File.ReadAllBytes(store.History.Owned.Paths.StartupBackupPath);
        var result = await owner.PublishCommittedHistoryAsync();
        Assert.Equal("committed", result.State); Assert.True(result.Committed); Assert.True(result.ProofPublished); Assert.True(result.IndexPublished);
        Assert.False(result.NativeWriteReady); Assert.False(result.ExecutionRequested); Assert.False(result.TransactionFinalized);
        owner.VerifyForCommit(); Assert.Equal("protected_committed", store.Reader().Inspect(store.Context).State);
        var payload = StartupHistoryProtection.Unprotect(File.ReadAllBytes(CommittedHistoryPath(store)));
        Assert.Equal(candidate.Binding, payload.Binding); Assert.Equal(candidate.Sequence + 1, payload.Sequence); Assert.NotEqual(candidate.Nonce, payload.Nonce);
        Assert.Equal(candidate.CandidateAt, payload.CandidateAt); Assert.True(payload.CommittedAt >= candidate.CandidateAt); Assert.Equal("committed", payload.Phase);
        Assert.Equal(before.Generation + 1, StartupHistoryProtection.UnprotectIndex(File.ReadAllBytes(store.IndexPath)).Generation);
        Assert.Equal(candidateBytes, File.ReadAllBytes(store.ProofPath)); Assert.Equal(ledgerBytes, File.ReadAllBytes(store.History.Owned.Paths.StartupBackupPath));
        Assert.Equal(result, await owner.PublishCommittedHistoryAsync(checkpoint: _ => throw new IOException("No replay.")));
        AssertCommitPendingEvidence(store); Assert.ThrowsAny<IOException>(() => File.Delete(store.History.Record.BackupPath!));
        await owner.DisposeAsync(); Assert.Equal(original, File.ReadAllBytes(store.History.Record.BackupPath!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreAsync(store.History.Record.Id));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.PublishCommittedHistoryAsync());
    }

    [Theory]
    [InlineData("commit_prepared", false, false, false)] [InlineData("commit_prepared", true, false, false)]
    [InlineData("commit_proof_flushed", false, false, false)] [InlineData("commit_proof_flushed", true, false, false)]
    [InlineData("commit_proof_published", false, true, false)] [InlineData("commit_proof_published", true, true, false)]
    [InlineData("commit_index_flushed", false, true, false)] [InlineData("commit_index_flushed", true, true, false)]
    [InlineData("commit_index_published", false, true, true)] [InlineData("commit_index_published", true, true, true)]
    [InlineData("commit_verified", false, true, true)] [InlineData("commit_verified", true, true, true)]
    public async Task HistoryCommitFaultAndCancellationRetainExactPublishedPhaseWithoutClaimingSuccess(string boundary, bool cancel, bool proof, bool index)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var owner = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified); using var cancellation = new CancellationTokenSource();
        var candidateBytes = File.ReadAllBytes(store.ProofPath); var oldIndex = File.ReadAllBytes(store.IndexPath);
        var result = await owner.PublishCommittedHistoryAsync(cancellation.Token, stage =>
        { if (stage != boundary) { return; } if (cancel) { cancellation.Cancel(); } else { throw new IOException("Owned commit fault."); } });
        Assert.False(result.Committed); Assert.Equal(proof, result.ProofPublished); Assert.Equal(index, result.IndexPublished);
        Assert.Equal(index ? "uncertain" : proof ? "partial" : "rejected", result.State);
        Assert.Equal(proof, File.Exists(CommittedHistoryPath(store))); Assert.Equal(candidateBytes, File.ReadAllBytes(store.ProofPath));
        if (!index) { Assert.Equal(oldIndex, File.ReadAllBytes(store.IndexPath)); }
        Assert.Equal(index ? "protected_committed" : "candidate", store.Reader().Inspect(store.Context).State);
        Assert.Equal(result, await owner.PublishCommittedHistoryAsync()); AssertCommitPendingEvidence(store);
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "stage-*.tmp"));
    }

    [Theory]
    [InlineData("commit_prepared", "ledger")] [InlineData("commit_prepared", "intent")] [InlineData("commit_prepared", "proof")]
    [InlineData("commit_proof_published", "ledger")] [InlineData("commit_proof_published", "intent")] [InlineData("commit_proof_published", "proof")]
    [InlineData("commit_index_published", "ledger")] [InlineData("commit_index_published", "intent")] [InlineData("commit_index_published", "proof")]
    public async Task HistoryCommitRechecksOwnerAndExactProtectedPhaseAfterEveryInjectableBoundary(string boundary, string field)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var owner = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified);
        var result = await owner.PublishCommittedHistoryAsync(checkpoint: stage =>
        {
            if (stage != boundary) { return; }
            if (field != "proof") { File.WriteAllBytes(store.History.Owned.Paths.StartupBackupPath + (field == "intent" ? ".transaction" : ""), "[]"u8.ToArray()); return; }
            var index = StartupHistoryProtection.UnprotectIndex(File.ReadAllBytes(store.IndexPath)); var row = Assert.Single(index.Entries);
            var path = Path.Combine(store.DirectoryPath, StartupHistoryStoreReader.ProofName(row.Binding.RecordId, row.Latest.Phase));
            var changed = StartupHistoryProtection.Unprotect(File.ReadAllBytes(path)) with { Nonce = Guid.NewGuid().ToString("N") };
            var bytes = StartupHistoryProtection.Protect(changed); store.WritePrivateFile(path, bytes);
            store.PublishIndex(index with { Entries = new[] { new StartupHistoryIndexEntry(row.Binding, HistoryLatest(changed, bytes)) } });
        });
        Assert.False(result.Committed); Assert.Equal(boundary == "commit_index_published" ? "uncertain" : boundary == "commit_proof_published" ? "partial" : "rejected", result.State);
        Assert.True(File.Exists(store.History.Record.BackupPath)); Assert.False(File.Exists(store.History.Record.Origin));
        Assert.True(File.Exists(store.History.Owned.Paths.StartupBackupPath + ".transaction"));
        Assert.DoesNotContain(StartupMutationOperation.NativeRollback, store.History.Owned.Operations.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HistoryCommitNeverReplacesAnOccupiedOrLateArrivingImmutableProof(bool late)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var owner = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified); byte[] foreign = [9, 8, 7];
        if (!late) { store.WritePrivateFile(CommittedHistoryPath(store), foreign); }
        var result = await owner.PublishCommittedHistoryAsync(checkpoint: stage => { if (late && stage == "commit_proof_flushed") { store.WritePrivateFile(CommittedHistoryPath(store), foreign); } });
        Assert.Equal(late ? "rejected" : "conflict", result.State); Assert.False(result.Committed); Assert.False(result.ProofPublished); Assert.False(result.IndexPublished);
        Assert.Equal(foreign, File.ReadAllBytes(CommittedHistoryPath(store))); Assert.Equal("candidate", store.Reader().Inspect(store.Context).State);
        AssertCommitPendingEvidence(store); Assert.Empty(Directory.GetFiles(store.DirectoryPath, "stage-*.tmp"));
    }

    [Fact]
    public async Task HistoryCommitReaderLockMakesIndexPublicationPartialNotAnAutomaticRetry()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var owner = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified); var before = File.ReadAllBytes(store.IndexPath);
        StartupHistoryCommitPublication result;
        using (var directory = StartupNativeSlotProbe.HistoryDirectoryReadLease.Open(store.DirectoryPath, store.Context.Root, store.Actor.Value, default))
        using (var reader = directory.OpenFile(StartupHistoryStoreReader.IndexName, StartupHistoryProtection.MaximumProtectedIndexBytes, default))
        { result = await owner.PublishCommittedHistoryAsync(); Assert.Equal("partial", result.State); Assert.True(result.ProofPublished); Assert.False(result.IndexPublished); }
        Assert.Equal(before, File.ReadAllBytes(store.IndexPath)); Assert.Equal(result, await owner.PublishCommittedHistoryAsync()); AssertCommitPendingEvidence(store);
    }

    [Fact]
    public async Task HistoryCommitRetainsAllUnrelatedIndexRowsAndTheirOriginalProofBytes()
    {
        using var store = HistoryTransactionStore(); using var other = new HistoryFixture(committed: false); using var manager = store.History.Owned.Manager();
        Assert.Equal("candidate", new StartupHistoryCandidatePublisher(other.Owned.Root, () => store.DirectoryPath).Publish(other.Payload).State);
        var otherPath = Path.Combine(store.DirectoryPath, other.Payload.Binding.RecordId + ".dpapi"); var otherBytes = File.ReadAllBytes(otherPath);
        await using var owner = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified);
        var before = StartupHistoryProtection.UnprotectIndex(File.ReadAllBytes(store.IndexPath)); var unrelated = before.Entries.Single(row => row.Binding.RecordId == other.Record.Id);
        Assert.True((await owner.PublishCommittedHistoryAsync()).Committed);
        var after = StartupHistoryProtection.UnprotectIndex(File.ReadAllBytes(store.IndexPath)); Assert.Equal(2, after.Entries.Count);
        Assert.Equal(unrelated, after.Entries.Single(row => row.Binding.RecordId == other.Record.Id)); Assert.Equal(otherBytes, File.ReadAllBytes(otherPath));
    }

    [Fact]
    public async Task HistoryCommitCannotBorrowADifferentSessionsVerifiedLedgerOwner()
    {
        using var store = HistoryTransactionStore(); using var alien = HistoryTransactionStore("Common Startup"); using var manager = store.History.Owned.Manager();
        await using var owner = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified);
        using var attempt = new StartupHistoryCandidatePublisher(alien.History.Owned.Root, () => alien.DirectoryPath).OpenSession(alien.History.Payload);
        Assert.NotNull(attempt.Session); Assert.Throws<InvalidOperationException>(() => owner.RequireCommitOwner(attempt.Session, default));
        StartupHistoryCommitPublication? borrowed = null;
        Assert.True((await owner.PublishCommittedHistoryAsync(checkpoint: stage =>
        { if (stage == "commit_prepared") { borrowed = attempt.Session.PublishCommitted(owner, default, null); } })).Committed);
        Assert.NotNull(borrowed); Assert.Equal("rejected", borrowed.State); Assert.False(borrowed.Committed);
        Assert.False(File.Exists(CommittedHistoryPath(alien))); Assert.True(File.Exists(alien.History.Record.Origin));
    }

    [Fact]
    public async Task HistoryCommitGenerationOverflowPrecedesTheImmutableCommitProof()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var owner = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified);
        var index = StartupHistoryProtection.UnprotectIndex(File.ReadAllBytes(store.IndexPath)); store.PublishIndex(index with { Generation = long.MaxValue });
        var before = File.ReadAllBytes(store.IndexPath); var result = await owner.PublishCommittedHistoryAsync();
        Assert.Equal("history_commit_generation_exhausted", result.Reason); Assert.False(result.Committed); Assert.False(File.Exists(CommittedHistoryPath(store)));
        Assert.Equal(before, File.ReadAllBytes(store.IndexPath)); AssertCommitPendingEvidence(store);
    }

    [Fact]
    public void HistoryCommittedReaderNeverFallsBackToTheCandidateLeafEvenWithSelfConsistentCommittedBytes()
    {
        using var store = new HistoryStoreFixture(); var candidatePath = Path.Combine(store.DirectoryPath, store.History.Record.Id + ".dpapi");
        Assert.Equal(Path.GetFullPath(candidatePath), candidatePath); Assert.Equal(store.DirectoryPath, Path.GetDirectoryName(candidatePath)); Assert.False(File.Exists(candidatePath));
        File.Move(store.ProofPath, candidatePath); var result = store.Reader().Inspect(store.Context);
        Assert.Equal("unverified", result.State); Assert.Equal("history_store_record_missing", result.Reason); Assert.False(result.NativeWriteReady);
    }

    [Theory]
    [InlineData(".dpapi", true)] [InlineData(".committed.dpapi", true)] [InlineData(".COMMITTED.dpapi", false)]
    [InlineData(".committed.dpapi:stream", false)] [InlineData(".other.dpapi", false)] [InlineData(".dpapi/child", false)]
    public void HistoryProofLeafNamesAdmitOnlyTheTwoFixedPhaseLayouts(string suffix, bool allowed)
    { Assert.Equal(allowed, StartupHistoryStoreReader.IsProofName(new string('a', 32) + suffix)); }

    [Fact]
    public async Task HistoryCommitDoesNotLoseAnUnrelatedLateIndexChangeWhileItsOwnCandidateStillMatches()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var owner = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified); byte[]? foreign = null;
        var result = await owner.PublishCommittedHistoryAsync(checkpoint: stage =>
        {
            if (stage != "commit_proof_published") { return; }
            var index = StartupHistoryProtection.UnprotectIndex(File.ReadAllBytes(store.IndexPath));
            var added = store.History.Payload with { Nonce = Guid.NewGuid().ToString("N"), Binding = store.History.Payload.Binding with
                { RecordId = Guid.NewGuid().ToString("N"), Name = "late-unrelated", RecordDigest = new string('c', 64) } };
            var bytes = StartupHistoryProtection.Protect(added);
            store.WritePrivateFile(Path.Combine(store.DirectoryPath, StartupHistoryStoreReader.ProofName(added.Binding.RecordId, added.Phase)), bytes);
            store.PublishIndex(index with { Entries = index.Entries.Append(new StartupHistoryIndexEntry(added.Binding, HistoryLatest(added, bytes))).ToArray() });
            foreign = File.ReadAllBytes(store.IndexPath); Assert.Equal("candidate", store.Reader().Inspect(store.Context).State);
        });
        Assert.Equal("partial", result.State); Assert.False(result.Committed); Assert.False(result.IndexPublished); Assert.NotNull(foreign);
        Assert.Equal(foreign, File.ReadAllBytes(store.IndexPath)); AssertCommitPendingEvidence(store);
    }

    [Theory]
    [InlineData("native_verified")] [InlineData("ledger_published")]
    public async Task HistoryCommitDoesNotUpgradeAnUnknownNativeOrUnverifiedLedgerAttemptByObservingItsFiles(string boundary)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var owner = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        var native = await owner.ArchiveAndPublishLedgerAsync(checkpoint: stage => { if (stage == boundary) { throw new IOException("Unverified earlier attempt."); } });
        Assert.True(native.NativeWritePerformed); Assert.False(native.LedgerVerified);
        var result = await owner.PublishCommittedHistoryAsync(); Assert.Equal("rejected", result.State); Assert.False(result.Committed);
        Assert.False(File.Exists(CommittedHistoryPath(store))); Assert.Equal("candidate", store.Reader().Inspect(store.Context).State); AssertCommitPendingEvidence(store);
    }

    [Fact]
    public async Task HistoryCommitRechecksTheLiveOriginalSlotAndNeverRemovesItsReplacement()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var owner = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified); byte[] replacement = [6, 7, 8];
        var result = await owner.PublishCommittedHistoryAsync(checkpoint: stage =>
        { if (stage == "commit_prepared") { File.WriteAllBytes(store.History.Record.Origin, replacement); } });
        Assert.False(result.Committed); Assert.Equal("rejected", result.State); Assert.Equal(replacement, File.ReadAllBytes(store.History.Record.Origin));
        Assert.False(File.Exists(CommittedHistoryPath(store))); Assert.True(File.Exists(store.History.Record.BackupPath));
        Assert.DoesNotContain(StartupMutationOperation.NativeRollback, store.History.Owned.Operations.Calls);
    }

    private static string CommittedHistoryPath(HistoryStoreFixture store) =>
        Path.Combine(store.DirectoryPath, StartupHistoryStoreReader.ProofName(store.History.Record.Id, "committed"));
    private static void AssertCommitPendingEvidence(HistoryStoreFixture store)
    {
        Assert.True(File.Exists(store.History.Owned.Paths.StartupBackupPath + ".transaction")); Assert.True(File.Exists(store.History.Record.BackupPath));
        Assert.False(File.Exists(store.History.Record.Origin)); Assert.DoesNotContain(StartupMutationOperation.NativeRollback, store.History.Owned.Operations.Calls);
        Assert.DoesNotContain(StartupMutationOperation.IntentCleanup, store.History.Owned.Operations.Calls);
    }
}
