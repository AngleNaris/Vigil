using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup", false, false)] [InlineData("Startup", true, false)]
    [InlineData("Startup", false, true)] [InlineData("Startup", true, true)]
    [InlineData("Common Startup", false, false)] [InlineData("Common Startup", true, false)]
    [InlineData("Common Startup", false, true)] [InlineData("Common Startup", true, true)]
    public async Task NativeInspectionDataRealAdapterPipeAndTypedClientMatchBackendWithoutAuthority(string source, bool archived, bool original)
    {
        using var owned = new Fixture(administrator: false); var metadata = new InspectionOwnedReader(owned); var slots = new PipeOwnedSlotReader(owned);
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        metadata.Paths = server.Paths; slots.Paths = server.Paths;
        var record = await WritePipeEvidenceRecord(owned, server, source);
        if (archived) { await File.WriteAllBytesAsync(record.BackupPath!, [1]); }
        if (original) { await File.WriteAllBytesAsync(record.Origin, [2, 3]); }
        var ledger = await File.ReadAllBytesAsync(server.Paths.StartupBackupPath); var yaml = await File.ReadAllBytesAsync(server.Paths.ConfigPath);
        var client = new AgentStartupClient(server.Client); var list = await client.ReadAsync(new("all", "shortcut", "disabled")); var entry = Assert.Single(list.Entries);
        var result = await client.InspectNativeAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), list.ConfigurationRevision);
        Assert.Equal(archived ? "present" : "missing", result.Slots.Archive.State); Assert.Equal(original ? "present" : "missing", result.Slots.Original.State);
        Assert.Equal(record.Id, result.Slots.BackupId); Assert.Equal(result.Slots.RecordMetadataDigest, result.RecordMetadataDigestAfter);
        Assert.Equal("unverified", result.HistoricalIdentity); Assert.Equal("not_checked", result.Metadata.OriginalBackupState);
        Assert.False(result.NativeWriteReady); Assert.False(result.NativeWritePerformed); Assert.False(result.ExecutionRequested);
        using var manager = new StartupManager(server.Paths, owned.Native, operations: owned.Operations);
        var direct = await manager.ObserveShortcutSlotsAsync(record.Id);
        Assert.Equal(direct.Archive with { ObservedAt = result.Slots.Archive.ObservedAt }, result.Slots.Archive);
        Assert.Equal(direct.Original with { ObservedAt = result.Slots.Original.ObservedAt }, result.Slots.Original);
        Assert.Equal(direct.RecordMetadataDigest, result.Slots.RecordMetadataDigest);
        Assert.Equal(ledger, await File.ReadAllBytesAsync(server.Paths.StartupBackupPath)); Assert.Equal(yaml, await File.ReadAllBytesAsync(server.Paths.ConfigPath));
        Assert.Equal(1, slots.Calls); Assert.Equal(0, owned.Native.WritableOpens); Assert.Empty(owned.Native.AccessedKeys); Assert.Empty(owned.Operations.Calls);
        owned.AssertNoTransactionOrTemps(); Assert.False(File.Exists(server.Paths.StartupBackupPath + ".transaction"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NativeInspectionDataPartialNativeLockIsIndependentUnknownNotTimeoutOrMissing(bool archive)
    {
        using var owned = new Fixture(); var metadata = new InspectionOwnedReader(owned); var slots = new PipeOwnedSlotReader(owned);
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        metadata.Paths = server.Paths; slots.Paths = server.Paths; var record = await WritePipeEvidenceRecord(owned, server);
        await File.WriteAllBytesAsync(record.BackupPath!, [1]); await File.WriteAllBytesAsync(record.Origin, [2]);
        var client = new AgentStartupClient(server.Client); var list = await client.ReadAsync(new("all", "shortcut", "disabled")); var entry = Assert.Single(list.Entries);
        using (var locked = new FileStream(archive ? record.BackupPath! : record.Origin, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await client.InspectNativeAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), list.ConfigurationRevision);
            Assert.Equal(archive ? "unknown" : "present", result.Slots.Archive.State); Assert.Equal(archive ? "present" : "unknown", result.Slots.Original.State);
            Assert.Equal(archive ? "archive_occupied" : "original_occupied", archive ? result.Slots.Archive.Reason : result.Slots.Original.Reason);
        }
        var next = await client.InspectNativeAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), list.ConfigurationRevision);
        Assert.Equal("present", next.Slots.Archive.State); Assert.Equal("present", next.Slots.Original.State); Assert.Equal(2, slots.Calls);
    }

    [Fact]
    public async Task NativeInspectionDataSourceAclDeniedRemainsUnknownThroughTheStrictWireContract()
    {
        using var owned = new Fixture(); var metadata = new InspectionOwnedReader(owned); var slots = new PipeOwnedSlotReader(owned);
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        metadata.Paths = server.Paths; slots.Paths = server.Paths; var record = await WritePipeEvidenceRecord(owned, server);
        var client = new AgentStartupClient(server.Client); var list = await client.ReadAsync(new("all", "shortcut", "disabled")); var entry = Assert.Single(list.Entries);
        var source = new DirectoryInfo(Path.GetDirectoryName(record.Origin)!); VerifyEvidenceOwnedPath(owned, source.FullName);
        var acl = source.GetAccessControl(); var original = acl.GetSecurityDescriptorBinaryForm(); var sddl = acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        try
        {
            acl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny)); source.SetAccessControl(acl);
            var result = await client.InspectNativeAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), list.ConfigurationRevision);
            Assert.Equal("missing", result.Slots.Archive.State); Assert.Equal("unknown", result.Slots.Original.State);
            Assert.Equal("original_permission_denied", result.Slots.Original.Reason); Assert.Null(result.Slots.Original.DirectoryIdentity); Assert.False(result.NativeWriteReady);
        }
        finally { acl.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access); source.SetAccessControl(acl); }
        Assert.Equal(sddl, source.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NativeInspectionDataInvalidCompleteLayoutReportsUnknownWithoutProbingEitherNamespace(bool origin)
    {
        using var owned = new Fixture(); var metadata = new InspectionOwnedReader(owned); var slots = new PipeOwnedSlotReader(owned);
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        metadata.Paths = server.Paths; slots.Paths = server.Paths; var record = await WritePipeEvidenceRecord(owned, server);
        record = origin ? record with { Origin = @"\\offline.invalid\never-probe\owned.lnk" } : record with { BackupPath = @"\\offline.invalid\never-probe\" + record.Id + ".lnk" };
        await File.WriteAllTextAsync(server.Paths.StartupBackupPath, JsonSerializer.Serialize(new[] { record }));
        var client = new AgentStartupClient(server.Client); var list = await client.ReadAsync(new("all", "shortcut", "disabled")); var entry = Assert.Single(list.Entries);
        var result = await client.InspectNativeAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), list.ConfigurationRevision);
        Assert.Equal("archive_context_invalid", result.Slots.Archive.Reason); Assert.Equal("original_context_invalid", result.Slots.Original.Reason);
        Assert.Null(result.Slots.Archive.ArchiveDirectory); Assert.Null(result.Slots.Original.SourceDirectory); Assert.False(result.NativeWriteReady);
    }

    [Fact]
    public async Task NativeInspectionDataActualLedgerEditAfterCaptureCannotPublishEvidenceOrClaimCas()
    {
        using var owned = new Fixture(); var metadata = new InspectionOwnedReader(owned); var slots = new PipeOwnedSlotReader(owned);
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        metadata.Paths = server.Paths; slots.Paths = server.Paths; var record = await WritePipeEvidenceRecord(owned, server);
        var client = new AgentStartupClient(server.Client); var list = await client.ReadAsync(new("all", "shortcut", "disabled")); var entry = Assert.Single(list.Entries);
        var changed = false;
        owned.Native.BeforeDirectory = () =>
        {
            if (changed) { return; } changed = true;
            File.WriteAllText(server.Paths.StartupBackupPath, JsonSerializer.Serialize(new[] { record with { Target = @"C:\changed-never-run.exe" } }));
        };
        try
        {
            var reply = await server.Client.SendResponseAsync("startup-inspect-native", entry.Id, StartupInspectionRequest.FromEntry(entry), expectedRevision: list.ConfigurationRevision);
            Assert.True(changed); Assert.Equal("startup_backup_changed", reply.ErrorCode); Assert.Null(reply.Data); Assert.Null(reply.OperationId);
            Assert.Empty(owned.Operations.Calls); Assert.Equal(0, owned.Native.WritableOpens);
        }
        finally { owned.Native.BeforeDirectory = null; }
    }

    private static async Task<StartupBackupRecord> WritePipeEvidenceRecord(Fixture owned, ConfigurationControlPlaneTests.ServerFixture server, string source = "Startup")
    {
        var configuration = await new ConfigurationLoader().LoadAsync(server.Root); var id = Guid.NewGuid().ToString("N");
        var archive = Path.Combine(configuration.Settings.BackupDirectory, "startup", id + ".lnk");
        Assert.StartsWith(Path.GetFullPath(server.Root) + Path.DirectorySeparatorChar, Path.GetFullPath(archive), StringComparison.Ordinal);
        var original = Path.Combine(owned.Native.GetShortcutDirectory(source), "owned.lnk"); VerifyEvidenceOwnedPath(owned, original);
        var record = new StartupBackupRecord { Id = id, Source = source, Name = "owned", Origin = original, BackupPath = archive,
            DisabledAt = DateTimeOffset.UnixEpoch, Target = @"\\offline.invalid\never-open-or-run.exe", WorkingDirectory = @"\\offline.invalid\never-probe" };
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!); Directory.CreateDirectory(Path.GetDirectoryName(server.Paths.StartupBackupPath)!);
        await File.WriteAllTextAsync(server.Paths.StartupBackupPath, JsonSerializer.Serialize(new[] { record })); return record;
    }
    private sealed class PipeOwnedSlotReader(Fixture owned) : IStartupSlotEvidenceReader
    {
        internal ProjectPaths? Paths; internal int Calls;
        public Task<StartupSlotEvidenceRead> ReadAsync(string backupId, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return new StartupSlotEvidenceReader(Paths!, owned.Native).ReadAsync(backupId, cancellationToken); }
    }
}
