using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;
using SystemAutorun.Core.Services;
using SystemAutorun.Windows.Health;
using SystemAutorun.Windows.Native;

namespace SystemAutorun.IntegrationTests;

public sealed class ManagedProcessReaderTests
{
    [Fact]
    public async Task SamplesEveryEnabledTaskAndKeepsTheTrackingFlagForRecoveryCounts()
    {
        using var current = Process.GetCurrentProcess();
        var monitored = CreateTask("monitored", expectedRunning: true);
        var untracked = CreateTask("untracked", expectedRunning: false);
        var disabled = CreateTask("disabled-untracked", expectedRunning: false) with { Enabled = false };
        var reader = new ManagedProcessReader();

        var snapshots = await reader.ReadAsync(
            new[] { untracked, disabled, monitored },
            new FixedExecutor(current.Id),
            new FixedHealthProbe(HealthResult.Ok("process-running", "Running.")));

        Assert.Equal(["monitored", "untracked"], snapshots.Select(row => row.TaskName).Order().ToArray());
        Assert.False(snapshots.Single(row => row.TaskName == "untracked").ExpectedRunning);
        Assert.NotEmpty(snapshots.Single(row => row.TaskName == "untracked").Processes);
        Assert.True(snapshots.Single(row => row.TaskName == "monitored").ExpectedRunning);

        // Both fixture tasks resolve to this test process and share one CPU sample cache,
        // so the first-read CPU contract is checked with a fresh reader and a single task.
        reader = new ManagedProcessReader();
        var snapshot = Assert.Single(await reader.ReadAsync([monitored], new FixedExecutor(current.Id),
            new FixedHealthProbe(HealthResult.Ok("process-running", "Running."))));
        Assert.Equal("monitored", snapshot.TaskName);
        Assert.Equal("OK", snapshot.State);
        var process = Assert.Single(snapshot.Processes);
        Assert.Equal(current.Id, process.ProcessId);
        Assert.True(process.WorkingSetBytes > 0);
        Assert.True(process.ThreadCount > 0);
        Assert.False(process.CpuUsageAvailable);
        Assert.False(snapshot.CpuUsageAvailable);
        await Task.Delay(TimeSpan.FromMilliseconds(10));
        var next = Assert.Single(await reader.ReadAsync([monitored], new FixedExecutor(current.Id),
            new FixedHealthProbe(HealthResult.Ok("process-running", "Running."))));
        Assert.True(next.CpuUsageAvailable);
        Assert.InRange(next.CpuUsagePercent, 0, 100);
    }

