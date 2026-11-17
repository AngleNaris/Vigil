using System.IO;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;
using Xunit;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup", false)] [InlineData("Common Startup", false)] [InlineData("Startup", true)]
    public async Task HistoryTransactionKeepsIntentAndSameObjectThroughExactLedgerReadbackWithoutCommitting(string source, bool realShortcut)
    {
        using var store = HistoryTransactionStore(source); using var manager = store.History.Owned.Manager();
        var candidate = store.History.Payload; byte[] expected = [1, 2, 3];
        if (realShortcut)
        {
            await store.History.Owned.CreateLinkAsync(source);
            using var capture = manager.CaptureShortcutForDisable(source, "owned"); expected = capture.CopyOriginalBytes(); var input = capture.Input;
            candidate = candidate with { Binding = candidate.Binding with { Original = new(input.FileIdentity, input.Length, input.LinkCount, input.Sha256) } };
        }
        await using var transaction = await manager.PrepareHistoryDisableAsync(candidate, store.History.Record, () => store.DirectoryPath);
        Assert.Throws<InvalidOperationException>(() => transaction.VerifyForCommit());
        Assert.True(File.Exists(store.History.Owned.Paths.StartupBackupPath + ".transaction")); Assert.False(File.Exists(store.History.Owned.Paths.StartupBackupPath));
        using (var intent = JsonDocument.Parse(File.ReadAllBytes(store.History.Owned.Paths.StartupBackupPath + ".transaction")))
        {
            Assert.Equal("history-disable", intent.RootElement.GetProperty("operation").GetString());
            Assert.True(intent.RootElement.GetProperty("previousLedgerMissing").GetBoolean());
            Assert.Equal(transaction.TransactionId, intent.RootElement.GetProperty("transactionId").GetString());
            Assert.Equal(store.History.Record.Id, intent.RootElement.GetProperty("record").GetProperty("id").GetString());
        }
        await Assert.ThrowsAsync<TimeoutException>(() => RootWriteLease.AcquireAsync(store.History.Owned.Paths, timeout: TimeSpan.FromMilliseconds(30)));
        await Assert.ThrowsAsync<TimeoutException>(() => StartupMutationLease.AcquireAsync(store.History.Owned.Paths, timeout: TimeSpan.FromMilliseconds(30)));
        Assert.ThrowsAny<IOException>(() => File.AppendAllText(store.History.Owned.Paths.ConfigPath, "# cannot drift"));
        var result = await transaction.ArchiveAndPublishLedgerAsync();
        Assert.Equal("ledger_verified_uncommitted", result.State); Assert.True(result.NativeWriteAttempted); Assert.True(result.NativeWritePerformed);
        Assert.True(result.LedgerPublicationAttempted); Assert.True(result.LedgerVerified); Assert.False(result.Committed);
        Assert.False(result.NativeWriteReady); Assert.False(result.ExecutionRequested); transaction.VerifyForCommit();
        var records = StartupBackupLedger.Read(File.ReadAllBytes(store.History.Owned.Paths.StartupBackupPath), requireComplete: true).Records;
        Assert.Equal(store.History.Record, Assert.Single(records)); Assert.False(File.Exists(store.History.Record.Origin));
        Assert.Equal("candidate", store.Reader().Inspect(store.Context).State); Assert.True(File.Exists(store.History.Owned.Paths.StartupBackupPath + ".transaction"));
        Assert.Equal(result, await transaction.ArchiveAndPublishLedgerAsync(checkpoint: _ => throw new IOException("Do not repeat.")));
        Assert.DoesNotContain(StartupMutationOperation.NativeRollback, store.History.Owned.Operations.Calls);
        Assert.DoesNotContain(StartupMutationOperation.IntentCleanup, store.History.Owned.Operations.Calls);
        Assert.ThrowsAny<IOException>(() => File.Delete(store.History.Record.BackupPath!));
        await transaction.DisposeAsync(); Assert.Throws<ObjectDisposedException>(() => transaction.VerifyForCommit());
        Assert.Equal(expected, File.ReadAllBytes(store.History.Record.BackupPath!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreAsync(store.History.Record.Id));
        await using var root = await RootWriteLease.AcquireAsync(store.History.Owned.Paths, timeout: TimeSpan.FromMilliseconds(100));
    }

    [Theory]
    [InlineData("archive_prepared", false, false)] [InlineData("before_native", false, false)]
    [InlineData("native_renamed", true, false)] [InlineData("native_verified", true, false)]
    [InlineData("before_ledger", true, false)] [InlineData("ledger_published", true, true)] [InlineData("ledger_verified", true, true)]
    public async Task HistoryTransactionFaultsRetainActualNativeAndLedgerEvidence(string boundary, bool moved, bool ledger)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var transaction = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        var result = await transaction.ArchiveAndPublishLedgerAsync(checkpoint: stage => { if (stage == boundary) { throw new IOException("Owned transaction fault."); } });
        Assert.Equal(moved ? "unverified_result" : "rejected", result.State); Assert.Equal(moved, result.NativeWritePerformed); Assert.False(result.LedgerVerified);
        Assert.Equal(!moved, File.Exists(store.History.Record.Origin)); Assert.Equal(moved, File.Exists(store.History.Record.BackupPath));
        Assert.Equal(ledger, File.Exists(store.History.Owned.Paths.StartupBackupPath)); AssertHistoryTransactionEvidence(store);
        Assert.Throws<InvalidOperationException>(() => transaction.VerifyForCommit()); await transaction.DisposeAsync();
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(moved ? store.History.Record.BackupPath! : store.History.Record.Origin));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreAsync(store.History.Record.Id));
    }

    [Theory]
    [InlineData("before_native", false, false)] [InlineData("native_renamed", true, false)]
    [InlineData("before_ledger", true, false)] [InlineData("ledger_published", true, true)]
    public async Task HistoryTransactionCancellationNeverDeletesIntentOrRollsBackNative(string boundary, bool moved, bool ledger)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var transaction = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        using var cancel = new CancellationTokenSource();
        var result = await transaction.ArchiveAndPublishLedgerAsync(cancel.Token, stage => { if (stage == boundary) { cancel.Cancel(); } });
        Assert.Equal(moved ? "unverified_result" : "rejected", result.State); Assert.Equal(moved, result.NativeWritePerformed); Assert.False(result.LedgerVerified);
        Assert.Equal(ledger, File.Exists(store.History.Owned.Paths.StartupBackupPath)); AssertHistoryTransactionEvidence(store);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HistoryTransactionIntentPublicationFailureNeverMovesAndPreservesPossiblePublishedIntent(bool after)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        void Fail(StartupMutationOperation operation) { if (operation == StartupMutationOperation.IntentPublication) { throw new IOException("Intent fault."); } }
        if (after) { store.History.Owned.Operations.After = Fail; } else { store.History.Owned.Operations.Before = Fail; }
        await Assert.ThrowsAsync<IOException>(() => manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath));
        Assert.Equal(after, File.Exists(store.History.Owned.Paths.StartupBackupPath + ".transaction")); Assert.True(File.Exists(store.History.Record.Origin));
        Assert.False(File.Exists(store.History.Record.BackupPath)); Assert.False(File.Exists(store.History.Owned.Paths.StartupBackupPath));
        Assert.Equal("candidate", store.Reader().Inspect(store.Context).State); Assert.Empty(Directory.GetFiles(store.History.Owned.Root, ".*.tmp"));
        await using var root = await RootWriteLease.AcquireAsync(store.History.Owned.Paths, timeout: TimeSpan.FromMilliseconds(100));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HistoryTransactionLedgerPublicationFaultNeverCompensatesEvenIfThePublishReturned(bool after)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var transaction = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        void Fail(StartupMutationOperation operation) { if (operation == StartupMutationOperation.LedgerPublication) { throw new IOException("Ledger fault."); } }
        if (after) { store.History.Owned.Operations.After = Fail; } else { store.History.Owned.Operations.Before = Fail; }
        var result = await transaction.ArchiveAndPublishLedgerAsync();
        Assert.Equal("unverified_result", result.State); Assert.True(result.NativeWritePerformed); Assert.True(result.LedgerPublicationAttempted); Assert.False(result.LedgerVerified);
        Assert.Equal(after, File.Exists(store.History.Owned.Paths.StartupBackupPath)); AssertHistoryTransactionEvidence(store);
    }

    [Theory]
    [InlineData("before_native")] [InlineData("native_renamed")] [InlineData("before_ledger_guard")] [InlineData("ledger_published")]
    public async Task HistoryTransactionDoesNotBlindlyOverwriteAChangedLedgerOrClaimItsReadback(string boundary)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var transaction = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        var foreign = "[]"u8.ToArray();
        if (boundary == "before_ledger_guard")
        { store.History.Owned.Operations.Before = operation => { if (operation == StartupMutationOperation.LedgerPublication) { File.WriteAllBytes(store.History.Owned.Paths.StartupBackupPath, foreign); } }; }
        var result = await transaction.ArchiveAndPublishLedgerAsync(checkpoint: stage =>
        { if (stage == boundary) { File.WriteAllBytes(store.History.Owned.Paths.StartupBackupPath, foreign); } });
        Assert.Equal(boundary == "before_native" ? "rejected" : "unverified_result", result.State);
        Assert.Equal(boundary != "before_native", result.NativeWritePerformed); Assert.False(result.LedgerVerified);
        Assert.Equal(foreign, File.ReadAllBytes(store.History.Owned.Paths.StartupBackupPath)); AssertHistoryTransactionEvidence(store);
    }

    [Theory]
    [InlineData("intent")] [InlineData("ledger")]
    public async Task HistoryTransactionCommitPreconditionRejectsPostReadbackDrift(string field)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var transaction = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        Assert.True((await transaction.ArchiveAndPublishLedgerAsync()).LedgerVerified);
        File.WriteAllBytes(store.History.Owned.Paths.StartupBackupPath + (field == "intent" ? ".transaction" : ""), "[]"u8.ToArray());
        Assert.Throws<InvalidOperationException>(() => transaction.VerifyForCommit());
        Assert.True(File.Exists(store.History.Record.BackupPath)); Assert.False(File.Exists(store.History.Record.Origin));
    }

    [Theory]
    [InlineData("existing-record")] [InlineData("bad-ledger")] [InlineData("draft")] [InlineData("config")]
    public async Task HistoryTransactionRejectsOldRecordsInvalidLedgerAndChangedDraftOrCurrentConfigurationBeforeIntent(string mode)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var record = store.History.Record;
        if (mode == "existing-record") { File.WriteAllBytes(store.History.Owned.Paths.StartupBackupPath, StartupBackupLedger.Write(new[] { record })); }
        if (mode == "bad-ledger") { File.WriteAllText(store.History.Owned.Paths.StartupBackupPath, "[{}"); }
        if (mode == "draft") { record = record with { Arguments = "not the bound draft" }; }
        if (mode == "config") { File.WriteAllText(store.History.Owned.Paths.ConfigPath, "version: 1\nsettings:\n  backup_dir: unrelated\ntasks: []\n"); }
        var error = await Record.ExceptionAsync(() => manager.PrepareHistoryDisableAsync(store.History.Payload, record, () => store.DirectoryPath));
        Assert.NotNull(error); Assert.True(error is InvalidOperationException or JsonException);
        Assert.False(File.Exists(store.History.Owned.Paths.StartupBackupPath + ".transaction")); Assert.False(Directory.Exists(store.DirectoryPath));
        Assert.True(File.Exists(store.History.Record.Origin)); Assert.False(File.Exists(store.History.Record.BackupPath));
    }

    [Fact]
    public async Task HistoryTransactionAppendsWithoutDroppingUnrelatedLegacyRecords()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        var previous = store.History.Record with { Id = Guid.NewGuid().ToString("N"), Name = "unrelated", Source = "HKCU", BackupPath = null,
            Origin = @"HKCU\Software\Owned", RawCommand = "preserve exactly" };
        File.WriteAllBytes(store.History.Owned.Paths.StartupBackupPath, StartupBackupLedger.Write(new[] { previous }));
        await using var transaction = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        Assert.True((await transaction.ArchiveAndPublishLedgerAsync()).LedgerVerified);
        var records = StartupBackupLedger.Read(File.ReadAllBytes(store.History.Owned.Paths.StartupBackupPath), requireComplete: true).Records;
        Assert.Equal(new[] { previous, store.History.Record }, records); AssertHistoryTransactionEvidence(store);
    }

    [Fact]
    public async Task HistoryTransactionCapacityFailurePrecedesCandidateIntentAndNative()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        var records = Enumerable.Range(0, StartupBackupLedger.MaximumRecords).Select(index => store.History.Record with
            { Id = Guid.NewGuid().ToString("N"), Source = "HKCU", Name = "prior-" + index, BackupPath = null }).ToArray();
        var before = StartupBackupLedger.Write(records); File.WriteAllBytes(store.History.Owned.Paths.StartupBackupPath, before);
        await Assert.ThrowsAsync<JsonException>(() => manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath));
        Assert.Equal(before, File.ReadAllBytes(store.History.Owned.Paths.StartupBackupPath)); Assert.False(Directory.Exists(store.DirectoryPath));
        Assert.False(File.Exists(store.History.Owned.Paths.StartupBackupPath + ".transaction")); Assert.True(File.Exists(store.History.Record.Origin));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HistoryTransactionNeverOverwritesAPreexistingOrLateLegacyIntent(bool late)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        var path = store.History.Owned.Paths.StartupBackupPath + ".transaction";
        var legacy = JsonSerializer.SerializeToUtf8Bytes(new { operation = "disable", record = store.History.Record });
        if (!late) { File.WriteAllBytes(path, legacy); }
        else { store.History.Owned.Operations.Before = operation => { if (operation == StartupMutationOperation.IntentPublication) { File.WriteAllBytes(path, legacy); } }; }
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath));
        Assert.Equal(legacy, File.ReadAllBytes(path)); Assert.True(File.Exists(store.History.Record.Origin)); Assert.False(File.Exists(store.History.Record.BackupPath));
        Assert.Empty(Directory.GetFiles(store.History.Owned.Root, ".*.tmp"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HistoryTransactionCleanupFailureAndAggregateDoNotLoseTheKnownNativeFact(bool aggregate)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager();
        await using var transaction = await manager.PrepareHistoryDisableAsync(store.History.Payload, store.History.Record, () => store.DirectoryPath);
        store.History.Owned.Operations.Before = operation =>
        {
            if (operation == StartupMutationOperation.TemporaryCleanup || aggregate && operation == StartupMutationOperation.LedgerPublication)
            { throw new IOException("Owned publication/cleanup failure."); }
        };
        var result = await transaction.ArchiveAndPublishLedgerAsync();
        Assert.Equal("unverified_result", result.State); Assert.True(result.NativeWritePerformed); Assert.False(result.LedgerVerified);
        Assert.Equal(!aggregate, File.Exists(store.History.Owned.Paths.StartupBackupPath));
        Assert.True(File.Exists(store.History.Owned.Paths.StartupBackupPath + ".transaction"));
        Assert.Equal(aggregate ? 1 : 0, Directory.GetFiles(store.History.Owned.Root, ".*.tmp").Length);
        Assert.DoesNotContain(StartupMutationOperation.NativeRollback, store.History.Owned.Operations.Calls);
        Assert.Equal(result, await transaction.ArchiveAndPublishLedgerAsync());
    }

    private static HistoryStoreFixture HistoryTransactionStore(string source = "Startup")
    {
        var store = new HistoryStoreFixture(source: source, committed: false, create: false, privateParent: true);
        File.WriteAllText(store.History.Owned.Paths.ConfigPath, "version: 1\nsettings:\n  backup_dir: history-archive\n  default_startup_delay: 0\ntasks: []\n");
        return store;
    }
    private static void AssertHistoryTransactionEvidence(HistoryStoreFixture store)
    {
        Assert.True(File.Exists(store.History.Owned.Paths.StartupBackupPath + ".transaction"));
        Assert.Equal("candidate", store.Reader().Inspect(store.Context).State);
        Assert.Empty(Directory.GetFiles(store.History.Owned.Root, ".*.tmp"));
        Assert.DoesNotContain(StartupMutationOperation.NativeRollback, store.History.Owned.Operations.Calls);
        Assert.DoesNotContain(StartupMutationOperation.IntentCleanup, store.History.Owned.Operations.Calls);
    }
}
