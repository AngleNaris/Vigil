using System.IO;
using System.Text.Json;
using Microsoft.Win32;
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
    [InlineData(RegistryValueKind.String)] [InlineData(RegistryValueKind.ExpandString)] [InlineData(RegistryValueKind.Binary)]
    public async Task InspectionNativeDataRealPipeUsesOnlyOwnedRegistryAndCurrentRootWithoutWrites(RegistryValueKind kind)
    {
        using var owned = new Fixture();
        object raw = kind == RegistryValueKind.Binary ? new byte[] { 2, 4, 8 } : "\"%TEMP%\\never-run-inspection.exe\" --literal";
        owned.Native.PutValue("HKCU", "owned", raw, kind);
        var writes = owned.Native.WritableOpens;
        var source = new InspectionOwnedReader(owned);
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: source);
        source.Paths = server.Paths;
        var client = new AgentStartupClient(server.Client);
        var snapshot = await client.ReadAsync(new("user", "registry", "active"));
        var entry = Assert.Single(snapshot.Entries);
        var inspection = await client.InspectAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), snapshot.ConfigurationRevision);
        Assert.Equal(entry, inspection.Entry); Assert.False(inspection.NativeWriteReady);
        Assert.Equal(kind == RegistryValueKind.Binary ? "blocked" : "reviewable", inspection.Actions[0].State);
        Assert.Equal(kind == RegistryValueKind.Binary ? "registry_value_type_unsupported" : null, inspection.Entry.Error);
        Assert.Equal(writes, owned.Native.WritableOpens); Assert.Empty(owned.Operations.Calls);
        Assert.Equal(JsonSerializer.Serialize(raw), JsonSerializer.Serialize(owned.Native.Value("HKCU", "owned")));
        Assert.False(File.Exists(server.Paths.StartupBackupPath)); owned.AssertNoTransactionOrTemps();
        Assert.All(owned.Native.AccessedKeys, path => Assert.StartsWith(owned.Native.RegistryRoot + "\\", path, StringComparison.Ordinal));
        if (kind == RegistryValueKind.String)
        {
            // Registry kind is deliberately NOT part of displayed metadata. The same digest
            // after this native change must still grant NO native write/preparation authority.
            owned.Native.Put("HKCU", "owned", (string)raw, RegistryValueKind.ExpandString);
            var currentWrites = owned.Native.WritableOpens;
            var metadataOnly = await client.InspectAsync(entry.Id, StartupInspectionRequest.FromEntry(entry), snapshot.ConfigurationRevision);
            Assert.Equal(entry.MetadataDigest, metadataOnly.MetadataDigest); Assert.False(metadataOnly.NativeWriteReady);
            Assert.Equal(currentWrites, owned.Native.WritableOpens);
        }
        owned.Native.Put("HKCU", "owned", "\"%TEMP%\\changed.exe\"", RegistryValueKind.String);
        var error = await Assert.ThrowsAsync<AgentCommandException>(() => client.InspectAsync(entry.Id,
            StartupInspectionRequest.FromEntry(entry), snapshot.ConfigurationRevision));
        Assert.Equal("startup_entry_changed", error.Code);
    }

    private sealed class InspectionOwnedReader(Fixture owned) : IStartupSnapshotReader
    {
        internal ProjectPaths? Paths;
        public Task<StartupManagementSnapshot> ReadAsync(AutorunConfiguration configuration, StartupListQuery query, CancellationToken cancellationToken = default)
        {
            // Registry-only query; no COM/real Startup source is involved. Both configuration
            // and ledger are the Pipe server's root, while native source mapping is private HKCU.
            using var manager = new StartupManager(Paths!, owned.Native, operations: owned.Operations);
            return Task.FromResult(manager.CaptureInventory(configuration, query, cancellationToken));
        }
    }
}
