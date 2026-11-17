using CommunityToolkit.Mvvm.Input;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.App.ViewModels;

public partial class MainWindowViewModel
{
    private int _appLaunchChecked;

    // Read choices from the revisioned configuration, not a possibly older health frame.
    private AutorunTask? SelectedAutomationTask => SelectedTask is { } row
        ? _monitoringConfiguration?.Configuration.Tasks.FirstOrDefault(task =>
            string.Equals(task.Name, row.Name, StringComparison.OrdinalIgnoreCase)
            && MonitoringTaskIdentity.For(task) == MonitoringTaskIdentity.For(row.Snapshot))
        : null;

    public bool SelectedStartOnAppLaunch => SelectedAutomationTask is { } task && _monitoringConfiguration is { } configuration
        && TaskAutomationPolicy.StartsOnAppLaunch(task, configuration.Preferences);

    public bool SelectedAutoRecover => SelectedAutomationTask is { } task && _monitoringConfiguration is { } configuration
        && TaskAutomationPolicy.Recovers(task, configuration.Preferences);

    private bool CanSetTaskAutomation() => IsLocalHostSelected && !IsBusy && SelectedAutomationTask is
        { Launch.RunAs: RunAsMode.CurrentUser, Launch.Trigger: LaunchTrigger.Logon };

    [RelayCommand(CanExecute = nameof(CanSetTaskAutomation))]
    private Task ToggleTaskAppLaunchAsync() => SetTaskAutomationAsync(new(StartOnAppLaunch: !SelectedStartOnAppLaunch));

    [RelayCommand(CanExecute = nameof(CanSetTaskAutomation))]
    private Task ToggleTaskAutoRecoverAsync() => SetTaskAutomationAsync(new(AutoRecover: !SelectedAutoRecover));

    private async Task SetTaskAutomationAsync(TaskAutomationPatch patch)
    {
        if (!CanSetTaskAutomation() || SelectedAutomationTask is not { } task) { return; }
        await RunBusyAsync($"正在保存 {task.Name} 的启动策略…", async () =>
        {
            _ = await _configuration.MutateAsync<ConfigurationMutationResult>("task-automation", task.Name, patch);
            await RefreshAfterConfigurationChangeAsync($"已保存 {task.Name} 的启动策略；手动停止记录保持不变。");
        });
    }

    private async Task CheckAppLaunchTasksAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _appLaunchChecked, 1) != 0 || _monitoringConfiguration is not { } configuration
            || !configuration.Configuration.Tasks.Any(task => TaskAutomationPolicy.StartsOnAppLaunch(task, configuration.Preferences)))
        { return; }
        await RunBusyAsync("正在检查应用启动策略…", async () =>
        {
            var results = await _client.SendAsync<CommandResult[]>("start-on-app-launch",
                timeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken);
            var failed = results.Where(result => !result.Succeeded).ToArray();
            await RefreshDashboardCoreAsync();
            StatusMessage = failed.Length == 0 ? $"应用启动策略检查完成，处理 {results.Length} 项。"
                : $"{failed.Length} 项未启动：{failed[0].Message}";
        });
    }

    private void NotifyTaskAutomation()
    {
        OnPropertyChanged(nameof(SelectedStartOnAppLaunch));
        OnPropertyChanged(nameof(SelectedAutoRecover));
        ToggleTaskAppLaunchCommand.NotifyCanExecuteChanged();
        ToggleTaskAutoRecoverCommand.NotifyCanExecuteChanged();
    }
}
