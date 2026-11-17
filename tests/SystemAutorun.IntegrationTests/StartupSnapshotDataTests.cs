using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

// Uses the existing real, private registry/folder fixtures; never system Run/Startup locations.
public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData(RegistryValueKind.Binary)] [InlineData(RegistryValueKind.None)]
    [InlineData(RegistryValueKind.MultiString)] [InlineData(RegistryValueKind.DWord)] [InlineData(RegistryValueKind.QWord)]
    public async Task SnapshotDataNonTextValuesAreReadOnlyMetadataRatherThanFabricatedCommands(RegistryValueKind kind)
    {
        using var fixture = new Fixture();
        object payload = kind switch
        {
            RegistryValueKind.MultiString => new[] { "first", "second" },
            RegistryValueKind.DWord => 42, RegistryValueKind.QWord => 42L,
            _ => new byte[] { 1, 3, 7 },
        };
        fixture.Native.PutValue("HKCU", "unsupported", payload, kind);
        var writes = fixture.Native.WritableOpens;
        using var manager = fixture.Manager();
        var snapshot = await manager.GetSnapshotAsync();
        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal("unsupported", entry.Name); Assert.Equal("HKCU", entry.Source);
        Assert.Null(entry.RawCommand); Assert.Null(entry.Target); Assert.False(entry.IsManaged);
        Assert.Equal("registry_value_type_unsupported", entry.Error); Assert.False(entry.CanModify);
        Assert.Empty(snapshot.SourceIssues); Assert.Equal(6, snapshot.ReadableSources);
        await AssertSnapshotDataParityAsync(fixture, manager, snapshot);
        Assert.Equal(writes, fixture.Native.WritableOpens);
        Assert.Equal(JsonSerializer.Serialize(payload), JsonSerializer.Serialize(fixture.Native.Value("HKCU", "unsupported")));
        Assert.Empty(fixture.Operations.Calls); fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData(RegistryValueKind.String)] [InlineData(RegistryValueKind.ExpandString)]
    public async Task SnapshotDataTextKeepsLiteralEnvironmentTokensAndMatchesInventory(RegistryValueKind kind)
    {
        using var fixture = new Fixture();
        const string raw = "\"%TEMP%\\tool.exe\" --literal \"two words\"";
        fixture.Native.Put("HKCU", "text", raw, kind);
        var writes = fixture.Native.WritableOpens;
        using var manager = fixture.Manager();
        var snapshot = await manager.GetSnapshotAsync();
        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal(raw, entry.RawCommand); Assert.Equal(@"%TEMP%\tool.exe", entry.Target);
        Assert.Equal("--literal \"two words\"", entry.Arguments); Assert.Null(entry.Error);
        await AssertSnapshotDataParityAsync(fixture, manager, snapshot);
        Assert.Equal(writes, fixture.Native.WritableOpens); Assert.Equal(raw, fixture.Native.Get("HKCU", "text"));
        Assert.False(File.Exists(fixture.Paths.StartupCachePath)); Assert.Empty(fixture.Operations.Calls);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\broken.exe --flag")]
    [InlineData("C:\\Program Files\\ambiguous.exe --flag")]
    [InlineData("\"C:\\owned\\tool.exe\"suffix --flag")]
    [InlineData("C:\\owned\\bro\"ken.exe --flag")]
    [InlineData("bro\"ken.exe")]
    public async Task SnapshotDataMalformedOrAmbiguousCommandIsNotAClaimedTarget(string raw)
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "unparseable", raw);
        using var manager = fixture.Manager();
        var snapshot = await manager.GetSnapshotAsync();
        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal(raw, entry.RawCommand); Assert.Null(entry.Target);
        Assert.Equal("registry_command_ambiguous", entry.Error); Assert.False(entry.CanModify);
        await AssertSnapshotDataParityAsync(fixture, manager, snapshot);
        Assert.Equal(raw, fixture.Native.Get("HKCU", "unparseable")); Assert.Empty(fixture.Operations.Calls);
    }

    [Fact]
    public async Task SnapshotDataAnExistingUnquotedSpacePathDoesNotChangeTheLexicalDecision()
    {
        using var fixture = new Fixture();
        var directory = Path.Combine(fixture.Root, "owned space"); Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "never-run.exe");
        byte[] bytes = [1, 3, 7]; await File.WriteAllBytesAsync(target, bytes);
        var raw = target + " --flag"; fixture.Native.Put("HKCU", "ambiguous", raw);
        using var manager = fixture.Manager();
        var snapshot = await manager.GetSnapshotAsync();
        var entry = Assert.Single(snapshot.Entries);
        Assert.Null(entry.Target); Assert.Equal(raw, entry.RawCommand);
        Assert.Equal("registry_command_ambiguous", entry.Error); Assert.False(entry.CanModify);
        await AssertSnapshotDataParityAsync(fixture, manager, snapshot);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target)); Assert.Empty(fixture.Operations.Calls);
    }

    [Fact]
    public async Task SnapshotDataOversizedRegistryTextIsAnEntryErrorWithoutEchoingTheValue()
    {
        using var fixture = new Fixture();
        var raw = "\"C:\\owned\\never-run.exe\" " + new string('x', 32768);
        fixture.Native.Put("HKCU", "oversized", raw);
        using var manager = fixture.Manager();
        var snapshot = await manager.GetSnapshotAsync();
        var entry = Assert.Single(snapshot.Entries);
        Assert.Null(entry.RawCommand); Assert.Null(entry.Target);
        Assert.Equal("registry_value_too_large", entry.Error); Assert.False(entry.CanModify);
        await AssertSnapshotDataParityAsync(fixture, manager, snapshot);
        Assert.Equal(raw, fixture.Native.Get("HKCU", "oversized")); Assert.Empty(fixture.Operations.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SnapshotDataMissingOriginalLinkRemainsUnverifiedMetadataRegardlessOfTargetExistence(bool targetExists)
    {
        using var fixture = new Fixture();
        var origin = await fixture.CreateLinkAsync("Startup");
        var target = Path.Combine(fixture.Root, "never-run.exe");
        if (targetExists) { await File.WriteAllBytesAsync(target, [1, 3, 7]); }
        using var manager = fixture.Manager();
        var disabled = await manager.DisableAsync("Startup", "owned", origin);
        var backupPath = fixture.BackupLink(disabled.BackupId); // Verifies this exact path is in the owned root.
        Assert.Equal(disabled.BackupId + ".lnk", Path.GetFileName(backupPath));
        File.Delete(backupPath);
        var ledger = await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath);
        var operations = fixture.Operations.Calls.Count;
        var directoryReads = 0; fixture.Native.BeforeDirectory = () => directoryReads++;
        var snapshot = await manager.GetSnapshotAsync();
        Assert.Equal(2, directoryReads); // Only the two fixed active sources, no per-backup eligibility lookup.
        var entry = Assert.Single(snapshot.Entries);
        Assert.True(entry.IsDisabled); Assert.False(entry.CanModify); Assert.Equal(disabled.BackupId, entry.BackupId);
        Assert.Equal(target, entry.Target); Assert.Equal(1, snapshot.Disabled);
        Assert.Equal(ledger, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath));
        Assert.Equal(operations, fixture.Operations.Calls.Count); Assert.False(File.Exists(origin));
        await AssertSnapshotDataParityAsync(fixture, manager, snapshot);
        fixture.AssertNoTransactionOrTemps();
    }

    [Fact]
    public async Task SnapshotDataForeignBackupRecordDoesNotHideOrChangeALiveEntry()
    {
        using var fixture = new Fixture();
        var origin = await fixture.CreateLinkAsync("Startup");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { new
        {
            id = "foreign-record", action = "disable", disabled_at = DateTimeOffset.UtcNow,
            source = "Common Startup", name = "owned", origin,
            target = Path.Combine(fixture.Root, "absent.exe"),
            backup_path = Path.Combine(fixture.Root, "absent-backup.lnk"),
        } });
        await File.WriteAllBytesAsync(fixture.Paths.StartupBackupPath, bytes);
        using var manager = fixture.Manager();
        var snapshot = await manager.GetSnapshotAsync();
        var active = Assert.Single(snapshot.Entries, entry => !entry.IsDisabled);
        Assert.Equal("Startup", active.Source); Assert.Equal(origin, active.Origin); Assert.Null(active.BackupId);
        var recorded = Assert.Single(snapshot.Entries, entry => entry.IsDisabled);
        Assert.Equal("Common Startup", recorded.Source); Assert.False(recorded.CanModify);
        Assert.Equal("foreign-record", recorded.BackupId); Assert.Equal(1, snapshot.TotalActive);
        await AssertSnapshotDataParityAsync(fixture, manager, snapshot);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath));
        Assert.Empty(fixture.Operations.Calls);
    }

    [Fact]
    public async Task SnapshotDataOneDeniedSourceDoesNotDiscardOtherReadableMetadata()
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "readable", fixture.Raw);
        fixture.Native.BeforeReadOnlyOpen = source =>
        { if (source == "HKLM") { throw new UnauthorizedAccessException("Owned source denial"); } };
        using var manager = fixture.Manager();
        var snapshot = await manager.GetSnapshotAsync();
        Assert.Equal("readable", Assert.Single(snapshot.Entries).Name); Assert.Equal(5, snapshot.ReadableSources);
        Assert.Equal(new StartupSourceIssue("HKLM", "permission_denied"), Assert.Single(snapshot.SourceIssues));
        await AssertSnapshotDataParityAsync(fixture, manager, snapshot);
        Assert.Empty(fixture.Operations.Calls); Assert.False(File.Exists(fixture.Paths.StartupCachePath));
    }

    [Theory]
    [InlineData("json", "backup_invalid")] [InlineData("null", "backup_invalid")]
    [InlineData("size", "source_unavailable")] [InlineData("reparse", "source_unavailable")]
    [InlineData("transaction", "startup_transaction_unresolved")]
    public async Task SnapshotDataBackupFailureIsASourceIssueWithoutRecoveryOrLossOfActiveRows(string mode, string reason)
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "readable", fixture.Raw);
        if (mode == "transaction") { await File.WriteAllTextAsync(fixture.Paths.StartupBackupPath + ".transaction", "{}"); }
        else if (mode == "size") { await File.WriteAllBytesAsync(fixture.Paths.StartupBackupPath, new byte[512 * 1024 + 1]); }
        else if (mode == "reparse")
        {
            var target = Path.Combine(fixture.Root, "owned-target.json"); await File.WriteAllTextAsync(target, "[]");
            File.CreateSymbolicLink(fixture.Paths.StartupBackupPath, target);
        }
        else { await File.WriteAllTextAsync(fixture.Paths.StartupBackupPath, mode == "null" ? "null" : "{bad-json"); }
        using var manager = fixture.Manager();
        var snapshot = await manager.GetSnapshotAsync();
        Assert.Equal("readable", Assert.Single(snapshot.Entries).Name); Assert.Equal(5, snapshot.ReadableSources);
        Assert.Equal(new StartupSourceIssue("backups", reason), Assert.Single(snapshot.SourceIssues));
        await AssertSnapshotDataParityAsync(fixture, manager, snapshot);
        Assert.Empty(fixture.Operations.Calls); Assert.False(File.Exists(fixture.Paths.StartupCachePath));
    }

    [Fact]
    public async Task SnapshotDataNoReadableSourceIsNotAConfidentEmptySnapshot()
    {
        using var fixture = new Fixture();
        fixture.Native.BeforeDirectory = () => throw new IOException("Owned folder unavailable");
        fixture.Native.BeforeReadOnlyOpen = _ => throw new UnauthorizedAccessException("Owned key unavailable");
        await File.WriteAllTextAsync(fixture.Paths.StartupBackupPath, "{bad-json");
        using var manager = fixture.Manager();
        var snapshot = await manager.GetSnapshotAsync();
        Assert.Empty(snapshot.Entries); Assert.Equal(0, snapshot.ReadableSources); Assert.Equal(6, snapshot.SourceIssues.Count);
        await AssertSnapshotDataParityAsync(fixture, manager, snapshot);
        Assert.Empty(fixture.Operations.Calls); Assert.False(File.Exists(fixture.Paths.StartupCachePath));
    }

    [Fact]
    public async Task SnapshotDataCancelledReadNeverOpensANativeSource()
    {
        using var fixture = new Fixture(); fixture.Native.AccessedKeys.Clear();
        fixture.Native.BeforeDirectory = () => throw new InvalidOperationException("Must not enumerate after cancellation");
        using var manager = fixture.Manager(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.GetSnapshotAsync(cancellation.Token));
        Assert.Empty(fixture.Native.AccessedKeys); Assert.Empty(fixture.Operations.Calls);
    }

    [Fact]
    public async Task SnapshotDataLegacyForeignProfileRecordIsListedWithoutClaimingRestorability()
    {
        using var fixture = new Fixture();
        var origin = Path.Combine(fixture.Root, "unrelated-profile", "startup", "legacy.lnk");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { new
        {
            id = "legacy", source = "Startup", name = "legacy", disabled_at = DateTimeOffset.UtcNow,
            origin, target = Path.Combine(fixture.Root, "missing.exe"), backup_path = Path.Combine(fixture.Root, "missing.lnk"),
        } });
        await File.WriteAllBytesAsync(fixture.Paths.StartupBackupPath, bytes);
        using var manager = fixture.Manager();
        var snapshot = await manager.GetSnapshotAsync();
        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal("legacy", entry.BackupId); Assert.Equal(origin, entry.Origin);
        Assert.True(entry.IsDisabled); Assert.False(entry.CanModify);
        await AssertSnapshotDataParityAsync(fixture, manager, snapshot);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath));
        Assert.Empty(fixture.Operations.Calls);
    }

    [Theory]
    [InlineData("name")] [InlineData("origin")] [InlineData("target")]
    [InlineData("raw_command")] [InlineData("id")]
    public async Task SnapshotDataMalformedLedgerFieldsAreSourceErrorsNotInvalidGuiRows(string field)
    {
        using var fixture = new Fixture();
        var row = JsonSerializer.SerializeToNode(new
        {
            id = "owned", source = "HKCU", name = "metadata", origin = "metadata-only",
            disabled_at = DateTimeOffset.UtcNow, target = @"C:\owned\never-run.exe", raw_command = "tool.exe",
        })!.AsObject();
        row[field] = field is "name" or "origin" ? null
            : field == "target" ? new string('x', 4097)
            : field == "id" ? new string('x', 257) : new string('x', 32769);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { row });
        await File.WriteAllBytesAsync(fixture.Paths.StartupBackupPath, bytes);
        using var manager = fixture.Manager();
        var snapshot = await manager.GetSnapshotAsync();
        Assert.Empty(snapshot.Entries); Assert.True(snapshot.Truncated); Assert.Equal(5, snapshot.ReadableSources);
        Assert.Equal(new StartupSourceIssue("backups", "backup_record_invalid"), Assert.Single(snapshot.SourceIssues));
        await AssertSnapshotDataParityAsync(fixture, manager, snapshot);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath));
        Assert.Empty(fixture.Operations.Calls);
    }

    [Fact]
    public async Task SnapshotDataOversizedRegistrySourceIsExplicitlyTruncatedInsteadOfAnUnboundedScan()
    {
        using var fixture = new Fixture();
        using (var key = fixture.Native.OpenRunKey("HKCU", writable: true, create: false))
        { for (var index = 0; index < 513; index++) { key!.SetValue("owned-" + index, fixture.Raw, RegistryValueKind.String); } }
        var writes = fixture.Native.WritableOpens;
        using var manager = fixture.Manager();
        var snapshot = await manager.GetSnapshotAsync();
        Assert.Empty(snapshot.Entries); Assert.True(snapshot.Truncated); Assert.Equal(5, snapshot.ReadableSources);
        Assert.Equal(new StartupSourceIssue("HKCU", "registry_source_limit"), Assert.Single(snapshot.SourceIssues));
        await AssertSnapshotDataParityAsync(fixture, manager, snapshot);
        Assert.Equal(writes, fixture.Native.WritableOpens); Assert.Empty(fixture.Operations.Calls);
    }

    private static async Task AssertSnapshotDataParityAsync(Fixture fixture, StartupManager manager, StartupManagementSnapshot snapshot)
    {
        var configuration = await new ConfigurationLoader().LoadAsync(fixture.Root);
        var inventory = manager.CaptureInventory(configuration, new StartupListQuery(), CancellationToken.None);
        Assert.Equal(inventory.Entries.ToArray(), snapshot.Entries.ToArray());
        Assert.Equal(inventory.SourceIssues.ToArray(), snapshot.SourceIssues.ToArray());
        Assert.Equal(inventory.TotalActive, snapshot.TotalActive); Assert.Equal(inventory.ManagedActive, snapshot.ManagedActive);
        Assert.Equal(inventory.Disabled, snapshot.Disabled); Assert.Equal(inventory.IsElevated, snapshot.IsElevated);
        Assert.Equal(inventory.Truncated, snapshot.Truncated); Assert.Equal(inventory.ReadableSources, snapshot.ReadableSources);
    }
}
