using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

public sealed class ConfigurationLoaderTests
{
    [Fact]
    public async Task LoadsVersionOneAndAppliesLegacyLaunchDefaults()
    {
        using var temp = new TempDirectory();
        temp.Write("autorun.yaml", ValidConfig);

        var configuration = await new ConfigurationLoader().LoadAsync(temp.Path);

        Assert.Equal(1, configuration.Version);
        Assert.Equal(2, configuration.Tasks.Count);
        var windows = configuration.GetTask("worker");
        Assert.Equal(AutorunTaskType.Windows, windows.Type);
        Assert.Equal(RunAsMode.CurrentUser, windows.Launch.RunAs);
        Assert.Equal(LaunchTrigger.Logon, windows.Launch.Trigger);
        Assert.Equal(WindowMode.Hidden, windows.Launch.WindowMode);
        Assert.False(windows.ExpectedRunning);
        Assert.Equal(7, windows.StartupDelay);
        Assert.Equal(System.IO.Path.Combine(temp.Path, "logs"), configuration.Settings.LogDirectory);
    }

    [Fact]
    public async Task OverrideChangesEnabledWithoutMutatingYaml()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Write("autorun.yaml", ValidConfig);
        var before = await File.ReadAllTextAsync(configPath);

        await new OverrideStore().SetEnabledAsync(temp.Path, "worker", false);
        var configuration = await new ConfigurationLoader().LoadAsync(temp.Path);

        Assert.False(configuration.GetTask("worker").Enabled);
        Assert.Equal(before, await File.ReadAllTextAsync(configPath));
        var overrideBytes = await File.ReadAllBytesAsync(System.IO.Path.Combine(temp.Path, "autorun.overrides.json"));
        Assert.False(overrideBytes.Length >= 3
            && overrideBytes[0] == 0xEF
            && overrideBytes[1] == 0xBB
            && overrideBytes[2] == 0xBF);
    }

    [Fact]
    public async Task LoadsLegacyArrayOverridesWithoutRewritingFile()
    {
        using var temp = new TempDirectory();
        temp.Write("autorun.yaml", ValidConfig);
        var overridePath = temp.Write(
            "autorun.overrides.json",
            """
            [
              {
                "name": "worker",
                "enabled": false
              }
            ]
            """);
        var before = await File.ReadAllBytesAsync(overridePath);

        var configuration = await new ConfigurationLoader().LoadAsync(temp.Path);

        Assert.False(configuration.GetTask("worker").Enabled);
        Assert.Equal(before, await File.ReadAllBytesAsync(overridePath));
    }

    [Fact]
    public async Task UpdatingLegacyArrayOverridesMigratesWithoutLosingEntries()
    {
        using var temp = new TempDirectory();
        temp.Write("autorun.yaml", ValidConfig);
        var overridePath = temp.Write(
            "autorun.overrides.json",
            """
            [
              {
                "name": "worker",
                "enabled": false
              },
              {
                "name": "future-task",
                "enabled": true
              }
            ]
            """);

        await new OverrideStore().SetEnabledAsync(temp.Path, "worker", true);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(overridePath));
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.True(document.RootElement.GetProperty("worker").GetProperty("Enabled").GetBoolean());
        Assert.True(document.RootElement.GetProperty("future-task").GetProperty("Enabled").GetBoolean());
    }

    [Fact]
    public async Task ConcurrentOverridesPreserveEveryTaskUpdate()
    {
        using var temp = new TempDirectory();
        var writes = Enumerable.Range(0, 32)
            .Select(index => new OverrideStore().SetEnabledAsync(
                temp.Path,
                $"task-{index}",
                index % 2 == 0));

        await Task.WhenAll(writes);

        var overridePath = System.IO.Path.Combine(temp.Path, "autorun.overrides.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(overridePath));
        Assert.Equal(32, document.RootElement.EnumerateObject().Count());
        for (var index = 0; index < 32; index++)
        {
            Assert.Equal(
                index % 2 == 0,
                document.RootElement.GetProperty($"task-{index}").GetProperty("Enabled").GetBoolean());
        }
    }

    [Fact]
    public async Task LoadsEmptyTasksSequence()
    {
        using var temp = new TempDirectory();
        temp.Write(
            "autorun.yaml",
            """
            version: 1
            settings:
              default_startup_delay: 5
            tasks: []
            """);

        var configuration = await new ConfigurationLoader().LoadAsync(temp.Path);

        Assert.Empty(configuration.Tasks);
        Assert.Equal(5, configuration.Settings.DefaultStartupDelay);
    }

    [Fact]
    public async Task RejectsDependencyCycles()
    {
        using var temp = new TempDirectory();
        temp.Write("autorun.yaml", CycleConfig);

        var exception = await Assert.ThrowsAsync<ConfigurationException>(
            () => new ConfigurationLoader().LoadAsync(temp.Path));

        Assert.Contains("cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsMissingDependencies()
    {
        using var temp = new TempDirectory();
        temp.Write(
            "autorun.yaml",
            ValidConfig.Replace("depends_on: ['worker']", "depends_on: ['missing']", StringComparison.Ordinal));

        var exception = await Assert.ThrowsAsync<ConfigurationException>(
            () => new ConfigurationLoader().LoadAsync(temp.Path));

        Assert.Contains("missing task", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsCorruptedOverridesInsteadOfSilentlyEnablingTasks()
    {
        using var temp = new TempDirectory();
        temp.Write("autorun.yaml", ValidConfig);
        temp.Write("autorun.overrides.json", "{ invalid-json");

        var exception = await Assert.ThrowsAsync<ConfigurationException>(
            () => new ConfigurationLoader().LoadAsync(temp.Path));

        Assert.Contains("overrides", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadsExplicitExpectedRunningFlag()
    {
        using var temp = new TempDirectory();
        temp.Write(
            "autorun.yaml",
            ValidConfig.Replace(
                "enabled: true",
                "enabled: true\n    expected_running: true",
                StringComparison.Ordinal));

        var configuration = await new ConfigurationLoader().LoadAsync(temp.Path);

        Assert.True(configuration.GetTask("worker").ExpectedRunning);
    }

    private const string ValidConfig = """
        version: 1
        settings:
          log_dir: logs
          backup_dir: backups
          default_startup_delay: 7
        tasks:
          - name: worker
            type: windows
            enabled: true
            exec: C:\Windows\System32\cmd.exe
            health:
              type: process
              pattern: cmd.exe
          - name: wsl
            type: wsl_boot
            enabled: true
            distro: Ubuntu
            depends_on: ['worker']
            health:
              type: wsl_running
              distro: Ubuntu
        """;

    private const string CycleConfig = """
        version: 1
        settings:
          log_dir: logs
          backup_dir: backups
        tasks:
          - name: first
            type: windows
            exec: C:\Windows\System32\cmd.exe
            depends_on: ['second']
            health:
              type: process
              pattern: cmd.exe
          - name: second
            type: wsl_boot
            distro: Ubuntu
            depends_on: ['first']
            health:
              type: wsl_running
              distro: Ubuntu
        """;
}
