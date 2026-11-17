using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    private static StartupBackupRecord LedgerRecord => new() { Id = Guid.NewGuid().ToString("N"), Source = "HKCU", Name = "old",
        Origin = "untrusted metadata", RawCommand = "old.exe", RegistryKind = RegistryValueKind.String, DisabledAt = DateTimeOffset.UnixEpoch };

    [Fact]
    public async Task LedgerDispositionActualDisableRestorePreservesOriginalFieldsAndExplicitState()
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw, RegistryValueKind.ExpandString);
        using var manager = fixture.Manager();
        var disabled = await manager.DisableAsync("HKCU", "owned", null);
        var original = Assert.Single(StartupBackupLedger.Read(await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath)).Records);
        Assert.Equal("disabled", original.Disposition); Assert.Null(original.RestoredAt);
        await manager.RestoreAsync(disabled.BackupId);
        var restored = Assert.Single(StartupBackupLedger.Read(await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath)).Records);
        Assert.Equal("restored", restored.Disposition); Assert.NotNull(restored.RestoredAt); Assert.Null(restored.RebuiltAt);
        Assert.Equal(original, restored with { Disposition = original.Disposition, RestoredAt = original.RestoredAt });
        Assert.Equal(fixture.Raw, fixture.Native.Get("HKCU", "owned")); Assert.Equal(RegistryValueKind.ExpandString, fixture.Native.Kind("HKCU", "owned"));
        Assert.DoesNotContain((await manager.GetSnapshotAsync()).Entries, entry => entry.IsDisabled);
        fixture.AssertNoTransactionOrTemps();
    }

    [Fact]
    public async Task LedgerDispositionHistoricalRebuiltIsPreservedButNeverRestoredOrListedAsPending()
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw, RegistryValueKind.String);
        var history = LedgerRecord with { Source = "Startup", RegistryKind = null, Disposition = "rebuilt", RebuiltAt = DateTimeOffset.UtcNow,
            Origin = @"\\never-probe.invalid\old-profile\original.lnk", BackupPath = @"\\never-probe.invalid\archive\original.lnk",
            Rebuild = new(@"\\never-probe.invalid\historical-only.exe", "--audit", null) };
        await File.WriteAllBytesAsync(fixture.Paths.StartupBackupPath, StartupBackupLedger.Write([history]));
        using var manager = fixture.Manager();
        Assert.DoesNotContain((await manager.GetSnapshotAsync()).Entries, entry => entry.IsDisabled);
        var writes = fixture.Native.WritableOpens;
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreAsync(history.Id));
        Assert.Equal(writes, fixture.Native.WritableOpens); Assert.Empty(fixture.Operations.Calls);
        await manager.DisableAsync("HKCU", "owned", null);
        var ledger = StartupBackupLedger.Read(await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath));
        Assert.Equal(history, ledger.Records[0]); Assert.Equal(2, ledger.Records.Count);
        var pending = Assert.Single((await manager.GetSnapshotAsync()).Entries);
        Assert.True(pending.IsDisabled); Assert.Equal(ledger.Records[1].Id, pending.BackupId);
        fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData("null")] [InlineData("unknown")] [InlineData("contradiction")]
    [InlineData("duplicate-id")] [InlineData("duplicate-field")] [InlineData("size")]
    [InlineData("reparse")] [InlineData("count")] [InlineData("action")] [InlineData("directory")]
    public async Task LedgerDispositionBadNativeInputNeverPublishesIntentOrOpensWritableNativeKey(string defect)
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw, RegistryValueKind.String);
        var old = LedgerRecord;
        var node = JsonSerializer.SerializeToNode(old)!.AsObject();
        byte[] bytes;
        if (defect == "null") { bytes = Encoding.UTF8.GetBytes("null"); }
        else if (defect == "size") { bytes = new byte[StartupBackupLedger.MaximumBytes + 1]; }
        else if (defect == "count")
        { bytes = JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(0, 513).Select(index => old with { Id = index.ToString() })); }
        else if (defect == "duplicate-id") { bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { old, old }); }
        else if (defect == "duplicate-field")
        { bytes = Encoding.UTF8.GetBytes("[" + node.ToJsonString().Replace("\"source\":", "\"SOURCE\":\"Startup\",\"source\":", StringComparison.Ordinal) + "]"); }
        else
        {
            if (defect == "unknown") { node["future_disposition"] = "preserve"; }
            if (defect == "contradiction") { node["disposition"] = "disabled"; node["restored_at"] = DateTimeOffset.UtcNow; }
            if (defect == "action") { node["action"] = "restore"; }
            bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { node });
        }
        if (defect == "reparse")
        {
            var target = Path.Combine(fixture.Root, "owned-ledger-target.json"); await File.WriteAllBytesAsync(target, bytes);
            File.CreateSymbolicLink(fixture.Paths.StartupBackupPath, target);
        }
        else if (defect == "directory") { Directory.CreateDirectory(fixture.Paths.StartupBackupPath); }
        else { await File.WriteAllBytesAsync(fixture.Paths.StartupBackupPath, bytes); }
        var writes = fixture.Native.WritableOpens;
        using var manager = fixture.Manager();
        Assert.NotNull(await Record.ExceptionAsync(() => manager.DisableAsync("HKCU", "owned", null)));
        Assert.NotNull(await Record.ExceptionAsync(() => manager.RestoreAsync(old.Id)));
        Assert.Equal(writes, fixture.Native.WritableOpens); Assert.Equal(fixture.Raw, fixture.Native.Get("HKCU", "owned"));
        Assert.Empty(fixture.Operations.Calls);
        if (defect == "directory") { Assert.True(Directory.Exists(fixture.Paths.StartupBackupPath)); }
        else { Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath)); }
        fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LedgerDispositionFullCandidateIsRejectedBeforeIntentAndBeforeRemovingTheSelectedValue(bool byteLimit)
    {
        using var fixture = new Fixture();
        var raw = byteLimit ? "\"C:\\never-run.exe\" " + new string('x', 32000) : fixture.Raw;
        fixture.Native.Put("HKCU", "owned", raw, RegistryValueKind.String);
        var seed = LedgerRecord with { Disposition = "restored", RestoredAt = DateTimeOffset.UtcNow,
            RawCommand = byteLimit ? new string('x', 32768) : "old.exe" };
        var records = Enumerable.Range(0, byteLimit ? 15 : 512).Select(index => seed with { Id = index.ToString(), Name = "old-" + index }).ToArray();
        var bytes = StartupBackupLedger.Write(records);
        await File.WriteAllBytesAsync(fixture.Paths.StartupBackupPath, bytes);
        var writes = fixture.Native.WritableOpens;
        using var manager = fixture.Manager();
        await Assert.ThrowsAnyAsync<JsonException>(() => manager.DisableAsync("HKCU", "owned", null));
        Assert.Equal(writes, fixture.Native.WritableOpens); Assert.Equal(raw, fixture.Native.Get("HKCU", "owned"));
        Assert.Empty(fixture.Operations.Calls); Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath));
        fixture.AssertNoTransactionOrTemps();
    }

    [Fact]
    public async Task LedgerDispositionRestoreCandidateCannotOverflowAfterTheNativeValueWasCreated()
    {
        using var fixture = new Fixture();
        var old = LedgerRecord with { RestoredAt = DateTimeOffset.UtcNow, Disposition = "restored", RawCommand = new string('x', 32768) };
        var selected = LedgerRecord;
        var records = Enumerable.Range(0, 15).Select(index => old with { Id = index.ToString() }).ToList();
        records.Add(old with { Id = "padding", RawCommand = "" }); records.Add(selected);
        var available = StartupBackupLedger.MaximumBytes - 5 - StartupBackupLedger.Write(records).Length;
        Assert.InRange(available, 1, 32768);
        records[15] = records[15] with { RawCommand = new string('x', available) };
        var bytes = StartupBackupLedger.Write(records);
        Assert.Equal(StartupBackupLedger.MaximumBytes - 5, bytes.Length);
        await File.WriteAllBytesAsync(fixture.Paths.StartupBackupPath, bytes);
        var writes = fixture.Native.WritableOpens;
        using var manager = fixture.Manager();
        await Assert.ThrowsAnyAsync<JsonException>(() => manager.RestoreAsync(selected.Id));
        Assert.Equal(writes, fixture.Native.WritableOpens); Assert.Null(fixture.Native.Value("HKCU", selected.Name));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath)); Assert.Empty(fixture.Operations.Calls);
        fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData("unknown")] [InlineData("contradiction")] [InlineData("duplicate")]
    public async Task LedgerDispositionPartialReadSignalsInvalidHistoryAndNeverPretendsReliableEmpty(string defect)
    {
        using var fixture = new Fixture(); var record = LedgerRecord;
        var node = JsonSerializer.SerializeToNode(record)!;
        if (defect == "unknown") { node["future"] = true; }
        else if (defect == "contradiction") { node["disposition"] = "restored"; }
        var bytes = defect == "duplicate" ? JsonSerializer.SerializeToUtf8Bytes(new[] { record, record })
            : JsonSerializer.SerializeToUtf8Bytes(new[] { node });
        await File.WriteAllBytesAsync(fixture.Paths.StartupBackupPath, bytes);
        using var manager = fixture.Manager(); var snapshot = await manager.GetSnapshotAsync();
        Assert.True(snapshot.Truncated); Assert.Equal(5, snapshot.ReadableSources);
        Assert.Equal(new StartupSourceIssue("backups", "backup_record_invalid"), Assert.Single(snapshot.SourceIssues));
        Assert.All(snapshot.Entries, entry => Assert.False(entry.CanModify));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath)); Assert.Empty(fixture.Operations.Calls);
    }
}
