using System.IO;
using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup")] [InlineData("Common Startup")]
    public async Task BackupSlotPresentHasActualStableIdentityButNoAuthenticityOrWriteAuthority(string source)
    {
        using var owned = new Fixture(administrator: false);
        var record = await WriteEvidenceRecord(owned, source);
        await File.WriteAllBytesAsync(record.BackupPath!, [4, 2, 8]); // NOT an authentic .lnk.
        var before = await File.ReadAllBytesAsync(owned.Paths.StartupBackupPath);
        using var manager = owned.Manager();
        var first = await manager.ObserveBackupSlotAsync(record.Id);
        var second = await manager.ObserveBackupSlotAsync(record.Id);
        Assert.Equal("present", first.Observation.State); Assert.Null(first.Observation.Reason);
        Assert.Equal(3, first.Observation.Length); Assert.Equal(1u, first.Observation.LinkCount);
        Assert.Equal("insensitive", first.Observation.CaseSensitivity);
        Assert.Equal(first.Observation.DirectoryIdentity, second.Observation.DirectoryIdentity);
        Assert.Equal(first.Observation.FileIdentity, second.Observation.FileIdentity);
        Assert.Equal(16, first.Observation.FileIdentity!.VolumeSerialNumber.Length); Assert.Equal(32, first.Observation.FileIdentity.FileId.Length);
        Assert.NotEqual(first.Observation.DirectoryIdentity, first.Observation.FileIdentity);
        Assert.False(first.NativeWriteReady); Assert.False(first.NativeWritePerformed); Assert.False(first.ExecutionRequested);
        Assert.Equal(first, JsonSerializer.Deserialize<StartupBackupSlotEvidence>(JsonSerializer.Serialize(first, AgentProtocol.JsonOptions), AgentProtocol.JsonOptions));
        Assert.Equal(before, await File.ReadAllBytesAsync(owned.Paths.StartupBackupPath));
        Assert.Equal(0, owned.Native.WritableOpens); Assert.Empty(owned.Operations.Calls); owned.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData("Startup")] [InlineData("Common Startup")]
    public async Task BackupSlotMissingRequiresAnExistingIdentifiedParentAndStillCannotAuthorizeAFabricatedRecord(string source)
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned, source);
        using var manager = owned.Manager(); var evidence = await manager.ObserveBackupSlotAsync(record.Id);
        Assert.Equal("missing", evidence.Observation.State); Assert.Equal(2, evidence.Observation.NativeErrorCode);
        Assert.NotNull(evidence.Observation.DirectoryIdentity); Assert.Null(evidence.Observation.FileIdentity);
        Assert.Null(evidence.Observation.Length); Assert.Null(evidence.Observation.LinkCount);
        Assert.False(evidence.NativeWriteReady); Assert.False(evidence.NativeWritePerformed); Assert.False(evidence.ExecutionRequested);
        Assert.False(File.Exists(record.BackupPath)); Assert.Empty(owned.Operations.Calls); owned.AssertNoTransactionOrTemps();
    }

    [Fact]
    public async Task BackupSlotMissingParentAndLockedLeafAreUnknownNotMissing()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned, createDirectory: false);
        using var manager = owned.Manager();
        var absent = await manager.ObserveBackupSlotAsync(record.Id);
        Assert.Equal("unknown", absent.Observation.State); Assert.Equal("archive_directory_missing", absent.Observation.Reason);
        Assert.Null(absent.Observation.DirectoryIdentity);
        Directory.CreateDirectory(Path.GetDirectoryName(record.BackupPath!)!);
        await File.WriteAllBytesAsync(record.BackupPath!, [1, 2, 3]);
        using (var locked = new FileStream(record.BackupPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var busy = await manager.ObserveBackupSlotAsync(record.Id);
            Assert.Equal("unknown", busy.Observation.State); Assert.Equal("archive_occupied", busy.Observation.Reason);
            Assert.Equal(32, busy.Observation.NativeErrorCode); Assert.NotNull(busy.Observation.DirectoryIdentity);
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(record.BackupPath!));
        Assert.False(File.Exists(owned.Paths.StartupBackupPath + ".transaction"));
    }

    [Theory]
    [InlineData("directory")] [InlineData("file-reparse")] [InlineData("parent-reparse")] [InlineData("oversize")]
    public async Task BackupSlotNonRegularOrUnboundedLeavesNeverBecomeMissing(string defect)
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned, createDirectory: defect != "parent-reparse");
        var directory = Path.GetDirectoryName(record.BackupPath!)!;
        var other = Path.Combine(owned.Root, "owned-reparse-target"); Directory.CreateDirectory(other);
        if (defect == "directory") { Directory.CreateDirectory(record.BackupPath!); }
        else if (defect == "file-reparse")
        { var target = Path.Combine(other, "target"); await File.WriteAllBytesAsync(target, [1]); File.CreateSymbolicLink(record.BackupPath!, target); }
        else if (defect == "parent-reparse") { Directory.CreateDirectory(Path.GetDirectoryName(directory)!); Directory.CreateSymbolicLink(directory, other); }
        else { await File.WriteAllBytesAsync(record.BackupPath!, new byte[65537]); }
        try
        {
            using var manager = owned.Manager(); var evidence = await manager.ObserveBackupSlotAsync(record.Id);
            Assert.Equal("unknown", evidence.Observation.State); Assert.Null(evidence.Observation.FileIdentity);
            Assert.False(evidence.NativeWriteReady); Assert.Empty(owned.Operations.Calls);
            if (defect == "directory") { Assert.Equal("archive_not_regular", evidence.Observation.Reason); }
            if (defect == "oversize") { Assert.Equal("archive_too_large", evidence.Observation.Reason); }
        }
        finally
        {
            if (defect == "file-reparse") { VerifyEvidenceOwnedPath(owned, record.BackupPath!); File.Delete(record.BackupPath!); }
            if (defect == "parent-reparse") { VerifyEvidenceOwnedPath(owned, directory); Directory.Delete(directory); }
        }
    }

    [Fact]
    public async Task BackupSlotDetectsReplacementAndReleasesEveryHandleAfterSeal()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned);
        await File.WriteAllBytesAsync(record.BackupPath!, [7]); using var manager = owned.Manager();
        var initial = await manager.ObserveBackupSlotAsync(record.Id);
        var saved = record.BackupPath + ".saved"; VerifyEvidenceOwnedPath(owned, record.BackupPath!); VerifyEvidenceOwnedPath(owned, saved);
        File.Move(record.BackupPath!, saved); await File.WriteAllBytesAsync(record.BackupPath!, [7]);
        var replaced = await manager.ObserveBackupSlotAsync(record.Id);
        Assert.NotEqual(initial.Observation.FileIdentity, replaced.Observation.FileIdentity);
        Assert.Equal(initial.RecordMetadataDigest, replaced.RecordMetadataDigest);
        Assert.Equal(initial.Observation.DirectoryIdentity, replaced.Observation.DirectoryIdentity);
        var directory = Path.GetDirectoryName(record.BackupPath!)!; var old = directory + ".old";
        VerifyEvidenceOwnedPath(owned, directory); VerifyEvidenceOwnedPath(owned, old); Directory.Move(directory, old); Directory.CreateDirectory(directory);
        var newParent = await manager.ObserveBackupSlotAsync(record.Id);
        Assert.Equal("missing", newParent.Observation.State); Assert.NotEqual(initial.Observation.DirectoryIdentity, newParent.Observation.DirectoryIdentity);
        Assert.False(newParent.NativeWriteReady); // Changed directory/metadata evidence cannot itself authorize creation.
    }

    [Fact]
    public async Task BackupSlotReadLeasesRejectOrdinaryParentRenameAndLeafWritesWhileHeld()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned); await File.WriteAllBytesAsync(record.BackupPath!, [7]);
        var directory = Path.GetDirectoryName(record.BackupPath!)!; var moved = directory + ".moved";
        VerifyEvidenceOwnedPath(owned, directory); VerifyEvidenceOwnedPath(owned, moved);
        var observed = StartupArchiveSlotProbe.Observe(record.BackupPath!, beforeSeal: () =>
        {
            Assert.Throws<IOException>(() => Directory.Move(directory, moved));
            Assert.Throws<IOException>(() => { using var writer = new FileStream(record.BackupPath!, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
        });
        Assert.Equal("present", observed.State); Assert.False(Directory.Exists(moved));
        await File.WriteAllBytesAsync(record.BackupPath!, [8]); Assert.Equal(new byte[] { 8 }, await File.ReadAllBytesAsync(record.BackupPath!));
    }

    [Theory]
    [InlineData("old-layout")] [InlineData("archive-network")] [InlineData("origin-network")]
    [InlineData("name-traversal")] [InlineData("reserved-space")]
    public async Task BackupSlotInvalidOrObsoleteMetadataDoesNotGetReportedAsMissing(string defect)
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned);
        owned.RewriteBackup(row =>
        {
            if (defect == "old-layout") { row["backup_path"] = Path.Combine(owned.Root, "obsolete", record.Id + ".lnk"); }
            else if (defect == "archive-network") { row["backup_path"] = @"\\offline.invalid\never-probe\" + record.Id + ".lnk"; }
            else if (defect == "origin-network") { row["origin"] = @"\\offline.invalid\never-probe\owned.lnk"; }
            else if (defect == "name-traversal") { row["name"] = "..\\owned"; }
            else { row["name"] = "CON "; row["origin"] = Path.Combine(owned.Native.GetShortcutDirectory("Startup"), "CON .lnk"); }
        });
        using var manager = owned.Manager(); var result = await manager.ObserveBackupSlotAsync(record.Id);
        Assert.Equal("unknown", result.Observation.State); Assert.Equal("archive_context_invalid", result.Observation.Reason);
        Assert.Null(result.Observation.ArchiveDirectory); Assert.Null(result.Observation.DirectoryIdentity); Assert.Empty(owned.Operations.Calls);
    }

    [Theory]
    [InlineData("terminal")] [InlineData("registry")]
    public async Task BackupSlotTerminalAndRegistryRecordsDoNotProbeAShortcutSlot(string defect)
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned);
        owned.RewriteBackup(row =>
        {
            if (defect == "terminal") { row["disposition"] = "restored"; row["restored_at"] = DateTimeOffset.UnixEpoch; }
            else { row["source"] = "HKCU"; row["backup_path"] = null; }
        });
        using var manager = owned.Manager(); var result = await manager.ObserveBackupSlotAsync(record.Id);
        Assert.Equal("not_applicable", result.Observation.State);
        Assert.Equal(defect == "terminal" ? "record_not_disabled" : "shortcut_required", result.Observation.Reason);
        Assert.Null(result.Observation.DirectoryIdentity); Assert.False(result.NativeWriteReady);
    }

    [Fact]
    public async Task BackupSlotCancellationAndUnresolvedIntentCannotProduceSuccessOrKeepHandles()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned); await File.WriteAllBytesAsync(record.BackupPath!, [3]);
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => StartupArchiveSlotProbe.Observe(record.BackupPath!, cancellation.Token, cancellation.Cancel));
        await File.WriteAllBytesAsync(record.BackupPath!, [4]);
        await File.WriteAllTextAsync(owned.Paths.StartupBackupPath + ".transaction", "unresolved");
        using var manager = owned.Manager(); await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ObserveBackupSlotAsync(record.Id));
        Assert.Empty(owned.Operations.Calls); Assert.Equal(new byte[] { 4 }, await File.ReadAllBytesAsync(record.BackupPath!));
    }

    private static async Task<StartupBackupRecord> WriteEvidenceRecord(Fixture owned, string source = "Startup", bool createDirectory = true)
    {
        var configuration = await new ConfigurationLoader().LoadAsync(owned.Root);
        var id = Guid.NewGuid().ToString("N"); var path = Path.Combine(configuration.Settings.BackupDirectory, "startup", id + ".lnk");
        VerifyEvidenceOwnedPath(owned, path);
        if (createDirectory) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); }
        var record = new StartupBackupRecord { Id = id, Source = source, Name = "owned", DisabledAt = DateTimeOffset.UnixEpoch,
            Origin = Path.Combine(owned.Native.GetShortcutDirectory(source), "owned.lnk"), BackupPath = path,
            Target = @"\\offline.invalid\never-open-or-run.exe", WorkingDirectory = @"\\offline.invalid\never-probe" };
        Directory.CreateDirectory(Path.GetDirectoryName(owned.Paths.StartupBackupPath)!);
        await File.WriteAllTextAsync(owned.Paths.StartupBackupPath, JsonSerializer.Serialize(new[] { record }));
        return record;
    }

    private static void VerifyEvidenceOwnedPath(Fixture owned, string path) =>
        Assert.StartsWith(Path.GetFullPath(owned.Root) + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.Ordinal);
}
