using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup", false)] [InlineData("Startup", true)]
    [InlineData("Common Startup", false)] [InlineData("Common Startup", true)]
    public void HistoryOperationStoreReadsRealPrivateFilesButDoesNotAttestFixtureIssuanceOrNativeOutcome(string source, bool committed)
    {
        using var disk = new OperationDiskFixture(source, committed); var before = disk.Images(); var root = OperationImages(disk.Store.History);
        var result = disk.Reader().Inspect(disk.Store.Context, disk.Link.Reference);
        Assert.Equal(committed ? "associated_committed" : "associated_candidate", result.State); Assert.Equal("history_correlation_only", result.Reason);
        Assert.Equal(disk.Link.TransactionId, result.TransactionId); Assert.Equal(disk.Store.Context.RecordId, result.RecordId);
        disk.AssertImages(before); AssertOperationUnchanged(disk.Store.History, root);
        Assert.Equal(committed ? "protected_committed" : "candidate", disk.Store.Reader().Inspect(disk.Store.Context).State);
        Assert.Equal(committed ? 4 : 3, before.Count); Assert.DoesNotContain(before.Keys, path => Path.GetFileName(path) == "writer-v1.lock");
    }

    [Theory]
    [InlineData("directory")] [InlineData("index")] [InlineData("record")] [InlineData("parent")] [InlineData("operation")] [InlineData("row")]
    public void HistoryOperationStoreMissingMaterialNeverCreatesRepairsOrGuesses(string missing)
    {
        using var disk = new OperationDiskFixture(create: missing != "directory");
        if (missing == "index") { File.Delete(disk.Store.IndexPath); }
        if (missing == "record") { File.Delete(disk.CurrentPath); }
        if (missing == "parent") { File.Delete(disk.Store.ProofPath); }
        if (missing == "operation") { File.Delete(disk.LinkPath); }
        if (missing == "row") { disk.Store.PublishIndex(disk.Store.Index with { Entries = [] }); }
        var before = disk.Images(); var result = disk.Reader().Inspect(disk.Store.Context, disk.Link.Reference);
        Assert.Equal(missing == "operation" ? "unlinked" : "unverified", result.State);
        Assert.Equal(missing == "row" ? "history_store_record_not_current" : "history_store_" + (missing == "directory" ? "location" : missing) + "_missing", result.Reason);
        Assert.Null(result.RecordId); Assert.Null(result.TransactionId); disk.AssertImages(before); disk.AssertNoNative();
        if (missing == "directory") { Assert.False(Directory.Exists(disk.Store.DirectoryPath)); }
    }

    [Fact]
    public void HistoryOperationStoreMissingLinkDoesNotRequireLegacyParentOrDowngradeLegacyHistory()
    {
        using var legacy = new HistoryStoreFixture(); var before = File.ReadAllBytes(legacy.ProofPath);
        var expected = new StartupHistoryOperationReference(Guid.CreateVersion7().ToString("N"), new string('a', 64), Guid.NewGuid().ToString("N"));
        var result = new StartupHistoryOperationStoreReader(legacy.History.Owned.Root, () => legacy.DirectoryPath).Inspect(legacy.Context, expected);
        Assert.Equal("unlinked", result.State); Assert.Equal("history_store_operation_missing", result.Reason); Assert.Null(result.RecordId); Assert.Null(result.TransactionId);
        Assert.Equal("protected_committed", legacy.Reader().Inspect(legacy.Context).State); Assert.Equal(before, File.ReadAllBytes(legacy.ProofPath));
        Assert.Equal(2, Directory.GetFiles(legacy.DirectoryPath).Length); Assert.Equal(0, legacy.History.Owned.Native.WritableOpens); Assert.Empty(legacy.History.Owned.Operations.Calls);
    }

    [Theory]
    [InlineData("index")] [InlineData("record")] [InlineData("parent")] [InlineData("link")]
    [InlineData("domain")] [InlineData("empty")] [InlineData("size")]
    [InlineData("operation-id")] [InlineData("fingerprint")] [InlineData("transaction")]
    public void HistoryOperationStoreRejectsChangedBytesDomainsAndCorrelationsWithoutExposingIds(string mode)
    {
        using var disk = new OperationDiskFixture();
        if (mode is "index" or "record" or "link")
        {
            var path = mode == "index" ? disk.Store.IndexPath : mode == "record" ? disk.CurrentPath : disk.LinkPath;
            var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; disk.Store.WritePrivateFile(path, bytes);
        }
        else if (mode == "parent") { disk.Store.WritePrivateFile(disk.Store.ProofPath, StartupHistoryProtection.Protect(disk.Store.History.Payload)); }
        else if (mode == "domain") { disk.Store.WritePrivateFile(disk.LinkPath, disk.Store.RecordBytes); }
        else if (mode == "empty") { disk.Store.WritePrivateFile(disk.LinkPath, []); }
        else if (mode == "size") { disk.Store.WritePrivateFile(disk.LinkPath, new byte[StartupHistoryProtection.MaximumProtectedBytes + 1]); }
        else
        {
            var changed = mode == "operation-id" ? disk.Link with { OperationId = Guid.CreateVersion7().ToString("N") }
                : mode == "fingerprint" ? disk.Link with { Fingerprint = new string('b', 64) } : disk.Link with { TransactionId = Guid.NewGuid().ToString("N") };
            disk.Store.WritePrivateFile(disk.LinkPath, StartupHistoryProtection.ProtectOperationLink(changed));
        }
        var before = disk.Images(); var result = disk.Reader().Inspect(disk.Store.Context, disk.Link.Reference);
        Assert.Equal("unverified", result.State); Assert.Null(result.RecordId); Assert.Null(result.TransactionId); disk.AssertImages(before); disk.AssertNoNative();
        if (mode is "operation-id" or "fingerprint" or "transaction") { Assert.Equal("history_operation_reference_changed", result.Reason); }
        if (mode == "parent") { Assert.Equal("history_operation_candidate_bytes_changed", result.Reason); }
        if (mode == "empty") { Assert.Equal("history_operation_payload_invalid", result.Reason); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HistoryOperationStorePinsEveryRequiredLeafAndDirectoryBeforeReturningIds(bool committed)
    {
        using var disk = new OperationDiskFixture(committed: committed); var images = disk.Images(); var seals = 0;
        var result = disk.Reader().Inspect(disk.Store.Context, disk.Link.Reference, afterIndexRead: () =>
        { Assert.Throws<IOException>(() => File.WriteAllBytes(disk.Store.IndexPath, [1])); }, beforeSeal: () =>
        {
            seals++;
            foreach (var path in images.Keys)
            {
                Assert.Throws<IOException>(() => File.WriteAllBytes(path, [1])); Assert.Throws<IOException>(() => File.Delete(path));
                Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));
            }
            Assert.Throws<IOException>(() => Directory.Move(disk.Store.DirectoryPath, disk.Store.DirectoryPath + ".moved"));
        });
        Assert.Equal(1, seals); Assert.Equal(committed ? "associated_committed" : "associated_candidate", result.State);
        disk.AssertImages(images); disk.AssertNoNative(); disk.RequireReleased();
    }

    [Theory]
    [InlineData("initial")] [InlineData("index")] [InlineData("seal")]
    public void HistoryOperationStoreCancellationDrainsAllRealReadHandles(string stage)
    {
        using var disk = new OperationDiskFixture(); var images = disk.Images(); using var canceled = new CancellationTokenSource();
        if (stage == "initial") { canceled.Cancel(); }
        Assert.ThrowsAny<OperationCanceledException>(() => disk.Reader().Inspect(disk.Store.Context, disk.Link.Reference, canceled.Token,
            afterIndexRead: stage == "index" ? canceled.Cancel : null, beforeSeal: stage == "seal" ? canceled.Cancel : null));
        disk.RequireReleased(); disk.AssertImages(images); disk.AssertNoNative();
        Assert.Equal("associated_committed", disk.Reader().Inspect(disk.Store.Context, disk.Link.Reference).State);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void HistoryOperationStoreMappingDriftDiscardsEvenALocallyMatchedOrUnlinkedResult(bool linked)
    {
        using var disk = new OperationDiskFixture(); if (!linked) { File.Delete(disk.LinkPath); }
        var mapping = disk.Store.DirectoryPath; var reader = new StartupHistoryOperationStoreReader(disk.Store.History.Owned.Root, () => mapping);
        var before = disk.Images(); var result = reader.Inspect(disk.Store.Context, disk.Link.Reference, beforeSeal: () => mapping += ".changed");
        Assert.Equal("unverified", result.State); Assert.Equal("history_store_mapping_changed", result.Reason);
        Assert.Null(result.RecordId); Assert.Null(result.TransactionId); Assert.False(Directory.Exists(mapping)); disk.AssertImages(before); disk.AssertNoNative();
    }

    [Theory]
    [InlineData("index")] [InlineData("record")] [InlineData("parent")] [InlineData("link")] [InlineData("directory")]
    public void HistoryOperationStoreLateAclDriftCannotLeakEarlierAssociationIds(string target)
    {
        using var disk = new OperationDiskFixture(); var path = target switch
        { "index" => disk.Store.IndexPath, "record" => disk.CurrentPath, "parent" => disk.Store.ProofPath, "link" => disk.LinkPath, _ => disk.Store.DirectoryPath };
        FileSystemSecurity Get() => target == "directory" ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        void Put(FileSystemSecurity acl) { if (acl is DirectorySecurity directory) { new DirectoryInfo(path).SetAccessControl(directory); } else { new FileInfo(path).SetAccessControl((FileSecurity)acl); } }
        var original = Get().GetSecurityDescriptorBinaryForm(); var images = disk.Images();
        try
        {
            var result = disk.Reader().Inspect(disk.Store.Context, disk.Link.Reference, beforeSeal: () =>
            {
                var acl = Get(); acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.WriteData, AccessControlType.Allow)); Put(acl);
            });
            Assert.Equal("unverified", result.State); Assert.Equal("history_store_permissions_invalid", result.Reason); Assert.Null(result.RecordId); Assert.Null(result.TransactionId);
        }
        finally { var acl = Get(); acl.SetSecurityDescriptorBinaryForm(original); Put(acl); Assert.Equal(original, Get().GetSecurityDescriptorBinaryForm()); }
        disk.RequireReleased(); disk.AssertImages(images); disk.AssertNoNative();
    }

    [Theory]
    [InlineData("parent")] [InlineData("link")] [InlineData("record")]
    public void HistoryOperationStoreDetectsSubstitutionBetweenIndexReadAndLeafOpen(string target)
    {
        using var disk = new OperationDiskFixture(); var result = disk.Reader().Inspect(disk.Store.Context, disk.Link.Reference, afterIndexRead: () =>
        {
            if (target == "parent") { disk.Store.WritePrivateFile(disk.Store.ProofPath, StartupHistoryProtection.Protect(disk.Store.History.Payload)); }
            else if (target == "link") { disk.Store.WritePrivateFile(disk.LinkPath, StartupHistoryProtection.ProtectOperationLink(disk.Link with { TransactionId = Guid.NewGuid().ToString("N") })); }
            else { var bytes = File.ReadAllBytes(disk.CurrentPath); bytes[^1] ^= 1; disk.Store.WritePrivateFile(disk.CurrentPath, bytes); }
        });
        Assert.Equal("unverified", result.State); Assert.Null(result.RecordId); Assert.Null(result.TransactionId); disk.AssertNoNative(); disk.RequireReleased();
        Assert.Equal(target == "parent" ? "history_operation_candidate_bytes_changed" : target == "link" ? "history_operation_reference_changed" : "history_protected_bytes_changed", result.Reason);
    }

    [Theory]
    [InlineData("inside")] [InlineData("context")] [InlineData("impersonation")]
    [InlineData("occupied-index")] [InlineData("occupied-record")] [InlineData("occupied-parent")] [InlineData("occupied-link")]
    public void HistoryOperationStoreRejectsWrongContextOrExclusiveOccupancy(string mode)
    {
        using var disk = new OperationDiskFixture(); var images = disk.Images(); StartupHistoryOperationCheck result;
        if (mode == "inside") { result = new StartupHistoryOperationStoreReader(disk.Store.History.Owned.Root, () => disk.Store.History.Owned.Root).Inspect(disk.Store.Context, disk.Link.Reference); }
        else if (mode == "context") { result = disk.Reader().Inspect(disk.Store.Context with { RecordDigest = new string('a', 64) }, disk.Link.Reference); }
        else if (mode == "impersonation") { using var user = WindowsIdentity.GetCurrent(); result = WindowsIdentity.RunImpersonated(user.AccessToken, () => disk.Reader().Inspect(disk.Store.Context, disk.Link.Reference)); }
        else
        {
            var path = mode switch { "occupied-index" => disk.Store.IndexPath, "occupied-record" => disk.CurrentPath, "occupied-parent" => disk.Store.ProofPath, _ => disk.LinkPath };
            using var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); result = disk.Reader().Inspect(disk.Store.Context, disk.Link.Reference);
        }
        Assert.Equal("unverified", result.State); Assert.Null(result.RecordId); Assert.Null(result.TransactionId); disk.AssertImages(images); disk.AssertNoNative(); disk.RequireReleased();
    }

    [Fact]
    public void HistoryOperationStoreDoesNotFollowHardlinkedOrReparseLinkFiles()
    {
        using var disk = new OperationDiskFixture(); var alias = Path.Combine(disk.Store.History.Owned.Root, "owned-operation-alias.bin");
        Assert.True(CreateHistoryHardLink(alias, disk.LinkPath, IntPtr.Zero));
        Assert.Equal("unverified", disk.Reader().Inspect(disk.Store.Context, disk.Link.Reference).State); File.Delete(alias);
        File.Delete(disk.LinkPath); var target = Path.Combine(disk.Store.History.Owned.Root, "never-read-operation-target.bin"); File.WriteAllBytes(target, [7, 8, 9]);
        File.CreateSymbolicLink(disk.LinkPath, target);
        using (File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { Assert.Equal("unverified", disk.Reader().Inspect(disk.Store.Context, disk.Link.Reference).State); }
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(target)); File.Delete(disk.LinkPath); disk.AssertNoNative();
    }

    [Fact]
    public void HistoryOperationStoreRequiresTheActualProjectDirectoryIdentity()
    {
        using var disk = new OperationDiskFixture(); var root = disk.Store.History.Owned.Root; var saved = root + ".saved-operation-root";
        Assert.Equal(Path.GetFullPath(saved), saved); Assert.False(Directory.Exists(saved)); Directory.Move(root, saved);
        try
        {
            Directory.CreateDirectory(root); var result = disk.Reader().Inspect(disk.Store.Context, disk.Link.Reference);
            Assert.Equal("unverified", result.State); Assert.Null(result.RecordId); Assert.Null(result.TransactionId);
        }
        finally { Assert.Empty(Directory.EnumerateFileSystemEntries(root)); Directory.Delete(root); Directory.Move(saved, root); }
        Assert.Equal("associated_committed", disk.Reader().Inspect(disk.Store.Context, disk.Link.Reference).State); disk.AssertNoNative();
    }

    [Theory]
    [InlineData("../outside")] [InlineData("00000000000000000000000000000000")] [InlineData("UPPER")]
    [InlineData("stream")] [InlineData("suffix")] [InlineData("proof")]
    public void HistoryOperationStoreNamesCannotBroadenReadOrPublicationPaths(string mode)
    {
        using var disk = new OperationDiskFixture(); var id = disk.Store.Context.RecordId; var name = mode switch
        {
            "UPPER" => new string('A', 32) + ".operation-v1.dpapi", "stream" => id + ".operation-v1.dpapi:alternate", "suffix" => id + ".operation-v1.dpapi.extra",
            "proof" => StartupHistoryStoreReader.ProofName(id, "candidate"), _ => mode,
        };
        Assert.False(StartupHistoryOperationStoreReader.IsLinkName(name));
        Assert.False(StartupHistoryStoreReader.IsProofName(StartupHistoryOperationStoreReader.LinkName(id)));
        if (mode != "proof")
        {
            using var lease = StartupNativeSlotProbe.HistoryDirectoryReadLease.Open(disk.Store.DirectoryPath, disk.Store.Context.Root, disk.Store.Actor.Value, default);
            Assert.Throws<ArgumentException>(() => lease.OpenFile(name, StartupHistoryProtection.MaximumProtectedBytes, default));
        }
        disk.AssertNoNative();
    }

    [Fact]
    public void HistoryOperationReadAdmissionDoesNotAuthorizeExistingStagePublication()
    {
        using var store = new HistoryStoreFixture(committed: false, privateParent: true);
        var name = StartupHistoryOperationStoreReader.LinkName(store.Context.RecordId); var target = Path.Combine(store.DirectoryPath, name);
        using (var writer = StartupNativeSlotProbe.HistoryStoreWriteLease.Open(store.DirectoryPath, store.Context.Root, store.Actor.Value, default))
        using (var stage = writer.CreateStage(store.RecordBytes, default))
        { Assert.Throws<ArgumentException>(() => stage.Publish(name, replaceIndex: false, default)); }
        Assert.False(File.Exists(target)); Assert.DoesNotContain(Directory.GetFiles(store.DirectoryPath), path => Path.GetFileName(path).StartsWith("stage-", StringComparison.Ordinal));
        Assert.Equal("candidate", store.Reader().Inspect(store.Context).State);
    }

    private sealed class OperationDiskFixture : IDisposable
    {
        internal HistoryStoreFixture Store { get; }
        internal StartupHistoryOperationLink Link { get; }
        internal StartupHistoryPayload Current { get; }
        internal string CurrentPath => Path.Combine(Store.DirectoryPath, StartupHistoryStoreReader.ProofName(Store.Context.RecordId, Current.Phase));
        internal string LinkPath => Path.Combine(Store.DirectoryPath, StartupHistoryOperationStoreReader.LinkName(Store.Context.RecordId));
        internal StartupHistoryOperationStoreReader Reader() => new(Store.History.Owned.Root, () => Store.DirectoryPath);
        // Explicit synthetic issuance/current/index. This fixture is NOT the real owner
        // publisher; only filesystem, ACL, CurrentUser DPAPI and read leases are real.
        internal OperationDiskFixture(string source = "Startup", bool committed = true, bool create = true)
        {
            Store = new(source, committed: false, create: create); Link = OperationLink(Store.History.Payload, Store.RecordBytes); Current = OperationCurrent(Store.History.Payload, committed);
            try
            {
                if (create)
                {
                    var current = committed ? StartupHistoryProtection.Protect(Current) : Store.RecordBytes;
                    if (committed) { Store.WritePrivateFile(CurrentPath, current); }
                    Store.PublishIndex(Store.Index with { Entries = [new(Current.Binding, HistoryLatest(Current, current))] });
                    Store.WritePrivateFile(LinkPath, StartupHistoryProtection.ProtectOperationLink(Link));
                }
            }
            catch { Store.Dispose(); throw; }
        }
        internal Dictionary<string, string> Images() => Directory.Exists(Store.DirectoryPath) ? Directory.GetFiles(Store.DirectoryPath)
            .ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal) : new(StringComparer.Ordinal);
        internal void AssertImages(Dictionary<string, string> expected)
        { var actual = Images(); Assert.Equal(expected.Count, actual.Count); foreach (var pair in expected) { Assert.True(actual.TryGetValue(pair.Key, out var value)); Assert.Equal(pair.Value, value); } }
        internal void RequireReleased() { foreach (var path in Images().Keys) { using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } } }
        internal void AssertNoNative()
        { Assert.Equal(0, Store.History.Owned.Native.WritableOpens); Assert.Empty(Store.History.Owned.Operations.Calls); Assert.False(File.Exists(Store.History.Owned.Paths.StartupBackupPath)); Assert.False(File.Exists(Store.History.Owned.Paths.StartupBackupPath + ".transaction")); }
        public void Dispose() => Store.Dispose();
    }
}
