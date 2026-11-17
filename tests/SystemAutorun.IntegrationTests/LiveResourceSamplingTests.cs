using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.IntegrationTests;

public sealed class LiveResourceSamplingTests
{
    [Fact]
    public async Task RealWindowsObservationsKeepTheirOriginalValuesTimesAndActualGap()
    {
        var task = new AutorunTask("owned-test-process", AutorunTaskType.Windows, true, "read-only resources",
            Environment.ProcessPath, [], AppContext.BaseDirectory, null, null, null, [], 0,
            new(HealthCheckType.Process, null, null, null, 1, null, null), LaunchOptions.LegacyDefault(AutorunTaskType.Windows), false);
        var reader = new ManagedProcessReader(); var buffer = new ResourceTrendBuffer();
        var observations = new List<ManagedProcessSnapshot>();
        ResourceTrendBuffer.Series? series = null;
        for (var index = 0; index < 4; index++)
        {
            if (index > 0) { await Task.Delay(TimeSpan.FromSeconds(index == 3 ? 4 : 1)); }
            var sample = Assert.Single(await reader.ReadAsync([task], new OwnPidOnly(), new PassiveFixtureHealth()));
            var process = Assert.Single(sample.Processes);
            Assert.Equal(Environment.ProcessId, process.ProcessId); Assert.NotNull(process.StartedAt);
            Assert.True(sample.WorkingSetBytes > 0);
            observations.Add(sample);
            series = buffer.Observe("isolated:owned-process", sample, DateTimeOffset.UtcNow, false)!;
            Assert.Equal(index + 1, series.Memory.Count);
            Assert.Equal(sample.CapturedAt, series.Memory[^1].At);
            Assert.Equal(sample.WorkingSetBytes, series.Memory[^1].Value);
            if (index == 0) { Assert.Null(series.Cpu[^1].Value); }
            else { Assert.Equal(sample.CpuUsagePercent, series.Cpu[^1].Value); }
            for (var repeat = 0; repeat < 5; repeat++) { buffer.Observe("isolated:owned-process", sample, DateTimeOffset.UtcNow, false); }
            Assert.Equal(index + 1, series.Memory.Count);
        }
        Assert.False(DashboardPlot.CanConnect(series!.Memory[2], series.Memory[3], ResourceTrendBuffer.MaximumGap));
        if (Environment.GetEnvironmentVariable("SA_RESOURCE_DATA_EVIDENCE") is { Length: > 0 } output)
        {
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            {
                source = "Real ManagedProcessReader/Windows PID resources, own test process only; synthetic passive health; no workload lifecycle or production IPC",
                pid = Environment.ProcessId, observations, cpu = series.Cpu, bytes = series.Memory,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Fact]
    public async Task OverlappingTasksShareOnePhysicalPidCaptureAndCannotConsumeItsBaselineTwice()
    {
        var firstTask = new AutorunTask("owned-alias-a", AutorunTaskType.Windows, true, "read-only shared PID", Environment.ProcessPath,
            [], AppContext.BaseDirectory, null, null, null, [], 0, new(HealthCheckType.Process, null, null, null, 1, null, null),
            LaunchOptions.LegacyDefault(AutorunTaskType.Windows), false);
        var reader = new ManagedProcessReader(); var tasks = new[] { firstTask, firstTask with { Name = "owned-alias-b" } };
        var first = await reader.ReadAsync(tasks, new OwnPidOnly(), new PassiveFixtureHealth());
        Assert.Equal(2, first.Count); Assert.All(first, row => Assert.False(row.CpuUsageAvailable));
        Assert.Same(Assert.Single(first[0].Processes), Assert.Single(first[1].Processes));
        await Task.Delay(20);
        var second = await reader.ReadAsync(tasks, new OwnPidOnly(), new PassiveFixtureHealth());
        Assert.All(second, row => Assert.True(row.CpuUsageAvailable));
        Assert.Same(Assert.Single(second[0].Processes), Assert.Single(second[1].Processes));
        Assert.Equal(second[0].CpuUsagePercent, second[1].CpuUsagePercent);
        Assert.Equal(second[0].WorkingSetBytes, second[1].WorkingSetBytes);
        if (Environment.GetEnvironmentVariable("SA_RESOURCE_SHARED_PID_EVIDENCE") is { Length: > 0 } output)
        {
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            { scope = "Real own PID, two overlapping task aliases, synthetic health; no process lifecycle", pid = Environment.ProcessId, first, second },
                new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    internal sealed class OwnPidOnly : ITaskExecutor
    {
        public IReadOnlyList<int> FindProcessIds(AutorunTask task) => [Environment.ProcessId];
        public Task<CommandResult> StartAsync(AutorunTask task, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No workload lifecycle in resource tests.");
        public Task<CommandResult> StopAsync(AutorunTask task, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No workload lifecycle in resource tests.");
    }
    internal sealed class PassiveFixtureHealth : IHealthProbe
    {
        public Task<HealthResult> CheckAsync(AutorunTask task, bool quick, CancellationToken cancellationToken = default) => Task.FromResult(HealthResult.Ok("fixture-only", "Synthetic health, real resource values."));
    }
}
