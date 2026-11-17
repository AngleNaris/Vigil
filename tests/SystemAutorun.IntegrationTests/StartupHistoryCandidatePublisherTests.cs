using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup")] [InlineData("Common Startup")]
    public void HistoryCandidatePublisherCreatesClosedPrivateStoreAndReadsBackOnlyCandidate(string source)
    {
        using var store = new HistoryStoreFixture(source: source, privateParent: true, committed: false, create: false);
        var result = HistoryPublisher(store).Publish(store.History.Payload);
        Assert.Equal("candidate", result.State); Assert.True(result.ProofPublished); Assert.True(result.IndexPublished); AssertPublicationNoAuthority(result, store);
        Assert.Equal("candidate", store.Reader().Inspect(store.Context).State);
        Assert.Equal(3, Directory.GetFiles(store.DirectoryPath).Length); Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.tmp"));
        var acl = new DirectoryInfo(store.DirectoryPath).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        Assert.True(acl.AreAccessRulesProtected); Assert.Equal(store.Actor, acl.GetOwner(typeof(SecurityIdentifier)));
        var index = StartupHistoryProtection.UnprotectIndex(File.ReadAllBytes(store.IndexPath));
        Assert.Equal(2, index.Generation); Assert.Equal("candidate", Assert.Single(index.Entries).Latest.Phase);
    }

    [Fact]
    public void HistoryCandidatePublisherHasNoCommittedBackfillEntryPoint()
    {
        using var store = new HistoryStoreFixture(privateParent: true, committed: true, create: false);
        var result = HistoryPublisher(store).Publish(store.History.Payload);
        Assert.Equal("rejected", result.State); Assert.False(Directory.Exists(store.DirectoryPath)); AssertPublicationNoAuthority(result, store);
    }

    [Fact]
    public void HistoryCandidatePublisherAddsDistinctSlotsButNeverReplacesExistingSlotOrRecord()
    {
        using var store = new HistoryStoreFixture(privateParent: true, committed: false, create: false); var publisher = HistoryPublisher(store);
        Assert.Equal("candidate", publisher.Publish(store.History.Payload).State);
        var second = HistoryNextCandidate(store); Assert.Equal("candidate", publisher.Publish(second).State);
        var before = File.ReadAllBytes(store.IndexPath);
        Assert.Equal("candidate", publisher.Publish(store.History.Payload).State);
        Assert.Equal("conflict", publisher.Publish(second with { Binding = second.Binding with { RecordId = Guid.NewGuid().ToString("N") } }).State);
        Assert.Equal(before, File.ReadAllBytes(store.IndexPath));
        var index = StartupHistoryProtection.UnprotectIndex(before); Assert.Equal(3, index.Generation); Assert.Equal(2, index.Entries.Count);
        Assert.Equal("candidate", store.Reader().Inspect(second.Binding.Context).State);
    }

    [Theory]
    [InlineData("store_opened", false, false)] [InlineData("index_read", false, false)]
    [InlineData("bootstrap_flushed", false, false)] [InlineData("bootstrap_published", false, false)]
    [InlineData("proof_flushed", false, false)] [InlineData("proof_published", true, false)]
    [InlineData("index_flushed", true, false)] [InlineData("index_published", true, true)] [InlineData("index_verified", true, true)]
    public void HistoryCandidatePublisherFaultBoundariesRetainPublishedEvidenceAndRemoveOnlyOwnedStages(string boundary, bool proof, bool index)
    {
        using var store = new HistoryStoreFixture(privateParent: true, committed: false, create: false);
        var result = HistoryPublisher(store).Publish(store.History.Payload, checkpoint: stage =>
        { if (stage == boundary) { throw new IOException("owned candidate boundary"); } });
        Assert.Equal(index ? "uncertain" : proof ? "partial" : "rejected", result.State); Assert.Equal(proof, result.ProofPublished); Assert.Equal(index, result.IndexPublished);
        Assert.Equal(proof, File.Exists(store.ProofPath));
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.tmp")); AssertPublicationNoAuthority(result, store);
        Assert.Equal(index ? "candidate" : "unverified", store.Reader().Inspect(store.Context).State);
        using (File.Open(Path.Combine(store.DirectoryPath, StartupNativeSlotProbe.HistoryStoreWriteLease.LockName), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        var oldProof = proof ? File.ReadAllBytes(store.ProofPath) : null;
        var retry = HistoryPublisher(store).Publish(store.History.Payload); Assert.Equal("candidate", retry.State); AssertPublicationNoAuthority(retry, store);
        if (oldProof is not null) { Assert.Equal(oldProof, File.ReadAllBytes(store.ProofPath)); }
        Assert.Single(StartupHistoryProtection.UnprotectIndex(File.ReadAllBytes(store.IndexPath)).Entries);
    }

    [Theory]
    [InlineData("proof_flushed", false, false)] [InlineData("proof_published", true, false)] [InlineData("index_published", true, true)]
    public void HistoryCandidatePublisherCancellationNeverMeansPublishedStateWasRolledBack(string boundary, bool proof, bool index)
    {
        using var store = new HistoryStoreFixture(privateParent: true, committed: false, create: false); using var canceled = new CancellationTokenSource();
        var result = HistoryPublisher(store).Publish(store.History.Payload, canceled.Token, stage => { if (stage == boundary) { canceled.Cancel(); } });
        Assert.Equal("history_candidate_canceled", result.Reason); Assert.Equal(index ? "uncertain" : proof ? "partial" : "rejected", result.State);
        Assert.Equal(proof, File.Exists(store.ProofPath)); Assert.Equal(index ? "candidate" : "unverified", store.Reader().Inspect(store.Context).State);
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.tmp")); AssertPublicationNoAuthority(result, store);
        using (File.Open(Path.Combine(store.DirectoryPath, StartupNativeSlotProbe.HistoryStoreWriteLease.LockName), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    [Fact]
    public void HistoryCandidatePublisherUsesOnePhysicalLockAcrossTwoRoots()
    {
        using var first = new HistoryStoreFixture(privateParent: true, committed: false, create: false);
        using var second = new HistoryStoreFixture(privateParent: true, committed: false, create: false); StartupHistoryPublication? competing = null;
        var other = new StartupHistoryCandidatePublisher(second.History.Owned.Root, () => first.DirectoryPath);
        var result = HistoryPublisher(first).Publish(first.History.Payload, checkpoint: stage =>
        { if (stage == "store_opened") { competing = other.Publish(second.History.Payload); } });
        Assert.Equal("candidate", result.State); Assert.NotNull(competing); Assert.Equal("rejected", competing.State);
        Assert.False(competing.ProofPublished); AssertPublicationNoAuthority(competing, second);
        Assert.Single(StartupHistoryProtection.UnprotectIndex(File.ReadAllBytes(first.IndexPath)).Entries);
        Assert.False(File.Exists(Path.Combine(first.DirectoryPath, second.History.Payload.Binding.RecordId + ".dpapi")));
    }

    [Fact]
    public void HistoryCandidatePublisherCannotReplaceAnIndexHeldByReaderAndDoesNotRollbackProof()
    {
        using var store = new HistoryStoreFixture(privateParent: true, committed: false, create: false); var publisher = HistoryPublisher(store);
        Assert.Equal("candidate", publisher.Publish(store.History.Payload).State); var before = File.ReadAllBytes(store.IndexPath);
        var next = HistoryNextCandidate(store); StartupHistoryPublication? publishing = null;
        var original = store.Reader().Inspect(store.Context, afterIndexRead: () => publishing = publisher.Publish(next));
        Assert.Equal("candidate", original.State); Assert.NotNull(publishing); Assert.Equal("partial", publishing.State);
        Assert.True(publishing.ProofPublished); Assert.False(publishing.IndexPublished); Assert.True(publishing.NativeError is 5 or 32);
        Assert.Equal(before, File.ReadAllBytes(store.IndexPath));
        Assert.True(File.Exists(Path.Combine(store.DirectoryPath, next.Binding.RecordId + ".dpapi")));
        Assert.Equal("unverified", store.Reader().Inspect(next.Binding.Context).State); AssertPublicationNoAuthority(publishing, store);
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.tmp"));
        var proof = File.ReadAllBytes(Path.Combine(store.DirectoryPath, next.Binding.RecordId + ".dpapi"));
        Assert.Equal("candidate", publisher.Publish(next).State);
        Assert.Equal(proof, File.ReadAllBytes(Path.Combine(store.DirectoryPath, next.Binding.RecordId + ".dpapi")));
        Assert.Equal(2, StartupHistoryProtection.UnprotectIndex(File.ReadAllBytes(store.IndexPath)).Entries.Count);
    }

    [Theory]
    [InlineData("missing-index")] [InlineData("damaged-index")] [InlineData("shadow-proof")]
    public void HistoryCandidatePublisherDoesNotReinitializeLostAuthorityOrOverwriteExistingEvidence(string mode)
    {
        using var store = new HistoryStoreFixture(privateParent: true, committed: false); var next = HistoryNextCandidate(store);
        var proofBefore = File.ReadAllBytes(store.ProofPath);
        if (mode == "missing-index") { File.Delete(store.IndexPath); }
        if (mode == "damaged-index") { store.WritePrivateFile(store.IndexPath, [3, 4, 5]); }
        var shadow = Path.Combine(store.DirectoryPath, next.Binding.RecordId + ".dpapi");
        if (mode == "shadow-proof") { store.WritePrivateFile(shadow, [7, 8, 9]); }
        var indexBefore = File.Exists(store.IndexPath) ? File.ReadAllBytes(store.IndexPath) : null;
        var result = HistoryPublisher(store).Publish(next);
        Assert.Equal(mode == "missing-index" ? "damaged" : mode == "shadow-proof" ? "conflict" : "rejected", result.State);
        Assert.False(result.ProofPublished); Assert.Equal(proofBefore, File.ReadAllBytes(store.ProofPath));
        if (indexBefore is null) { Assert.False(File.Exists(store.IndexPath)); } else { Assert.Equal(indexBefore, File.ReadAllBytes(store.IndexPath)); }
        if (mode == "shadow-proof") { Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(shadow)); }
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.tmp")); AssertPublicationNoAuthority(result, store);
    }

    [Theory]
    [InlineData("lock-data")] [InlineData("lock-hardlink")] [InlineData("lock-reparse")] [InlineData("directory-acl")]
    public void HistoryCandidatePublisherRejectsUnsafePrivateStateWithoutRepair(string mode)
    {
        using var store = new HistoryStoreFixture(privateParent: true, committed: false);
        var lockPath = Path.Combine(store.DirectoryPath, StartupNativeSlotProbe.HistoryStoreWriteLease.LockName);
        if (mode == "lock-data") { store.WritePrivateFile(lockPath, [1]); }
        if (mode == "lock-hardlink")
        {
            store.WritePrivateFile(lockPath, []); Assert.True(CreateHistoryHardLink(Path.Combine(store.History.Owned.Root, "owned-lock-alias.bin"), lockPath, IntPtr.Zero));
        }
        var target = Path.Combine(store.History.Owned.Root, "never-write-lock-target.bin");
        if (mode == "lock-reparse") { File.WriteAllBytes(target, [5, 6, 7]); File.CreateSymbolicLink(lockPath, target); }
        if (mode == "directory-acl")
        {
            var info = new DirectoryInfo(store.DirectoryPath); var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Write, AccessControlType.Allow)); info.SetAccessControl(acl);
        }
        var before = File.ReadAllBytes(store.IndexPath); var result = HistoryPublisher(store).Publish(HistoryNextCandidate(store));
        Assert.Equal("rejected", result.State); Assert.Equal(before, File.ReadAllBytes(store.IndexPath)); AssertPublicationNoAuthority(result, store);
        if (mode == "lock-data") { Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(lockPath)); }
        if (mode == "lock-reparse") { Assert.Equal(new byte[] { 5, 6, 7 }, File.ReadAllBytes(target)); File.Delete(lockPath); }
    }

    [Theory]
    [InlineData("impersonation")] [InlineData("inside-root")] [InlineData("mapping")]
    public void HistoryCandidatePublisherRejectsWrongExecutionContextWithoutGrantingAuthority(string mode)
    {
        using var store = new HistoryStoreFixture(privateParent: true, committed: false, create: false); StartupHistoryPublication result;
        if (mode == "impersonation")
        { using var identity = WindowsIdentity.GetCurrent(); result = WindowsIdentity.RunImpersonated(identity.AccessToken, () => HistoryPublisher(store).Publish(store.History.Payload)); }
        else if (mode == "inside-root")
        { result = new StartupHistoryCandidatePublisher(store.History.Owned.Root, () => Path.Combine(store.History.Owned.Root, "forbidden-history")).Publish(store.History.Payload); }
        else
        {
            var count = 0; result = new StartupHistoryCandidatePublisher(store.History.Owned.Root, () => ++count == 1 ? store.DirectoryPath : store.DirectoryPath + ".changed").Publish(store.History.Payload);
            Assert.Equal("uncertain", result.State); Assert.True(result.IndexPublished); Assert.False(Directory.Exists(store.DirectoryPath + ".changed"));
        }
        Assert.NotEqual("candidate", result.State); AssertPublicationNoAuthority(result, store);
        if (mode != "mapping") { Assert.False(Directory.Exists(store.DirectoryPath)); }
    }

    [Theory]
    [InlineData(1, true)] [InlineData(2, false)]
    public void HistoryCandidatePublisherCreatesOnlyTheBoundedMissingDirectoryLevels(int extraLevels, bool allowed)
    {
        using var store = new HistoryStoreFixture(privateParent: true, committed: false, create: false);
        var location = extraLevels == 1 ? Path.Combine(store.DirectoryPath, "state") : Path.Combine(store.DirectoryPath, "a", "state");
        var result = new StartupHistoryCandidatePublisher(store.History.Owned.Root, () => location).Publish(store.History.Payload);
        Assert.Equal(allowed ? "candidate" : "rejected", result.State); AssertPublicationNoAuthority(result, store);
        if (allowed) { Assert.Equal("candidate", new StartupHistoryStoreReader(store.History.Owned.Root, () => location).Inspect(store.Context).State); }
        else { Assert.False(Directory.Exists(store.DirectoryPath)); }
    }

    [Theory]
    [InlineData("missing-record")] [InlineData("latest-hash")] [InlineData("nonce")] [InlineData("committed-latest")]
    public void HistoryCandidatePublisherDoesNotRepairMissingProofOrRollBackAnExistingLatest(string mode)
    {
        using var store = new HistoryStoreFixture(committed: false, privateParent: true); var payload = store.History.Payload;
        if (mode == "missing-record") { File.Delete(store.ProofPath); }
        if (mode == "nonce") { payload = payload with { Nonce = Guid.NewGuid().ToString("N") }; }
        if (mode is "latest-hash" or "committed-latest")
        {
            var row = store.Index.Entries[0]; var latest = mode == "latest-hash" ? row.Latest with { ProtectedDigest = new string('a', 64) } : row.Latest with { Phase = "committed" };
            store.PublishIndex(store.Index with { Entries = new[] { row with { Latest = latest } } });
        }
        var before = File.ReadAllBytes(store.IndexPath); var result = HistoryPublisher(store).Publish(payload);
        Assert.Equal(mode == "missing-record" ? "damaged" : "conflict", result.State);
        if (mode == "missing-record") { Assert.Equal("history_candidate_record_missing", result.Reason); Assert.False(File.Exists(store.ProofPath)); }
        Assert.Equal(before, File.ReadAllBytes(store.IndexPath)); AssertPublicationNoAuthority(result, store);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HistoryCandidatePublisherAdoptsOnlyAnExactProtectedOrphanAgainstAValidIndependentIndex(bool conflicting)
    {
        using var store = new HistoryStoreFixture(committed: false, privateParent: true);
        store.PublishIndex(store.Index with { Entries = Array.Empty<StartupHistoryIndexEntry>() });
        var before = File.ReadAllBytes(store.ProofPath); var beforeIndex = File.ReadAllBytes(store.IndexPath);
        var payload = conflicting ? store.History.Payload with { Nonce = Guid.NewGuid().ToString("N") } : store.History.Payload;
        var result = HistoryPublisher(store).Publish(payload);
        Assert.Equal(conflicting ? "conflict" : "candidate", result.State); Assert.Equal(before, File.ReadAllBytes(store.ProofPath));
        if (conflicting) { Assert.Equal(beforeIndex, File.ReadAllBytes(store.IndexPath)); }
        else { Assert.Equal("candidate", store.Reader().Inspect(store.Context).State); }
        AssertPublicationNoAuthority(result, store);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HistoryCandidatePublisherRejectsExhaustedGenerationAndCapacityBeforePublishingProof(bool capacity)
    {
        using var store = new HistoryStoreFixture(committed: false, privateParent: true); var row = store.Index.Entries[0];
        var index = capacity ? store.Index with { Entries = Enumerable.Range(0, StartupHistoryIndexFormat.MaximumEntries).Select(number => row with
        { Binding = row.Binding with { RecordId = Guid.NewGuid().ToString("N"), Name = "entry-" + number } }).ToArray() }
            : store.Index with { Generation = long.MaxValue };
        store.PublishIndex(index); var before = File.ReadAllBytes(store.IndexPath); var next = HistoryNextCandidate(store);
        var result = HistoryPublisher(store).Publish(next);
        Assert.Equal(capacity ? "conflict" : "rejected", result.State); Assert.False(result.ProofPublished);
        Assert.Equal(before, File.ReadAllBytes(store.IndexPath)); Assert.False(File.Exists(Path.Combine(store.DirectoryPath, next.Binding.RecordId + ".dpapi")));
        AssertPublicationNoAuthority(result, store);
    }

    [Fact]
    public void HistoryCandidatePublisherRejectsAnUnsafeOwnedContainerWithoutRepairingIt()
    {
        using var store = new HistoryStoreFixture(committed: false, create: false, privateParent: true);
        var info = new DirectoryInfo(Path.GetDirectoryName(store.DirectoryPath)!); var original = info.GetAccessControl();
        var binary = original.GetSecurityDescriptorBinaryForm(); var sddl = original.GetSecurityDescriptorSddlForm(AccessControlSections.All);
        try
        {
            var changed = info.GetAccessControl(); changed.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Write, AccessControlType.Allow));
            info.SetAccessControl(changed); var unsafeSddl = info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            var result = HistoryPublisher(store).Publish(store.History.Payload); Assert.Equal("rejected", result.State); Assert.Equal("history_candidate_permissions_invalid", result.Reason);
            Assert.False(Directory.Exists(store.DirectoryPath)); Assert.Equal(unsafeSddl, info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
            AssertPublicationNoAuthority(result, store);
        }
        finally
        {
            var restore = info.GetAccessControl(); restore.SetSecurityDescriptorBinaryForm(binary); info.SetAccessControl(restore);
            Assert.Equal(sddl, info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        }
    }

    private static StartupHistoryCandidatePublisher HistoryPublisher(HistoryStoreFixture store) => new(store.History.Owned.Root, () => store.DirectoryPath);
    private static StartupHistoryPayload HistoryNextCandidate(HistoryStoreFixture store) => store.History.Payload with
    { Nonce = Guid.NewGuid().ToString("N"), Binding = store.History.Payload.Binding with { RecordId = Guid.NewGuid().ToString("N"), Name = "next-owned", RecordDigest = new string('b', 64) } };
    private static void AssertPublicationNoAuthority(StartupHistoryPublication result, HistoryStoreFixture store)
    {
        Assert.False(result.NativeWriteReady); Assert.False(result.NativeWritePerformed); Assert.False(result.ExecutionRequested);
        AssertHistoryNoAuthority(new("unverified", null), store.History);
    }
}
