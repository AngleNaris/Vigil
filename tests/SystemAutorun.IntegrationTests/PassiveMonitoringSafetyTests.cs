using System.IO;
using System.Text.Json;
using SystemAutorun.Agent;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.IntegrationTests;

public sealed class PassiveMonitoringSafetyTests
{
    [Theory]
    [InlineData(HealthCheckType.SystemdActive, AutorunTaskType.WslSystemd)]
    [InlineData(HealthCheckType.WslProcess, AutorunTaskType.WslCommand)]
    public async Task PassiveGuestHealthDoesNotExecuteWslAndCannotReportAFault(
        HealthCheckType healthType, AutorunTaskType taskType)
    {
        // No running/stopped distro is required or touched. This path must never invoke wsl.exe.
        var task = GuestTask(healthType, taskType);
        var health = await new WindowsHealthProbe().CheckPassiveAsync(task);
        Assert.False(health.Observed);
        Assert.False(health.Healthy);
        Assert.Equal("passive-wsl-probe-unavailable", health.Reason);
        var snapshot = TaskSnapshot.From(task, health);
        Assert.Equal("UNKNOWN", snapshot.Status);
        Assert.False(snapshot.HealthObserved);
        Assert.Equal("OFF", TaskSnapshot.From(task with { Enabled = false }, health).Status);
    }

    [Fact]
    public async Task ReadRpcsUsePassiveProbeAndDoNotBorrowDistroKeepaliveProcesses()
    {
        var probe = new PassiveOnlyProbe();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(probe);
        await File.WriteAllTextAsync(fixture.Paths.ConfigPath, """
            version: 1
            settings:
              default_startup_delay: 0
            tasks:
              - name: passive-guest
                type: wsl_systemd
                distro: fixture-must-never-run
                service: fixture.service
                expected_running: true
                health:
                  type: systemd_active
                  distro: fixture-must-never-run
                  service: fixture.service
            """);
        var listed = Assert.Single(await fixture.Client.SendAsync<TaskSnapshot[]>("list"));
        Assert.Equal("UNKNOWN", listed.Status);
        Assert.False(listed.HealthObserved);
        var detail = await fixture.Client.SendAsync<TaskStatusDetail>("status", "passive-guest");
        Assert.False(detail.Health.Observed);
        Assert.Empty(detail.Processes);
        var process = Assert.Single(await fixture.Client.SendAsync<ManagedProcessSnapshot[]>("process-monitor"));
        Assert.Equal("UNKNOWN", process.State);
        Assert.False(process.HealthObserved);
        Assert.Empty(process.Processes);
        Assert.Equal(3, probe.PassiveCalls);
    }

    [Fact]
    public async Task BackgroundObserverReusesThisCycleHealthWithoutActivatingOrReprobingGuest()
    {
        var probe = new PassiveOnlyProbe();
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(probe);
        var task = GuestTask(HealthCheckType.SystemdActive, AutorunTaskType.WslSystemd);
        var configuration = new AutorunConfiguration(1,
            new AutorunSettings(Path.Combine(fixture.Root, "logs"), Path.Combine(fixture.Root, "backups"), 0), [task], fixture.Root);
        var orchestrator = new TaskOrchestrator(fixture.Cache, new OverrideStore(), new NoExecution(), probe, new FileLog());
        using var host = new HostStatusReader();
        var source = new WindowsAgentMonitoringSource(orchestrator, host);
        var health = await source.ObserveTasksAsync(configuration, default);
        var resources = await source.ObserveProcessesAsync(configuration, health, default);
        Assert.Equal(1, probe.PassiveCalls);
        Assert.False(Assert.Single(health).HealthObserved);
        var process = Assert.Single(resources);
        Assert.False(process.HealthObserved);
        Assert.Empty(process.Processes);
        Assert.NotNull(process.TaskIdentity);
    }

    private sealed class NoExecution : ITaskExecutor
    {
        public Task<CommandResult> StartAsync(AutorunTask task, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No execution.");
        public Task<CommandResult> StopAsync(AutorunTask task, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No execution.");
        public IReadOnlyList<int> FindProcessIds(AutorunTask task) => throw new InvalidOperationException("No guest keepalive attribution.");
    }

    [Fact]
    public async Task CancelledPassiveProbeDoesNotRunAnyCommand()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsHealthProbe()
            .CheckPassiveAsync(GuestTask(HealthCheckType.SystemdActive, AutorunTaskType.WslSystemd), cancelled.Token));
    }

    [Fact]
    public void OlderHealthJsonKeepsObservedDefaultAndUnknownRoundTripsExplicitly()
    {
        var older = JsonSerializer.Deserialize<HealthResult>("""{"healthy":false,"reason":"test","detail":"test"}""", AgentProtocol.JsonOptions)!;
        Assert.True(older.Observed);
        var unknown = HealthResult.Unknown("unavailable", "no safe source");
        Assert.Equal(unknown, JsonSerializer.Deserialize<HealthResult>(
            JsonSerializer.Serialize(unknown, AgentProtocol.JsonOptions), AgentProtocol.JsonOptions));
    }

    private static AutorunTask GuestTask(HealthCheckType healthType, AutorunTaskType taskType) => new(
        "passive-guest", taskType, true, "isolated passive probe fixture", null, [], null,
        "fixture-must-never-run", "fixture.service", null, [], 0,
        new HealthCheckDefinition(healthType, "fixture-pattern", null, null, 5,
            "fixture-must-never-run", "fixture.service"),
        LaunchOptions.LegacyDefault(taskType), ExpectedRunning: true);

    private sealed class PassiveOnlyProbe : IHealthProbe
    {
        public int PassiveCalls { get; private set; }
        public Task<HealthResult> CheckAsync(AutorunTask task, bool quick, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A read RPC attempted an activating probe.");
        public Task<HealthResult> CheckPassiveAsync(AutorunTask task, CancellationToken cancellationToken = default)
        {
            PassiveCalls++;
            return Task.FromResult(HealthResult.Unknown("fixture-passive-unavailable", "No guest commands executed."));
        }
    }
}
