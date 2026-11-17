using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup", false, false)] [InlineData("Startup", true, false)]
    [InlineData("Startup", false, true)] [InlineData("Startup", true, true)]
    [InlineData("Common Startup", false, false)] [InlineData("Common Startup", true, false)]
    [InlineData("Common Startup", false, true)] [InlineData("Common Startup", true, true)]
    public async Task ShortcutSlotsObserveBothFixedNamespacesWithoutClaimingReadiness(string source, bool archivePresent, bool originalPresent)
    {
        using var owned = new Fixture(administrator: false); var record = await WriteEvidenceRecord(owned, source);
        if (archivePresent) { await File.WriteAllBytesAsync(record.BackupPath!, [1]); }
        if (originalPresent) { await File.WriteAllBytesAsync(record.Origin, [2, 3]); }
        var ledger = await File.ReadAllBytesAsync(owned.Paths.StartupBackupPath);
        using var manager = owned.Manager(); var evidence = await manager.ObserveShortcutSlotsAsync(record.Id);
        Assert.Equal(archivePresent ? "present" : "missing", evidence.Archive.State);
        Assert.Equal(originalPresent ? "present" : "missing", evidence.Original.State);
        Assert.Equal(owned.Native.GetShortcutDirectory(source), evidence.Original.SourceDirectory);
        Assert.NotNull(evidence.Original.DirectoryIdentity); Assert.NotEqual(evidence.Archive.DirectoryIdentity, evidence.Original.DirectoryIdentity);
        if (originalPresent) { Assert.Equal(2, evidence.Original.Length); Assert.NotNull(evidence.Original.FileIdentity); }
        else { Assert.Null(evidence.Original.FileIdentity); Assert.Equal(2, evidence.Original.NativeErrorCode); }
        Assert.False(evidence.NativeWriteReady); Assert.False(evidence.NativeWritePerformed); Assert.False(evidence.ExecutionRequested);
        Assert.Equal(evidence, JsonSerializer.Deserialize<StartupShortcutSlotEvidence>(JsonSerializer.Serialize(evidence, AgentProtocol.JsonOptions), AgentProtocol.JsonOptions));
        var compatibility = await manager.ObserveBackupSlotAsync(record.Id);
        Assert.Equal(evidence.RecordMetadataDigest, compatibility.RecordMetadataDigest); Assert.Equal(evidence.Archive.State, compatibility.Observation.State);
        Assert.Equal(ledger, await File.ReadAllBytesAsync(owned.Paths.StartupBackupPath));
        Assert.Equal(0, owned.Native.WritableOpens); Assert.Empty(owned.Operations.Calls); owned.AssertNoTransactionOrTemps();
    }

    [Fact]
    public async Task ShortcutSlotsKeepIndependentErrorsAndArchiveOnlyCompatibilityNeverOpensOriginal()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned); await File.WriteAllBytesAsync(record.Origin, [2]);
        using var manager = owned.Manager();
        using (var locked = new FileStream(record.Origin, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal("missing", (await manager.ObserveBackupSlotAsync(record.Id)).Observation.State);
            var both = await manager.ObserveShortcutSlotsAsync(record.Id);
            Assert.Equal("missing", both.Archive.State); Assert.Equal("unknown", both.Original.State);
            Assert.Equal("original_occupied", both.Original.Reason); Assert.Equal(32, both.Original.NativeErrorCode);
        }
        await File.WriteAllBytesAsync(record.BackupPath!, [1]);
        using (var locked = new FileStream(record.BackupPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var both = await manager.ObserveShortcutSlotsAsync(record.Id);
            Assert.Equal("unknown", both.Archive.State); Assert.Equal("archive_occupied", both.Archive.Reason);
            Assert.Equal("present", both.Original.State); Assert.False(both.NativeWriteReady);
        }
        await File.WriteAllBytesAsync(record.Origin, [3]); Assert.Empty(owned.Operations.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ShortcutSlotsOriginalAclDeniedIsNotMissingAndDaclIsExactlyRestored(bool directory)
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned); await File.WriteAllBytesAsync(record.Origin, [7]);
        var file = new FileInfo(record.Origin); var folder = new DirectoryInfo(Path.GetDirectoryName(record.Origin)!);
        VerifyEvidenceOwnedPath(owned, directory ? folder.FullName : file.FullName);
        var fileAcl = file.GetAccessControl(); var folderAcl = folder.GetAccessControl();
        var original = directory ? folderAcl.GetSecurityDescriptorBinaryForm() : fileAcl.GetSecurityDescriptorBinaryForm();
        var sddl = directory ? folderAcl.GetSecurityDescriptorSddlForm(AccessControlSections.Access) : fileAcl.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        var deny = new FileSystemAccessRule(identity.User!, FileSystemRights.ReadData, AccessControlType.Deny);
        try
        {
            if (directory) { folderAcl.AddAccessRule(deny); folder.SetAccessControl(folderAcl); }
            else { fileAcl.AddAccessRule(deny); file.SetAccessControl(fileAcl); }
            using var manager = owned.Manager(); var both = await manager.ObserveShortcutSlotsAsync(record.Id);
            Assert.Equal("missing", both.Archive.State); Assert.Equal("unknown", both.Original.State);
            Assert.Equal("original_permission_denied", both.Original.Reason); Assert.Equal(5, both.Original.NativeErrorCode);
            Assert.Equal(directory, both.Original.DirectoryIdentity is null); Assert.False(both.NativeWriteReady);
        }
        finally
        {
            if (directory) { folderAcl.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access); folder.SetAccessControl(folderAcl); }
            else { fileAcl.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access); file.SetAccessControl(fileAcl); }
        }
        Assert.Equal(sddl, directory ? folder.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access) : file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.Equal(new byte[] { 7 }, await File.ReadAllBytesAsync(record.Origin));
    }

    [Theory]
    [InlineData("directory")] [InlineData("leaf-reparse")] [InlineData("parent-reparse")] [InlineData("oversize")]
    public async Task ShortcutSlotsRejectNonRegularOriginalsWithoutFollowingOrParsingThem(string defect)
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned);
        var source = Path.GetDirectoryName(record.Origin)!; var other = Path.Combine(owned.Root, "owned-other-source"); Directory.CreateDirectory(other);
        if (defect == "directory") { Directory.CreateDirectory(record.Origin); }
        else if (defect == "leaf-reparse")
        { var target = Path.Combine(other, "target"); await File.WriteAllBytesAsync(target, [9]); File.CreateSymbolicLink(record.Origin, target); }
        else if (defect == "parent-reparse")
        { VerifyEvidenceOwnedPath(owned, source); Directory.Delete(source); Directory.CreateSymbolicLink(source, other); }
        else { await File.WriteAllBytesAsync(record.Origin, new byte[65537]); }
        try
        {
            using var manager = owned.Manager(); var both = await manager.ObserveShortcutSlotsAsync(record.Id);
            Assert.Equal("missing", both.Archive.State); Assert.Equal("unknown", both.Original.State);
            Assert.Null(both.Original.FileIdentity); Assert.False(both.NativeWriteReady); Assert.Empty(owned.Operations.Calls);
            if (defect == "directory") { Assert.Equal("original_not_regular", both.Original.Reason); }
            if (defect == "oversize") { Assert.Equal("original_too_large", both.Original.Reason); }
        }
        finally
        {
            if (defect == "leaf-reparse") { VerifyEvidenceOwnedPath(owned, record.Origin); File.Delete(record.Origin); }
            if (defect == "parent-reparse") { VerifyEvidenceOwnedPath(owned, source); Directory.Delete(source); Directory.CreateDirectory(source); }
        }
    }

    [Fact]
    public async Task ShortcutSlotsMissingOriginalParentIsUnknownAndNotCreated()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned); await File.WriteAllBytesAsync(record.BackupPath!, [1]);
        var source = Path.GetDirectoryName(record.Origin)!; VerifyEvidenceOwnedPath(owned, source); Directory.Delete(source);
        using var manager = owned.Manager(); var both = await manager.ObserveShortcutSlotsAsync(record.Id);
        Assert.Equal("present", both.Archive.State); Assert.Equal("unknown", both.Original.State);
        Assert.Equal("original_directory_missing", both.Original.Reason); Assert.Null(both.Original.DirectoryIdentity);
        Assert.False(Directory.Exists(source)); Assert.False(both.NativeWriteReady);
    }

    [Fact]
    public async Task ShortcutSlotsObserveOriginalReplacementAndReleasedHandlesWithoutChangingRecordDigest()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned); await File.WriteAllBytesAsync(record.Origin, [7]);
        using var manager = owned.Manager(); var first = await manager.ObserveShortcutSlotsAsync(record.Id);
        var saved = record.Origin + ".saved"; VerifyEvidenceOwnedPath(owned, record.Origin); VerifyEvidenceOwnedPath(owned, saved);
        File.Move(record.Origin, saved); await File.WriteAllBytesAsync(record.Origin, [7]);
        var second = await manager.ObserveShortcutSlotsAsync(record.Id);
        Assert.NotEqual(first.Original.FileIdentity, second.Original.FileIdentity); Assert.Equal(first.RecordMetadataDigest, second.RecordMetadataDigest);
        Assert.Equal(first.Original.DirectoryIdentity, second.Original.DirectoryIdentity);
        var source = Path.GetDirectoryName(record.Origin)!; var moved = source + ".moved"; VerifyEvidenceOwnedPath(owned, source); VerifyEvidenceOwnedPath(owned, moved);
        var held = StartupOriginalSlotProbe.Observe(record.Origin, beforeSeal: () =>
        {
            Assert.Throws<IOException>(() => Directory.Move(source, moved));
            Assert.Throws<IOException>(() => { using var writer = new FileStream(record.Origin, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
        });
        Assert.Equal("present", held.State); Assert.False(Directory.Exists(moved));
    }

    [Fact]
    public async Task ShortcutSlotsUnresolvedIntentAndCancellationCannotProduceEvidence()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned); await File.WriteAllBytesAsync(record.Origin, [7]);
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => StartupOriginalSlotProbe.Observe(record.Origin, cancellation.Token, cancellation.Cancel));
        await File.WriteAllBytesAsync(record.Origin, [8]); using var manager = owned.Manager();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ObserveShortcutSlotsAsync(record.Id, cancellation.Token));
        await File.WriteAllTextAsync(owned.Paths.StartupBackupPath + ".transaction", "unresolved");
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ObserveShortcutSlotsAsync(record.Id));
        Assert.Equal(new byte[] { 8 }, await File.ReadAllBytesAsync(record.Origin)); Assert.Empty(owned.Operations.Calls);
    }

    [Theory]
    [InlineData("terminal")] [InlineData("registry")] [InlineData("old-archive")]
    [InlineData("archive-network")] [InlineData("origin-network")] [InlineData("other-source")]
    [InlineData("name-traversal")] [InlineData("reserved")]
    public async Task ShortcutSlotsValidateAllMetadataBeforeEitherSlotAndNeverProbeUnrelatedNamespaces(string defect)
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned);
        owned.RewriteBackup(row =>
        {
            if (defect == "terminal") { row["disposition"] = "restored"; row["restored_at"] = DateTimeOffset.UnixEpoch; }
            else if (defect == "registry") { row["source"] = "HKCU"; row["backup_path"] = null; }
            else if (defect == "old-archive") { row["backup_path"] = Path.Combine(owned.Root, "old", record.Id + ".lnk"); }
            else if (defect == "archive-network") { row["backup_path"] = @"\\offline.invalid\never-probe\" + record.Id + ".lnk"; }
            else if (defect == "origin-network") { row["origin"] = @"\\offline.invalid\never-probe\owned.lnk"; }
            else if (defect == "other-source") { row["origin"] = Path.Combine(owned.Native.GetShortcutDirectory("Common Startup"), "owned.lnk"); }
            else if (defect == "name-traversal") { row["name"] = "..\\owned"; }
            else { row["name"] = "CON "; row["origin"] = Path.Combine(owned.Native.GetShortcutDirectory("Startup"), "CON .lnk"); }
        });
        using var manager = owned.Manager(); var both = await manager.ObserveShortcutSlotsAsync(record.Id);
        Assert.Equal(defect is "terminal" or "registry" ? "not_applicable" : "unknown", both.Original.State);
        Assert.Equal(both.Original.State, both.Archive.State); Assert.Null(both.Original.SourceDirectory); Assert.Null(both.Archive.ArchiveDirectory);
        Assert.Null(both.Original.DirectoryIdentity); Assert.Null(both.Archive.DirectoryIdentity); Assert.Empty(owned.Operations.Calls);
    }
}
