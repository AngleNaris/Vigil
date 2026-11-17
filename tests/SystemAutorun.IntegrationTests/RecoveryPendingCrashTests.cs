using System.IO;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Recovery;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("flushed")] [InlineData("published")] [InlineData("reserved")]
    public async Task RecoveryPendingCrashKeepsOriginalIdentityAcrossProcessesWithoutReplayOrRecoveredClear(string stage)
    {
        using var root = new RecoveryHintRoot(); var input = root.WriteInput(); var bundle = new CrashHostBundle(root.Path);
        var mode = "recovery-pending-" + stage; await using var owner = bundle.Start(mode); await owner.WaitForCheckpointAsync(mode);
        var ids = new HashSet<int> { owner.Id }; var expected = stage == "flushed" ? RecoveryPendingState.Missing : RecoveryPendingState.Present;
        if (stage == "reserved")
        {
            // Reserve returned, so there is no root lease held across a future RPC.
            // Another process/name remains usable while this owner is still alive.
            await using var other = bundle.Start("recovery-pending-other"); using var report = await other.ReadReportAsync();
            Assert.True(ids.Add(report.RootElement.GetProperty("pid").GetInt32())); Assert.Equal("another", RecoverySnapshot(report).Operation!.TaskName);
        }
        var before = RecoveryImages(root);
        await using (var reader = bundle.Start("recovery-pending-read"))
        {
            using var report = await reader.ReadReportAsync(); Assert.True(ids.Add(report.RootElement.GetProperty("pid").GetInt32()));
            Assert.Equal(expected, RecoverySnapshot(report).State); AssertRecoveryImages(root, before);
        }
        await using (var competing = bundle.Start("recovery-pending-attempt"))
        {
            using var report = await competing.ReadReportAsync(); Assert.True(ids.Add(report.RootElement.GetProperty("pid").GetInt32()));
            Assert.Equal(stage == "reserved" ? "pending_exists" : "pending_storage_unavailable", report.RootElement.GetProperty("code").GetString());
            AssertRecoveryImages(root, before);
        }
        await owner.TerminateAsync(); Assert.NotEqual(0, owner.ExitCode);
        Assert.Empty(File.ReadAllBytes(System.IO.Path.Combine(root.Path, RootWriteLease.FileName)));
        Assert.Equal(stage == "flushed" ? 1 : 0, Directory.GetFiles(root.Path, "*.tmp").Length);
        for (var repeat = 0; repeat < 2; repeat++)
        {
            await using var reader = bundle.Start("recovery-pending-read"); using var report = await reader.ReadReportAsync();
            Assert.True(ids.Add(report.RootElement.GetProperty("pid").GetInt32())); Assert.Equal(root.Path, report.RootElement.GetProperty("root").GetString());
            var snapshot = RecoverySnapshot(report); Assert.Equal(expected, snapshot.State);
            if (stage == "flushed") { Assert.False(snapshot.BlocksSubmission); Assert.Null(snapshot.Operation); }
            else { Assert.True(snapshot.BlocksSubmission); Assert.Equal(input.OperationId, snapshot.Operation!.OperationId); Assert.Equal(input.ExpectedRevision, snapshot.Operation.ExpectedRevision); Assert.Equal(input.TaskName, snapshot.Operation.TaskName); }
            AssertRecoveryImages(root, before);
        }
        if (stage == "flushed")
        {
            // Publication never returned; this test host contains no mutation send.
            await using var next = bundle.Start("recovery-pending-new"); using var report = await next.ReadReportAsync();
            Assert.Equal(input.NextOperationId, RecoverySnapshot(report).Operation!.OperationId);
            var orphan = Assert.Single(before.Keys, path => path.EndsWith(".tmp", StringComparison.Ordinal)); Assert.Equal(before[orphan], File.ReadAllBytes(orphan));
        }
        else
        {
            await using var refused = bundle.Start("recovery-pending-attempt"); using var report = await refused.ReadReportAsync();
            Assert.Equal("pending_exists", report.RootElement.GetProperty("code").GetString()); AssertRecoveryImages(root, before);
        }
    }

    [Fact]
    public async Task RecoveryPendingCorruptionStaysUnavailableAcrossIndependentReadersAndWriter()
    {
        using var root = new RecoveryHintRoot(); var input = root.WriteInput(); var bundle = new CrashHostBundle(root.Path);
        var pending = System.IO.Path.Combine(root.Path, new RecoveryPendingStore(new(root.Path), input.TaskName).FileName);
        File.WriteAllText(pending, "{\"operationId\":\"untrusted-fixture\"}"); var before = RecoveryImages(root);
        for (var repeat = 0; repeat < 2; repeat++)
        {
            await using var reader = bundle.Start("recovery-pending-read"); using var report = await reader.ReadReportAsync(); var state = RecoverySnapshot(report);
            Assert.Equal(RecoveryPendingState.Unavailable, state.State); Assert.Equal("pending_invalid", state.Reason); Assert.True(state.BlocksSubmission); Assert.Null(state.Operation);
            AssertRecoveryImages(root, before);
        }
        await using var refused = bundle.Start("recovery-pending-attempt"); using var rejection = await refused.ReadReportAsync();
        Assert.Equal("pending_invalid", rejection.RootElement.GetProperty("code").GetString()); AssertRecoveryImages(root, before);
        Assert.Empty(Directory.GetFiles(root.Path, "*.tmp"));
    }

    private static RecoveryPendingSnapshot RecoverySnapshot(JsonDocument report) => report.RootElement.GetProperty("snapshot").Deserialize<RecoveryPendingSnapshot>(AgentProtocol.JsonOptions)!;
    private static Dictionary<string, byte[]> RecoveryImages(RecoveryHintRoot root)
    {
        var lease = new FileInfo(System.IO.Path.Combine(root.Path, RootWriteLease.FileName)); if (lease.Exists) { Assert.Equal(0, lease.Length); }
        return Directory.GetFiles(root.Path, "*", SearchOption.TopDirectoryOnly).Where(path => System.IO.Path.GetFileName(path) != RootWriteLease.FileName)
            .Order(StringComparer.Ordinal).ToDictionary(path => path, File.ReadAllBytes);
    }
    private static void AssertRecoveryImages(RecoveryHintRoot root, Dictionary<string, byte[]> before)
    { var after = RecoveryImages(root); Assert.Equal(before.Keys.Order(), after.Keys.Order()); foreach (var row in before) { Assert.Equal(row.Value, after[row.Key]); } }
    private sealed class RecoveryHintRoot : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SystemAutorun-startup-mutation-" + Guid.NewGuid().ToString("N"));
        internal RecoveryHintRoot() { Directory.CreateDirectory(Path); _ = RequireCrashRoot(Path); File.WriteAllText(System.IO.Path.Combine(Path, "autorun.yaml"), "version: 1\ntasks: []\n"); }
        internal RecoveryPendingWorkerInput WriteInput()
        {
            var input = new RecoveryPendingWorkerInput(AgentProtocol.NewOperationId(), AgentProtocol.NewOperationId(), new string('a', 64), "media");
            File.WriteAllBytes(System.IO.Path.Combine(Path, "recovery-pending-input.json"), JsonSerializer.SerializeToUtf8Bytes(input, AgentProtocol.JsonOptions)); return input;
        }
        public void Dispose() { if (Directory.Exists(Path)) { _ = RequireCrashRoot(Path); Directory.Delete(Path, recursive: true); } }
    }
}
