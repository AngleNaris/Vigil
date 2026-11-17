using System.IO;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GuiNativeObservationUsesOwnedActualLedgerFixedSlotsAndStrictPipeWithoutNativeAuthority(bool occupied)
    {
        using var owned = new Fixture(administrator: false);
        var metadata = new InspectionOwnedReader(owned); var slots = new PipeOwnedSlotReader(owned);
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        metadata.Paths = server.Paths; slots.Paths = server.Paths;
        var record = await WritePipeEvidenceRecord(owned, server);
        await File.WriteAllBytesAsync(record.BackupPath!, [1]);
        var ledger = await File.ReadAllBytesAsync(server.Paths.StartupBackupPath);
        var yaml = await File.ReadAllBytesAsync(server.Paths.ConfigPath);
        using var locked = occupied ? new FileStream(record.BackupPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            using var model = new StartupManagerViewModel(new(server.Client), new StartupCacheStore(server.Paths.StartupCachePath));
            await model.InitializeAsync(); model.SelectedEntry = model.Entries.Single(item => item.Entry.IsDisabled);
            Assert.Equal(record.Id, model.SelectedEntry.Entry.BackupId); Assert.True(model.InspectNativeCommand.CanExecute(null));
            await model.InspectNativeCommand.ExecuteAsync(null);
            Assert.True(model.ShowDetails); Assert.NotNull(model.Inspection); Assert.False(model.Inspection.NativeWriteReady);
            Assert.Equal("not_checked", model.Inspection.OriginalBackupState);
            Assert.Contains(occupied ? "unknown" : "present", model.DetailText); Assert.Contains("missing", model.DetailText);
            Assert.Contains("历史身份未验证", model.DetailText); Assert.Contains("不授权", model.DetailText);
            if (occupied) { Assert.Contains("unknown 不是缺失", model.StatusMessage); Assert.Contains("archive_occupied", model.DetailText); }
            Assert.False(model.AddToTasksCommand.CanExecute(null)); Assert.False(model.RestoreCommand.CanExecute(null));
            Assert.False(model.DisableCommand.CanExecute(null)); model.RestoreCommand.Execute(null); model.DisableCommand.Execute(null);
        });
        Assert.Equal(ledger, await File.ReadAllBytesAsync(server.Paths.StartupBackupPath));
        Assert.Equal(yaml, await File.ReadAllBytesAsync(server.Paths.ConfigPath)); Assert.Equal(1, slots.Calls);
        Assert.Equal(0, owned.Native.WritableOpens); Assert.Empty(owned.Operations.Calls); owned.AssertNoTransactionOrTemps();
    }
}
