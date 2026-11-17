using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup", false, false)] [InlineData("Startup", true, false)]
    [InlineData("Startup", false, true)] [InlineData("Startup", true, true)]
    [InlineData("Common Startup", false, false)] [InlineData("Common Startup", true, false)]
    [InlineData("Common Startup", false, true)] [InlineData("Common Startup", true, true)]
    public async Task NativeInspectionCliDataActualConsoleUsesRealPipeLedgerAndFixedNativeSlots(string source, bool archived, bool original)
    {
        using var owned = new Fixture(administrator: false); var metadata = new InspectionOwnedReader(owned); var slots = new PipeOwnedSlotReader(owned);
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        metadata.Paths = server.Paths; slots.Paths = server.Paths; var record = await WritePipeEvidenceRecord(owned, server, source);
        if (archived) { await File.WriteAllBytesAsync(record.BackupPath!, [1]); } if (original) { await File.WriteAllBytesAsync(record.Origin, [2]); }
        var ledger = await File.ReadAllBytesAsync(server.Paths.StartupBackupPath); var yaml = await File.ReadAllBytesAsync(server.Paths.ConfigPath);
        var list = await new AgentStartupClient(server.Client).ReadAsync(new("all", "shortcut", "disabled")); var entry = Assert.Single(list.Entries);
        var result = await RunNativeInspectionConsole(server.Root, entry, list.ConfigurationRevision);
        Assert.Equal(0, result.Exit); Assert.Empty(result.Error); Assert.Single(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(result.Output); var root = document.RootElement; var data = root.GetProperty("data");
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32()); Assert.Equal(2, data.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(archived ? "present" : "missing", data.GetProperty("slots").GetProperty("archive").GetProperty("state").GetString());
        Assert.Equal(original ? "present" : "missing", data.GetProperty("slots").GetProperty("original").GetProperty("state").GetString());
        Assert.Equal("unverified", data.GetProperty("historicalIdentity").GetString()); Assert.False(data.GetProperty("nativeWriteReady").GetBoolean());
        Assert.False(data.GetProperty("nativeWritePerformed").GetBoolean()); Assert.False(data.GetProperty("executionRequested").GetBoolean());
        Assert.Null(root.GetProperty("operationId").GetString()); Assert.NotEqual(JsonValueKind.Null, root.GetProperty("observedAt").ValueKind);
        Assert.Equal(ledger, await File.ReadAllBytesAsync(server.Paths.StartupBackupPath)); Assert.Equal(yaml, await File.ReadAllBytesAsync(server.Paths.ConfigPath));
        Assert.Equal(1, slots.Calls); Assert.Empty(owned.Operations.Calls); Assert.Equal(0, owned.Native.WritableOpens); Assert.Empty(owned.Native.AccessedKeys);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task NativeInspectionCliDataLockedSlotIsPartialNonzeroInJsonAndHumanOutput(bool archive, bool json)
    {
        using var owned = new Fixture(); var metadata = new InspectionOwnedReader(owned); var slots = new PipeOwnedSlotReader(owned);
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        metadata.Paths = server.Paths; slots.Paths = server.Paths; var record = await WritePipeEvidenceRecord(owned, server);
        await File.WriteAllBytesAsync(record.BackupPath!, [1]); await File.WriteAllBytesAsync(record.Origin, [2]);
        var list = await new AgentStartupClient(server.Client).ReadAsync(new("all", "shortcut", "disabled")); var entry = Assert.Single(list.Entries);
        using (var locked = new FileStream(archive ? record.BackupPath! : record.Origin, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await RunNativeInspectionConsole(server.Root, entry, list.ConfigurationRevision, json: json);
            Assert.Equal(1, result.Exit); Assert.Equal(1, slots.Calls);
            if (json)
            {
                Assert.Empty(result.Error); using var document = JsonDocument.Parse(result.Output); var root = document.RootElement;
                Assert.False(root.GetProperty("ok").GetBoolean()); Assert.Equal("startup_native_observation_unknown", root.GetProperty("error").GetProperty("code").GetString());
                var evidence = root.GetProperty("data").GetProperty("slots");
                Assert.Equal(archive ? "unknown" : "present", evidence.GetProperty("archive").GetProperty("state").GetString());
                Assert.Equal(archive ? "present" : "unknown", evidence.GetProperty("original").GetProperty("state").GetString());
            }
            else
            {
                Assert.Contains("startup_native_observation_unknown", result.Error); Assert.Contains(archive ? "archive: unknown" : "original: unknown", result.Output);
                Assert.Contains(archive ? "archive_occupied" : "original_occupied", result.Output); Assert.Contains("Both missing does not authorize", result.Output);
            }
        }
        await File.WriteAllBytesAsync(record.Origin, [3]); await File.WriteAllBytesAsync(record.BackupPath!, [4]);
    }

    [Fact]
    public async Task NativeInspectionCliDataActualParentAclDenialHasPermissionExitAndKeepsUnknownEvidence()
    {
        using var owned = new Fixture(); var metadata = new InspectionOwnedReader(owned); var slots = new PipeOwnedSlotReader(owned);
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        metadata.Paths = server.Paths; slots.Paths = server.Paths; var record = await WritePipeEvidenceRecord(owned, server);
        var list = await new AgentStartupClient(server.Client).ReadAsync(new("all", "shortcut", "disabled")); var entry = Assert.Single(list.Entries);
        var directory = new DirectoryInfo(Path.GetDirectoryName(record.Origin)!); VerifyEvidenceOwnedPath(owned, directory.FullName);
        var acl = directory.GetAccessControl(); var original = acl.GetSecurityDescriptorBinaryForm(); var sddl = acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        try
        {
            acl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny)); directory.SetAccessControl(acl);
            var result = await RunNativeInspectionConsole(server.Root, entry, list.ConfigurationRevision); Assert.Equal(4, result.Exit); Assert.Empty(result.Error);
            using var document = JsonDocument.Parse(result.Output); var root = document.RootElement;
            Assert.Equal("startup_native_permission_denied", root.GetProperty("error").GetProperty("code").GetString());
            var source = root.GetProperty("data").GetProperty("slots").GetProperty("original");
            Assert.Equal("unknown", source.GetProperty("state").GetString()); Assert.Equal("original_permission_denied", source.GetProperty("reason").GetString());
            Assert.Equal(JsonValueKind.Null, source.GetProperty("directoryIdentity").ValueKind);
        }
        finally { acl.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access); directory.SetAccessControl(acl); }
        Assert.Equal(sddl, directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    [Fact]
    public async Task NativeInspectionCliDataRealLedgerEditReturnsSelectedRecordConflictWithoutEvidence()
    {
        using var owned = new Fixture(); var metadata = new InspectionOwnedReader(owned); var slots = new PipeOwnedSlotReader(owned);
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        metadata.Paths = server.Paths; slots.Paths = server.Paths; var record = await WritePipeEvidenceRecord(owned, server);
        var list = await new AgentStartupClient(server.Client).ReadAsync(new("all", "shortcut", "disabled")); var entry = Assert.Single(list.Entries); var changed = false;
        owned.Native.BeforeDirectory = () =>
        {
            if (changed) { return; } changed = true;
            File.WriteAllText(server.Paths.StartupBackupPath, JsonSerializer.Serialize(new[] { record with { Target = @"C:\changed-never-run.exe" } }));
        };
        try
        {
            var result = await RunNativeInspectionConsole(server.Root, entry, list.ConfigurationRevision); Assert.True(changed); Assert.Equal(5, result.Exit);
            using var document = JsonDocument.Parse(result.Output); var root = document.RootElement;
            Assert.Equal("startup_backup_changed", root.GetProperty("error").GetProperty("code").GetString()); Assert.Equal(JsonValueKind.Null, root.GetProperty("data").ValueKind);
            Assert.Null(root.GetProperty("operationId").GetString()); Assert.Equal(1, slots.Calls); Assert.Empty(owned.Operations.Calls);
        }
        finally { owned.Native.BeforeDirectory = null; }
    }

    [Fact]
    public async Task NativeInspectionCliDataInvalidArchiveContextCannotBecomeATrustedMissingResult()
    {
        using var owned = new Fixture(); var metadata = new InspectionOwnedReader(owned); var slots = new PipeOwnedSlotReader(owned);
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        metadata.Paths = server.Paths; slots.Paths = server.Paths; var record = await WritePipeEvidenceRecord(owned, server);
        await File.WriteAllTextAsync(server.Paths.StartupBackupPath, JsonSerializer.Serialize(new[] { record with { BackupPath = @"\\offline.invalid\never-probe\" + record.Id + ".lnk" } }));
        var list = await new AgentStartupClient(server.Client).ReadAsync(new("all", "shortcut", "disabled")); var entry = Assert.Single(list.Entries);
        var result = await RunNativeInspectionConsole(server.Root, entry, list.ConfigurationRevision); Assert.Equal(1, result.Exit);
        using var document = JsonDocument.Parse(result.Output); var evidence = document.RootElement.GetProperty("data").GetProperty("slots");
        Assert.Equal("archive_context_invalid", evidence.GetProperty("archive").GetProperty("reason").GetString());
        Assert.Equal("original_context_invalid", evidence.GetProperty("original").GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, evidence.GetProperty("archive").GetProperty("archiveDirectory").ValueKind);
    }

    [Fact]
    public async Task NativeInspectionCliDataDefaultConsoleStaysMetadataAndExplicitHumanModeSeparatesBothSlots()
    {
        using var owned = new Fixture(); var metadata = new InspectionOwnedReader(owned); var slots = new PipeOwnedSlotReader(owned);
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: metadata, startupSlotReader: slots);
        metadata.Paths = server.Paths; slots.Paths = server.Paths; _ = await WritePipeEvidenceRecord(owned, server);
        var listed = await RunOwnedCliConsole(["startup", "list", "--scope", "user", "--type", "shortcut", "--state", "disabled", "--root", server.Root, "--json"], "");
        Assert.Equal(0, listed.Exit); Assert.Empty(listed.Error);
        using var listedJson = JsonDocument.Parse(listed.Output);
        var list = listedJson.RootElement.GetProperty("data").Deserialize<StartupInventorySnapshot>(AgentProtocol.JsonOptions)!; var entry = Assert.Single(list.Entries);
        var old = await RunNativeInspectionConsole(server.Root, entry, list.ConfigurationRevision, native: false, json: false);
        Assert.Equal(0, old.Exit); Assert.Equal(0, slots.Calls); Assert.Contains("metadata-only", old.Output); Assert.DoesNotContain("archive: missing", old.Output);
        var current = await RunNativeInspectionConsole(server.Root, entry, list.ConfigurationRevision, json: false);
        Assert.Equal(0, current.Exit); Assert.Equal(1, slots.Calls); Assert.Empty(current.Error); Assert.Contains("native-read-only", current.Output);
        Assert.Contains("archive: missing", current.Output); Assert.Contains("original: missing", current.Output); Assert.Contains("historical identity: unverified", current.Output);
        Assert.Contains("NOT a snapshot/CAS", current.Output); Assert.Contains("Both missing does not authorize reconstruction", current.Output);
    }

    private static Task<(int Exit, string Output, string Error)> RunNativeInspectionConsole(string root, StartupEntry entry, string revision, bool native = true, bool json = true) =>
        RunOwnedCliConsole(StartupNativeInspectionCliTests.Arguments(root, entry.Id, revision, native, json),
            JsonSerializer.Serialize(StartupInspectionRequest.FromEntry(entry), AgentProtocol.JsonOptions));

    private static async Task<(int Exit, string Output, string Error)> RunOwnedCliConsole(string[] arguments, string input)
    {
        var executable = typeof(StartupMutationAdapterTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == "CliExecutablePath").Value!;
        Assert.True(File.Exists(executable)); Assert.Contains(@"\src\SystemAutorun.Cli\bin\", Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Owned Console did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(input); process.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
