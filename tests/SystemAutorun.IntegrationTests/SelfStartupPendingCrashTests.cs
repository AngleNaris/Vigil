using System.IO;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("flushed")] [InlineData("published")] [InlineData("reserved")]
    public async Task SelfPendingProcessCrashPreservesOriginalIdentityAndFreshReadersNeverReplayOrClear(string stage)
    {
        using var root = new SelfPendingRoot(); var input = root.WriteInput(); var bundle = new CrashHostBundle(root.Path);
        var mode = "self-pending-" + stage; await using var owner = bundle.Start(mode); await owner.WaitForCheckpointAsync(mode);
        var ids = new HashSet<int> { owner.Id }; var expected = stage == "flushed" ? SelfStartupPendingState.Missing : SelfStartupPendingState.Present;
        var before = SelfPendingImages(root);
        await using (var liveReader = bundle.Start("self-pending-read"))
        {
            using var report = await liveReader.ReadReportAsync(); Assert.True(ids.Add(report.RootElement.GetProperty("pid").GetInt32()));
            Assert.Equal(expected, SelfPendingSnapshot(report).State); AssertSelfPendingImages(root, before);
        }
        await using (var competing = bundle.Start("self-pending-attempt"))
        {
            using var report = await competing.ReadReportAsync(); Assert.True(ids.Add(report.RootElement.GetProperty("pid").GetInt32()));
            Assert.Equal(stage == "reserved" ? "pending_exists" : "pending_storage_unavailable", report.RootElement.GetProperty("code").GetString());
            AssertSelfPendingImages(root, before);
        }
        await owner.TerminateAsync(); Assert.NotEqual(0, owner.ExitCode);
        Assert.Empty(File.ReadAllBytes(System.IO.Path.Combine(root.Path, RootWriteLease.FileName)));
        Assert.Equal(stage == "flushed" ? 1 : 0, Directory.EnumerateFiles(root.Path, "*.tmp").Count());
        for (var repeat = 0; repeat < 2; repeat++)
        {
            await using var reader = bundle.Start("self-pending-read"); using var report = await reader.ReadReportAsync();
            Assert.True(ids.Add(report.RootElement.GetProperty("pid").GetInt32())); Assert.Equal(root.Path, report.RootElement.GetProperty("root").GetString());
            var snapshot = SelfPendingSnapshot(report); Assert.Equal(expected, snapshot.State);
            if (stage == "flushed") { Assert.False(snapshot.BlocksSubmission); Assert.Null(snapshot.Operation); }
            else { Assert.True(snapshot.BlocksSubmission); Assert.Equal(input.OperationId, snapshot.Operation!.OperationId); Assert.Equal(input.Enabled, snapshot.Operation.Enabled); }
            AssertSelfPendingImages(root, before);
        }
        if (stage == "flushed")
        {
            // No reservation returned and no submit exists in this worker. An
            // orphan temporary is not a sent operation or authority to recover.
            await using var next = bundle.Start("self-pending-new"); using var report = await next.ReadReportAsync();
            Assert.Equal(input.NextOperationId, SelfPendingSnapshot(report).Operation!.OperationId);
            var orphan = Assert.Single(before.Keys, path => path.EndsWith(".tmp", StringComparison.Ordinal));
            Assert.Equal(before[orphan], File.ReadAllBytes(orphan));
        }
        else
        {
            await using var refused = bundle.Start("self-pending-attempt"); using var report = await refused.ReadReportAsync();
            Assert.Equal("pending_exists", report.RootElement.GetProperty("code").GetString()); AssertSelfPendingImages(root, before);
        }
    }

    [Fact]
    public async Task SelfPendingCorruptRecordRemainsFailClosedAcrossIndependentProcesses()
    {
        using var root = new SelfPendingRoot(); _ = root.WriteInput(); var bundle = new CrashHostBundle(root.Path);
        File.WriteAllText(System.IO.Path.Combine(root.Path, SelfStartupPendingStore.FileName), "{\"operationId\":\"invalid\"}"); var before = SelfPendingImages(root);
        for (var repeat = 0; repeat < 2; repeat++)
        {
            await using var reader = bundle.Start("self-pending-read"); using var report = await reader.ReadReportAsync(); var snapshot = SelfPendingSnapshot(report);
            Assert.Equal(SelfStartupPendingState.Unavailable, snapshot.State); Assert.Equal("pending_invalid", snapshot.Reason);
            Assert.True(snapshot.BlocksSubmission); Assert.Null(snapshot.Operation); AssertSelfPendingImages(root, before);
        }
        await using var refused = bundle.Start("self-pending-attempt"); using var rejection = await refused.ReadReportAsync();
        Assert.Equal("pending_invalid", rejection.RootElement.GetProperty("code").GetString());
        // The failed writer may create only the normal empty cooperative lock.
        var lockPath = System.IO.Path.Combine(root.Path, RootWriteLease.FileName); if (File.Exists(lockPath)) { Assert.Empty(File.ReadAllBytes(lockPath)); }
        Assert.Equal(before[System.IO.Path.Combine(root.Path, SelfStartupPendingStore.FileName)], File.ReadAllBytes(System.IO.Path.Combine(root.Path, SelfStartupPendingStore.FileName)));
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp"));
    }

    private static SelfStartupPendingSnapshot SelfPendingSnapshot(JsonDocument report) =>
        report.RootElement.GetProperty("snapshot").Deserialize<SelfStartupPendingSnapshot>(AgentProtocol.JsonOptions)!;
    // The cooperative lease is intentionally unreadable while held, not a data
    // snapshot. Check its metadata separately; verify empty bytes after owner exit.
    private static Dictionary<string, byte[]> SelfPendingImages(SelfPendingRoot root)
    {
        var lease = new FileInfo(System.IO.Path.Combine(root.Path, RootWriteLease.FileName));
        if (lease.Exists) { Assert.Equal(0, lease.Length); }
        return Directory.GetFiles(root.Path, "*", SearchOption.TopDirectoryOnly)
            .Where(path => System.IO.Path.GetFileName(path) != RootWriteLease.FileName)
            .Order(StringComparer.Ordinal).ToDictionary(path => path, File.ReadAllBytes);
    }
    private static void AssertSelfPendingImages(SelfPendingRoot root, Dictionary<string, byte[]> before)
    { var after = SelfPendingImages(root); Assert.Equal(before.Keys.Order(), after.Keys.Order()); foreach (var row in before) { Assert.Equal(row.Value, after[row.Key]); } }

    private sealed class SelfPendingRoot : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SystemAutorun-startup-mutation-" + Guid.NewGuid().ToString("N"));
        internal SelfPendingRoot()
        { Directory.CreateDirectory(Path); _ = RequireCrashRoot(Path); File.WriteAllText(System.IO.Path.Combine(Path, "autorun.yaml"), "version: 1\ntasks: []\n"); }
        internal SelfPendingWorkerInput WriteInput()
        {
            var input = new SelfPendingWorkerInput(AgentProtocol.NewOperationId(), AgentProtocol.NewOperationId(), true);
            File.WriteAllBytes(System.IO.Path.Combine(Path, "self-pending-input.json"), JsonSerializer.SerializeToUtf8Bytes(input, AgentProtocol.JsonOptions)); return input;
        }
        public void Dispose()
        { if (Directory.Exists(Path)) { _ = RequireCrashRoot(Path); Directory.Delete(Path, recursive: true); } }
    }
}
