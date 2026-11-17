using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

public sealed class ConfigurationFileEditorTests
{
    [Fact]
    public async Task AddsTaskWithoutRewritingExistingCommentsOrUnknownFields()
    {
        using var temp = new TempDirectory();
        temp.Write("autorun.yaml", BaseConfig);
        var executable = Environment.ProcessPath!;
        var editor = new ConfigurationFileEditor();

        var result = await editor.AddWindowsTaskAsync(
            new ProjectPaths(temp.Path),
            new AddWindowsTaskRequest(
                "added-tool",
                executable,
                new[] { "--background", "two words" },
                "新增工具",
                WindowMode: WindowMode.Normal));

        var content = await File.ReadAllTextAsync(Path.Combine(temp.Path, "autorun.yaml"));
        Assert.Contains("# keep-this-comment", content, StringComparison.Ordinal);
        Assert.Contains("custom_field: keep", content, StringComparison.Ordinal);
        Assert.Contains("name: \"added-tool\"", content, StringComparison.Ordinal);
        Assert.Contains("window_mode: normal", content, StringComparison.Ordinal);
        Assert.Contains("expected_running: true", content, StringComparison.Ordinal);
        Assert.True(File.Exists(result.BackupPath));
        var loaded = await new ConfigurationLoader().LoadAsync(temp.Path);
        Assert.Equal(executable, loaded.GetTask("added-tool").Executable);
        Assert.Equal(WindowMode.Normal, loaded.GetTask("added-tool").Launch.WindowMode);
        Assert.True(loaded.GetTask("added-tool").ExpectedRunning);
    }

    [Fact]
    public async Task AddsFirstTaskToEmptyTasksSequenceWithoutBom()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Write("autorun.yaml", EmptyConfig + Environment.NewLine);
        var executable = Environment.ProcessPath!;

        var result = await new ConfigurationFileEditor().AddWindowsTaskAsync(
            new ProjectPaths(temp.Path),
            new AddWindowsTaskRequest("first-tool", executable, Array.Empty<string>()));

        var content = await File.ReadAllTextAsync(configPath);
        var normalizedContent = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("tasks:\n  - name: \"first-tool\"", normalizedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("tasks: []", content, StringComparison.Ordinal);
        var tasksLine = content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("tasks:", StringComparison.Ordinal));
        Assert.Equal("tasks:", tasksLine);
        Assert.True(File.Exists(result.BackupPath));
        var loaded = await new ConfigurationLoader().LoadAsync(temp.Path);
        var task = Assert.Single(loaded.Tasks);
        Assert.Equal("first-tool", task.Name);
        Assert.Equal(executable, task.Executable);
        Assert.Equal(Path.GetFileName(executable), task.Health.Pattern);
        Assert.Equal(WindowMode.Hidden, task.Launch.WindowMode);
        Assert.Contains("window_mode: hidden", content, StringComparison.Ordinal);

