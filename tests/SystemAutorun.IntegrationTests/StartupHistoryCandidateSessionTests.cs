using System.IO;
using System.Security.Principal;
using SystemAutorun.Windows.Startup;
using Xunit;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup")] [InlineData("Common Startup")]
    public void HistoryCandidateSessionMovesTheSameHeldObjectAndRetainsAllLeasesWithoutCommitting(string source)
    {
        using var store = new HistoryStoreFixture(source: source, committed: false, create: false, privateParent: true);
        using var manager = store.History.Owned.Manager(); var capture = manager.CaptureShortcutForDisable(source, "owned");
        using var attempt = HistoryPublisher(store).OpenSession(store.History.Payload); Assert.Equal("candidate", attempt.Publication.State);
        var session = Assert.IsType<StartupHistoryCandidateSession>(attempt.Session); var stages = new List<string>();
        var result = session.ArchiveCapturedShortcut(capture, () => store.Context.ArchiveBase.Path, checkpoint: stage => stages.Add(stage));
        Assert.Equal("archived_uncommitted", result.State); Assert.True(result.NativeWriteAttempted); Assert.True(result.NativeWritePerformed);
        Assert.False(result.Committed); Assert.False(result.NativeWriteReady); Assert.False(result.ExecutionRequested);
        Assert.Equal(new[] { "archive_prepared", "before_native", "native_renamed", "native_verified" }, stages);
        Assert.False(File.Exists(store.History.Record.Origin!)); Assert.True(File.Exists(store.History.Record.BackupPath!));
        Assert.Equal(new byte[] { 1, 2, 3 }, capture.CopyOriginalBytes()); session.VerifyCurrent();
        Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(store.History.Record.BackupPath!, [4]));
        Assert.ThrowsAny<IOException>(() => File.Delete(store.History.Record.BackupPath!));
        Assert.ThrowsAny<IOException>(() => Directory.Move(store.Context.Source.Directory.Path, store.Context.Source.Directory.Path + ".moved"));
        Assert.ThrowsAny<IOException>(() => Directory.Move(store.Context.ArchiveBase.Path, store.Context.ArchiveBase.Path + ".moved"));
        using (var blocked = HistoryPublisher(store).OpenSession(store.History.Payload)) { Assert.Null(blocked.Session); Assert.Equal("rejected", blocked.Publication.State); }
        Assert.Equal(result, session.ArchiveCapturedShortcut(capture, () => throw new InvalidOperationException("Must not retry native.")));
        AssertHistorySessionNoCommit(store, attempt); Assert.Equal("candidate", store.Reader().Inspect(store.Context).State);
        session.Dispose(); Assert.Throws<ObjectDisposedException>(() => capture.CopyOriginalBytes());
        Assert.Throws<ObjectDisposedException>(() => session.VerifyCurrent());
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(store.History.Record.BackupPath!));
        using (File.Open(store.History.Record.BackupPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Assert.Equal("candidate", HistoryPublisher(store).Publish(store.History.Payload).State);
    }

    [Theory]
    [InlineData("archive_prepared", false)] [InlineData("before_native", false)]
    [InlineData("native_renamed", true)] [InlineData("native_verified", true)]
    public void HistoryCandidateSessionFaultsNeverReverseAMaybePublishedNativeResult(string boundary, bool moved)
    {
        using var store = new HistoryStoreFixture(committed: false, create: false, privateParent: true);
        using var manager = store.History.Owned.Manager(); var capture = manager.CaptureShortcutForDisable("Startup", "owned");
        using var attempt = HistoryPublisher(store).OpenSession(store.History.Payload); var session = Assert.IsType<StartupHistoryCandidateSession>(attempt.Session);
        var result = session.ArchiveCapturedShortcut(capture, () => store.Context.ArchiveBase.Path,
            checkpoint: stage => { if (stage == boundary) { throw new IOException("Owned native-stage fault."); } });
        Assert.Equal(moved ? "unverified_result" : "rejected", result.State); Assert.Equal(moved, result.NativeWriteAttempted); Assert.Equal(moved, result.NativeWritePerformed);
        Assert.Equal(!moved, File.Exists(store.History.Record.Origin!)); Assert.Equal(moved, File.Exists(store.History.Record.BackupPath!));
        session.VerifyCurrent(); AssertHistorySessionNoCommit(store, attempt); Assert.Equal("candidate", store.Reader().Inspect(store.Context).State);
        session.Dispose(); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(moved ? store.History.Record.BackupPath! : store.History.Record.Origin!));
        Assert.Equal("candidate", HistoryPublisher(store).Publish(store.History.Payload).State);
    }

    [Theory]
    [InlineData("archive_prepared", false)] [InlineData("before_native", false)]
    [InlineData("native_renamed", true)] [InlineData("native_verified", true)]
    public void HistoryCandidateSessionCancellationPreservesCandidateAndNativeAttemptTruth(string boundary, bool moved)
    {
        using var store = new HistoryStoreFixture(committed: false, create: false, privateParent: true);
        using var manager = store.History.Owned.Manager(); var capture = manager.CaptureShortcutForDisable("Startup", "owned");
        using var attempt = HistoryPublisher(store).OpenSession(store.History.Payload); var session = Assert.IsType<StartupHistoryCandidateSession>(attempt.Session);
        using var cancel = new CancellationTokenSource();
        var result = session.ArchiveCapturedShortcut(capture, () => store.Context.ArchiveBase.Path, cancel.Token,
            stage => { if (stage == boundary) { cancel.Cancel(); } });
        Assert.Equal("history_native_canceled", result.Reason); Assert.Equal(moved ? "unverified_result" : "rejected", result.State);
        Assert.Equal(moved, result.NativeWriteAttempted); Assert.Equal(moved, result.NativeWritePerformed);
        Assert.Equal(!moved, File.Exists(store.History.Record.Origin!)); Assert.Equal(moved, File.Exists(store.History.Record.BackupPath!));
        AssertHistorySessionNoCommit(store, attempt); session.Dispose();
        using (File.Open(moved ? store.History.Record.BackupPath! : store.History.Record.Origin!, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    [Theory]
    [InlineData("leaf-id")] [InlineData("hash")] [InlineData("source-id")] [InlineData("archive-id")] [InlineData("archive-case")]
    public void HistoryCandidateSessionRequiresLiveInputAndArchiveBindingNotJustProtectedCandidate(string mode)
    {
        using var store = new HistoryStoreFixture(committed: false, create: false, privateParent: true); var candidate = store.History.Payload; var binding = candidate.Binding;
        var changed = binding.Original.Identity with { FileId = new string('a', 32) };
        binding = mode switch
        {
            "leaf-id" => binding with { Original = binding.Original with { Identity = changed } },
            "hash" => binding with { Original = binding.Original with { Sha256 = new string('a', 64) } },
            "source-id" => binding with { Source = binding.Source with { Directory = binding.Source.Directory with { Identity = changed } } },
            "archive-id" => binding with { Archive = binding.Archive with { Identity = changed } },
            _ => binding with { Archive = binding.Archive with { CaseSensitivity = binding.Archive.CaseSensitivity == "insensitive" ? "sensitive" : "insensitive" } },
        };
        using var manager = store.History.Owned.Manager(); var capture = manager.CaptureShortcutForDisable("Startup", "owned");
        using var attempt = HistoryPublisher(store).OpenSession(candidate with { Binding = binding }); var session = Assert.IsType<StartupHistoryCandidateSession>(attempt.Session);
        var result = session.ArchiveCapturedShortcut(capture, () => store.Context.ArchiveBase.Path);
        Assert.Equal("rejected", result.State); Assert.False(result.NativeWriteAttempted); Assert.False(result.NativeWritePerformed);
        Assert.True(File.Exists(store.History.Record.Origin!)); Assert.False(File.Exists(store.History.Record.BackupPath!)); AssertHistorySessionNoCommit(store, attempt);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HistoryCandidateSessionNeverReplacesAnArchiveSlotIncludingAKernelTimeCollision(bool late)
    {
        using var store = new HistoryStoreFixture(committed: false, create: false, privateParent: true);
        using var manager = store.History.Owned.Manager(); var capture = manager.CaptureShortcutForDisable("Startup", "owned");
        using var attempt = HistoryPublisher(store).OpenSession(store.History.Payload); var session = Assert.IsType<StartupHistoryCandidateSession>(attempt.Session);
        if (!late) { File.WriteAllBytes(store.History.Record.BackupPath!, [8, 9]); }
        var result = session.ArchiveCapturedShortcut(capture, () => store.Context.ArchiveBase.Path,
            checkpoint: stage => { if (late && stage == "before_native") { File.WriteAllBytes(store.History.Record.BackupPath!, [8, 9]); } });
        Assert.Equal(late ? "unverified_result" : "rejected", result.State); Assert.Equal(late, result.NativeWriteAttempted); Assert.False(result.NativeWritePerformed);
        Assert.True(File.Exists(store.History.Record.Origin!)); Assert.Equal(new byte[] { 8, 9 }, File.ReadAllBytes(store.History.Record.BackupPath!));
        AssertHistorySessionNoCommit(store, attempt);
    }

    [Theory]
    [InlineData("wrong-config")] [InlineData("scope-change")] [InlineData("closed-input")]
    public void HistoryCandidateSessionRefusesStaleConfigurationScopeAndDisposedInputBeforeNative(string mode)
    {
        using var store = new HistoryStoreFixture(committed: false, create: false, privateParent: true);
        using var manager = store.History.Owned.Manager(); var capture = manager.CaptureShortcutForDisable("Startup", "owned");
        using var attempt = HistoryPublisher(store).OpenSession(store.History.Payload); var session = Assert.IsType<StartupHistoryCandidateSession>(attempt.Session);
        if (mode == "closed-input") { capture.Dispose(); }
        var configured = mode == "wrong-config" ? store.History.Owned.Root : store.Context.ArchiveBase.Path;
        var result = session.ArchiveCapturedShortcut(capture, () => configured, checkpoint: stage =>
        { if (mode == "scope-change" && stage == "before_native") { store.History.Owned.Native.BeforeDirectory = () => throw new IOException("Owned scope observation unavailable."); } });
        Assert.Equal("rejected", result.State); Assert.False(result.NativeWriteAttempted); Assert.False(result.NativeWritePerformed);
        Assert.True(File.Exists(store.History.Record.Origin!)); Assert.False(File.Exists(store.History.Record.BackupPath!)); AssertHistorySessionNoCommit(store, attempt);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HistoryCandidateSessionDoesNotRollbackWhenOriginalReappearsOrConfigurationDriftsAfterRename(bool configDrift)
    {
        using var store = new HistoryStoreFixture(committed: false, create: false, privateParent: true);
        using var manager = store.History.Owned.Manager(); var capture = manager.CaptureShortcutForDisable("Startup", "owned");
        using var attempt = HistoryPublisher(store).OpenSession(store.History.Payload); var session = Assert.IsType<StartupHistoryCandidateSession>(attempt.Session);
        var configured = store.Context.ArchiveBase.Path;
        var result = session.ArchiveCapturedShortcut(capture, () => configured, checkpoint: stage =>
        {
            if (stage != "native_renamed") { return; }
            if (configDrift) { configured = store.History.Owned.Root; } else { File.WriteAllBytes(store.History.Record.Origin!, [9]); }
        });
        Assert.Equal("unverified_result", result.State); Assert.True(result.NativeWritePerformed); Assert.True(result.NativeWriteAttempted);
        Assert.True(File.Exists(store.History.Record.BackupPath!)); Assert.Equal(!configDrift, File.Exists(store.History.Record.Origin!));
        session.Dispose(); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(store.History.Record.BackupPath!));
        if (!configDrift) { Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(store.History.Record.Origin!)); }
        AssertHistorySessionNoCommit(store, attempt);
    }

    [Fact]
    public void HistoryCandidateSessionKeepsThePhysicalStoreLeaseAcrossOtherRootsUntilDisposed()
    {
        using var first = new HistoryStoreFixture(committed: false, create: false, privateParent: true);
        using var other = new HistoryStoreFixture(committed: false, create: false, privateParent: true);
        using var attempt = HistoryPublisher(first).OpenSession(first.History.Payload); Assert.NotNull(attempt.Session);
        var secondPublisher = new StartupHistoryCandidatePublisher(other.History.Owned.Root, () => first.DirectoryPath);
        using (var blocked = secondPublisher.OpenSession(other.History.Payload)) { Assert.Null(blocked.Session); Assert.Equal("rejected", blocked.Publication.State); }
        attempt.Session.Dispose(); Assert.Equal("candidate", secondPublisher.Publish(other.History.Payload).State);
        Assert.Equal(2, StartupHistoryProtection.UnprotectIndex(File.ReadAllBytes(first.IndexPath)).Entries.Count);
        AssertHistorySessionNoCommit(first, attempt);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HistoryCandidateSessionRejectsRealSameUserImpersonationAndLiveStoreMappingDrift(bool mapping)
    {
        using var store = new HistoryStoreFixture(committed: false, create: false, privateParent: true);
        using var manager = store.History.Owned.Manager(); var capture = manager.CaptureShortcutForDisable("Startup", "owned");
        var selected = store.DirectoryPath; var publisher = new StartupHistoryCandidatePublisher(store.History.Owned.Root, () => selected);
        using var attempt = publisher.OpenSession(store.History.Payload); var session = Assert.IsType<StartupHistoryCandidateSession>(attempt.Session);
        StartupHistoryArchiveResult result;
        if (mapping)
        {
            selected = store.History.Owned.Root;
            result = session.ArchiveCapturedShortcut(capture, () => store.Context.ArchiveBase.Path);
        }
        else
        {
            using var identity = WindowsIdentity.GetCurrent();
            result = WindowsIdentity.RunImpersonated(identity.AccessToken, () => session.ArchiveCapturedShortcut(capture, () => store.Context.ArchiveBase.Path));
        }
        Assert.Equal("rejected", result.State); Assert.False(result.NativeWriteAttempted); Assert.False(result.NativeWritePerformed);
        Assert.True(File.Exists(store.History.Record.Origin!)); Assert.False(File.Exists(store.History.Record.BackupPath!));
        AssertHistorySessionNoCommit(store, attempt);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HistoryCandidateSessionDoesNotCreateOrFollowAMissingOrReparsedArchive(bool reparse)
    {
        using var store = new HistoryStoreFixture(committed: false, create: false, privateParent: true);
        VerifyEvidenceOwnedPath(store.History.Owned, store.Context.Archive.Path); Directory.Delete(store.Context.Archive.Path);
        var target = Path.Combine(store.History.Owned.Root, "owned-archive-target"); VerifyEvidenceOwnedPath(store.History.Owned, target);
        if (reparse) { Directory.CreateDirectory(target); Directory.CreateSymbolicLink(store.Context.Archive.Path, target); }
        try
        {
            using var manager = store.History.Owned.Manager(); var capture = manager.CaptureShortcutForDisable("Startup", "owned");
            using var attempt = HistoryPublisher(store).OpenSession(store.History.Payload); var session = Assert.IsType<StartupHistoryCandidateSession>(attempt.Session);
            var result = session.ArchiveCapturedShortcut(capture, () => store.Context.ArchiveBase.Path);
            Assert.Equal("rejected", result.State); Assert.False(result.NativeWriteAttempted); Assert.False(result.NativeWritePerformed);
            Assert.True(File.Exists(store.History.Record.Origin!)); Assert.Equal(reparse, Directory.Exists(store.Context.Archive.Path));
            if (reparse) { Assert.Empty(Directory.EnumerateFileSystemEntries(target)); }
            AssertHistorySessionNoCommit(store, attempt);
        }
        finally { if (reparse) { VerifyEvidenceOwnedPath(store.History.Owned, store.Context.Archive.Path); Directory.Delete(store.Context.Archive.Path); } }
    }

    [Theory]
    [InlineData("nonce")] [InlineData("sequence")] [InlineData("original")] [InlineData("body-time")]
    public void HistoryCandidateSessionRejectsASelfConsistentDifferentProtectedCandidateBeforeNative(string mode)
    {
        using var store = new HistoryStoreFixture(committed: false, create: false, privateParent: true);
        using var manager = store.History.Owned.Manager(); var capture = manager.CaptureShortcutForDisable("Startup", "owned");
        using var attempt = HistoryPublisher(store).OpenSession(store.History.Payload); var session = Assert.IsType<StartupHistoryCandidateSession>(attempt.Session);
        var changed = mode switch
        {
            "nonce" => store.History.Payload with { Nonce = Guid.NewGuid().ToString("N") },
            "sequence" => store.History.Payload with { Sequence = 2 },
            "original" => store.History.Payload with { Binding = store.History.Payload.Binding with
                { Original = store.History.Payload.Binding.Original with { Sha256 = new string('a', 64) } } },
            _ => store.History.Payload with { CandidateAt = store.History.Payload.CandidateAt.AddTicks(1) },
        };
        var bytes = StartupHistoryProtection.Protect(changed); var index = StartupHistoryProtection.UnprotectIndex(File.ReadAllBytes(store.IndexPath));
        store.WritePrivateFile(store.ProofPath, bytes);
        store.PublishIndex(index with { Generation = index.Generation + 1, Entries = new[] { new StartupHistoryIndexEntry(changed.Binding, HistoryLatest(changed, bytes)) } });
        // Both authentic leaves agree with each other and the context-only reader,
        // but not with this live session's exact active candidate.
        Assert.Equal("candidate", store.Reader().Inspect(store.Context).State);
        var result = session.ArchiveCapturedShortcut(capture, () => store.Context.ArchiveBase.Path);
        Assert.Equal("rejected", result.State); Assert.False(result.NativeWriteAttempted); Assert.False(result.NativeWritePerformed);
        Assert.True(File.Exists(store.History.Record.Origin!)); Assert.False(File.Exists(store.History.Record.BackupPath!));
        AssertHistorySessionNoCommit(store, attempt);
    }

    private static void AssertHistorySessionNoCommit(HistoryStoreFixture store, StartupHistoryCandidateSessionAttempt attempt)
    {
        Assert.False(attempt.Publication.NativeWriteReady); Assert.False(attempt.Publication.NativeWritePerformed); Assert.False(attempt.Publication.ExecutionRequested);
        Assert.Equal(0, store.History.Owned.Native.WritableOpens); Assert.Empty(store.History.Owned.Operations.Calls);
        Assert.False(File.Exists(store.History.Owned.Paths.StartupBackupPath)); Assert.False(File.Exists(store.History.Owned.Paths.StartupBackupPath + ".transaction"));
        Assert.All(StartupHistoryProtection.UnprotectIndex(File.ReadAllBytes(store.IndexPath)).Entries, entry => Assert.Equal("candidate", entry.Latest.Phase));
    }
}
