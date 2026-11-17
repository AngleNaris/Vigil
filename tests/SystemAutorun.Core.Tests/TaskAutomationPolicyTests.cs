using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.Core.Tests;

public sealed class TaskAutomationPolicyTests
{
    public static IEnumerable<object?[]> Policies()
    {
        foreach (bool? starts in new bool?[] { null, false, true })
        foreach (bool? recovers in new bool?[] { null, false, true })
        foreach (var legacyStarts in new[] { false, true })
        foreach (var legacyRecovers in new[] { false, true })
        foreach (var tracked in new[] { false, true })
        { yield return [starts, recovers, legacyStarts, legacyRecovers, tracked]; }
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public void ExplicitChoicesAreIndependentAndOverrideLegacyPreferences(
        bool? starts, bool? recovers, bool legacyStarts, bool legacyRecovers, bool tracked)
    {
        var baseline = new AutorunTask("worker", AutorunTaskType.Windows, true, "", "worker.exe", [], null,
            null, null, null, [], 0, new(HealthCheckType.Process, "worker.exe", null, null, 10, null, null),
            LaunchOptions.LegacyDefault(AutorunTaskType.Windows), tracked);
        var task = baseline with { StartOnAppLaunch = starts, AutoRecover = recovers };
        var preferences = new ApplicationSettings { AutoStartMissingOnLaunch = legacyStarts, AutoRecoverFailedTasks = legacyRecovers };
        Assert.Equal(starts ?? legacyStarts, TaskAutomationPolicy.StartsOnAppLaunch(task, preferences));
        Assert.Equal(recovers ?? (legacyRecovers && tracked), TaskAutomationPolicy.Recovers(task, preferences));
        var snapshot = TaskSnapshot.From(task, HealthResult.Fail("process-not-found", "missing"));
        Assert.Equal(TaskAutomationPolicy.StartsOnAppLaunch(task, preferences), TaskAutomationPolicy.StartsOnAppLaunch(snapshot, preferences));
        Assert.Equal(TaskAutomationPolicy.Recovers(task, preferences), TaskAutomationPolicy.Recovers(snapshot, preferences));
        Assert.Equal(MonitoringTaskIdentity.For(baseline), MonitoringTaskIdentity.For(task));
    }

    [Fact]
    public async Task TwoChoicesCommitTogetherWithoutChangingLegacyFlagsOrUnrelatedTasks()
    {
        using var temp = new TempDirectory();
        temp.Write("autorun.yaml", Config);
        var workspace = new ConfigurationWorkspace(new ProjectPaths(temp.Path));
        var before = await workspace.ReadAsync();
        Assert.Null(before.Configuration.GetTask("worker").StartOnAppLaunch);
        Assert.Null(before.Configuration.GetTask("worker").AutoRecover);
        var mutation = await workspace.SetAutomationAsync("worker", new(false, true), before.Revision);
        var after = await workspace.ReadAsync();
        Assert.Equal(mutation.Revision, after.Revision);
        Assert.False(after.Configuration.GetTask("worker").StartOnAppLaunch);
        Assert.True(after.Configuration.GetTask("worker").AutoRecover);
        Assert.True(after.Configuration.GetTask("worker").ExpectedRunning);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before.Configuration.GetTask("second")),
            System.Text.Json.JsonSerializer.Serialize(after.Configuration.GetTask("second")));
        var text = await File.ReadAllTextAsync(new ProjectPaths(temp.Path).ConfigPath);
        Assert.Contains("# preserve-policy-comment", text, StringComparison.Ordinal);
        Assert.Contains("custom_field: keep", text, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ConfigurationConflictException>(() =>
            workspace.SetAutomationAsync("worker", new(true, false), before.Revision));
        Assert.Equal(text, await File.ReadAllTextAsync(new ProjectPaths(temp.Path).ConfigPath));
    }

    [Fact]
    public async Task AddWritesExplicitChoicesAndEmptyPatchIsRejected()
    {
        using var temp = new TempDirectory();
        temp.Write("autorun.yaml", Config);
        var paths = new ProjectPaths(temp.Path);
        var editor = new ConfigurationFileEditor();
        await editor.AddWindowsTaskAsync(paths, new("added", @"C:\Windows\System32\cmd.exe", [],
            StartOnAppLaunch: true, AutoRecover: false));
        var added = (await new ConfigurationLoader().LoadAsync(temp.Path)).GetTask("added");
        Assert.True(added.StartOnAppLaunch);
        Assert.False(added.AutoRecover);
        await Assert.ThrowsAsync<ArgumentException>(() => editor.SetAutomationAsync(paths, "worker", new()));
        Assert.True(AgentProtocol.IsConfigurationMutation("task-automation"));
        Assert.True(AgentProtocol.IsMutation("start-on-app-launch"));
    }

    private const string Config = """
        version: 1
        settings:
          log_dir: logs
          backup_dir: backups
          custom_field: keep
        tasks:
          # preserve-policy-comment
          - name: worker
            type: windows
            enabled: true
            expected_running: true
            exec: C:\Windows\System32\cmd.exe
            health: { type: process, pattern: cmd.exe }
          - name: second
            type: windows
            enabled: true
            start_on_app_launch: false
            auto_recover: false
            exec: C:\Windows\System32\notepad.exe
            health: { type: process, pattern: notepad.exe }
        """;
}
