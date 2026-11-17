using System.Diagnostics;
using System.IO;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;
using SystemAutorun.Windows.Health;

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
}