        var bytes = await File.ReadAllBytesAsync(configPath);
        Assert.False(bytes.Length >= 3
            && bytes[0] == 0xEF
            && bytes[1] == 0xBB
            && bytes[2] == 0xBF);
    }

    [Fact]
    public async Task RemovesOneTaskAndPreservesFollowingTask()
    {
        using var temp = new TempDirectory();
        temp.Write("autorun.yaml", BaseConfig);
        var editor = new ConfigurationFileEditor();

        _ = await editor.RemoveTaskAsync(new ProjectPaths(temp.Path), "worker");

        var content = await File.ReadAllTextAsync(Path.Combine(temp.Path, "autorun.yaml"));
        Assert.Contains("# keep-this-comment", content, StringComparison.Ordinal);
        var loaded = await new ConfigurationLoader().LoadAsync(temp.Path);
        Assert.DoesNotContain(loaded.Tasks, task => task.Name == "worker");
        Assert.Equal("second", Assert.Single(loaded.Tasks).Name);
    }

    [Fact]
    public async Task RejectsRemovalWhenTaskHasDependents()
    {
        using var temp = new TempDirectory();
        temp.Write(
            "autorun.yaml",
            BaseConfig.Replace(
                "name: second",
                "name: second\n    depends_on: ['worker']",
                StringComparison.Ordinal));

        var exception = await Assert.ThrowsAsync<ConfigurationException>(() =>
            new ConfigurationFileEditor().RemoveTaskAsync(new ProjectPaths(temp.Path), "worker"));

        Assert.Contains("required by", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddsWslSystemdTaskWithDependencyAndTcpHealth()
    {
        using var temp = new TempDirectory();
        temp.Write("autorun.yaml", BaseConfig);
        var editor = new ConfigurationFileEditor();

        _ = await editor.AddTaskAsync(
            new ProjectPaths(temp.Path),
            new AddTaskRequest(
                "home-dashboard",
                AutorunTaskType.WslSystemd,
                "家庭服务器仪表盘",
                Distro: "Ubuntu",
                Service: "home-dashboard.service",
                DependsOn: new[] { "worker" },
                StartupDelay: 12,
                HealthType: HealthCheckType.Tcp,
                HealthHost: "127.0.0.1",
                HealthPort: 8080,
                HealthTimeoutSeconds: 45,
                Launch: new LaunchOptions(
                    RunAsMode.CurrentUser,
                    LaunchTrigger.Logon,
                    WindowMode.NoConsole,
                    ProcessLifecycle.Tracked)));

        var content = await File.ReadAllTextAsync(Path.Combine(temp.Path, "autorun.yaml"));
        Assert.Contains("type: wsl_systemd", content, StringComparison.Ordinal);
        Assert.Contains("depends_on: [\"worker\"]", content, StringComparison.Ordinal);
        Assert.Contains("type: tcp", content, StringComparison.Ordinal);
        Assert.Contains("port: 8080", content, StringComparison.Ordinal);
        Assert.Contains("timeout: 45", content, StringComparison.Ordinal);

        var loaded = await new ConfigurationLoader().LoadAsync(temp.Path);
        var task = loaded.GetTask("home-dashboard");
        Assert.Equal(AutorunTaskType.WslSystemd, task.Type);
        Assert.Equal("Ubuntu", task.Distro);
        Assert.Equal("home-dashboard.service", task.Service);
        Assert.Equal(new[] { "worker" }, task.DependsOn);
        Assert.Equal(HealthCheckType.Tcp, task.Health.Type);
        Assert.Equal(8080, task.Health.Port);
    }

    [Fact]
    public async Task AddsWslCommandWithProcessHealth()
    {
        using var temp = new TempDirectory();
        temp.Write("autorun.yaml", EmptyConfig);

        _ = await new ConfigurationFileEditor().AddTaskAsync(
            new ProjectPaths(temp.Path),
            new AddTaskRequest(
                "media-indexer",
                AutorunTaskType.WslCommand,
                "媒体索引",
                Distro: "Debian",
                Command: "/opt/indexer/start.sh",
                HealthType: HealthCheckType.WslProcess,
                HealthPattern: "indexer",
                Launch: LaunchOptions.LegacyDefault(AutorunTaskType.WslCommand)));

        var loaded = await new ConfigurationLoader().LoadAsync(temp.Path);
        var task = Assert.Single(loaded.Tasks);
        Assert.Equal("/opt/indexer/start.sh", task.Command);
        Assert.Equal(HealthCheckType.WslProcess, task.Health.Type);
        Assert.Equal("indexer", task.Health.Pattern);
        Assert.Equal("Debian", task.Health.Distro);
    }

    [Fact]
    public async Task AddsWindowsServiceTask()
    {
        using var temp = new TempDirectory();
        temp.Write("autorun.yaml", EmptyConfig);

        _ = await new ConfigurationFileEditor().AddTaskAsync(
            new ProjectPaths(temp.Path),
            new AddTaskRequest(
                "file-sharing",
                AutorunTaskType.WindowsService,
                "Windows 文件共享",
                Service: "LanmanServer",
                HealthType: HealthCheckType.WindowsService,
                HealthTimeoutSeconds: 20,
                Launch: new LaunchOptions(
                    RunAsMode.System,
                    LaunchTrigger.Boot,
                    WindowMode.NoConsole,
                    ProcessLifecycle.Service)));

        var content = await File.ReadAllTextAsync(Path.Combine(temp.Path, "autorun.yaml"));
        Assert.Contains("type: windows_service", content, StringComparison.Ordinal);
        Assert.Contains("service: \"LanmanServer\"", content, StringComparison.Ordinal);
        Assert.Contains("lifecycle: service", content, StringComparison.Ordinal);
        Assert.Contains("type: windows_service", content, StringComparison.Ordinal);

        var task = Assert.Single((await new ConfigurationLoader().LoadAsync(temp.Path)).Tasks);
        Assert.Equal(AutorunTaskType.WindowsService, task.Type);
        Assert.Equal(HealthCheckType.WindowsService, task.Health.Type);
        Assert.Equal(ProcessLifecycle.Service, task.Launch.Lifecycle);
    }

    [Fact]
    public async Task TogglesExpectedRunningWithoutRewritingUnrelatedContent()
    {
        using var temp = new TempDirectory();
        temp.Write("autorun.yaml", BaseConfig);
        var editor = new ConfigurationFileEditor();

        _ = await editor.SetExpectedRunningAsync(
            new ProjectPaths(temp.Path),
            "worker",
            true);

        var enabled = await File.ReadAllTextAsync(Path.Combine(temp.Path, "autorun.yaml"));
        Assert.Contains("# keep-this-comment", enabled, StringComparison.Ordinal);
        Assert.Contains("custom_field: keep", enabled, StringComparison.Ordinal);
        Assert.Contains("expected_running: true", enabled, StringComparison.Ordinal);
        Assert.True((await new ConfigurationLoader().LoadAsync(temp.Path))
            .GetTask("worker").ExpectedRunning);

        _ = await editor.SetExpectedRunningAsync(
            new ProjectPaths(temp.Path),
            "worker",
            false);

        var disabled = await File.ReadAllTextAsync(Path.Combine(temp.Path, "autorun.yaml"));
        Assert.Contains("expected_running: false", disabled, StringComparison.Ordinal);
        Assert.False((await new ConfigurationLoader().LoadAsync(temp.Path))
            .GetTask("worker").ExpectedRunning);
    }

    private const string BaseConfig = """
        version: 1
        settings:
          log_dir: logs
          backup_dir: backups
          default_startup_delay: 5
          custom_field: keep
        tasks:
          # keep-this-comment
          - name: worker
            type: windows
            enabled: true
            exec: C:\Windows\System32\cmd.exe
            health:
              type: process
              pattern: cmd.exe
          - name: second
            type: wsl_boot
            enabled: true
            distro: Ubuntu
            health:
              type: wsl_running
              distro: Ubuntu
        """;

    private const string EmptyConfig = """
        version: 1
        settings:
          default_startup_delay: 5
        tasks: []
        """;
}
