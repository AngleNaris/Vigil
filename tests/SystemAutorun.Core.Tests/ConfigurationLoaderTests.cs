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
