using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Windows.Startup;
using Xunit;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup", "prepared", "original_matches")]
    [InlineData("Common Startup", "prepared", "original_matches")]
    [InlineData("Startup", "native-unknown", "archive_matches")]
    [InlineData("Common Startup", "native-unknown", "archive_matches")]
    [InlineData("Startup", "ledger", "archive_matches")]
    [InlineData("Common Startup", "ledger", "archive_matches")]
    [InlineData("Startup", "committed", "archive_matches")]
    [InlineData("Common Startup", "committed", "archive_matches")]
    public async Task HistoryNativeQueryObservesPhysicalSlotsButNeverFinalizesAnyPendingPhase(string source, string phase, string expected)
    {
        using var store = HistoryTransactionStore(source); await PrepareNativeReconciliation(store, phase);
        var before = IntentDataImages(store); var access = new ReconciliationAccess(store.History.Owned.Native);
        var original = File.Exists(store.History.Record.Origin) ? File.ReadAllBytes(store.History.Record.Origin) : null;
        var archive = File.Exists(store.History.Record.BackupPath) ? File.ReadAllBytes(store.History.Record.BackupPath!) : null;
        var result = await NativeQuery(store, access);
        Assert.Equal("observed_pending", result.Metadata.State); Assert.Equal(expected, result.Native.State); Assert.NotNull(result.Native.ObservedAt);
        Assert.True(access.DirectoryCalls > 0); AssertReconciliationNoAuthority(result); AssertIntentImagesUnchanged(before);
        Assert.Equal(original is not null, File.Exists(store.History.Record.Origin)); Assert.Equal(archive is not null, File.Exists(store.History.Record.BackupPath));
        if (original is not null) { Assert.Equal(original, File.ReadAllBytes(store.History.Record.Origin)); }
        if (archive is not null) { Assert.Equal(archive, File.ReadAllBytes(store.History.Record.BackupPath!)); }
        Assert.DoesNotContain(StartupMutationOperation.IntentCleanup, store.History.Owned.Operations.Calls);
        Assert.DoesNotContain(StartupMutationOperation.NativeRollback, store.History.Owned.Operations.Calls);
    }

    [Theory]
    [InlineData("intent-missing")] [InlineData("intent-invalid")] [InlineData("configuration-changed")]
    [InlineData("ledger-changed")] [InlineData("index-missing")] [InlineData("proof-missing")]
    [InlineData("coordination-missing")] [InlineData("original-declaration-changed")]
    public async Task HistoryNativeQueryNeverResolvesKnownFoldersBeforeMetadataAndIndependentProofAreEligible(string mode)
    {
        using var store = HistoryTransactionStore(); await PrepareNativeReconciliation(store, "committed");
        switch (mode)
        {
            case "intent-missing": DeleteNativeOwnedFile(store, IntentPath(store)); break;
            case "intent-invalid": File.WriteAllText(IntentPath(store), "{}"); break;
            case "configuration-changed": File.AppendAllText(store.History.Owned.Paths.ConfigPath, "\n"); break;
            case "ledger-changed": File.AppendAllText(store.History.Owned.Paths.StartupBackupPath, "\n"); break;
            case "index-missing": DeleteNativeHistoryFile(store, store.IndexPath); break;
            case "proof-missing": DeleteNativeHistoryFile(store, CommittedHistoryPath(store)); break;
            case "coordination-missing": DeleteNativeOwnedFile(store, Path.Combine(store.History.Owned.Root, RootWriteLease.FileName)); break;
            case "original-declaration-changed":
                var json = JsonNode.Parse(File.ReadAllBytes(IntentPath(store)))!;
                json["candidate"]!["Binding"]!["Original"]!["Sha256"] = new string('0', 64); File.WriteAllText(IntentPath(store), json.ToJsonString()); break;
        }
        var before = IntentDataImages(store); var access = new ReconciliationAccess(store.History.Owned.Native);
        var result = await NativeQuery(store, access);
        Assert.Equal("unobserved", result.Native.State); Assert.Equal(0, access.DirectoryCalls);
        AssertReconciliationNoAuthority(result); AssertIntentImagesUnchanged(before);
    }

    [Fact]
    public async Task HistoryNativeQueryDoesNotLetSelfDeclaredConfigurationImageRedirectTheAuthenticatedArchive()
    {
        using var store = HistoryTransactionStore(); await PrepareNativeReconciliation(store, "committed");
        File.WriteAllText(store.History.Owned.Paths.ConfigPath, "version: 1\nsettings: { backup_dir: unrelated-not-created }\ntasks: []\n");
        var intent = StartupHistoryIntentFormat.Read(File.ReadAllBytes(IntentPath(store))) with
        { ConfigurationSha256 = StartupHistoryIntentFormat.Hash(File.ReadAllBytes(store.History.Owned.Paths.ConfigPath)) };
        File.WriteAllBytes(IntentPath(store), StartupHistoryIntentFormat.Write(intent));
        var access = new ReconciliationAccess(store.History.Owned.Native); var before = IntentDataImages(store);
        var result = await NativeQuery(store, access);
        Assert.Equal("matched_image", result.Metadata.ConfigurationImage); Assert.Equal("unverified", result.Native.State);
        Assert.Equal("history_native_configuration_changed", result.Native.Reason); Assert.Equal(0, access.DirectoryCalls);
        Assert.False(Directory.Exists(Path.Combine(store.History.Owned.Root, "unrelated-not-created")));
        AssertReconciliationNoAuthority(result); AssertIntentImagesUnchanged(before);
    }

    [Theory]
    [InlineData("both-missing", "history_native_both_missing")]
    [InlineData("both-present", "history_native_both_present")]
    [InlineData("new-object-same-bytes", "history_native_archive_changed")]
    [InlineData("same-object-new-bytes", "history_native_archive_changed")]
    [InlineData("hardlink", "history_native_slots_unavailable")]
    [InlineData("symlink", "history_native_slots_unavailable")]
    [InlineData("directory", "history_native_slots_unavailable")]
    [InlineData("oversized", "history_native_slots_unavailable")]
    public async Task HistoryNativeQueryRejectsConflictsAndRequiresOneHandleIdentityLinkCountAndContent(string mode, string reason)
    {
        using var store = HistoryTransactionStore(); await PrepareNativeReconciliation(store, "committed");
        var record = store.History.Record; var backup = record.BackupPath!; var saved = backup + ".saved";
        VerifyEvidenceOwnedPath(store.History.Owned, backup); VerifyEvidenceOwnedPath(store.History.Owned, saved);
        var identity = StartupArchiveSlotProbe.Observe(backup).FileIdentity;
        if (mode is "both-missing" or "new-object-same-bytes" or "symlink" or "directory") { File.Move(backup, saved); }
        switch (mode)
        {
            case "both-present": VerifyEvidenceOwnedPath(store.History.Owned, record.Origin); File.WriteAllBytes(record.Origin, [1, 2, 3]); break;
            case "new-object-same-bytes": File.Copy(saved, backup); Assert.NotEqual(identity, StartupArchiveSlotProbe.Observe(backup).FileIdentity); break;
            case "same-object-new-bytes": File.WriteAllBytes(backup, [3, 2, 1]); Assert.Equal(identity, StartupArchiveSlotProbe.Observe(backup).FileIdentity); break;
            case "hardlink": Assert.True(EvidenceNativeCases.CreateHardLinkW(saved, backup, IntPtr.Zero)); break;
            case "symlink": File.CreateSymbolicLink(backup, saved); break;
            case "directory": Directory.CreateDirectory(backup); break;
            case "oversized": File.WriteAllBytes(backup, new byte[65537]); break;
        }
        var before = IntentDataImages(store); var result = await NativeQuery(store, new(store.History.Owned.Native));
        Assert.Equal("unverified", result.Native.State); Assert.Equal(reason, result.Native.Reason);
        AssertReconciliationNoAuthority(result); AssertIntentImagesUnchanged(before);
    }

    [Fact]
    public async Task HistoryNativeQueryRejectsChangedOriginalBeforeAnyArchive()
    {
        using var store = HistoryTransactionStore(); await PrepareNativeReconciliation(store, "prepared");
        File.WriteAllBytes(store.History.Record.Origin, [3, 2, 1]);
        var result = await NativeQuery(store, new(store.History.Owned.Native));
        Assert.Equal("unverified", result.Native.State); Assert.Equal("history_native_original_changed", result.Native.Reason); AssertReconciliationNoAuthority(result);
    }

    [Theory]
    [InlineData("source")] [InlineData("archive-base")] [InlineData("archive")]
    public async Task HistoryNativeQueryRejectsReplacedDirectoryIdentitiesEvenWithIdenticalLeafBytes(string which)
    {
        using var store = HistoryTransactionStore(); await PrepareNativeReconciliation(store, "committed"); var binding = store.History.Payload.Binding;
        var path = which switch { "source" => binding.Source.Directory.Path, "archive-base" => binding.ArchiveBase.Path, _ => binding.Archive.Path };
        var saved = path + ".saved"; VerifyEvidenceOwnedPath(store.History.Owned, path); VerifyEvidenceOwnedPath(store.History.Owned, saved);
        Directory.Move(path, saved);
        try
        {
            Directory.CreateDirectory(path);
            if (which != "source")
            {
                var destination = which == "archive-base" ? Path.Combine(path, "startup") : path; Directory.CreateDirectory(destination);
                var original = which == "archive-base" ? Path.Combine(saved, "startup", binding.RecordId + ".lnk") : Path.Combine(saved, binding.RecordId + ".lnk");
                File.Copy(original, Path.Combine(destination, binding.RecordId + ".lnk"));
            }
            var result = await NativeQuery(store, new(store.History.Owned.Native)); Assert.Equal("unverified", result.Native.State); AssertReconciliationNoAuthority(result);
        }
        finally
        {
            VerifyEvidenceOwnedPath(store.History.Owned, path); Assert.False((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
            Directory.Delete(path, recursive: true); VerifyEvidenceOwnedPath(store.History.Owned, saved); Directory.Move(saved, path);
        }
    }

    [Fact]
    public async Task HistoryNativeQueryUsesReadOnlyAccessAndDeniesLaterNativeRootAndProofWriters()
    {
        using var store = HistoryTransactionStore(); await PrepareNativeReconciliation(store, "committed"); var backup = store.History.Record.BackupPath!;
        using var existingReader = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read);
        using (var readOnly = StartupNativeSlotProbe.ShortcutReadLease.OpenReadOnly(backup, default))
        { Assert.Throws<InvalidOperationException>(() => readOnly.RenameHandle); }
        var before = IntentDataImages(store); var seen = false;
        var result = await NativeQuery(store, new(store.History.Owned.Native), checkpoint: stage =>
        {
            if (stage == "history_read")
            { Assert.ThrowsAny<IOException>(() => { using var writer = new FileStream(backup, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }); return; }
            if (stage != "native_slots_read") { return; } seen = true;
            foreach (var path in before.Keys.Append(backup))
            { Assert.ThrowsAny<IOException>(() => { using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }); }
            Assert.ThrowsAny<IOException>(() => File.Delete(backup));
        });
        Assert.True(seen); Assert.Equal("archive_matches", result.Native.State); AssertReconciliationNoAuthority(result); AssertIntentImagesUnchanged(before);
    }

    [Fact]
    public async Task HistoryNativeQueryRejectsAnAlreadyOpenSharedWriterRatherThanMixingObjectAndHashFacts()
    {
        using var store = HistoryTransactionStore(); await PrepareNativeReconciliation(store, "committed");
        using var writer = new FileStream(store.History.Record.BackupPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var result = await NativeQuery(store, new(store.History.Owned.Native));
        Assert.Equal("unverified", result.Native.State); Assert.Equal("history_native_slots_unavailable", result.Native.Reason); AssertReconciliationNoAuthority(result);
    }

    [Theory]
    [InlineData("native_slots_read")] [InlineData("history_read")]
    public async Task HistoryNativeQueryRechecksAnAbsentOriginalAfterHoldingTheArchive(string at)
    {
        using var store = HistoryTransactionStore(); await PrepareNativeReconciliation(store, "committed");
        var result = await NativeQuery(store, new(store.History.Owned.Native), checkpoint: stage =>
        { if (stage == at) { File.WriteAllBytes(store.History.Record.Origin, [1, 2, 3]); } });
        Assert.Equal("unverified", result.Native.State); Assert.True(File.Exists(store.History.Record.Origin)); AssertReconciliationNoAuthority(result);
    }

    [Fact]
    public async Task HistoryNativeQueryRechecksSourceMappingAfterReadingTheSlots()
    {
        using var store = HistoryTransactionStore(); await PrepareNativeReconciliation(store, "committed"); var access = new ReconciliationAccess(store.History.Owned.Native);
        var result = await NativeQuery(store, access, checkpoint: stage =>
        {
            if (stage == "native_slots_read") { access.Map = _ => store.History.Owned.Native.GetShortcutDirectory("Common Startup"); }
        });
        Assert.Equal("unverified", result.Native.State); Assert.Equal("history_native_source_changed", result.Native.Reason); AssertReconciliationNoAuthority(result);
    }

    [Fact]
    public async Task HistoryNativeQueryDoesNotTreatCaseSensitiveMissingAsEstablishedAbsence()
    {
        using var store = HistoryTransactionStore(); var binding = store.History.Payload.Binding;
        SetHistorySourceCaseForQuery(store, true);
        try
        {
            var payload = store.History.Payload with { Binding = binding with { Source = binding.Source with { Directory = binding.Source.Directory with { CaseSensitivity = "sensitive" } } } };
            await PrepareNativeReconciliation(store, "committed", payload);
            var result = await NativeQuery(store, new(store.History.Owned.Native));
            Assert.Equal("unverified", result.Native.State); AssertReconciliationNoAuthority(result);
        }
        finally { SetHistorySourceCaseForQuery(store, false); }
    }

    [Fact]
    public async Task HistoryNativeQueryDoesNotOpenIrrelevantMalformedOverrides()
    {
        using var store = HistoryTransactionStore(); await PrepareNativeReconciliation(store, "committed");
        var path = store.History.Owned.Paths.OverridesPath; VerifyEvidenceOwnedPath(store.History.Owned, path); File.WriteAllText(path, "not JSON and not part of backup settings");
        var result = await NativeQuery(store, new(store.History.Owned.Native));
        Assert.Equal("archive_matches", result.Native.State); Assert.Equal("not JSON and not part of backup settings", File.ReadAllText(path)); AssertReconciliationNoAuthority(result);
    }

    [Fact]
    public async Task HistoryNativeQueryReResolvesEnvironmentBackedArchiveSettingsWithoutChangingTheConfigurationFile()
    {
        using var store = HistoryTransactionStore(); var variable = "SYSTEMAUTORUN_R45_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, store.History.Payload.Binding.ArchiveBase.Path);
        try
        {
            File.WriteAllText(store.History.Owned.Paths.ConfigPath, $"version: 1\nsettings:\n  backup_dir: '%{variable}%'\ntasks: []\n");
            await PrepareNativeReconciliation(store, "committed"); var before = IntentDataImages(store);
            var result = await NativeQuery(store, new(store.History.Owned.Native), checkpoint: stage =>
            { if (stage == "native_slots_read") { Environment.SetEnvironmentVariable(variable, Path.Combine(store.History.Owned.Root, "changed-not-created")); } });
            Assert.Equal("unverified", result.Native.State); Assert.Equal("history_native_configuration_changed", result.Native.Reason);
            Assert.False(Directory.Exists(Path.Combine(store.History.Owned.Root, "changed-not-created"))); AssertIntentImagesUnchanged(before); AssertReconciliationNoAuthority(result);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task HistoryNativeQueryDiscardsNativeJoinWhenTheHeldIndependentProofFailsItsFinalAclSeal()
    {
        using var store = HistoryTransactionStore(); await PrepareNativeReconciliation(store, "committed"); var info = new FileInfo(store.IndexPath);
        // Windows adds AI on managed ACL publication. Establish that stable, still
        // private fixture baseline before capturing the exact binary/SDDL to restore.
        var baseline = info.GetAccessControl(AccessControlSections.Access);
        baseline.SetSecurityDescriptorBinaryForm(baseline.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access); info.SetAccessControl(baseline);
        var original = info.GetAccessControl(AccessControlSections.Access); var binary = original.GetSecurityDescriptorBinaryForm();
        var sddl = original.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        try
        {
            var result = await NativeQuery(store, new(store.History.Owned.Native), checkpoint: stage =>
            {
                if (stage != "native_slots_read") { return; }
                var acl = info.GetAccessControl(); acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.WriteData, AccessControlType.Allow));
                info.SetAccessControl(acl);
            });
            Assert.Equal("unverified", result.Native.State); Assert.Equal("history_native_join_unverified", result.Native.Reason);
            Assert.Equal("unverified", result.Metadata.HistoryState); AssertReconciliationNoAuthority(result);
        }
        finally
        {
            var restored = new FileSecurity(); restored.SetSecurityDescriptorBinaryForm(binary, AccessControlSections.Access); info.SetAccessControl(restored);
            Assert.Equal(sddl, info.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
    }

    [Theory]
    [InlineData("native_configuration_loaded")] [InlineData("images_read")] [InlineData("native_slots_read")] [InlineData("history_read")]
    public async Task HistoryNativeQueryCancellationReleasesActualReadLeasesWithoutClearingIntent(string cancelAt)
    {
        using var store = HistoryTransactionStore(); await PrepareNativeReconciliation(store, "committed"); using var cancellation = new CancellationTokenSource();
        var before = IntentDataImages(store); var seen = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeQuery(store, new(store.History.Owned.Native), cancellation.Token, stage =>
        { if (stage == cancelAt) { seen = true; cancellation.Cancel(); } }));
        Assert.True(seen); AssertIntentImagesUnchanged(before);
        foreach (var leaf in new[] { StartupMutationLease.FileName, RootWriteLease.FileName })
        { using var available = new FileStream(Path.Combine(store.History.Owned.Root, leaf), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        using var archiveAvailable = new FileStream(store.History.Record.BackupPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var indexAvailable = new FileStream(store.IndexPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static async Task PrepareNativeReconciliation(HistoryStoreFixture store, string phase, StartupHistoryPayload? payload = null)
    {
        using var manager = store.History.Owned.Manager();
        await using var owner = await manager.PrepareHistoryDisableAsync(payload ?? store.History.Payload, store.History.Record, () => store.DirectoryPath);
        if (phase == "native-unknown")
        { Assert.False((await owner.ArchiveAndPublishLedgerAsync(checkpoint: stage => { if (stage == "native_verified") { throw new IOException("Owned unknown native outcome."); } })).LedgerVerified); }
        else if (phase != "prepared") { Assert.True((await owner.ArchiveAndPublishLedgerAsync()).LedgerVerified); }
        if (phase == "committed") { Assert.True((await owner.PublishCommittedHistoryAsync()).Committed); }
    }
    private static Task<StartupHistoryReconciliationObservation> NativeQuery(HistoryStoreFixture store, ReconciliationAccess access,
        CancellationToken token = default, Action<string>? checkpoint = null) =>
        new StartupHistoryIntentReader(store.History.Owned.Root, () => store.DirectoryPath).InspectNativeAsync(access, token, checkpoint);
    private static void AssertReconciliationNoAuthority(StartupHistoryReconciliationObservation result)
    {
        AssertIntentQueryNoAuthority(result.Metadata); Assert.False(result.Native.NativeWriteReady); Assert.False(result.Native.ExecutionRequested);
        Assert.False(result.Native.TransactionFinalized); Assert.False(result.Native.MayFinalize);
    }
    private static void SetHistorySourceCaseForQuery(HistoryStoreFixture store, bool sensitive)
    {
        var origin = store.History.Record.Origin; var saved = Path.Combine(store.History.Owned.Root, "r45-case-original.lnk");
        VerifyEvidenceOwnedPath(store.History.Owned, origin); VerifyEvidenceOwnedPath(store.History.Owned, saved);
        var present = File.Exists(origin); if (present) { File.Move(origin, saved); }
        try { SetEvidenceDirectoryCaseSensitivity(Path.GetDirectoryName(origin)!, sensitive); }
        finally { if (present) { File.Move(saved, origin); } }
    }
    private static void DeleteNativeOwnedFile(HistoryStoreFixture store, string path)
    { VerifyEvidenceOwnedPath(store.History.Owned, path); File.Delete(path); }
    private static void DeleteNativeHistoryFile(HistoryStoreFixture store, string path)
    { Assert.Equal(Path.GetFullPath(path), path); Assert.Equal(store.DirectoryPath, Path.GetDirectoryName(path)); File.Delete(path); }

    private sealed class ReconciliationAccess(OwnedNativeAccess owned) : IStartupNativeAccess
    {
        internal int DirectoryCalls { get; private set; }
        internal Func<string, string>? Map { get; set; }
        public bool IsAdministrator => throw new InvalidOperationException("A passive query must not infer ownership from elevation.");
        public string GetShortcutDirectory(string source) { DirectoryCalls++; return Map?.Invoke(source) ?? owned.GetShortcutDirectory(source); }
        public RegistryKey? OpenRunKey(string source, bool writable, bool create) => throw new InvalidOperationException("No registry IO in shortcut reconciliation.");
    }
}
