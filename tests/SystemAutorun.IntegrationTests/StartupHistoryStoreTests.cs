using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup", false)] [InlineData("Startup", true)]
    [InlineData("Common Startup", false)] [InlineData("Common Startup", true)]
    public void HistoryStoreReadsIndependentProtectedLatestWithoutPublishingOrNativeAuthority(string source, bool committed)
    {
        using var store = new HistoryStoreFixture(source, committed); var index = File.ReadAllBytes(store.IndexPath); var proof = File.ReadAllBytes(store.ProofPath);
        var result = store.Reader().Inspect(store.Context);
        Assert.Equal(committed ? "protected_committed" : "candidate", result.State); AssertHistoryNoAuthority(result, store.History);
        Assert.Equal(index, File.ReadAllBytes(store.IndexPath)); Assert.Equal(proof, File.ReadAllBytes(store.ProofPath));
        Assert.Equal(2, Directory.GetFiles(store.DirectoryPath).Length);
        Assert.False(store.DirectoryPath.StartsWith(store.History.Owned.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("directory")] [InlineData("index")] [InlineData("record")] [InlineData("not-current")]
    public void HistoryStoreMissingMaterialNeverCreatesDirectoriesFilesOrTrust(string missing)
    {
        using var store = new HistoryStoreFixture(create: missing != "directory");
        if (missing == "index") { File.Delete(store.IndexPath); }
        if (missing == "record") { File.Delete(store.ProofPath); }
        if (missing == "not-current") { store.PublishIndex(store.Index with { Entries = Array.Empty<StartupHistoryIndexEntry>() }); }
        var before = Directory.Exists(store.DirectoryPath) ? Directory.GetFiles(store.DirectoryPath) : [];
        var result = store.Reader().Inspect(store.Context);
        Assert.Equal("unverified", result.State); AssertHistoryNoAuthority(result, store.History);
        if (missing == "directory") { Assert.False(Directory.Exists(store.DirectoryPath)); }
        else { Assert.Equal(before, Directory.GetFiles(store.DirectoryPath)); }
    }

    [Fact]
    public void HistoryStoreNeedsNoCurrentOriginalLeafAndDoesNotReadUnsignedLedgerOrBackupPaths()
    {
        using var store = new HistoryStoreFixture();
        File.Delete(store.History.Record.Origin);
        File.WriteAllText(store.History.Owned.Paths.StartupBackupPath, "deliberately invalid unsigned ledger");
        var result = store.Reader().Inspect(store.Context);
        Assert.Equal("protected_committed", result.State); Assert.False(result.NativeWriteReady);
        Assert.Equal("deliberately invalid unsigned ledger", File.ReadAllText(store.History.Owned.Paths.StartupBackupPath));
        Assert.False(File.Exists(store.History.Record.BackupPath));
        Assert.Equal(0, store.History.Owned.Native.WritableOpens); Assert.Empty(store.History.Owned.Operations.Calls);
        // Stored original facts are only protected claims: no native operation occurred.
    }

    [Theory]
    [InlineData("duplicate-record")] [InlineData("same-slot-other-root")] [InlineData("case-slot")]
    [InlineData("actor")] [InlineData("generation")] [InlineData("nonce")] [InlineData("count")]
    public void HistoryIndexRejectsAmbiguousOrMalformedAuthorityRows(string mode)
    {
        using var store = new HistoryStoreFixture(); var index = store.Index; var row = index.Entries[0];
        index = mode switch
        {
            "duplicate-record" => index with { Entries = new[] { row, row } },
            "same-slot-other-root" => index with { Entries = new[] { row, row with { Binding = row.Binding with { RecordId = Guid.NewGuid().ToString("N"), Root = row.Binding.Root with { Path = @"C:\never-probe-other-root" } } } } },
            "case-slot" => index with { Entries = new[] { row, row with { Binding = row.Binding with { RecordId = Guid.NewGuid().ToString("N"), Name = row.Binding.Name.ToUpperInvariant() } } } },
            "actor" => index with { ActorSid = index.ActorSid == "S-1-5-18" ? "S-1-5-19" : "S-1-5-18" },
            "generation" => index with { Generation = 0 },
            "nonce" => index with { Nonce = new string('0', 32) },
            _ => index with { Entries = Enumerable.Repeat(row, StartupHistoryIndexFormat.MaximumEntries + 1).ToArray() },
        };
        Assert.Throws<JsonException>(() => StartupHistoryIndexFormat.Write(index));
        AssertHistoryNoAuthority(new("unverified", null), store.History);
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("unknown")] [InlineData("missing")] [InlineData("null")] [InlineData("bytes")]
    public void HistoryIndexStrictReaderRejectsDamagedJsonAndLimits(string mode)
    {
        using var store = new HistoryStoreFixture(); var bytes = StartupHistoryIndexFormat.Write(store.Index);
        if (mode == "duplicate") { bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal)); }
        else if (mode == "bytes") { bytes = new byte[StartupHistoryIndexFormat.MaximumBytes + 1]; }
        else
        {
            var root = JsonNode.Parse(bytes)!.AsObject();
            if (mode == "unknown") { root["nativeWriteReady"] = true; }
            else if (mode == "missing") { root.Remove("generation"); }
            else { root["entries"] = null; }
            bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        }
        Assert.Throws<JsonException>(() => StartupHistoryIndexFormat.Read(bytes));
    }

    [Fact]
    public void HistoryIndexCanKeepTwoDistinctSlotsAndFreezesDecodedCollections()
    {
        using var store = new HistoryStoreFixture(); var first = store.History.Payload;
        var second = first with { Nonce = Guid.NewGuid().ToString("N"), Binding = first.Binding with { RecordId = Guid.NewGuid().ToString("N"), Name = "second", RecordDigest = new string('b', 64) } };
        var secondCipher = StartupHistoryProtection.Protect(second);
        store.WritePrivateFile(Path.Combine(store.DirectoryPath, StartupHistoryStoreReader.ProofName(second.Binding.RecordId, second.Phase)), secondCipher);
        store.PublishIndex(store.Index with { Entries = new[] { store.Index.Entries[0], new StartupHistoryIndexEntry(second.Binding, HistoryLatest(second, secondCipher)) } });
        Assert.Equal("protected_committed", store.Reader().Inspect(store.Context).State);
        Assert.Equal("protected_committed", store.Reader().Inspect(second.Binding.Context).State);
        var decoded = StartupHistoryProtection.UnprotectIndex(File.ReadAllBytes(store.IndexPath));
        Assert.Equal(2, decoded.Entries.Count); Assert.Throws<NotSupportedException>(() => ((IList<StartupHistoryIndexEntry>)decoded.Entries).Clear());
    }

    [Theory]
    [InlineData("index-corrupt")] [InlineData("record-corrupt")] [InlineData("record-old")]
    [InlineData("record-domain")] [InlineData("index-domain")] [InlineData("index-size")] [InlineData("record-size")]
    public void HistoryStoreRejectsTamperReplayWrongDomainAndOversizeFiles(string mode)
    {
        using var store = new HistoryStoreFixture();
        if (mode == "index-corrupt") { var bytes = File.ReadAllBytes(store.IndexPath); bytes[^1] ^= 1; store.WritePrivateFile(store.IndexPath, bytes); }
        if (mode == "record-corrupt") { var bytes = store.RecordBytes.ToArray(); bytes[^1] ^= 1; store.WritePrivateFile(store.ProofPath, bytes); }
        if (mode == "record-old")
        {
            var next = store.History.Payload with { Nonce = Guid.NewGuid().ToString("N"), Sequence = 2 };
            var nextBytes = StartupHistoryProtection.Protect(next);
            store.PublishIndex(store.Index with { Entries = new[] { new StartupHistoryIndexEntry(next.Binding, HistoryLatest(next, nextBytes)) } });
            // Old proof remains; protected latest cannot be inferred from that proof.
        }
        if (mode == "record-domain")
        {
            var bytes = File.ReadAllBytes(store.IndexPath); store.WritePrivateFile(store.ProofPath, bytes);
            var row = store.Index.Entries[0]; store.PublishIndex(store.Index with { Entries = new[] { row with { Latest = row.Latest with { ProtectedDigest = Convert.ToHexStringLower(SHA256.HashData(bytes)) } } } });
        }
        if (mode == "index-domain") { store.WritePrivateFile(store.IndexPath, store.RecordBytes); }
        if (mode == "index-size") { store.WritePrivateFile(store.IndexPath, new byte[StartupHistoryProtection.MaximumProtectedIndexBytes + 1]); }
        if (mode == "record-size") { store.WritePrivateFile(store.ProofPath, new byte[StartupHistoryProtection.MaximumProtectedBytes + 1]); }
        var result = store.Reader().Inspect(store.Context); Assert.Equal("unverified", result.State); AssertHistoryNoAuthority(result, store.History);
    }

    [Theory]
    [InlineData("directory-write")] [InlineData("directory-inheritance")]
    [InlineData("file-write")] [InlineData("file-delete")] [InlineData("file-dacl")]
    public void HistoryStoreRejectsActualUnsafeAclsWithoutRepairingThem(string mode)
    {
        using var store = new HistoryStoreFixture(); var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        if (mode.StartsWith("directory", StringComparison.Ordinal))
        {
            var info = new DirectoryInfo(store.DirectoryPath); var acl = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (mode == "directory-inheritance") { acl.SetAccessRuleProtection(false, true); }
            else { acl.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Write, AccessControlType.Allow)); }
            info.SetAccessControl(acl);
            var before = info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            Assert.Equal("unverified", store.Reader().Inspect(store.Context).State);
            Assert.Equal(before, info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        }
        else
        {
            var info = new FileInfo(store.ProofPath); var acl = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            var rights = mode == "file-write" ? FileSystemRights.WriteData : mode == "file-delete" ? FileSystemRights.Delete : FileSystemRights.ChangePermissions;
            acl.AddAccessRule(new FileSystemAccessRule(everyone, rights, AccessControlType.Allow)); info.SetAccessControl(acl);
            var before = info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            Assert.Equal("unverified", store.Reader().Inspect(store.Context).State);
            Assert.Equal(before, info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        }
        AssertHistoryNoAuthority(new("unverified", null), store.History);
    }

    [Fact]
    public void HistoryStoreUsesReadOnlyHandlesAndRetainsBothLeavesAndParentUntilSeal()
    {
        using var store = new HistoryStoreFixture();
        var result = store.Reader().Inspect(store.Context, afterIndexRead: () =>
        {
            Assert.Throws<IOException>(() => File.WriteAllBytes(store.IndexPath, [1]));
        }, beforeSeal: () =>
        {
            foreach (var path in new[] { store.IndexPath, store.ProofPath })
            {
                Assert.Throws<IOException>(() => File.WriteAllBytes(path, [1])); Assert.Throws<IOException>(() => File.Delete(path));
                Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));
            }
            Assert.Throws<IOException>(() => Directory.Move(store.DirectoryPath, store.DirectoryPath + ".moved"));
        });
        Assert.Equal("protected_committed", result.State); AssertHistoryNoAuthority(result, store.History);
        // Deny both delete routes: child DELETE and parent FILE_DELETE_CHILD.
        var info = new FileInfo(store.ProofPath); var parent = new DirectoryInfo(store.DirectoryPath);
        var originalFile = info.GetAccessControl().GetSecurityDescriptorBinaryForm();
        var originalParent = parent.GetAccessControl().GetSecurityDescriptorBinaryForm();
        try
        {
            var acl = info.GetAccessControl(); acl.AddAccessRule(new FileSystemAccessRule(store.Actor, FileSystemRights.Delete, AccessControlType.Deny)); info.SetAccessControl(acl);
            var parentAcl = parent.GetAccessControl(); parentAcl.AddAccessRule(new FileSystemAccessRule(store.Actor, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny)); parent.SetAccessControl(parentAcl);
            Assert.Equal("protected_committed", store.Reader().Inspect(store.Context).State);
        }
        finally
        {
            var fileAcl = info.GetAccessControl(); fileAcl.SetSecurityDescriptorBinaryForm(originalFile); info.SetAccessControl(fileAcl);
            var parentAcl = parent.GetAccessControl(); parentAcl.SetSecurityDescriptorBinaryForm(originalParent); parent.SetAccessControl(parentAcl);
            Assert.Equal(originalFile, info.GetAccessControl().GetSecurityDescriptorBinaryForm());
            Assert.Equal(originalParent, parent.GetAccessControl().GetSecurityDescriptorBinaryForm());
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HistoryStoreCancellationReleasesRealReadersBeforeReturning(bool late)
    {
        using var store = new HistoryStoreFixture(); using var canceled = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => store.Reader().Inspect(store.Context, canceled.Token,
            afterIndexRead: late ? null : canceled.Cancel, beforeSeal: late ? canceled.Cancel : null));
        using (File.Open(store.IndexPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using (File.Open(store.ProofPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Assert.Equal("protected_committed", store.Reader().Inspect(store.Context).State);
    }

    [Theory]
    [InlineData("inside-root")] [InlineData("mapping")] [InlineData("context")]
    [InlineData("impersonation")] [InlineData("occupied-index")] [InlineData("occupied-record")]
    public void HistoryStoreRejectsInvalidContextMappingImpersonationAndOccupancy(string mode)
    {
        using var store = new HistoryStoreFixture(); StartupHistoryCheck result;
        if (mode == "inside-root") { result = new StartupHistoryStoreReader(store.History.Owned.Root, () => store.History.Owned.Root).Inspect(store.Context); }
        else if (mode == "mapping")
        {
            var count = 0; var reader = new StartupHistoryStoreReader(store.History.Owned.Root, () => ++count == 1 ? store.DirectoryPath : store.DirectoryPath + ".changed");
            result = reader.Inspect(store.Context); Assert.False(Directory.Exists(store.DirectoryPath + ".changed"));
        }
        else if (mode == "context") { result = store.Reader().Inspect(store.Context with { RecordDigest = new string('a', 64) }); }
        else if (mode == "impersonation")
        {
            using var user = WindowsIdentity.GetCurrent(); result = WindowsIdentity.RunImpersonated(user.AccessToken, () => store.Reader().Inspect(store.Context));
        }
        else
        {
            using var locked = File.Open(mode == "occupied-index" ? store.IndexPath : store.ProofPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            result = store.Reader().Inspect(store.Context);
        }
        Assert.Equal("unverified", result.State); AssertHistoryNoAuthority(result, store.History);
    }

    [Fact]
    public void HistoryStoreRejectsHardlinksAndReparseLeavesWithoutReadingTheirTargets()
    {
        using var store = new HistoryStoreFixture(); var alias = Path.Combine(store.History.Owned.Root, "owned-proof-alias.bin");
        Assert.True(CreateHistoryHardLink(alias, store.ProofPath, IntPtr.Zero));
        Assert.Equal("unverified", store.Reader().Inspect(store.Context).State); File.Delete(alias);
        File.Delete(store.ProofPath); var target = Path.Combine(store.History.Owned.Root, "never-read-target.bin"); File.WriteAllBytes(target, [7, 8, 9]);
        File.CreateSymbolicLink(store.ProofPath, target);
        using (File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { Assert.Equal("unverified", store.Reader().Inspect(store.Context).State); }
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(target)); File.Delete(store.ProofPath);
    }

    [Fact]
    public void HistoryIndexAcceptsTheEntryLimitButRejectsOversizeEncodingBeforePublication()
    {
        using var store = new HistoryStoreFixture(); var row = store.Index.Entries[0];
        var rows = Enumerable.Range(0, StartupHistoryIndexFormat.MaximumEntries).Select(number => row with
        { Binding = row.Binding with { RecordId = Guid.NewGuid().ToString("N"), Name = "entry-" + number } }).ToArray();
        var index = store.Index with { Entries = rows };
        Assert.Equal(StartupHistoryIndexFormat.MaximumEntries, StartupHistoryIndexFormat.Read(StartupHistoryIndexFormat.Write(index)).Entries.Count);
        index = index with { Entries = rows.Select(entry => entry with { Binding = entry.Binding with
        { Root = entry.Binding.Root with { Path = @"C:\" + new string('r', 4093) } } }).ToArray() };
        Assert.Throws<JsonException>(() => StartupHistoryIndexFormat.Write(index));
    }

    [Fact]
    public void HistoryStoreWholeStateRollbackIsNotClaimedToBeDetectedOrWriteEligible()
    {
        using var store = new HistoryStoreFixture(); var oldIndex = File.ReadAllBytes(store.IndexPath);
        var next = store.History.Payload with { Sequence = 2, Nonce = Guid.NewGuid().ToString("N") };
        var nextBytes = StartupHistoryProtection.Protect(next);
        store.WritePrivateFile(store.ProofPath, nextBytes);
        store.PublishIndex(store.Index with { Generation = 2, Nonce = Guid.NewGuid().ToString("N"),
            Entries = new[] { new StartupHistoryIndexEntry(next.Binding, HistoryLatest(next, nextBytes)) } });
        Assert.Equal("protected_committed", store.Reader().Inspect(store.Context).State);
        store.WritePrivateFile(store.ProofPath, store.RecordBytes); store.WritePrivateFile(store.IndexPath, oldIndex);
        var rolledBack = store.Reader().Inspect(store.Context);
        Assert.Equal("protected_committed", rolledBack.State); Assert.Equal("history_producer_not_connected", rolledBack.Reason);
        AssertHistoryNoAuthority(rolledBack, store.History); // Explicit G1 exclusion, not an anti-rollback claim.
    }

    [Fact]
    public void HistoryStoreRejectsSamePathProjectReplacementUsingTheActualPinnedRootId()
    {
        using var store = new HistoryStoreFixture(); var root = store.History.Owned.Root; var saved = root + ".saved-root";
        Assert.Equal(Path.GetFullPath(saved), saved); Assert.False(Directory.Exists(saved)); Directory.Move(root, saved);
        try
        {
            Directory.CreateDirectory(root); var result = store.Reader().Inspect(store.Context);
            Assert.Equal("unverified", result.State); Assert.False(result.NativeWriteReady);
        }
        finally
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(root)); Directory.Delete(root); Directory.Move(saved, root);
        }
        Assert.Equal("protected_committed", store.Reader().Inspect(store.Context).State);
    }

    [Fact]
    public void HistoryStoreRejectsAReparseDirectoryBeforeOpeningItsIndex()
    {
        using var store = new HistoryStoreFixture(); var saved = store.DirectoryPath + ".saved-store";
        Assert.Equal(Path.GetFullPath(saved), saved); Assert.False(Directory.Exists(saved)); Directory.Move(store.DirectoryPath, saved);
        try
        {
            Directory.CreateSymbolicLink(store.DirectoryPath, saved);
            using var locked = File.Open(Path.Combine(saved, StartupHistoryStoreReader.IndexName), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal("unverified", store.Reader().Inspect(store.Context).State);
        }
        finally
        {
            if (Directory.Exists(store.DirectoryPath))
            { Assert.True((File.GetAttributes(store.DirectoryPath) & FileAttributes.ReparsePoint) != 0); Directory.Delete(store.DirectoryPath); }
            Directory.Move(saved, store.DirectoryPath);
        }
    }

    [Fact]
    public void HistoryStoreHeldFileBuffersAreClearedAndObservedAclDriftIsRejected()
    {
        using var store = new HistoryStoreFixture();
        using (var directory = StartupNativeSlotProbe.HistoryDirectoryReadLease.Open(store.DirectoryPath, store.Context.Root, store.Actor.Value, default))
        {
            byte[] held;
            using (var file = directory.OpenFile(StartupHistoryStoreReader.IndexName, StartupHistoryProtection.MaximumProtectedIndexBytes, default))
            { held = file.Bytes; Assert.Contains(held, value => value != 0); }
            Assert.All(held, value => Assert.Equal(0, value));
        }
        var result = store.Reader().Inspect(store.Context, beforeSeal: () =>
        {
            var info = new FileInfo(store.IndexPath); var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.WriteData, AccessControlType.Allow));
            info.SetAccessControl(acl);
        });
        Assert.Equal("unverified", result.State); Assert.Equal("history_store_permissions_invalid", result.Reason);
    }

    private sealed class HistoryStoreFixture : IDisposable
    {
        internal HistoryFixture History { get; }
        internal string DirectoryPath { get; }
        internal string IndexPath => Path.Combine(DirectoryPath, StartupHistoryStoreReader.IndexName);
        internal string ProofPath => Path.Combine(DirectoryPath, StartupHistoryStoreReader.ProofName(History.Payload.Binding.RecordId, History.Payload.Phase));
        internal SecurityIdentifier Actor { get; }
        internal byte[] RecordBytes { get; }
        internal StartupHistoryIndex Index { get; private set; }
        internal StartupHistoryContext Context => History.Payload.Binding.Context;
        internal StartupHistoryStoreReader Reader() => new(History.Owned.Root, () => DirectoryPath);
        private readonly string? _container;
        internal HistoryStoreFixture(string source = "Startup", bool committed = true, bool create = true, bool privateParent = false)
        {
            History = new(source, committed); DirectoryPath = History.Owned.Root + ".history-state";
            if (privateParent) { _container = DirectoryPath + ".container"; DirectoryPath = Path.Combine(_container, "store"); }
            Actor = new SecurityIdentifier(History.Payload.Binding.ActorSid);
            RecordBytes = StartupHistoryProtection.Protect(History.Payload);
            Index = new(1, Actor.Value, 1, Guid.NewGuid().ToString("N"), new[] { new StartupHistoryIndexEntry(History.Payload.Binding, HistoryLatest(History.Payload, RecordBytes)) });
            try
            {
                if (_container is not null)
                {
                    Assert.Equal(Path.GetFullPath(_container), _container); Assert.False(Directory.Exists(_container));
                    Directory.CreateDirectory(_container); new DirectoryInfo(_container).SetAccessControl((DirectorySecurity)HistoryPrivateSecurity(Actor, directory: true));
                }
                if (create)
                {
                    Assert.Equal(Path.GetFullPath(DirectoryPath), DirectoryPath); Assert.False(Directory.Exists(DirectoryPath));
                    Directory.CreateDirectory(DirectoryPath); new DirectoryInfo(DirectoryPath).SetAccessControl((DirectorySecurity)HistoryPrivateSecurity(Actor, directory: true));
                    WritePrivateFile(ProofPath, RecordBytes); PublishIndex(Index);
                }
            }
            catch { Dispose(); throw; }
        }
        internal void WritePrivateFile(string path, byte[] bytes)
        {
            Assert.Equal(DirectoryPath, Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes);
            new FileInfo(path).SetAccessControl((FileSecurity)HistoryPrivateSecurity(Actor, directory: false));
        }
        internal void PublishIndex(StartupHistoryIndex index) { WritePrivateFile(IndexPath, StartupHistoryProtection.ProtectIndex(index)); Index = index; }
        public void Dispose()
        {
            try
            {
                if (Directory.Exists(DirectoryPath))
                {
                    Assert.Equal(Path.GetFullPath(DirectoryPath), DirectoryPath); Assert.Equal(_container ?? Path.GetDirectoryName(History.Owned.Root), Path.GetDirectoryName(DirectoryPath));
                    Assert.False((File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) != 0);
                    new DirectoryInfo(DirectoryPath).SetAccessControl((DirectorySecurity)HistoryPrivateSecurity(Actor, directory: true));
                    Directory.Delete(DirectoryPath, recursive: true);
                }
                if (_container is not null && Directory.Exists(_container))
                {
                    Assert.Equal(Path.GetFullPath(_container), _container); Assert.Equal(Path.GetDirectoryName(History.Owned.Root), Path.GetDirectoryName(_container));
                    Assert.False((File.GetAttributes(_container) & FileAttributes.ReparsePoint) != 0); Assert.Empty(Directory.EnumerateFileSystemEntries(_container));
                    Directory.Delete(_container);
                }
            }
            finally { History.Dispose(); }
        }
    }

    private static FileSystemSecurity HistoryPrivateSecurity(SecurityIdentifier actor, bool directory)
    {
        FileSystemSecurity acl = directory ? new DirectorySecurity() : new FileSecurity();
        acl.SetOwner(actor); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { actor, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
        {
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
                PropagationFlags.None, AccessControlType.Allow));
        }
        return acl;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHistoryHardLink(string link, string existing, IntPtr security);
}