    [Fact]
    public async Task DisabledMonitoredTaskIsReportedAsOffWithoutHealthProbe()
    {
        var probe = new CountingHealthProbe();
        var task = CreateTask("disabled", expectedRunning: true) with { Enabled = false };

        var snapshot = Assert.Single(await new ManagedProcessReader().ReadAsync(
            new[] { task },
            new FixedExecutor(),
            probe));

        Assert.Equal("OFF", snapshot.State);
        Assert.False(snapshot.Healthy);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task RollsUpDescendantProcessTreeCpuMemoryThreadsDiskAndNetworkWithoutChangingRootIdentity()
    {
        var task = CreateTask("parent-app", expectedRunning: true);
        var rootFileTime = DateTime.UtcNow.AddMinutes(-10).ToFileTimeUtc();
        var childFileTime = DateTime.UtcNow.AddMinutes(-5).ToFileTimeUtc();
        var grandFileTime = DateTime.UtcNow.AddMinutes(-2).ToFileTimeUtc();
        var staleNonChildFileTime = DateTime.UtcNow.AddMinutes(-20).ToFileTimeUtc(); // Older than root => PID reuse, must ignore

        var net = new FakeNetworkSource();
        var metrics = new Dictionary<int, NativeMethods.NativeProcessMetric>();
        var reader = new ManagedProcessReader(net, () => metrics);

        // Tick 1: Root (1000) -> Child (2000) -> Grandchild (3000), plus stale PID-reused 4000 claiming Parent=1000
        metrics[1000] = new(1000, 100, "parent-app", rootFileTime, TimeSpan.FromSeconds(10).Ticks, 50L << 20, 40L << 20, 4, 10_000, 5_000);
        metrics[2000] = new(2000, 1000, "worker", childFileTime, TimeSpan.FromSeconds(2).Ticks, 30L << 20, 20L << 20, 3, 20_000, 10_000);
        metrics[3000] = new(3000, 2000, "sub-worker", grandFileTime, TimeSpan.FromSeconds(1).Ticks, 20L << 20, 15L << 20, 2, 30_000, 15_000);
        metrics[4000] = new(4000, 1000, "pid-reused", staleNonChildFileTime, TimeSpan.FromSeconds(50).Ticks, 999L << 20, 999L << 20, 99, 999_999, 999_999);
        net.Set(1000, 1_000, 500);
        net.Set(2000, 2_000, 1_000);
        net.Set(3000, 3_000, 1_500);
        net.Set(4000, 9_999_999, 9_999_999);

        var first = Assert.Single(await reader.ReadAsync([task], new FixedExecutor(1000),
            new FixedHealthProbe(HealthResult.Ok("process-running", "Running."))));
        var firstRoot = Assert.Single(first.Processes);
        Assert.Equal(1000, firstRoot.ProcessId);
        Assert.False(first.CpuUsageAvailable);
        Assert.Equal(100L << 20, first.WorkingSetBytes);
        Assert.Equal(75L << 20, first.PrivateMemoryBytes);
        Assert.Equal(9, first.ThreadCount);

        var identityBeforeChildExit = ResourceProcessIdentity.For(first, first.CapturedAt);
        Assert.NotNull(identityBeforeChildExit);

        await Task.Delay(250);

        // Tick 2: Root + Child + Grandchild accumulate disk & network bytes
        metrics[1000] = metrics[1000] with
        {
            TotalProcessorTimeTicks = TimeSpan.FromSeconds(10.05).Ticks,
            DiskReadBytes = 11_000,
            DiskWriteBytes = 5_500,
        };
        metrics[2000] = metrics[2000] with
        {
            TotalProcessorTimeTicks = TimeSpan.FromSeconds(2.05).Ticks,
            DiskReadBytes = 22_000,
            DiskWriteBytes = 11_000,
        };
        metrics[3000] = metrics[3000] with
        {
            TotalProcessorTimeTicks = TimeSpan.FromSeconds(1.05).Ticks,
            DiskReadBytes = 33_000,
            DiskWriteBytes = 16_500,
        };
        net.Set(1000, 3_000, 1_500);
        net.Set(2000, 6_000, 3_000);
        net.Set(3000, 9_000, 4_500);

        var second = Assert.Single(await reader.ReadAsync([task], new FixedExecutor(1000),
            new FixedHealthProbe(HealthResult.Ok("process-running", "Running."))));
        var secondRoot = Assert.Single(second.Processes);
        Assert.True(second.CpuUsageAvailable);
        Assert.True(second.CpuUsagePercent > 0);
        Assert.True(second.TotalDiskReadBytesPerSecond > 0);
        Assert.True(second.TotalDiskWriteBytesPerSecond > 0);
        Assert.True(second.TotalNetworkReceiveBytesPerSecond > 0);
        Assert.True(second.TotalNetworkSendBytesPerSecond > 0);
        Assert.True(secondRoot.DiskReadBytesPerSecond > 0);
        Assert.True(secondRoot.NetworkReceiveBytesPerSecond > 0);

        // Even after short-lived grandchild exits, root's continuity identity does not break.
        metrics.Remove(3000);
        var third = Assert.Single(await reader.ReadAsync([task], new FixedExecutor(1000),
            new FixedHealthProbe(HealthResult.Ok("process-running", "Running."))));
        Assert.Equal(identityBeforeChildExit, ResourceProcessIdentity.For(third, third.CapturedAt));
    }

    [Fact]
    public async Task LiveNativeMetricsAndProcessNetworkTrackerOperateAndCleanUpWithoutLeakingSession()
    {
        var metrics = NativeMethods.TryReadProcessMetrics();
        Assert.NotNull(metrics);
        Assert.True(metrics.TryGetValue(Environment.ProcessId, out var self));
        Assert.True(self.ParentProcessId > 0);
        Assert.True(self.DiskReadBytes >= 0);
        Assert.True(self.DiskWriteBytes >= 0);

        var sessionName = $"Vigil-TestNet-{Environment.ProcessId}-{Guid.NewGuid():N}";
        using var tracker = new ProcessNetworkCounterTracker(sessionName);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient();
        var acceptTask = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var server = await acceptTask;

        var payload = new byte[4096];
        Array.Fill(payload, (byte)0x5A);
        await client.GetStream().WriteAsync(payload);
        await client.GetStream().FlushAsync();
        var received = new byte[4096];
        var read = await server.GetStream().ReadAsync(received);
        Assert.True(read > 0);

        if (tracker.IsActive)
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            (long Recv, long Send) totals = default;
            while (DateTime.UtcNow < deadline)
            {
                totals = tracker.ReadCumulativeBytes(Environment.ProcessId);
                if (totals.Send > 0 || totals.Recv > 0)
                {
                    break;
                }
                await Task.Delay(50);
            }
            Assert.True(totals.Send > 0 || totals.Recv > 0, "Expected loopback TCP traffic on active Kernel-Network ETW session.");
        }
        else
        {
            var totals = tracker.ReadCumulativeBytes(Environment.ProcessId);
            Assert.Equal((0, 0), totals);
        }
    }

    private static AutorunTask CreateTask(string name, bool expectedRunning) => new(
        name,
        AutorunTaskType.Windows,
        true,
        name,
        Environment.ProcessPath,
        Array.Empty<string>(),
        AppContext.BaseDirectory,
        null,
        null,
        null,
        Array.Empty<string>(),
        0,
        new HealthCheckDefinition(
            HealthCheckType.Process,
            Path.GetFileName(Environment.ProcessPath),
            null,
            null,
            5,
            null,
            null),
        LaunchOptions.LegacyDefault(AutorunTaskType.Windows),
        expectedRunning);

    private sealed class FixedHealthProbe(HealthResult result) : IHealthProbe
    {
        public Task<HealthResult> CheckAsync(
            AutorunTask task,
            bool quick,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class CountingHealthProbe : IHealthProbe
    {
        public int CallCount { get; private set; }

        public Task<HealthResult> CheckAsync(
            AutorunTask task,
            bool quick,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(HealthResult.Ok("unused", "unused"));
        }
    }

    private sealed class FixedExecutor(params int[] processIds) : ITaskExecutor
    {
        public Task<CommandResult> StartAsync(
            AutorunTask task,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandResult(true, "unused"));

        public Task<CommandResult> StopAsync(
            AutorunTask task,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandResult(true, "unused"));

        public IReadOnlyList<int> FindProcessIds(AutorunTask task) => processIds;
    }

    private sealed class FakeNetworkSource : IProcessNetworkCounterSource
    {
        private readonly Dictionary<int, (long Received, long Sent)> _bytes = [];

        public void Set(int pid, long received, long sent) => _bytes[pid] = (received, sent);

        public (long ReceivedBytes, long SentBytes) ReadCumulativeBytes(int processId) =>
            _bytes.TryGetValue(processId, out var pair) ? pair : (0, 0);
    }
}
