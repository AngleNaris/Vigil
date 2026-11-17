using System.ComponentModel;
using System.IO;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;
using Xunit;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup")] [InlineData("Common Startup")]
    public async Task HistoryDraftIsServerDerivedWithoutPublishingAndItsFreshOwnerCanCompleteTheInternalChain(string source)
    {
        using var store = HistoryTransactionStore(source); using var manager = store.History.Owned.Manager();
        var entry = await DraftEntry(store, manager, source); var revision = await ConfigurationRevision.ReadAsync(store.History.Owned.Paths);
        var before = DraftDataImages(store); var draft = await manager.CaptureHistoryDisableDraftAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), revision);
        var again = await manager.CaptureHistoryDisableDraftAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), revision);
        AssertDraftNoAuthority(draft); Assert.Equal(revision, draft.ConfigurationRevision); Assert.InRange(draft.ObservedAt, draft.Candidate.CandidateAt, DateTimeOffset.UtcNow);
        Assert.Equal("candidate", draft.Candidate.Phase); Assert.Equal(1, draft.Candidate.Sequence); Assert.Null(draft.Candidate.CommittedAt);
        Assert.NotEqual(store.History.Record.Id, draft.Record.Id); Assert.NotEqual(draft.Record.Id, again.Record.Id); Assert.NotEqual(draft.Candidate.Nonce, again.Candidate.Nonce);
        Assert.Equal("disable", draft.Record.Action); Assert.Equal("disabled", draft.Record.Disposition); Assert.Null(draft.Record.RegistryKind);
        Assert.Equal(entry.RawCommand, draft.Record.RawCommand); Assert.Equal(entry.Target, draft.Record.Target); Assert.Equal(entry.Arguments, draft.Record.Arguments);
        Assert.Equal(entry.WorkingDirectory, draft.Record.WorkingDirectory); Assert.Equal(entry.Origin, draft.Record.Origin); Assert.Equal(entry.Name, draft.Record.Name);
        Assert.Equal(draft.Record.DisabledAt, draft.Candidate.CandidateAt); Assert.Equal(draft.Record.Id, draft.Candidate.Binding.RecordId);
        Assert.Equal(StartupHistoryFormat.RecordDigest(draft.Record), draft.Candidate.Binding.RecordDigest);
        Assert.Equal(Path.Combine(store.History.Owned.Root, "history-archive", "startup", draft.Record.Id + ".lnk"), draft.Record.BackupPath);
        using (var capture = manager.CaptureShortcutForDisable(source, entry.Name))
        {
            var input = capture.Input; Assert.Equal(StartupHistoryProtection.Actor(), draft.Candidate.Binding.ActorSid);
            Assert.Equal(new StartupHistoryOriginal(input.FileIdentity, input.Length, input.LinkCount, input.Sha256), draft.Candidate.Binding.Original);
        }
        AssertDraftDataUnchanged(store, before); Assert.False(Directory.Exists(store.DirectoryPath)); Assert.Empty(store.History.Owned.Operations.Calls);
        var original = File.ReadAllBytes(entry.Origin);
        await using var owner = await manager.PrepareHistoryDisableAsync(draft, () => store.DirectoryPath);
        Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified); Assert.True((await owner.PublishCommittedHistoryAsync()).Committed);
        Assert.True((await owner.FinalizeCommittedAsync()).TransactionFinalized); await owner.DisposeAsync();
        Assert.Equal(original, File.ReadAllBytes(draft.Record.BackupPath!)); Assert.False(File.Exists(entry.Origin));
        Assert.Equal("protected_committed", new StartupHistoryStoreReader(store.History.Owned.Root, () => store.DirectoryPath).Inspect(draft.Candidate.Binding.Context).State);
        AssertDraftNoAuthority(draft); store.History.Owned.AssertNoTransactionOrTemps(); Assert.Equal(0, store.History.Owned.Native.WritableOpens);
    }

    [Theory]
    [InlineData("config")] [InlineData("overrides")] [InlineData("preferences")]
    public async Task HistoryDraftConsumptionRechecksTheCompleteWorkspaceRevisionBeforePublishing(string mode)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var draft = await manager.CaptureHistoryDisableDraftAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), await ConfigurationRevision.ReadAsync(store.History.Owned.Paths));
        var paths = store.History.Owned.Paths;
        if (mode == "config") { File.AppendAllText(paths.ConfigPath, "\n"); }
        else { File.WriteAllText(mode == "overrides" ? paths.OverridesPath : paths.GuiSettingsPath, "{}"); }
        var before = DraftDataImages(store);
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => manager.PrepareHistoryDisableAsync(draft, () => store.DirectoryPath));
        AssertDraftDataUnchanged(store, before); Assert.False(Directory.Exists(store.DirectoryPath)); Assert.Empty(store.History.Owned.Operations.Calls);
        store.History.Owned.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData("content")] [InlineData("identity")] [InlineData("scope")]
    public async Task HistoryDraftConsumptionNeverTreatsItsSnapshotAsContinuousNativeAuthority(string mode)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var draft = await manager.CaptureHistoryDisableDraftAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), await ConfigurationRevision.ReadAsync(store.History.Owned.Paths));
        if (mode != "scope")
        {
            var bytes = File.ReadAllBytes(entry.Origin);
            if (mode == "content") { bytes[^1] ^= 1; }
            else { Assert.Equal(Path.Combine(store.History.Owned.Native.GetShortcutDirectory("Startup"), "owned.lnk"), entry.Origin); File.Delete(entry.Origin); }
            File.WriteAllBytes(entry.Origin, bytes);
        }
        else { store.History.Owned.Native.BeforeDirectory = () => throw new IOException("Scope unavailable."); }
        var before = DraftDataImages(store);
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => manager.PrepareHistoryDisableAsync(draft, () => store.DirectoryPath)); }
        finally { store.History.Owned.Native.BeforeDirectory = null; }
        AssertDraftDataUnchanged(store, before); Assert.False(Directory.Exists(store.DirectoryPath)); Assert.Empty(store.History.Owned.Operations.Calls);
    }

    [Theory]
    [InlineData("base")] [InlineData("archive")] [InlineData("occupied")]
    public async Task HistoryDraftConsumptionRejectsStaleArchiveContextOrOccupiedDestinationBeforePublishing(string mode)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var draft = await manager.CaptureHistoryDisableDraftAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), await ConfigurationRevision.ReadAsync(store.History.Owned.Paths));
        var archiveBase = Path.Combine(store.History.Owned.Root, "history-archive"); var archive = Path.Combine(archiveBase, "startup");
        if (mode == "occupied")
        {
            Assert.Equal(Path.Combine(archive, draft.Record.Id + ".lnk"), draft.Record.BackupPath); File.WriteAllBytes(draft.Record.BackupPath!, [7, 8, 9]);
        }
        else
        {
            var moved = mode == "base" ? archiveBase : archive; var parked = Path.Combine(store.History.Owned.Root, "parked-archive");
            Assert.Equal(mode == "base" ? draft.Candidate.Binding.ArchiveBase.Path : draft.Candidate.Binding.Archive.Path, moved);
            Assert.StartsWith(store.History.Owned.Root + Path.DirectorySeparatorChar, parked, StringComparison.Ordinal); Assert.False(Directory.Exists(parked));
            Directory.Move(moved, parked); Directory.CreateDirectory(archive);
        }
        var before = DraftDataImages(store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.PrepareHistoryDisableAsync(draft, () => store.DirectoryPath));
        AssertDraftDataUnchanged(store, before); Assert.False(Directory.Exists(store.DirectoryPath)); Assert.Empty(store.History.Owned.Operations.Calls);
        store.History.Owned.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData("registry")] [InlineData("disabled")] [InlineData("rebuild")] [InlineData("bad-name")]
    [InlineData("invalid-revision")] [InlineData("uppercase-revision")]
    public async Task HistoryDraftFactoryRejectsOtherIntentsAndMalformedSelectionsWithoutDataWrites(string mode)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var selection = StartupInspectionRequest.FromEntry(entry); var revision = await ConfigurationRevision.ReadAsync(store.History.Owned.Paths);
        switch (mode)
        {
            case "registry": selection = selection with { Source = "HKCU" }; break;
            case "disabled": selection = selection with { IsDisabled = true }; break;
            case "rebuild": selection = selection with { RebuildInput = new(Path.Combine(store.History.Owned.Root, "never-run.exe")) }; break;
            case "bad-name": selection = selection with { Name = "../other" }; break;
            case "invalid-revision": revision = "not-a-revision"; break;
            case "uppercase-revision": revision = "A" + revision[1..]; break;
        }
        var before = DraftDataImages(store);
        await Assert.ThrowsAsync<ArgumentException>(() => manager.CaptureHistoryDisableDraftAsync(entry.Id, selection, revision));
        AssertDraftDataUnchanged(store, before); Assert.False(Directory.Exists(store.DirectoryPath)); Assert.Empty(store.History.Owned.Operations.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HistoryDraftFactoryRequiresTheExactFreshDisplayedEntry(bool wrongId)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var selection = StartupInspectionRequest.FromEntry(entry); if (!wrongId) { selection = selection with { MetadataDigest = new string('0', 64) }; }
        var before = DraftDataImages(store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CaptureHistoryDisableDraftAsync(wrongId ? "missing" : entry.Id, selection,
            ConfigurationRevision.ReadAsync(store.History.Owned.Paths).GetAwaiter().GetResult()));
        AssertDraftDataUnchanged(store, before); Assert.False(Directory.Exists(store.DirectoryPath));
    }

    [Theory]
    [InlineData("draft_input_pinned")] [InlineData("draft_metadata_read")] [InlineData("draft_verified")]
    public async Task HistoryDraftFactoryCancellationNeverPublishesOrMoves(string boundary)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var before = DraftDataImages(store); using var cancellation = new CancellationTokenSource(); var revision = await ConfigurationRevision.ReadAsync(store.History.Owned.Paths);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.CaptureHistoryDisableDraftAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), revision,
            cancellation.Token, stage => { if (stage == boundary) { cancellation.Cancel(); } }));
        AssertDraftDataUnchanged(store, before); Assert.False(Directory.Exists(store.DirectoryPath)); Assert.Empty(store.History.Owned.Operations.Calls);
        await using var root = await RootWriteLease.AcquireAsync(store.History.Owned.Paths, timeout: TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task HistoryDraftFactoryPinsTheOriginalDuringFreshComProjectionWithoutWeakeningSharing()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager); var pinned = false;
        var before = DraftDataImages(store); var revision = await ConfigurationRevision.ReadAsync(store.History.Owned.Paths);
        var draft = await manager.CaptureHistoryDisableDraftAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), revision, checkpoint: stage =>
        {
            if (stage != "draft_input_pinned") { return; } pinned = true;
            Assert.ThrowsAny<IOException>(() => File.AppendAllText(entry.Origin, "changed"));
            Assert.ThrowsAny<IOException>(() => File.Move(entry.Origin, Path.Combine(store.History.Owned.Root, "other.lnk")));
            Assert.ThrowsAny<IOException>(() => File.Delete(entry.Origin));
        });
        Assert.True(pinned); AssertDraftNoAuthority(draft); AssertDraftDataUnchanged(store, before);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HistoryDraftFactoryRejectsLateNonMainConfigurationDrift(bool preferences)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var path = preferences ? store.History.Owned.Paths.GuiSettingsPath : store.History.Owned.Paths.OverridesPath;
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => manager.CaptureHistoryDisableDraftAsync(entry.Id, StartupInspectionRequest.FromEntry(entry),
            ConfigurationRevision.ReadAsync(store.History.Owned.Paths).GetAwaiter().GetResult(), checkpoint: stage => { if (stage == "draft_metadata_read") { File.WriteAllText(path, "{}"); } }));
        Assert.Equal("{}", File.ReadAllText(path)); Assert.False(Directory.Exists(store.DirectoryPath)); Assert.Empty(store.History.Owned.Operations.Calls);
        store.History.Owned.AssertNoTransactionOrTemps(); Assert.True(File.Exists(entry.Origin));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HistoryDraftFactoryDoesNotCreateMissingArchiveContext(bool removeBase)
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        var archiveBase = Path.Combine(store.History.Owned.Root, "history-archive"); var archive = Path.Combine(archiveBase, "startup");
        Assert.Equal(store.History.Payload.Binding.Archive.Path, archive); Assert.Empty(Directory.EnumerateFileSystemEntries(archive)); Directory.Delete(archive);
        if (removeBase) { Assert.Empty(Directory.EnumerateFileSystemEntries(archiveBase)); Directory.Delete(archiveBase); }
        var before = DraftDataImages(store);
        await Assert.ThrowsAsync<IOException>(() => manager.CaptureHistoryDisableDraftAsync(entry.Id, StartupInspectionRequest.FromEntry(entry),
            ConfigurationRevision.ReadAsync(store.History.Owned.Paths).GetAwaiter().GetResult()));
        AssertDraftDataUnchanged(store, before); Assert.False(Directory.Exists(archive)); Assert.False(Directory.Exists(store.DirectoryPath));
    }

    [Theory]
    [InlineData("Startup")] [InlineData("Common Startup")]
    public async Task HistoryDraftFactoryRejectsAnArchiveInsideEitherFixedStartupFolder(string source)
    {
        using var store = HistoryTransactionStore(source); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager, source);
        var other = source == "Startup" ? "Common Startup" : "Startup";
        var archiveBase = Path.Combine(store.History.Owned.Native.GetShortcutDirectory(other), "contained-archive");
        Assert.StartsWith(store.History.Owned.Root + Path.DirectorySeparatorChar, archiveBase, StringComparison.Ordinal);
        Directory.CreateDirectory(Path.Combine(archiveBase, "startup"));
        File.WriteAllText(store.History.Owned.Paths.ConfigPath, $"version: 1\nsettings:\n  backup_dir: '{archiveBase}'\ntasks: []\n");
        var revision = await ConfigurationRevision.ReadAsync(store.History.Owned.Paths); var before = DraftDataImages(store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CaptureHistoryDisableDraftAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), revision));
        AssertDraftDataUnchanged(store, before); Assert.False(Directory.Exists(store.DirectoryPath)); Assert.Empty(store.History.Owned.Operations.Calls);
    }

    [Fact]
    public async Task HistoryDraftFactoryDoesNotIssueHistoryForExistingUntrustedRows()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var entry = await DraftEntry(store, manager);
        File.WriteAllBytes(store.History.Owned.Paths.StartupBackupPath, StartupBackupLedger.Write(new[] { store.History.Record }));
        var draft = await manager.CaptureHistoryDisableDraftAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), await ConfigurationRevision.ReadAsync(store.History.Owned.Paths));
        await using var owner = await manager.PrepareHistoryDisableAsync(draft, () => store.DirectoryPath); Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified);
        Assert.True((await owner.PublishCommittedHistoryAsync()).Committed); Assert.True((await owner.FinalizeCommittedAsync()).TransactionFinalized); await owner.DisposeAsync();
        var records = StartupBackupLedger.Read(File.ReadAllBytes(store.History.Owned.Paths.StartupBackupPath), requireComplete: true).Records;
        Assert.Equal(store.History.Record, records[0]); Assert.Equal(draft.Record, records[1]);
        var index = StartupHistoryProtection.UnprotectIndex(File.ReadAllBytes(store.IndexPath)); Assert.Equal(draft.Record.Id, Assert.Single(index.Entries).Binding.RecordId);
        Assert.False(File.Exists(store.ProofPath)); AssertDraftNoAuthority(draft);
    }

    private static async Task<StartupEntry> DraftEntry(HistoryStoreFixture store, StartupManager manager, string source = "Startup")
    { await store.History.Owned.CreateLinkAsync(source); return Assert.Single((await manager.GetSnapshotAsync()).Entries, entry => entry.Source == source && !entry.IsDisabled); }
    private static Dictionary<string, byte[]> DraftDataImages(HistoryStoreFixture store) => Directory.GetFiles(store.History.Owned.Root, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) is not (RootWriteLease.FileName or StartupMutationLease.FileName)).ToDictionary(path => path, File.ReadAllBytes);
    private static void AssertDraftDataUnchanged(HistoryStoreFixture store, Dictionary<string, byte[]> before)
    { var after = DraftDataImages(store); Assert.Equal(before.Keys.Order(), after.Keys.Order()); foreach (var (path, bytes) in before) { Assert.Equal(bytes, after[path]); } }
    private static void AssertDraftNoAuthority(StartupHistoryDisableDraft draft)
    { Assert.False(draft.NativeWriteReady); Assert.False(draft.NativeWritePerformed); Assert.False(draft.ExecutionRequested); Assert.False(draft.Committed); }
}
