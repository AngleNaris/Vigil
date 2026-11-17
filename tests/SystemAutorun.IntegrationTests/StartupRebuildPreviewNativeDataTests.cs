using System.IO;
using System.Text.Json;
using SystemAutorun.Cli;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Fact]
    public async Task RebuildPreviewReadsRealBoundedLedgerButNeitherOpensTargetNorTrustsHistoricalPaths()
    {
        using var owned = new Fixture();
        var source = new InspectionOwnedReader(owned);
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        source.Paths = server.Paths;
        var target = Path.Combine(owned.Root, "locked-never-run.exe"); var targetBytes = new byte[] { 4, 2, 8 };
        await File.WriteAllBytesAsync(target, targetBytes);
        var record = new StartupBackupRecord
        {
            Id = Guid.NewGuid().ToString("N"), DisabledAt = DateTimeOffset.UnixEpoch, Source = "Startup", Name = "owned",
            Origin = @"\\offline.invalid\never-probe-origin\owned.lnk", BackupPath = @"\\offline.invalid\never-probe-archive\owned.lnk",
            Target = @"\\offline.invalid\never-use-ledger-target.exe", Arguments = "--untrusted-history",
        };
        Directory.CreateDirectory(Path.GetDirectoryName(server.Paths.StartupBackupPath)!);
        await File.WriteAllTextAsync(server.Paths.StartupBackupPath, JsonSerializer.Serialize(new[] { record }));
        var ledgerBytes = await File.ReadAllBytesAsync(server.Paths.StartupBackupPath);
        var configurationBytes = await File.ReadAllBytesAsync(server.Paths.ConfigPath);
        var writes = owned.Native.WritableOpens;
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var client = new AgentStartupClient(server.Client);
            var inventory = await client.ReadAsync(new("user", "shortcut", "disabled"));
            var selected = Assert.Single(inventory.Entries);
            Assert.Equal(record.Target, selected.Target); Assert.Equal(record.Origin, selected.Origin);
            var input = new StartupRebuildInput(target, "--explicit", owned.Root);
            var request = StartupInspectionRequest.FromEntry(selected) with { RebuildInput = input };
            var review = await client.InspectAsync(selected.Id, request, inventory.ConfigurationRevision);
            Assert.Equal(StartupRebuildInputPolicy.Preview(input), review.RebuildPreview);
            Assert.Equal("not_checked", review.OriginalBackupState); Assert.False(review.NativeWriteReady);
            Assert.False(review.NativeWritePerformed); Assert.False(review.ExecutionRequested);
            var output = new StringWriter(); var error = new StringWriter();
            Assert.Equal(0, await new CliApplication().RunAsync(["startup", "inspect", selected.Id, "--file", "-", "--expected-revision", inventory.ConfigurationRevision,
                "--root", server.Root, "--json"], new StringReader(JsonSerializer.Serialize(request, AgentProtocol.JsonOptions)), output, error));
            Assert.Empty(error.ToString()); using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(target, json.RootElement.GetProperty("data").GetProperty("rebuildPreview").GetProperty("input").GetProperty("target").GetString());
            Assert.Null(json.RootElement.GetProperty("operationId").GetString());
        }
        Assert.Equal(targetBytes, await File.ReadAllBytesAsync(target));
        Assert.Equal(ledgerBytes, await File.ReadAllBytesAsync(server.Paths.StartupBackupPath));
        Assert.Equal(configurationBytes, await File.ReadAllBytesAsync(server.Paths.ConfigPath));
        Assert.Equal(writes, owned.Native.WritableOpens); Assert.Empty(owned.Operations.Calls); owned.AssertNoTransactionOrTemps();
        Assert.Empty((await server.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration.Tasks);
    }
}
