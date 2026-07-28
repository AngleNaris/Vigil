using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;

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
                "新增工具"));

        var content = await File.ReadAllTextAsync(Path.Combine(temp.Path, "autorun.yaml"));
        Assert.Contains("# keep-this-comment", content, StringComparison.Ordinal);
        Assert.Contains("custom_field: keep", content, StringComparison.Ordinal);
        Assert.Contains("name: \"added-tool\"", content, StringComparison.Ordinal);
        Assert.True(File.Exists(result.BackupPath));
        var loaded = await new ConfigurationLoader().LoadAsync(temp.Path);
        Assert.Equal(executable, loaded.GetTask("added-tool").Executable);
    }

    [Fact]
    public async Task AddsFirstTaskToEmptyTasksSequenceWithoutBom()
    {
        using var temp = new TempDirectory();
        var configPath = temp.Write("autorun.yaml", EmptyConfig);
        var executable = Environment.ProcessPath!;

        var result = await new ConfigurationFileEditor().AddWindowsTaskAsync(
            new ProjectPaths(temp.Path),
            new AddWindowsTaskRequest("first-tool", executable, Array.Empty<string>()));

        var content = await File.ReadAllTextAsync(configPath);
        Assert.Contains("tasks:\n  - name: \"first-tool\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("tasks: []", content, StringComparison.Ordinal);
        Assert.True(File.Exists(result.BackupPath));
        var loaded = await new ConfigurationLoader().LoadAsync(temp.Path);
        var task = Assert.Single(loaded.Tasks);
        Assert.Equal("first-tool", task.Name);
        Assert.Equal(executable, task.Executable);
        Assert.Equal(Path.GetFileName(executable), task.Health.Pattern);

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
