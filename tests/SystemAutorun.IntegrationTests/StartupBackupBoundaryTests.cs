using System.IO;
using System.Text.Json.Nodes;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    private static void SetBoundaryBackupDirectory(Fixture fixture, string directory) =>
        File.WriteAllText(fixture.Paths.ConfigPath, "version: 1\nsettings:\n  default_startup_delay: 0\n  backup_dir: '"
            + directory.Replace("'", "''", StringComparison.Ordinal) + "'\ntasks: []\n");

    [Theory]
    [InlineData("relative")] [InlineData("drive-relative")] [InlineData("root-relative")]
    [InlineData("unc")] [InlineData("device")] [InlineData("stream")]
    [InlineData("parent")] [InlineData("trailing-dot")] [InlineData("trailing-space")]
    [InlineData("other-id")] [InlineData("null-path")] [InlineData("wrong-name")]
    [InlineData("wrong-id")] [InlineData("empty-guid")] [InlineData("wrong-action")]
    [InlineData("reserved")] [InlineData("percent")] [InlineData("foreign-origin")]
    [InlineData("origin-stream")] [InlineData("origin-relative")] [InlineData("origin-extension")]
    [InlineData("unknown-source")]
    public async Task InvalidShortcutBackupMetadataIsRejectedBeforeIntentOrNative(string mode)
    {
        using var fixture = new Fixture(); var origin = await fixture.CreateLinkAsync("Startup");
        using var manager = fixture.Manager(); var disabled = await manager.DisableAsync("Startup", "owned", origin);
        var backup = fixture.BackupLink(disabled.BackupId); var bytes = await File.ReadAllBytesAsync(backup);
        var requestId = disabled.BackupId;
        fixture.RewriteBackup(row =>
        {
            var directory = Path.GetDirectoryName(backup)!;
            switch (mode)
            {
                case "relative": row["backup_path"] = Path.GetRelativePath(fixture.Root, backup); break;
                case "drive-relative": row["backup_path"] = backup[..2] + backup[3..]; break;
                case "root-relative": row["backup_path"] = backup[2..]; break;
                case "unc": row["backup_path"] = @"\\invalid.example.test\share\" + disabled.BackupId + ".lnk"; break;
                case "device": row["backup_path"] = @"\\?\" + backup; break;
                case "stream": row["backup_path"] = backup + ":payload"; break;
                case "parent": row["backup_path"] = Path.Combine(directory, "..", "startup", disabled.BackupId + ".lnk"); break;
                case "trailing-dot": row["backup_path"] = backup + "."; break;
                case "trailing-space": row["backup_path"] = backup + " "; break;
                case "other-id": row["backup_path"] = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".lnk"); break;
                case "null-path": row["backup_path"] = null; break;
                case "wrong-name": row["name"] = "other"; break;
                case "wrong-id": row["id"] = requestId = "not-a-guid"; break;
                case "empty-guid": row["id"] = requestId = Guid.Empty.ToString("N"); break;
                case "wrong-action": row["action"] = "restore"; break;
                case "reserved": row["backup_path"] = Path.Combine(directory, "NUL", disabled.BackupId + ".lnk"); break;
                case "percent": row["backup_path"] = Path.Combine(directory, "%TEMP%", disabled.BackupId + ".lnk"); break;
                case "foreign-origin": row["origin"] = Path.Combine(fixture.Native.GetShortcutDirectory("Common Startup"), "owned.lnk"); break;
                case "origin-stream": row["origin"] = origin + ":payload"; break;
                case "origin-relative": row["origin"] = "owned.lnk"; break;
                case "origin-extension": row["origin"] = Path.ChangeExtension(origin, ".txt"); break;
                case "unknown-source": row["source"] = "arbitrary"; break;
                default: throw new InvalidOperationException("Unknown owned fixture mode.");
            }
        });
        fixture.Operations.Calls.Clear(); var ledger = await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreAsync(requestId));
        Assert.False(File.Exists(origin)); Assert.Equal(bytes, await File.ReadAllBytesAsync(backup));
        Assert.Equal(ledger, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath));
        Assert.Empty(fixture.Operations.Calls); fixture.AssertNoTransactionOrTemps();
    }

    [Fact]
    public async Task OperatedPathsAreDerivedFromTheFixedAuthorityNotEditableParentSpelling()
    {
        using var fixture = new Fixture(); var origin = await fixture.CreateLinkAsync("Startup");
        using var manager = fixture.Manager(); var disabled = await manager.DisableAsync("Startup", "owned", origin);
        var source = fixture.Native.GetShortcutDirectory("Startup");
        using (var paths = StartupShortcutPathLease.Open(Path.Combine(fixture.Root, "backups"), disabled.BackupId,
            "owned", Path.Combine(source.ToUpperInvariant(), "owned.lnk"), fixture.BackupLink(disabled.BackupId), source,
            [source, fixture.Native.GetShortcutDirectory("Common Startup")], restoring: true))
        { Assert.Equal(origin, paths.Origin); Assert.Equal(fixture.BackupLink(disabled.BackupId), paths.BackupPath); }
        fixture.RewriteBackup(row => row["name"] = "OWNED"); fixture.Operations.Calls.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreAsync(disabled.BackupId));
        Assert.Empty(fixture.Operations.Calls); fixture.AssertNoTransactionOrTemps();
    }

    [Fact]
    public async Task MissingShortcutBackupCannotBeReconstructedFromAnExistingMetadataTarget()
    {
        using var fixture = new Fixture(); var origin = await fixture.CreateLinkAsync("Startup");
        using var manager = fixture.Manager(); var disabled = await manager.DisableAsync("Startup", "owned", origin);
        var backup = fixture.BackupLink(disabled.BackupId);
        Assert.StartsWith(fixture.Root + Path.DirectorySeparatorChar, backup, StringComparison.Ordinal);
        File.Delete(backup); await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "never-run.exe"), [0, 1, 2]);
        fixture.Operations.Calls.Clear(); var ledger = await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath);
        await Assert.ThrowsAsync<FileNotFoundException>(() => manager.RestoreAsync(disabled.BackupId));
        Assert.False(File.Exists(origin)); Assert.Empty(fixture.Operations.Calls);
        Assert.Equal(ledger, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath)); fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CurrentCustomArchiveInsideOrOutsideRootStillRoundTripsExactBytes(bool outsideRoot)
    {
        using var fixture = new Fixture(); using var other = new Fixture();
        var configured = Path.Combine(outsideRoot ? other.Root : fixture.Root, "nested", "自定 backup");
        SetBoundaryBackupDirectory(fixture, configured);
        var origin = await fixture.CreateLinkAsync("Startup"); var bytes = await File.ReadAllBytesAsync(origin);
        using var manager = fixture.Manager(); var disabled = await manager.DisableAsync("Startup", "owned", origin);
        var expected = Path.Combine(configured, "startup", disabled.BackupId + ".lnk");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(expected));
        await manager.RestoreAsync(disabled.BackupId);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(origin)); Assert.False(File.Exists(expected)); fixture.AssertNoTransactionOrTemps();
    }

    [Fact]
    public async Task ChangingArchiveConfigurationRequiresExplicitlyRestoringTheOriginalContext()
    {
        using var fixture = new Fixture(); var origin = await fixture.CreateLinkAsync("Startup");
        using var manager = fixture.Manager(); var disabled = await manager.DisableAsync("Startup", "owned", origin);
        var original = fixture.BackupLink(disabled.BackupId); var bytes = await File.ReadAllBytesAsync(original);
        SetBoundaryBackupDirectory(fixture, Path.Combine(fixture.Root, "different-archive"));
        fixture.Operations.Calls.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreAsync(disabled.BackupId));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(original)); Assert.Empty(fixture.Operations.Calls); fixture.AssertNoTransactionOrTemps();
        SetBoundaryBackupDirectory(fixture, Path.Combine(fixture.Root, "backups"));
        await manager.RestoreAsync(disabled.BackupId); Assert.Equal(bytes, await File.ReadAllBytesAsync(origin)); fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task ShortcutDirectoryParentsRemainPinnedUntilMutationReturns(bool restore, bool archive)
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(Path.Combine(fixture.Root, "backups", "startup"));
        using var manager = fixture.Manager(); var state = await PrepareStageAsync(fixture, manager, true, restore);
        var directory = archive ? Path.Combine(fixture.Root, "backups", "startup") : fixture.Native.GetShortcutDirectory("Startup");
        var renamed = directory + "-renamed";
        Assert.StartsWith(fixture.Root + Path.DirectorySeparatorChar, directory, StringComparison.Ordinal);
        fixture.Operations.Before = operation =>
        {
            if (operation != StartupMutationOperation.NativeMutation) { return; }
            var error = Record.Exception(() => Directory.Move(directory, renamed));
            if (error is null) { Directory.Move(renamed, directory); Assert.Fail("Directory parent could be renamed during the native mutation."); }
            Assert.True(error is IOException or UnauthorizedAccessException, error.ToString());
            Assert.True(Directory.Exists(directory)); Assert.False(Directory.Exists(renamed));
        };
        await RunStageAsync(manager, state, restore);
        Directory.Move(directory, renamed); Directory.Move(renamed, directory); // all operation-held directory handles were released
        fixture.AssertNoTransactionOrTemps();
    }

    [Fact]
    public async Task ARemovedStartupDirectoryIsCreatedOneLevelAtATimeUnderThePinnedAncestor()
    {
        using var fixture = new Fixture(); var origin = await fixture.CreateLinkAsync("Startup"); var bytes = await File.ReadAllBytesAsync(origin);
        using var manager = fixture.Manager(); var disabled = await manager.DisableAsync("Startup", "owned", origin);
        var directory = fixture.Native.GetShortcutDirectory("Startup");
        Assert.StartsWith(fixture.Root + Path.DirectorySeparatorChar, directory, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(directory)); Directory.Delete(directory);
        await manager.RestoreAsync(disabled.BackupId);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(origin)); fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ArchivesInsideEitherStartupFolderAreRefusedBeforeAnyNativeWrite(bool common)
    {
        using var fixture = new Fixture(); var origin = await fixture.CreateLinkAsync("Startup"); var bytes = await File.ReadAllBytesAsync(origin);
        SetBoundaryBackupDirectory(fixture, fixture.Native.GetShortcutDirectory(common ? "Common Startup" : "Startup"));
        using var manager = fixture.Manager();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DisableAsync("Startup", "owned", origin));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(origin)); Assert.Empty(fixture.Operations.Calls); fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DuplicateMatchingBackupIdsNeverChooseTheFirstEntry(bool differentCase)
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw);
        using var manager = fixture.Manager(); var disabled = await manager.DisableAsync("HKCU", "owned", null);
        var rows = JsonNode.Parse(await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath))!.AsArray();
        var duplicate = rows[0]!.DeepClone().AsObject(); if (differentCase) { duplicate["id"] = disabled.BackupId.ToUpperInvariant(); }
        rows.Add(duplicate); await File.WriteAllTextAsync(fixture.Paths.StartupBackupPath, rows.ToJsonString());
        fixture.Operations.Calls.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreAsync(disabled.BackupId));
        Assert.Null(fixture.Native.Get("HKCU", "owned")); Assert.Empty(fixture.Operations.Calls); fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ShortcutPreparationFailureDoesNotLockTheRootAsAnUnknownNativeMutation(bool restore)
    {
        using var fixture = new Fixture(); using var manager = fixture.Manager();
        var state = await PrepareStageAsync(fixture, manager, true, restore);
        fixture.Operations.Before = operation =>
        { if (operation == StartupMutationOperation.ShortcutPreparation) { throw new UnauthorizedAccessException("owned directory preparation refused"); } };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RunStageAsync(manager, state, restore));
        await AssertStageNativeAsync(fixture, state, !restore);
        Assert.DoesNotContain(StartupMutationOperation.NativeMutation, fixture.Operations.Calls); fixture.AssertNoTransactionOrTemps();
        fixture.Operations.Before = null;
        Assert.True((await RunStageAsync(manager, state, restore)).Succeeded); fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationAfterDirectoryPreparationButBeforeMoveLeavesNoUnknownIntent(bool restore)
    {
        using var fixture = new Fixture(); using var manager = fixture.Manager();
        var state = await PrepareStageAsync(fixture, manager, true, restore); using var cancel = new CancellationTokenSource();
        fixture.Operations.After = operation => { if (operation == StartupMutationOperation.ShortcutPreparation) { cancel.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunStageAsync(manager, state, restore, cancel.Token));
        await AssertStageNativeAsync(fixture, state, !restore);
        Assert.DoesNotContain(StartupMutationOperation.NativeMutation, fixture.Operations.Calls); fixture.AssertNoTransactionOrTemps();
    }

    private static void RemoveBoundaryReparse(string path, string root, bool directory)
    {
        Assert.StartsWith(root + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.Ordinal);
        Assert.True((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
        if (directory) { Directory.Delete(path); } else { File.Delete(path); }
    }

    [Theory]
    [InlineData("archive-directory")] [InlineData("startup-directory")] [InlineData("backup-file")]
    public async Task ExistingReparseParentsOrBackupLeavesCannotRedirectRestore(string mode)
    {
        using var fixture = new Fixture(); using var other = new Fixture();
        var origin = await fixture.CreateLinkAsync("Startup"); using var manager = fixture.Manager();
        var disabled = await manager.DisableAsync("Startup", "owned", origin);
        var backup = fixture.BackupLink(disabled.BackupId); var bytes = await File.ReadAllBytesAsync(backup);
        var directory = mode != "backup-file";
        var redirected = mode switch
        {
            "archive-directory" => Path.Combine(fixture.Root, "backups"),
            "startup-directory" => fixture.Native.GetShortcutDirectory("Startup"),
            _ => backup,
        };
        var saved = Path.Combine(other.Root, "saved");
        Assert.StartsWith(fixture.Root + Path.DirectorySeparatorChar, redirected, StringComparison.Ordinal);
        if (directory) { Directory.Move(redirected, saved); Directory.CreateSymbolicLink(redirected, saved); }
        else { File.Move(redirected, saved); File.CreateSymbolicLink(redirected, saved); }
        try
        {
            fixture.Operations.Calls.Clear(); var ledger = await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath);
            var error = await Record.ExceptionAsync(() => manager.RestoreAsync(disabled.BackupId));
            Assert.True(error is IOException or InvalidOperationException, error?.ToString());
            Assert.False(File.Exists(origin)); Assert.Empty(fixture.Operations.Calls);
            Assert.Equal(ledger, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath)); fixture.AssertNoTransactionOrTemps();
        }
        finally
        {
            RemoveBoundaryReparse(redirected, fixture.Root, directory);
            if (directory) { Directory.Move(saved, redirected); } else { File.Move(saved, redirected); }
        }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(backup));
    }

    [Fact]
    public async Task ReparseInsertedAtAMissingArchiveParentIsRejectedBeforeDeeperCreation()
    {
        using var fixture = new Fixture(); using var other = new Fixture();
        var origin = await fixture.CreateLinkAsync("Startup"); var bytes = await File.ReadAllBytesAsync(origin);
        var redirected = Path.Combine(fixture.Root, "backups");
        fixture.Operations.Before = operation =>
        { if (operation == StartupMutationOperation.ShortcutPreparation) { Directory.CreateSymbolicLink(redirected, other.Root); } };
        using var manager = fixture.Manager();
        try
        {
            var error = await Record.ExceptionAsync(() => manager.DisableAsync("Startup", "owned", origin));
            Assert.True(error is IOException or InvalidOperationException, error?.ToString());
            Assert.IsNotType<StartupMutationRecoveryRequiredException>(error);
            Assert.False(Directory.Exists(Path.Combine(other.Root, "startup"))); Assert.Equal(bytes, await File.ReadAllBytesAsync(origin));
            Assert.DoesNotContain(StartupMutationOperation.NativeMutation, fixture.Operations.Calls);
            fixture.AssertNoTransactionOrTemps();
        }
        finally { RemoveBoundaryReparse(redirected, fixture.Root, directory: true); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReparseLeafInsertedAfterAdmissionIsRefusedDuringMovePreparation(bool restore)
    {
        using var fixture = new Fixture(); using var other = new Fixture(); using var manager = fixture.Manager();
        var state = await PrepareStageAsync(fixture, manager, true, restore);
        var redirected = restore ? fixture.BackupLink(state.BackupId!) : state.Origin!;
        var saved = Path.Combine(other.Root, "saved-link.lnk"); var bytes = await File.ReadAllBytesAsync(redirected);
        fixture.Operations.Before = operation =>
        {
            if (operation != StartupMutationOperation.ShortcutPreparation) { return; }
            Assert.StartsWith(fixture.Root + Path.DirectorySeparatorChar, redirected, StringComparison.Ordinal);
            File.Move(redirected, saved); File.CreateSymbolicLink(redirected, saved);
        };
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunStageAsync(manager, state, restore));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(saved));
            Assert.True((File.GetAttributes(redirected) & FileAttributes.ReparsePoint) != 0);
            Assert.DoesNotContain(StartupMutationOperation.NativeMutation, fixture.Operations.Calls);
            fixture.AssertNoTransactionOrTemps();
        }
        finally { RemoveBoundaryReparse(redirected, fixture.Root, directory: false); File.Move(saved, redirected); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OutOfArchiveBackupPathCannotMoveAnUnrelatedOwnedFileIntoStartup(bool otherRoot)
    {
        using var fixture = new Fixture(); using var other = new Fixture();
        var origin = await fixture.CreateLinkAsync("Startup");
        using var manager = fixture.Manager();
        var disabled = await manager.DisableAsync("Startup", "owned", origin);
        var originalBackup = fixture.BackupLink(disabled.BackupId);
        var originalBytes = await File.ReadAllBytesAsync(originalBackup);
        var foreign = Path.Combine(otherRoot ? other.Root : fixture.Root,
            otherRoot ? disabled.BackupId + ".lnk" : "unrelated-private-data.bin");
        await File.WriteAllBytesAsync(foreign, [1, 2, 5, 8, 13]);
        fixture.RewriteBackup(row => row["backup_path"] = foreign);
        fixture.Operations.Calls.Clear();
        var ledger = await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath);
        var error = await Record.ExceptionAsync(() => manager.RestoreAsync(disabled.BackupId));
        Assert.True(File.Exists(foreign), "Restore moved an unrelated file selected solely by editable backup_path.");
        Assert.Equal(new byte[] { 1, 2, 5, 8, 13 }, await File.ReadAllBytesAsync(foreign));
        Assert.False(File.Exists(origin)); Assert.Equal(originalBytes, await File.ReadAllBytesAsync(originalBackup));
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(ledger, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath));
        Assert.Empty(fixture.Operations.Calls); fixture.AssertNoTransactionOrTemps();
    }
}
