using System.IO;
using SystemAutorun.Agent;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.IntegrationTests;

public sealed class TaskOrchestratorTests
{
    [Fact]
    public async Task DisabledWorkloadsUseLocalizedStatusDetail()
    {
        var root = CreateRoot();
        try
        {
            await WriteConfigurationAsync(
                root,
                """
                version: 1
                settings:
                  default_startup_delay: 0
                tasks:
                  - name: disabled-workload
                    type: windows
                    enabled: false
                    exec: C:\Windows\System32\notepad.exe
                    working_dir: C:\Windows\System32
                    health:
                      type: process
                      pattern: notepad.exe
                """);
            var orchestrator = CreateOrchestrator(root);

            var snapshot = Assert.Single(await orchestrator.ListAsync());

            Assert.Equal("OFF", snapshot.Status);
            Assert.Equal("工作负载已禁用。", snapshot.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DoctorChecksConfiguredWindowsServicesWithoutRequiringWsl()
    {
        var root = CreateRoot();
        try
        {
            var missingService = $"SystemAutorun-Missing-{Guid.NewGuid():N}";
            await WriteConfigurationAsync(
                root,
                $$"""
                version: 1
                settings:
                  default_startup_delay: 0
                tasks:
                  - name: event-log
                    type: windows_service
                    enabled: false
                    service: EventLog
                    health:
                      type: windows_service
                  - name: missing-service
                    type: windows_service
                    enabled: false
                    service: {{missingService}}
                    health:
                      type: windows_service
                """,
                new System.Text.UTF8Encoding(false));
            var orchestrator = CreateOrchestrator(root);

            var report = await orchestrator.DoctorAsync();

            Assert.DoesNotContain(report.Checks, check => check.Check == "wsl.exe");
            var existing = Assert.Single(
                report.Checks,
                check => check.Check == "Windows service: event-log");
            Assert.True(existing.Ok, existing.Detail);
            Assert.Contains("EventLog", existing.Detail, StringComparison.Ordinal);
            var missing = Assert.Single(
                report.Checks,
                check => check.Check == "Windows service: missing-service");
            Assert.False(missing.Ok);
            Assert.Contains(missingService, missing.Detail, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "SystemAutorun.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static Task WriteConfigurationAsync(
        string root,
        string configuration,
        System.Text.Encoding? encoding = null) =>
        File.WriteAllTextAsync(
            Path.Combine(root, "autorun.yaml"),
            configuration,
            encoding ?? new System.Text.UTF8Encoding(false));

    private static TaskOrchestrator CreateOrchestrator(string root) =>
        new(
            new ConfigurationCache(new ConfigurationLoader(), new ProjectPaths(root)),
            new OverrideStore(),
            new NoopExecutor(),
            new NoopHealthProbe(),
            new FileLog());

    private sealed class NoopHealthProbe : IHealthProbe
    {
        public Task<HealthResult> CheckAsync(
            AutorunTask task,
            bool quick,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(HealthResult.Ok("unused", "unused"));
    }

    private sealed class NoopExecutor : ITaskExecutor
    {
        public Task<CommandResult> StartAsync(
            AutorunTask task,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandResult(true, "unused"));

        public Task<CommandResult> StopAsync(
            AutorunTask task,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandResult(true, "unused"));

        public IReadOnlyList<int> FindProcessIds(AutorunTask task) => Array.Empty<int>();
    }
}
