using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    private sealed record StageFixture(string Source, string? Origin, byte[]? LinkBytes, string? BackupId);

    private static async Task<StageFixture> PrepareStageAsync(Fixture fixture, StartupManager manager, bool shortcut, bool restore)
    {
        var source = shortcut ? "Startup" : "HKCU";
        var origin = shortcut ? await fixture.CreateLinkAsync(source) : null;
        var bytes = shortcut ? await File.ReadAllBytesAsync(origin!) : null;
        if (!shortcut) { fixture.Native.Put(source, "owned", fixture.Raw, RegistryValueKind.ExpandString); }
        var id = restore ? (await manager.DisableAsync(source, "owned", origin)).BackupId : null;
        fixture.Operations.Calls.Clear();
        return new(source, origin, bytes, id);
    }

    private static Task<StartupMutationResult> RunStageAsync(StartupManager manager, StageFixture state, bool restore,
        CancellationToken cancellationToken = default) => restore
        ? manager.RestoreAsync(state.BackupId!, cancellationToken)
        : manager.DisableAsync(state.Source, "owned", state.Origin, cancellationToken);

    private static async Task AssertStageNativeAsync(Fixture fixture, StageFixture state, bool active)
    {
        if (state.Origin is not null)
        {
            Assert.Equal(active, File.Exists(state.Origin));
            if (active) { Assert.Equal(state.LinkBytes, await File.ReadAllBytesAsync(state.Origin)); }
        }
        else if (active)
        {
            Assert.Equal(fixture.Raw, fixture.Native.Get(state.Source, "owned"));
            Assert.Equal(RegistryValueKind.ExpandString, fixture.Native.Kind(state.Source, "owned"));
        }
        else { Assert.Null(fixture.Native.Value(state.Source, "owned")); }
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(false, false, true)]
    [InlineData(false, true, false)] [InlineData(false, true, true)]
    [InlineData(true, false, false)] [InlineData(true, false, true)]
    [InlineData(true, true, false)] [InlineData(true, true, true)]
    public async Task OpaqueNativeCallFailureNeverCompensatesOrDeletesIntent(bool restore, bool shortcut, bool after)
    {
        using var fixture = new Fixture(); using var manager = fixture.Manager();
        var state = await PrepareStageAsync(fixture, manager, shortcut, restore);
        var oldLedger = File.Exists(fixture.Paths.StartupBackupPath) ? await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath) : null;
        void Fail(StartupMutationOperation operation)
        { if (operation == StartupMutationOperation.NativeMutation) { throw new IOException("opaque owned native boundary"); } }
        if (after) { fixture.Operations.After = Fail; } else { fixture.Operations.Before = Fail; }
        var error = await Assert.ThrowsAsync<StartupMutationRecoveryRequiredException>(() => RunStageAsync(manager, state, restore));
        Assert.Equal("native_mutation", error.Stage); Assert.False(error.CommitConfirmed); Assert.NotEmpty(error.BackupId);
        Assert.Equal("opaque owned native boundary", error.InnerException!.Message);
        await AssertStageNativeAsync(fixture, state, restore ? after : !after);
        if (shortcut && after && !restore)
        {
            using var intent = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath + ".transaction"));
            var backup = intent.RootElement.GetProperty("record").GetProperty("backup_path").GetString()!;
            Assert.StartsWith(fixture.Root + Path.DirectorySeparatorChar, backup, StringComparison.Ordinal);
            Assert.Equal(state.LinkBytes, await File.ReadAllBytesAsync(backup));
        }
        if (oldLedger is null) { Assert.False(File.Exists(fixture.Paths.StartupBackupPath)); }
        else { Assert.Equal(oldLedger, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath)); }
        Assert.True(File.Exists(fixture.Paths.StartupBackupPath + ".transaction"));
        Assert.DoesNotContain(StartupMutationOperation.NativeRollback, fixture.Operations.Calls);
        using var fresh = fixture.Manager();
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunStageAsync(fresh, state, restore));
        Assert.Equal(1, fixture.Operations.Calls.Count(operation => operation == StartupMutationOperation.NativeMutation));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task OpaqueLedgerPublicationFailureRetainsIntentWithoutBlindRollback(bool restore, bool after)
    {
        using var fixture = new Fixture(); using var manager = fixture.Manager();
        var state = await PrepareStageAsync(fixture, manager, false, restore);
        var oldLedger = restore ? await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath) : null;
        void Fail(StartupMutationOperation operation)
        { if (operation == StartupMutationOperation.LedgerPublication) { throw new IOException("opaque owned ledger boundary"); } }
        if (after) { fixture.Operations.After = Fail; } else { fixture.Operations.Before = Fail; }
        var error = await Assert.ThrowsAsync<StartupMutationRecoveryRequiredException>(() => RunStageAsync(manager, state, restore));
        Assert.Equal("ledger_publication", error.Stage); Assert.False(error.CommitConfirmed);
        await AssertStageNativeAsync(fixture, state, restore);
        if (after)
        {
            using var ledger = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath));
            Assert.Equal(restore, ledger.RootElement[0].GetProperty("restored_at").ValueKind != JsonValueKind.Null);
        }
        else if (oldLedger is null) { Assert.False(File.Exists(fixture.Paths.StartupBackupPath)); }
        else { Assert.Equal(oldLedger, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath)); }
        Assert.True(File.Exists(fixture.Paths.StartupBackupPath + ".transaction"));
        Assert.DoesNotContain(StartupMutationOperation.NativeRollback, fixture.Operations.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunStageAsync(manager, state, restore));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OpaqueIntentPublicationFailureNeverInvokesNative(bool after)
    {
        using var fixture = new Fixture(); using var manager = fixture.Manager();
        var state = await PrepareStageAsync(fixture, manager, false, false);
        void Fail(StartupMutationOperation operation)
        { if (operation == StartupMutationOperation.IntentPublication) { throw new IOException("opaque owned intent boundary"); } }
        if (after) { fixture.Operations.After = Fail; } else { fixture.Operations.Before = Fail; }
        var error = await Assert.ThrowsAsync<StartupMutationRecoveryRequiredException>(() => RunStageAsync(manager, state, false));
        Assert.Equal("intent_publication", error.Stage); Assert.False(error.CommitConfirmed);
        await AssertStageNativeAsync(fixture, state, true);
        Assert.Equal(after, File.Exists(fixture.Paths.StartupBackupPath + ".transaction"));
        Assert.False(File.Exists(fixture.Paths.StartupBackupPath));
        Assert.DoesNotContain(StartupMutationOperation.NativeMutation, fixture.Operations.Calls);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task CancellationAfterReturnedNativeCallButBeforeLedgerPublicationCompensates(bool restore, bool shortcut)
    {
        using var fixture = new Fixture(); using var manager = fixture.Manager();
        var state = await PrepareStageAsync(fixture, manager, shortcut, restore);
        var oldLedger = restore ? await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath) : null;
        using var cancel = new CancellationTokenSource();
        fixture.Operations.After = operation => { if (operation == StartupMutationOperation.NativeMutation) { cancel.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunStageAsync(manager, state, restore, cancel.Token));
        await AssertStageNativeAsync(fixture, state, !restore);
        if (oldLedger is null) { Assert.False(File.Exists(fixture.Paths.StartupBackupPath)); }
        else { Assert.Equal(oldLedger, await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath)); }
        Assert.Contains(StartupMutationOperation.NativeRollback, fixture.Operations.Calls);
        Assert.DoesNotContain(StartupMutationOperation.LedgerPublication, fixture.Operations.Calls);
        if (shortcut)
        {
            var backups = Path.Combine(fixture.Root, "backups");
            var links = Directory.Exists(backups) ? Directory.GetFiles(backups, "*.lnk", SearchOption.AllDirectories) : [];
            Assert.Equal(restore ? 1 : 0, links.Length);
            if (restore) { Assert.Equal(state.LinkBytes, await File.ReadAllBytesAsync(links[0])); }
        }
        fixture.AssertNoTransactionOrTemps();
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task OpaqueIntentCleanupFailureCannotUndoAConfirmedCommit(bool restore, bool after)
    {
        using var fixture = new Fixture(); using var manager = fixture.Manager();
        var state = await PrepareStageAsync(fixture, manager, false, restore);
        void Fail(StartupMutationOperation operation)
        { if (operation == StartupMutationOperation.IntentCleanup) { throw new IOException("opaque owned cleanup boundary"); } }
        if (after) { fixture.Operations.After = Fail; } else { fixture.Operations.Before = Fail; }
        var error = await Assert.ThrowsAsync<StartupMutationRecoveryRequiredException>(() => RunStageAsync(manager, state, restore));
        Assert.Equal("intent_cleanup", error.Stage); Assert.True(error.CommitConfirmed);
        await AssertStageNativeAsync(fixture, state, restore);
        using var ledger = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture.Paths.StartupBackupPath));
        Assert.Equal(restore, ledger.RootElement[0].GetProperty("restored_at").ValueKind != JsonValueKind.Null);
        Assert.Equal(!after, File.Exists(fixture.Paths.StartupBackupPath + ".transaction"));
        Assert.DoesNotContain(StartupMutationOperation.NativeRollback, fixture.Operations.Calls);
    }

    [Fact]
    public async Task PreNativeFailureAndIntentCleanupFailurePreserveBothCauses()
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw);
        fixture.Native.BeforeWritableOpen = () => { fixture.Native.BeforeWritableOpen = null; fixture.Native.Delete("HKCU", "owned"); };
        fixture.Operations.Before = operation => { if (operation == StartupMutationOperation.IntentCleanup) { throw new IOException("owned intent cleanup failed"); } };
        using var manager = fixture.Manager();
        var error = await Assert.ThrowsAsync<StartupMutationRecoveryRequiredException>(() => manager.DisableAsync("HKCU", "owned", null));
        Assert.Equal("intent_cleanup", error.Stage); Assert.False(error.CommitConfirmed);
        var causes = Assert.IsType<AggregateException>(error.InnerException);
        Assert.IsType<KeyNotFoundException>(causes.InnerExceptions[0]); Assert.Equal("owned intent cleanup failed", causes.InnerExceptions[1].Message);
        Assert.True(File.Exists(fixture.Paths.StartupBackupPath + ".transaction"));
        Assert.DoesNotContain(StartupMutationOperation.NativeMutation, fixture.Operations.Calls);
    }

    [Fact]
    public async Task CreatingMissingRegistryKeyIsIncludedInTheOpaqueNativeBoundary()
    {
        using var fixture = new Fixture(); using var manager = fixture.Manager();
        var state = await PrepareStageAsync(fixture, manager, false, true); fixture.Native.DeleteSource("HKCU");
        fixture.Operations.After = operation => { if (operation == StartupMutationOperation.NativeMutation) { throw new IOException("after create and set"); } };
        var error = await Assert.ThrowsAsync<StartupMutationRecoveryRequiredException>(() => RunStageAsync(manager, state, true));
        Assert.Equal("native_mutation", error.Stage); await AssertStageNativeAsync(fixture, state, true);
        Assert.True(File.Exists(fixture.Paths.StartupBackupPath + ".transaction"));
        Assert.DoesNotContain(StartupMutationOperation.NativeRollback, fixture.Operations.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OpaqueNativeFailureDoesNotCompensateOverAForeignValue(bool restore)
    {
        using var fixture = new Fixture(); using var manager = fixture.Manager();
        var state = await PrepareStageAsync(fixture, manager, false, restore);
        fixture.Operations.After = operation =>
        {
            if (operation != StartupMutationOperation.NativeMutation) { return; }
            fixture.Native.Put("HKCU", "owned", "foreign value", RegistryValueKind.String);
            throw new IOException("opaque after foreign writer");
        };
        await Assert.ThrowsAsync<StartupMutationRecoveryRequiredException>(() => RunStageAsync(manager, state, restore));
        Assert.Equal("foreign value", fixture.Native.Get("HKCU", "owned")); Assert.Equal(RegistryValueKind.String, fixture.Native.Kind("HKCU", "owned"));
        Assert.True(File.Exists(fixture.Paths.StartupBackupPath + ".transaction"));
        Assert.DoesNotContain(StartupMutationOperation.NativeRollback, fixture.Operations.Calls);
    }

    [Fact]
    public async Task DirectoryAtIntentLeafBlocksBeforeNativeOrPublication()
    {
        using var fixture = new Fixture(); fixture.Native.Put("HKCU", "owned", fixture.Raw);
        Directory.CreateDirectory(fixture.Paths.StartupBackupPath + ".transaction");
        using var manager = fixture.Manager();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DisableAsync("HKCU", "owned", null));
        Assert.Empty(fixture.Operations.Calls); Assert.Equal(fixture.Raw, fixture.Native.Get("HKCU", "owned"));
    }

    [Fact]
    public async Task TemporaryCleanupFailureDoesNotMaskTheOriginalCanceledWrite()
    {
        using var fixture = new Fixture(); using var manager = fixture.Manager();
        var state = await PrepareStageAsync(fixture, manager, false, false);
        using var cancel = new CancellationTokenSource();
        fixture.Operations.After = operation =>
        {
            if (operation != StartupMutationOperation.NativeMutation) { return; }
            cancel.Cancel();
            fixture.Operations.Before = boundary => { if (boundary == StartupMutationOperation.TemporaryCleanup) { throw new IOException("owned candidate cleanup failure"); } };
        };
        var error = await Assert.ThrowsAsync<AggregateException>(() => RunStageAsync(manager, state, false, cancel.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerExceptions[0]);
        Assert.Equal("owned candidate cleanup failure", error.InnerExceptions[1].Message);
        await AssertStageNativeAsync(fixture, state, true);
        Assert.False(File.Exists(fixture.Paths.StartupBackupPath + ".transaction"));
        Assert.NotEmpty(Directory.GetFiles(fixture.Root, ".*.tmp")); // fixture disposal removes owned candidates
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ShortcutRollbackDoesNotTreatChangedOrMissingStateAsACompletedRollback(bool restore)
    {
        using var fixture = new Fixture(); using var manager = fixture.Manager();
        var state = await PrepareStageAsync(fixture, manager, true, restore);
        using var cancel = new CancellationTokenSource();
        fixture.Operations.After = operation => { if (operation == StartupMutationOperation.NativeMutation) { cancel.Cancel(); } };
        fixture.Operations.Before = operation =>
        {
            if (operation != StartupMutationOperation.NativeRollback) { return; }
            Assert.StartsWith(fixture.Root + Path.DirectorySeparatorChar, state.Origin!, StringComparison.Ordinal);
            if (restore) { File.Delete(state.Origin!); }
            else { File.WriteAllBytes(state.Origin!, [7, 9, 11]); }
        };
        var error = await Assert.ThrowsAsync<StartupMutationRecoveryRequiredException>(() => RunStageAsync(manager, state, restore, cancel.Token));
        Assert.Equal("native_rollback", error.Stage);
        var causes = Assert.IsType<AggregateException>(error.InnerException);
        Assert.IsAssignableFrom<OperationCanceledException>(causes.InnerExceptions[0]); Assert.IsType<IOException>(causes.InnerExceptions[1]);
        if (!restore) { Assert.Equal(new byte[] { 7, 9, 11 }, await File.ReadAllBytesAsync(state.Origin!)); }
        Assert.True(File.Exists(fixture.Paths.StartupBackupPath + ".transaction"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunStageAsync(manager, state, restore));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationAfterConfirmedLedgerPublicationDoesNotReclassifyACommitAsCanceled(bool restore)
    {
        using var fixture = new Fixture(); using var manager = fixture.Manager();
        var state = await PrepareStageAsync(fixture, manager, false, restore);
        using var cancel = new CancellationTokenSource();
        fixture.Operations.After = operation => { if (operation == StartupMutationOperation.LedgerPublication) { cancel.Cancel(); } };
        var result = await RunStageAsync(manager, state, restore, cancel.Token);
        Assert.True(cancel.IsCancellationRequested); Assert.True(result.Succeeded);
        await AssertStageNativeAsync(fixture, state, restore);
        Assert.DoesNotContain(StartupMutationOperation.NativeRollback, fixture.Operations.Calls);
        fixture.AssertNoTransactionOrTemps();
    }

    [Fact]
    public async Task ReturnedLedgerPublicationFollowedByCandidateCleanupFailureDoesNotRollback()
    {
        using var fixture = new Fixture(); using var manager = fixture.Manager();
        var state = await PrepareStageAsync(fixture, manager, false, false);
        fixture.Operations.After = operation =>
        {
            if (operation == StartupMutationOperation.LedgerPublication)
            { fixture.Operations.Before = boundary => { if (boundary == StartupMutationOperation.TemporaryCleanup) { throw new IOException("after confirmed ledger"); } }; }
        };
        var error = await Assert.ThrowsAsync<StartupMutationRecoveryRequiredException>(() => RunStageAsync(manager, state, false));
        Assert.Equal("ledger_publication", error.Stage); Assert.True(error.CommitConfirmed);
        await AssertStageNativeAsync(fixture, state, false);
        Assert.True(File.Exists(fixture.Paths.StartupBackupPath)); Assert.True(File.Exists(fixture.Paths.StartupBackupPath + ".transaction"));
        Assert.DoesNotContain(StartupMutationOperation.NativeRollback, fixture.Operations.Calls);
    }
}
