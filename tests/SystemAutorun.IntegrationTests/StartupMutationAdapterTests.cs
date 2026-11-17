using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("HKCU")] [InlineData("HKLM")] [InlineData("HKLM-WOW64")]
    public async Task RegistryDisableRestoreUsesOnlyOwnedKeysAndPreservesExactString(string source)
    {
        using var fixture = new Fixture();
        var raw = $"\"{Path.Combine(fixture.Root, "never-run.exe")}\" --fixture \"two words\"";
        fixture.Native.Put(source, "owned", raw);
        using var manager = fixture.Manager();
        var entry = Assert.Single((await manager.GetSnapshotAsync()).Entries);
        Assert.Equal(source, entry.Source); Assert.Equal(raw, entry.RawCommand);
        var disabled = await manager.DisableAsync(source, entry.Name, entry.Origin);
        Assert.True(disabled.Succeeded); Assert.Null(fixture.Native.Get(source, entry.Name));
        fixture.AssertNoTransactionOrTemps();
        var backup = Assert.Single((await manager.GetSnapshotAsync()).Entries);
        Assert.True(backup.IsDisabled); Assert.Equal(disabled.BackupId, backup.BackupId);
        var restored = await manager.RestoreAsync(disabled.BackupId);
        Assert.True(restored.Succeeded); Assert.Equal(raw, fixture.Native.Get(source, entry.Name));
        Assert.False(Assert.Single((await manager.GetSnapshotAsync()).Entries).IsDisabled);
        fixture.AssertNoTransactionOrTemps();
        Assert.All(fixture.Native.AccessedKeys, path => Assert.StartsWith(fixture.Native.RegistryRoot + "\\", path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Startup")] [InlineData("Common Startup")]
    public async Task ShortcutDisableRestoreMovesTheExactOwnedLinkBytes(string source)
    {
        using var fixture = new Fixture();
        var origin = await fixture.CreateLinkAsync(source);
        var bytes = await File.ReadAllBytesAsync(origin);
        using var manager = fixture.Manager();
        var entry = Assert.Single((await manager.GetSnapshotAsync()).Entries);
        var disabled = await manager.DisableAsync(source, entry.Name, entry.Origin);
        Assert.False(File.Exists(origin));
        var backupPath = fixture.BackupLink(disabled.BackupId);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(backupPath));
        Assert.True(Assert.Single((await manager.GetSnapshotAsync()).Entries).IsDisabled);
        await manager.RestoreAsync(disabled.BackupId);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(origin)); Assert.False(File.Exists(backupPath));
        fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData("HKLM")] [InlineData("HKLM-WOW64")] [InlineData("Common Startup")]
    public async Task NonAdministratorCannotOpenAnyWritableSourceOrCreateATransaction(string source)
    {
        using var fixture = new Fixture(administrator: false);
        if (source == "Common Startup") { await fixture.CreateLinkAsync(source); }
        else { fixture.Native.Put(source, "owned", fixture.Raw); }
        var before = fixture.Native.WritableOpens;
        using var manager = fixture.Manager();
        var entry = Assert.Single((await manager.GetSnapshotAsync()).Entries);
        Assert.False(entry.CanModify); Assert.True(entry.RequiresAdministrator);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.DisableAsync(source, entry.Name, entry.Origin));
        Assert.Equal(before, fixture.Native.WritableOpens);
        Assert.False(File.Exists(fixture.Paths.StartupBackupPath)); fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData("missing")] [InlineData("wrong-origin")] [InlineData("unresolved")]
    public async Task RejectedSelectionOrPendingTransactionNeverMutatesNativeState(string mode)
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw);
        using var manager = fixture.Manager();
        var entry = Assert.Single((await manager.GetSnapshotAsync()).Entries);
        var before = fixture.Native.WritableOpens;
        if (mode == "unresolved") { await File.WriteAllTextAsync(fixture.Paths.StartupBackupPath + ".transaction", "owned pending intent"); }
        if (mode == "unresolved")
        { await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DisableAsync("HKCU", entry.Name, entry.Origin)); }
        else
        { await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.DisableAsync("HKCU", mode == "missing" ? "absent" : entry.Name, mode == "wrong-origin" ? "other origin" : entry.Origin)); }
        Assert.Equal(before, fixture.Native.WritableOpens); Assert.Equal(fixture.Raw, fixture.Native.Get("HKCU", "owned"));
        Assert.False(File.Exists(fixture.Paths.StartupBackupPath));
        if (mode == "unresolved") { Assert.Equal("owned pending intent", await File.ReadAllTextAsync(fixture.Paths.StartupBackupPath + ".transaction")); }
        else { fixture.AssertNoTransactionOrTemps(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RestoreCollisionPreservesTheOtherEntryAndOriginalBackup(bool shortcut)
    {
        using var fixture = new Fixture();
        var source = shortcut ? "Startup" : "HKCU";
        var origin = shortcut ? await fixture.CreateLinkAsync(source) : null;
        if (!shortcut) { fixture.Native.Put(source, "owned", fixture.Raw); }
        using var manager = fixture.Manager();
        var entry = Assert.Single((await manager.GetSnapshotAsync()).Entries);
        var disabled = await manager.DisableAsync(source, entry.Name, entry.Origin);
        var ledger = await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath);
        if (shortcut) { await File.WriteAllBytesAsync(origin!, [1, 3, 5]); }
        else { fixture.Native.Put(source, entry.Name, "a different native command"); }
        await Assert.ThrowsAsync<IOException>(() => manager.RestoreAsync(disabled.BackupId));
        if (shortcut) { Assert.Equal(new byte[] { 1, 3, 5 }, await File.ReadAllBytesAsync(origin!)); }
        else { Assert.Equal("a different native command", fixture.Native.Get(source, entry.Name)); }
        Assert.Equal(ledger, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath));
        fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedDisableLedgerPublicationRetainsNativeMutationAndIntent(bool shortcut)
    {
        using var fixture = new Fixture();
        var source = shortcut ? "Startup" : "HKCU";
        var origin = shortcut ? await fixture.CreateLinkAsync(source) : null;
        var bytes = shortcut ? await File.ReadAllBytesAsync(origin!) : null;
        if (!shortcut) { fixture.Native.Put(source, "owned", fixture.Raw); }
        // An already occupied input is now rejected before effects. Plant the obstruction
        // at the actual publication call to retain coverage of UNKNOWN publication outcome.
        fixture.Operations.Before = operation =>
        {
            if (operation == StartupMutationOperation.LedgerPublication) { Directory.CreateDirectory(fixture.Paths.StartupBackupPath); }
        };
        using var manager = fixture.Manager();
        var entry = Assert.Single((await manager.GetSnapshotAsync()).Entries);
        var error = await Assert.ThrowsAsync<StartupMutationRecoveryRequiredException>(() => manager.DisableAsync(source, entry.Name, entry.Origin));
        Assert.Equal("ledger_publication", error.Stage); Assert.False(error.CommitConfirmed);
        Assert.IsType<UnauthorizedAccessException>(error.InnerException);
        if (shortcut)
        {
            Assert.False(File.Exists(origin));
            using var intent = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath + ".transaction"));
            var backup = intent.RootElement.GetProperty("record").GetProperty("backup_path").GetString()!;
            Assert.StartsWith(fixture.Root + Path.DirectorySeparatorChar, backup, StringComparison.Ordinal);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(backup));
        }
        else { Assert.Null(fixture.Native.Get(source, entry.Name)); }
        Assert.True(File.Exists(fixture.Paths.StartupBackupPath + ".transaction"));
        Assert.DoesNotContain(StartupMutationOperation.NativeRollback, fixture.Operations.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DisableAsync(source, entry.Name, entry.Origin));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedRestoreLedgerPublicationRetainsNativeMutationAndIntent(bool shortcut)
    {
        using var fixture = new Fixture();
        var source = shortcut ? "Startup" : "HKCU";
        var origin = shortcut ? await fixture.CreateLinkAsync(source) : null;
        var bytes = shortcut ? await File.ReadAllBytesAsync(origin!) : null;
        if (!shortcut) { fixture.Native.Put(source, "owned", fixture.Raw); }
        using var manager = fixture.Manager();
        var entry = Assert.Single((await manager.GetSnapshotAsync()).Entries);
        var disabled = await manager.DisableAsync(source, entry.Name, entry.Origin);
        var ledger = await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath);
        FileStream? blocker = null;
        void BlockCommit() { blocker ??= new FileStream(fixture.Paths.StartupBackupPath, FileMode.Open, FileAccess.Read, FileShare.None); }
        if (shortcut) { fixture.Native.BeforeDirectory = BlockCommit; }
        else { fixture.Native.BeforeWritableOpen = BlockCommit; }
        try
        {
            var error = await Assert.ThrowsAsync<StartupMutationRecoveryRequiredException>(() => manager.RestoreAsync(disabled.BackupId));
            Assert.Equal("ledger_publication", error.Stage); Assert.False(error.CommitConfirmed);
            Assert.IsType<UnauthorizedAccessException>(error.InnerException);
        }
        finally { fixture.Native.BeforeDirectory = null; fixture.Native.BeforeWritableOpen = null; blocker?.Dispose(); }
        if (shortcut)
        { Assert.Equal(bytes, await File.ReadAllBytesAsync(origin!)); Assert.False(File.Exists(fixture.BackupLink(disabled.BackupId))); }
        else { Assert.Equal(fixture.Raw, fixture.Native.Get(source, entry.Name)); }
        Assert.Equal(ledger, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath));
        Assert.True(File.Exists(fixture.Paths.StartupBackupPath + ".transaction"));
        Assert.DoesNotContain(StartupMutationOperation.NativeRollback, fixture.Operations.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreAsync(disabled.BackupId));
    }

    [Fact]
    public async Task FailedRollbackRetainsIntentAndRefusesTheNextMutation()
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw);
        using var manager = fixture.Manager();
        var entry = Assert.Single((await manager.GetSnapshotAsync()).Entries);
        using var cancel = new CancellationTokenSource();
        fixture.Operations.After = operation => { if (operation == StartupMutationOperation.NativeMutation) { cancel.Cancel(); } };
        var opens = 0;
        fixture.Native.BeforeWritableOpen = () => { if (++opens == 2) { throw new IOException("Owned rollback failure"); } };
        var error = await Assert.ThrowsAsync<StartupMutationRecoveryRequiredException>(() => manager.DisableAsync("HKCU", entry.Name, entry.Origin, cancel.Token));
        Assert.Equal("native_rollback", error.Stage);
        var causes = Assert.IsType<AggregateException>(error.InnerException);
        Assert.IsAssignableFrom<OperationCanceledException>(causes.InnerExceptions[0]);
        Assert.Equal("Owned rollback failure", causes.InnerExceptions[1].Message);
        Assert.Null(fixture.Native.Get("HKCU", entry.Name)); Assert.True(File.Exists(fixture.Paths.StartupBackupPath + ".transaction"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DisableAsync("HKCU", entry.Name, entry.Origin));
        Assert.Equal(2, opens);
    }

    [Fact]
    public async Task CancellationWhileAwaitingSameRootLeaseMakesNoNativeCall()
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw);
        using var held = await StartupMutationLease.AcquireAsync(fixture.Paths);
        using var manager = fixture.Manager(); var before = fixture.Native.WritableOpens;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.DisableAsync("HKCU", "owned", null, cancel.Token));
        Assert.Equal(before, fixture.Native.WritableOpens); Assert.Equal(fixture.Raw, fixture.Native.Get("HKCU", "owned"));
        Assert.False(File.Exists(fixture.Paths.StartupBackupPath)); fixture.AssertNoTransactionOrTemps();
    }

    [Fact]
    public async Task LeaseSurvivesAcquiringThreadExitAndCanBeDisposedOnAnotherThread()
    {
        using var fixture = new Fixture();
        var ready = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { ready.SetResult(StartupMutationLease.AcquireAsync(fixture.Paths).GetAwaiter().GetResult()); }
            catch (Exception error) { ready.SetException(error); }
        }) { IsBackground = true };
        thread.Start(); using var held = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<TimeoutException>(() => StartupMutationLease.AcquireAsync(fixture.Paths, timeout: TimeSpan.FromMilliseconds(80)));
        await Task.Run(held.Dispose);
        using var again = await StartupMutationLease.AcquireAsync(fixture.Paths, timeout: TimeSpan.FromSeconds(1));
        Assert.Equal(0, new FileInfo(Path.Combine(fixture.Root, StartupMutationLease.FileName)).Length);
    }

    [Fact]
    public async Task StartupLeaseDoesNotBlockConfigurationOrAnotherRootsNativeWriter()
    {
        using var first = new Fixture(); using var second = new Fixture();
        first.Native.Put("HKCU", "owned", first.Raw);
        using var held = await StartupMutationLease.AcquireAsync(first.Paths);
        using var configuration = await RootWriteLease.AcquireAsync(first.Paths, timeout: TimeSpan.FromSeconds(1));
        // Intentional negative guarantee: two roots can address the SAME external source.
        using var other = new StartupManager(second.Paths, first.Native);
        var result = await other.DisableAsync("HKCU", "owned", null).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Succeeded); Assert.Null(first.Native.Get("HKCU", "owned"));
        Assert.False(File.Exists(first.Paths.StartupBackupPath)); Assert.True(File.Exists(second.Paths.StartupBackupPath));
    }

    [Theory]
    [InlineData("HKCU", RegistryHive.CurrentUser, RegistryView.Default)]
    [InlineData("HKLM", RegistryHive.LocalMachine, RegistryView.Registry64)]
    [InlineData("HKLM-WOW64", RegistryHive.LocalMachine, RegistryView.Registry32)]
    public void ProductionRegistryMappingRemainsFixed(string source, RegistryHive hive, RegistryView view)
    { Assert.Equal((hive, view), WindowsStartupNativeAccess.RegistryLocation(source)); }

    [Theory]
    [InlineData("arbitrary")] [InlineData("Startup")]
    public void ProductionRegistryMappingRejectsUnsupportedSourcesBeforeOpeningAKey(string source)
    { Assert.Throws<ArgumentOutOfRangeException>(() => WindowsStartupNativeAccess.Instance.OpenRunKey(source, true, true)); }

    [Fact]
    public async Task PhysicalDirectoryAliasSharesTheStartupLeaseAndTheLockFileIsNotDeleted()
    {
        using var fixture = new Fixture(); var alias = fixture.Root + "-alias";
        Directory.CreateSymbolicLink(alias, fixture.Root);
        try
        {
            using var held = await StartupMutationLease.AcquireAsync(fixture.Paths);
            await Assert.ThrowsAsync<TimeoutException>(() => StartupMutationLease.AcquireAsync(new(alias), timeout: TimeSpan.FromMilliseconds(80)));
            var lockPath = Path.Combine(fixture.Root, StartupMutationLease.FileName);
            Assert.Throws<IOException>(() => File.Delete(lockPath));
            held.Dispose();
            using var again = await StartupMutationLease.AcquireAsync(new(alias), timeout: TimeSpan.FromSeconds(1));
            Assert.True(File.Exists(lockPath));
        }
        finally
        {
            Assert.Equal(fixture.Root + "-alias", Path.GetFullPath(alias));
            Assert.True((File.GetAttributes(alias) & FileAttributes.ReparsePoint) != 0);
            Directory.Delete(alias);
        }
    }

    [Fact]
    public async Task CancellationBeforeNativeInvocationDoesNotWriteOrCreateASuccessLedger()
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw);
        using var manager = fixture.Manager();
        using var cancel = new CancellationTokenSource();
        fixture.Native.BeforeWritableOpen = cancel.Cancel;
        var before = fixture.Native.WritableOpens;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.DisableAsync("HKCU", "owned", null, cancel.Token));
        Assert.Equal(before + 1, fixture.Native.WritableOpens); // non-creating open; no native write or rollback
        Assert.DoesNotContain(StartupMutationOperation.NativeMutation, fixture.Operations.Calls);
        Assert.Equal(fixture.Raw, fixture.Native.Get("HKCU", "owned"));
        Assert.False(File.Exists(fixture.Paths.StartupBackupPath)); fixture.AssertNoTransactionOrTemps();
        fixture.Native.BeforeWritableOpen = null;
        Assert.True((await manager.DisableAsync("HKCU", "owned", null)).Succeeded);
    }

    [Fact]
    public async Task ReadOnlyInventorySharesTheSameInjectedFixedRegistrySources()
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw);
        var configuration = await new SystemAutorun.Core.Configuration.ConfigurationLoader().LoadAsync(fixture.Root);
        using var manager = fixture.Manager();
        var snapshot = manager.CaptureInventory(configuration, new("user", "registry", "active"), CancellationToken.None);
        Assert.Empty(snapshot.SourceIssues); Assert.Equal("owned", Assert.Single(snapshot.Entries).Name);
        Assert.All(fixture.Native.AccessedKeys, path => Assert.StartsWith(fixture.Native.RegistryRoot + "\\", path, StringComparison.Ordinal));
        Assert.False(File.Exists(fixture.Paths.StartupBackupPath));
    }

    [Theory]
    [InlineData("Startup", Environment.SpecialFolder.Startup)]
    [InlineData("Common Startup", Environment.SpecialFolder.CommonStartup)]
    public void ProductionShortcutMappingRemainsTheStandardWindowsFolder(string source, Environment.SpecialFolder folder)
    { Assert.Equal(Environment.GetFolderPath(folder), WindowsStartupNativeAccess.Instance.GetShortcutDirectory(source)); }

    [Fact]
    public void LocationInjectionIsNotPartOfThePublicConstructorContract()
    {
        var constructor = Assert.Single(typeof(StartupManager).GetConstructors());
        Assert.Equal(new[] { typeof(ProjectPaths), typeof(SystemAutorun.Core.Configuration.ConfigurationLoader) },
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.False(typeof(IStartupNativeAccess).IsPublic);
    }

    [Theory]
    [InlineData("HKCU")] [InlineData("HKLM")] [InlineData("HKLM-WOW64")]
    public async Task ExpandStringRoundTripPreservesUnexpandedRawAndOriginalKind(string source)
    {
        using var fixture = new Fixture();
        const string raw = "\"%SystemRoot%\\fixture-never-run.exe\" --literal \"%TEMP%\" --雪";
        fixture.Native.Put(source, "owned", raw, RegistryValueKind.ExpandString);
        using var manager = fixture.Manager();
        var disabled = await manager.DisableAsync(source, "owned", null);
        using var backup = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath));
        await manager.RestoreAsync(disabled.BackupId);
        Assert.Equal(raw, fixture.Native.Get(source, "owned"));
        Assert.Equal(RegistryValueKind.ExpandString, fixture.Native.Kind(source, "owned"));
        Assert.Equal(2, Assert.Single(backup.RootElement.EnumerateArray()).GetProperty("registry_value_kind").GetInt32());
        fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData(RegistryValueKind.String, "")]
    [InlineData(RegistryValueKind.ExpandString, "")]
    [InlineData(RegistryValueKind.String, " \t\r\n")]
    [InlineData(RegistryValueKind.ExpandString, " \t\r\n")]
    [InlineData(RegistryValueKind.String, "\"%TEMP%\\never-run.exe\" --雪")]
    [InlineData(RegistryValueKind.ExpandString, "\"%TEMP%\\never-run.exe\" --雪")]
    public async Task TypedStringBackupPreservesEmptyWhitespaceAndLiteralPercentData(RegistryValueKind kind, string raw)
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", raw, kind);
        using var manager = fixture.Manager();
        var disabled = await manager.DisableAsync("HKCU", "owned", null);
        using var ledger = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath));
        Assert.Equal((int)kind, Assert.Single(ledger.RootElement.EnumerateArray()).GetProperty("registry_value_kind").GetInt32());
        var configuration = await new SystemAutorun.Core.Configuration.ConfigurationLoader().LoadAsync(fixture.Root);
        var inventory = manager.CaptureInventory(configuration, new("user", "registry", "disabled"), CancellationToken.None);
        Assert.Empty(inventory.SourceIssues); Assert.True(Assert.Single(inventory.Entries).IsDisabled);
        await manager.RestoreAsync(disabled.BackupId);
        Assert.Equal(raw, fixture.Native.Get("HKCU", "owned")); Assert.Equal(kind, fixture.Native.Kind("HKCU", "owned"));
        fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData(RegistryValueKind.Binary)] [InlineData(RegistryValueKind.None)]
    [InlineData(RegistryValueKind.MultiString)] [InlineData(RegistryValueKind.DWord)] [InlineData(RegistryValueKind.QWord)]
    public async Task NonTextRegistryValuesAreRefusedBeforeWritableOpenOrIntent(RegistryValueKind kind)
    {
        using var fixture = new Fixture();
        object payload = kind switch
        {
            RegistryValueKind.MultiString => new[] { "first", "second" },
            RegistryValueKind.DWord => 42, RegistryValueKind.QWord => 42L,
            _ => new byte[] { 1, 3, 7 },
        };
        fixture.Native.PutValue("HKCU", "owned", payload, kind);
        var before = fixture.Native.WritableOpens;
        using var manager = fixture.Manager();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DisableAsync("HKCU", "owned", null));
        Assert.Equal(before, fixture.Native.WritableOpens); Assert.Equal(kind, fixture.Native.Kind("HKCU", "owned"));
        Assert.Equal(JsonSerializer.Serialize(payload), JsonSerializer.Serialize(fixture.Native.Value("HKCU", "owned")));
        Assert.False(File.Exists(fixture.Paths.StartupBackupPath)); fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData("raw")] [InlineData("kind")] [InlineData("binary")] [InlineData("missing")]
    public async Task ChangedRegistryValueAtDeleteBoundaryIsNotDeletedOrBackedUp(string drift)
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw, RegistryValueKind.ExpandString);
        using var manager = fixture.Manager();
        fixture.Native.BeforeWritableOpen = () =>
        {
            fixture.Native.BeforeWritableOpen = null;
            Assert.True(File.Exists(fixture.Paths.StartupBackupPath + ".transaction"));
            switch (drift)
            {
                case "raw": fixture.Native.Put("HKCU", "owned", fixture.Raw + " --other", RegistryValueKind.ExpandString); break;
                case "kind": fixture.Native.Put("HKCU", "owned", fixture.Raw, RegistryValueKind.String); break;
                case "binary": fixture.Native.PutValue("HKCU", "owned", new byte[] { 3, 5 }, RegistryValueKind.Binary); break;
                case "missing": fixture.Native.Delete("HKCU", "owned"); break;
            }
        };
        var error = await Record.ExceptionAsync(() => manager.DisableAsync("HKCU", "owned", null));
        if (drift == "missing") { Assert.IsType<KeyNotFoundException>(error); Assert.Null(fixture.Native.Value("HKCU", "owned")); }
        else
        {
            Assert.IsType<InvalidOperationException>(error);
            Assert.Equal(drift == "kind" ? RegistryValueKind.String : drift == "binary" ? RegistryValueKind.Binary : RegistryValueKind.ExpandString,
                fixture.Native.Kind("HKCU", "owned"));
            if (drift == "binary") { Assert.Equal(new byte[] { 3, 5 }, Assert.IsType<byte[]>(fixture.Native.Value("HKCU", "owned"))); }
            else { Assert.Equal(fixture.Raw + (drift == "raw" ? " --other" : ""), fixture.Native.Get("HKCU", "owned")); }
        }
        Assert.False(File.Exists(fixture.Paths.StartupBackupPath)); fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData("raw")] [InlineData("binary")] [InlineData("missing")]
    public async Task CaptureRefusesChangedSelectionBeforeCreatingAnIntent(string drift)
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw);
        var reads = 0;
        fixture.Native.BeforeReadOnlyOpen = source =>
        {
            if (source != "HKCU" || ++reads != 2) { return; }
            if (drift == "raw") { fixture.Native.Put(source, "owned", fixture.Raw + " --other"); }
            else if (drift == "binary") { fixture.Native.PutValue(source, "owned", new byte[] { 7 }, RegistryValueKind.Binary); }
            else { fixture.Native.Delete(source, "owned"); }
        };
        using var manager = fixture.Manager();
        var error = await Record.ExceptionAsync(() => manager.DisableAsync("HKCU", "owned", null));
        Assert.True(error is InvalidOperationException or KeyNotFoundException);
        Assert.False(File.Exists(fixture.Paths.StartupBackupPath)); fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(3)] [InlineData(4)] [InlineData(7)] [InlineData(11)] [InlineData(999)]
    public async Task RestoreRefusesUnsupportedOrUnknownBackupKindBeforeIntent(int kind)
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw);
        using var manager = fixture.Manager(); var disabled = await manager.DisableAsync("HKCU", "owned", null);
        fixture.RewriteBackup(row => row["registry_value_kind"] = kind);
        var ledger = await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath); var before = fixture.Native.WritableOpens;
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreAsync(disabled.BackupId));
        Assert.Equal(before, fixture.Native.WritableOpens); Assert.Null(fixture.Native.Value("HKCU", "owned"));
        Assert.Equal(ledger, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath)); fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData("\"ExpandString\"")] [InlineData("{}")] [InlineData("[]")] [InlineData("2.5")]
    public async Task RestoreRefusesMalformedKindJsonBeforeNativeWrite(string kindJson)
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw);
        using var manager = fixture.Manager(); var disabled = await manager.DisableAsync("HKCU", "owned", null);
        fixture.RewriteBackup(row => row["registry_value_kind"] = JsonNode.Parse(kindJson));
        var before = fixture.Native.WritableOpens;
        await Assert.ThrowsAsync<JsonException>(() => manager.RestoreAsync(disabled.BackupId));
        Assert.Equal(before, fixture.Native.WritableOpens); Assert.Null(fixture.Native.Value("HKCU", "owned")); fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LegacyMissingOrNullKindKeepsRegSzCompatibilityWithoutGuessingFromPercent(bool explicitNull)
    {
        using var fixture = new Fixture(); const string raw = "\"%TEMP%\\never-run.exe\"";
        fixture.Native.Put("HKCU", "owned", raw, RegistryValueKind.ExpandString);
        using var manager = fixture.Manager(); var disabled = await manager.DisableAsync("HKCU", "owned", null);
        fixture.RewriteBackup(row => { if (explicitNull) { row["registry_value_kind"] = null; } else { row.Remove("registry_value_kind"); } });
        await manager.RestoreAsync(disabled.BackupId);
        Assert.Equal(raw, fixture.Native.Get("HKCU", "owned")); Assert.Equal(RegistryValueKind.String, fixture.Native.Kind("HKCU", "owned"));
        fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task TypedBackupWithNullRawCommandIsRefused(int kind)
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw);
        using var manager = fixture.Manager(); var disabled = await manager.DisableAsync("HKCU", "owned", null);
        fixture.RewriteBackup(row => { row["registry_value_kind"] = kind; row["raw_command"] = null; });
        var before = fixture.Native.WritableOpens;
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreAsync(disabled.BackupId));
        Assert.Equal(before, fixture.Native.WritableOpens); Assert.Null(fixture.Native.Value("HKCU", "owned")); fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData(RegistryValueKind.String)] [InlineData(RegistryValueKind.ExpandString)]
    public async Task CanceledBeforeLedgerPublicationRestoresTheOriginalValueKind(RegistryValueKind kind)
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw, kind);
        using var cancel = new CancellationTokenSource();
        fixture.Operations.After = operation => { if (operation == StartupMutationOperation.NativeMutation) { cancel.Cancel(); } };
        using var manager = fixture.Manager();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.DisableAsync("HKCU", "owned", null, cancel.Token));
        Assert.Equal(fixture.Raw, fixture.Native.Get("HKCU", "owned")); Assert.Equal(kind, fixture.Native.Kind("HKCU", "owned"));
        fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData(false, true)] [InlineData(true, true)]
    [InlineData(false, false)] [InlineData(true, false)]
    public async Task RollbackDoesNotOverwriteOrDeleteAChangedForeignValue(bool restore, bool kindChanged)
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw, RegistryValueKind.ExpandString);
        using var manager = fixture.Manager();
        var backupId = restore ? (await manager.DisableAsync("HKCU", "owned", null)).BackupId : null;
        using var cancel = new CancellationTokenSource();
        fixture.Operations.After = operation => { if (operation == StartupMutationOperation.NativeMutation) { cancel.Cancel(); } };
        var foreignKind = kindChanged ? RegistryValueKind.String : RegistryValueKind.ExpandString;
        var foreignRaw = fixture.Raw + (kindChanged ? "" : " --foreign");
        var calls = 0;
        fixture.Native.BeforeWritableOpen = () =>
        {
            if (++calls == 2)
            { fixture.Native.BeforeWritableOpen = null; fixture.Native.Put("HKCU", "owned", foreignRaw, foreignKind); }
        };
        try
        {
            var error = await Assert.ThrowsAsync<StartupMutationRecoveryRequiredException>(() => restore
                ? manager.RestoreAsync(backupId!, cancel.Token) : manager.DisableAsync("HKCU", "owned", null, cancel.Token));
            Assert.Equal("native_rollback", error.Stage);
            var causes = Assert.IsType<AggregateException>(error.InnerException);
            Assert.IsAssignableFrom<OperationCanceledException>(causes.InnerExceptions[0]);
            Assert.IsType<IOException>(causes.InnerExceptions[1]);
        }
        finally { fixture.Native.BeforeWritableOpen = null; }
        Assert.Equal(2, calls); Assert.Equal(foreignRaw, fixture.Native.Get("HKCU", "owned"));
        Assert.Equal(foreignKind, fixture.Native.Kind("HKCU", "owned"));
        Assert.True(File.Exists(fixture.Paths.StartupBackupPath + ".transaction"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DisableAsync("HKCU", "owned", null));
    }

    [Theory]
    [InlineData(RegistryValueKind.String)] [InlineData(RegistryValueKind.ExpandString)]
    public async Task CanceledBeforeLedgerPublicationRemovesOnlyItsOwnRestoredTypedValue(RegistryValueKind kind)
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw, kind);
        using var manager = fixture.Manager(); var disabled = await manager.DisableAsync("HKCU", "owned", null);
        var ledger = await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath);
        using var cancel = new CancellationTokenSource();
        fixture.Operations.After = operation => { if (operation == StartupMutationOperation.NativeMutation) { cancel.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.RestoreAsync(disabled.BackupId, cancel.Token));
        Assert.Null(fixture.Native.Value("HKCU", "owned"));
        Assert.Equal(ledger, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath)); fixture.AssertNoTransactionOrTemps();
    }

    [Fact]
    public async Task CaseOnlyValueNameRecreationBetweenScanAndCaptureUsesWindowsIdentity()
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw, RegistryValueKind.ExpandString);
        var reads = 0;
        fixture.Native.BeforeReadOnlyOpen = source =>
        {
            if (source != "HKCU" || ++reads != 2) { return; }
            fixture.Native.Delete(source, "owned"); fixture.Native.Put(source, "OWNED", fixture.Raw, RegistryValueKind.ExpandString);
        };
        using var manager = fixture.Manager(); var disabled = await manager.DisableAsync("HKCU", "owned", null);
        Assert.Null(fixture.Native.Value("HKCU", "OWNED"));
        await manager.RestoreAsync(disabled.BackupId);
        Assert.Equal(fixture.Raw, fixture.Native.Get("HKCU", "owned")); Assert.Equal(RegistryValueKind.ExpandString, fixture.Native.Kind("HKCU", "owned"));
        fixture.AssertNoTransactionOrTemps();
    }

    [Fact]
    public async Task RestoreRecreatesOnlyTheFixedOwnedKeyWhenAnExternalWriterRemovedIt()
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw, RegistryValueKind.ExpandString);
        using var manager = fixture.Manager(); var disabled = await manager.DisableAsync("HKCU", "owned", null);
        fixture.Native.DeleteSource("HKCU");
        await manager.RestoreAsync(disabled.BackupId);
        Assert.Equal(fixture.Raw, fixture.Native.Get("HKCU", "owned")); Assert.Equal(RegistryValueKind.ExpandString, fixture.Native.Kind("HKCU", "owned"));
        fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CommittedLedgerIsNotRolledBackWhenIntentCleanupIsBlocked(bool restore)
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw, RegistryValueKind.ExpandString);
        using var manager = fixture.Manager(); string? id = null;
        if (restore) { id = (await manager.DisableAsync("HKCU", "owned", null)).BackupId; }
        FileStream? blocker = null;
        fixture.Native.BeforeWritableOpen = () => blocker ??= new(fixture.Paths.StartupBackupPath + ".transaction", FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var error = await Assert.ThrowsAsync<StartupMutationRecoveryRequiredException>(() => restore ? manager.RestoreAsync(id!) : manager.DisableAsync("HKCU", "owned", null));
            Assert.Equal("intent_cleanup", error.Stage); Assert.True(error.CommitConfirmed);
            Assert.Equal(restore ? "restore" : "disable", error.Operation); Assert.NotEmpty(error.BackupId);
            Assert.IsAssignableFrom<IOException>(error.InnerException);
        }
        finally { fixture.Native.BeforeWritableOpen = null; blocker?.Dispose(); }
        if (restore)
        { Assert.Equal(fixture.Raw, fixture.Native.Get("HKCU", "owned")); Assert.Equal(RegistryValueKind.ExpandString, fixture.Native.Kind("HKCU", "owned")); }
        else { Assert.Null(fixture.Native.Get("HKCU", "owned")); }
        using var ledger = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath));
        Assert.Equal(restore, ledger.RootElement[0].GetProperty("restored_at").ValueKind != JsonValueKind.Null);
        Assert.True(File.Exists(fixture.Paths.StartupBackupPath + ".transaction"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DisableAsync("HKCU", "owned", null));
    }

    private sealed class ControlledOperations : IStartupMutationOperations
    {
        internal Action<StartupMutationOperation>? Before { get; set; }
        internal Action<StartupMutationOperation>? After { get; set; }
        internal List<StartupMutationOperation> Calls { get; } = [];
        public void Execute(StartupMutationOperation operation, Action action)
        { Calls.Add(operation); Before?.Invoke(operation); action(); After?.Invoke(operation); }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "SystemAutorun-startup-mutation-" + Guid.NewGuid().ToString("N"));
        internal ProjectPaths Paths { get; }
        internal OwnedNativeAccess Native { get; }
        internal Fixture(bool administrator = true)
        {
            Directory.CreateDirectory(Root); Paths = new(Root);
            File.WriteAllText(Paths.ConfigPath, "version: 1\nsettings:\n  default_startup_delay: 0\ntasks: []\n");
            Native = new(Root, administrator);
        }
        internal string Raw => $"\"{Path.Combine(Root, "never-run.exe")}\" --owned-fixture";
        internal ControlledOperations Operations { get; } = new();
        internal StartupManager Manager() => new(Paths, Native, operations: Operations);
        internal void RewriteBackup(Action<JsonObject> edit)
        {
            var rows = JsonNode.Parse(File.ReadAllBytes(Paths.StartupBackupPath))!.AsArray();
            edit(Assert.Single(rows)!.AsObject());
            File.WriteAllText(Paths.StartupBackupPath, rows.ToJsonString());
        }
        internal string BackupLink(string id)
        {
            using var ledger = JsonDocument.Parse(File.ReadAllBytes(Paths.StartupBackupPath));
            var path = ledger.RootElement.EnumerateArray().Single(row => row.GetProperty("id").GetString() == id)
                .GetProperty("backup_path").GetString()!;
            Assert.StartsWith(Root + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
            return path;
        }
        internal async Task<string> CreateLinkAsync(string source)
        {
            var path = Path.Combine(Native.GetShortcutDirectory(source), "owned.lnk");
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    object? shell = null; object? link = null;
                    try
                    {
                        shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
                        link = StartupComDispatch.Call(shell, "CreateShortcut", path);
                        StartupComDispatch.SetText(link, "TargetPath", Path.Combine(Root, "never-run.exe"));
                        StartupComDispatch.SetText(link, "Arguments", "--owned \"two words\"");
                        StartupComDispatch.SetText(link, "WorkingDirectory", Root); StartupComDispatch.Command(link, "Save");
                    }
                    finally
                    {
                        StartupManager.KeepComObjectAlive(link);
                        StartupManager.KeepComObjectAlive(shell);
                    }
                    done.SetResult();
                }
                catch (Exception error) { done.SetException(error); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            return path;
        }
        internal void AssertNoTransactionOrTemps()
        {
            Assert.False(File.Exists(Paths.StartupBackupPath + ".transaction"));
            Assert.Empty(Directory.GetFiles(Root, ".*.tmp"));
        }
        public void Dispose()
        {
            Native.Dispose();
            var full = Path.GetFullPath(Root);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(full));
            Assert.StartsWith("SystemAutorun-startup-mutation-", Path.GetFileName(full), StringComparison.Ordinal);
            Directory.Delete(full, recursive: true);
        }
    }

    private sealed class OwnedNativeAccess : IStartupNativeAccess, IDisposable
    {
        private readonly string _root;
        private readonly string _id = Guid.NewGuid().ToString("N");
        internal string RegistryRoot => @"Software\SystemAutorun.Mutation.Tests." + _id;
        internal List<string> AccessedKeys { get; } = [];
        internal int WritableOpens { get; private set; }
        internal Action? BeforeWritableOpen { get; set; }
        internal Action<string>? BeforeReadOnlyOpen { get; set; }
        internal Action? BeforeDirectory { get; set; }
        public bool IsAdministrator { get; }
        internal OwnedNativeAccess(string root, bool administrator)
        {
            _root = root; IsAdministrator = administrator;
            foreach (var source in new[] { "HKCU", "HKLM", "HKLM-WOW64" })
            { using var key = OpenRunKey(source, true, true); }
            WritableOpens = 0; AccessedKeys.Clear();
            Directory.CreateDirectory(GetShortcutDirectory("Startup"));
            Directory.CreateDirectory(GetShortcutDirectory("Common Startup"));
        }
        public string GetShortcutDirectory(string source)
        {
            BeforeDirectory?.Invoke();
            return Path.Combine(_root, source switch
            {
                "Startup" => "user-startup", "Common Startup" => "common-startup",
                _ => throw new ArgumentOutOfRangeException(nameof(source)),
            });
        }
        private static string Child(string source) => source switch
        {
            "HKCU" => "user", "HKLM" => "machine64", "HKLM-WOW64" => "machine32",
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        internal void DeleteSource(string source)
        {
            var path = RegistryRoot + "\\" + Child(source);
            Assert.StartsWith(RegistryRoot + "\\", path, StringComparison.Ordinal);
            Registry.CurrentUser.DeleteSubKeyTree(path);
        }
        public RegistryKey? OpenRunKey(string source, bool writable, bool create)
        {
            var path = RegistryRoot + "\\" + Child(source); AccessedKeys.Add(path);
            if (writable) { WritableOpens++; BeforeWritableOpen?.Invoke(); }
            else { BeforeReadOnlyOpen?.Invoke(source); }
            return create ? Registry.CurrentUser.CreateSubKey(path, writable) : Registry.CurrentUser.OpenSubKey(path, writable);
        }
        internal void Put(string source, string name, string value, RegistryValueKind kind = RegistryValueKind.String)
            => PutValue(source, name, value, kind);
        internal void PutValue(string source, string name, object value, RegistryValueKind kind)
        { using var key = OpenRunKey(source, true, true); key!.SetValue(name, value, kind); }
        internal RegistryValueKind Kind(string source, string name)
        { using var key = OpenRunKey(source, false, false); return key!.GetValueKind(name); }
        internal object? Value(string source, string name)
        { using var key = OpenRunKey(source, false, false); return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); }
        internal string? Get(string source, string name) => Value(source, name) as string;
        internal void Delete(string source, string name)
        { using var key = OpenRunKey(source, true, false); key!.DeleteValue(name); }
        public void Dispose()
        {
            Assert.True(Guid.TryParseExact(_id, "N", out _));
            Assert.Equal(@"Software\SystemAutorun.Mutation.Tests." + _id, RegistryRoot);
            Registry.CurrentUser.DeleteSubKeyTree(RegistryRoot, throwOnMissingSubKey: false);
        }
    }
}
