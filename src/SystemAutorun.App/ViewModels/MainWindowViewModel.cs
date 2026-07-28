using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly AgentClient _client;
    private readonly NativeCacheStore _cache;
    private readonly GuiSettingsStore _settings;
    private readonly ProjectPaths _paths;
    private readonly ConfigurationFileEditor _configurationEditor;

    [ObservableProperty]
    private TaskRowViewModel? selectedTask;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private FilterOption selectedStatusFilter;

    [ObservableProperty]
    private FilterOption selectedTypeFilter;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartMissingCommand))]
    private bool isBusy;

    [ObservableProperty]
    private bool autoRefresh;

    [ObservableProperty]
    private bool startMissingOnLaunch;

    [ObservableProperty]
    private bool isSelfStartupEnabled;

    [ObservableProperty]
    private string statusMessage = "就绪。";

    [ObservableProperty]
    private string countMessage = string.Empty;

    [ObservableProperty]
    private bool isEmpty = true;

    internal MainWindowViewModel(
        AgentClient client,
        NativeCacheStore cache,
        GuiSettingsStore settings,
        ProjectPaths paths,
        ConfigurationFileEditor configurationEditor)
    {
        _client = client;
        _cache = cache;
        _settings = settings;
        _paths = paths;
        _configurationEditor = configurationEditor;

        StatusFilters = new[]
        {
            new FilterOption("全部状态", string.Empty),
            new FilterOption("正常 OK", "OK"),
            new FilterOption("异常 FAIL", "FAIL"),
            new FilterOption("禁用 OFF", "OFF"),
        };
        TypeFilters = new[]
        {
            new FilterOption("全部类型", string.Empty),
            new FilterOption("Windows", "windows"),
            new FilterOption("WSL Boot", "wsl_boot"),
            new FilterOption("WSL systemd", "wsl_systemd"),
            new FilterOption("WSL command", "wsl_command"),
        };
        selectedStatusFilter = StatusFilters[0];
        selectedTypeFilter = TypeFilters[0];
        TasksView = CollectionViewSource.GetDefaultView(Tasks);
        TasksView.Filter = FilterTask;
    }

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = new();

    public ICollectionView TasksView { get; }

    public IReadOnlyList<FilterOption> StatusFilters { get; }

    public IReadOnlyList<FilterOption> TypeFilters { get; }

    public event EventHandler<string>? ErrorRequested;

    public event EventHandler<DoctorReport>? DoctorRequested;

    public event EventHandler<TaskStatusDetail>? DetailRequested;

    public event EventHandler? StartupManagerRequested;

    public event EventHandler? AddProgramRequested;

    public Func<string, bool>? ConfirmRequested { get; set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(true);
        StartMissingOnLaunch = settings.AutoStartMissingOnLaunch;
        var cached = await _cache.LoadAsync(cancellationToken).ConfigureAwait(true);
        if (cached.Count > 0)
        {
            ReplaceTasks(cached);
            StatusMessage = "已显示缓存，正在刷新。";
        }

        await RefreshAsync();
        if (StartMissingOnLaunch)
        {
            await StartMissingAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunGlobalCommand))]
    private async Task RefreshAsync()
    {
        await RunBusyAsync("正在刷新任务状态...", async () =>
        {
            var snapshots = await _client.SendAsync<TaskSnapshot[]>("list");
            ReplaceTasks(snapshots);
            await _cache.SaveAsync(snapshots);
            StatusMessage = "任务状态已刷新。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanOperateSelectedTask))]
    private async Task StartAsync() => await RunTaskCommandAsync("start", "启动");

    [RelayCommand(CanExecute = nameof(CanOperateSelectedTask))]
    private async Task StopAsync() => await RunTaskCommandAsync("stop", "停止");

    [RelayCommand(CanExecute = nameof(CanOperateSelectedTask))]
    private async Task RestartAsync() => await RunTaskCommandAsync("restart", "重启");

    [RelayCommand(CanExecute = nameof(CanOperateSelectedTask))]
    private async Task EnableAsync() => await RunTaskMutationAsync("enable", "启用");

    [RelayCommand(CanExecute = nameof(CanOperateSelectedTask))]
    private async Task DisableAsync() => await RunTaskMutationAsync("disable", "禁用");

    [RelayCommand(CanExecute = nameof(CanOperateSelectedTask))]
    private async Task ShowDetailAsync()
    {
        var selected = SelectedTask;
        if (selected is null)
        {
            return;
        }

        await RunBusyAsync("正在读取详细状态...", async () =>
        {
            var detail = await _client.SendAsync<TaskStatusDetail>("status", selected.Name);
            DetailRequested?.Invoke(this, detail);
            StatusMessage = $"已读取 {selected.Name} 的详细状态。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanRunGlobalCommand))]
    private async Task DoctorAsync()
    {
        await RunBusyAsync("正在执行系统诊断...", async () =>
        {
            var report = await _client.SendAsync<DoctorReport>("doctor", timeout: TimeSpan.FromSeconds(60));
            DoctorRequested?.Invoke(this, report);
            StatusMessage = report.AllOk ? "系统诊断通过。" : $"系统诊断发现 {report.Failed} 项问题。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanRunGlobalCommand))]
    private async Task StartMissingAsync()
    {
        await RunBusyAsync("正在补启动未运行任务...", async () =>
        {
            var results = await _client.SendAsync<CommandResult[]>(
                "start-missing",
                timeout: TimeSpan.FromMinutes(5));
            var failed = results.Count(result => !result.Succeeded);
            StatusMessage = failed == 0
                ? $"补启动完成，共处理 {results.Length} 项。"
                : $"补启动完成，{failed} 项失败。";
            await RefreshCoreAsync();
        });
    }

    [RelayCommand]
    private void OpenStartupManager() => StartupManagerRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void AddProgram() => AddProgramRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(CanOperateSelectedTask))]
    private async Task RemoveTaskAsync()
    {
        var selected = SelectedTask;
        if (selected is null
            || !(ConfirmRequested?.Invoke(
                $"确定将任务“{selected.Name}”移出列表吗？正在运行的进程不会被停止。") ?? false))
        {
            return;
        }

        await RunBusyAsync($"正在移除 {selected.Name}...", async () =>
        {
            _ = await _configurationEditor.RemoveTaskAsync(_paths, selected.Name);
            StatusMessage = $"{selected.Name} 已移出配置；现有进程未停止。";
            await RefreshCoreAsync();
        });
    }

    internal async Task<bool> AddWindowsTaskAsync(AddWindowsTaskRequest request)
    {
        var succeeded = false;
        await RunBusyAsync($"正在添加 {request.Name}...", async () =>
        {
            _ = await _configurationEditor.AddWindowsTaskAsync(_paths, request);
            StatusMessage = $"已添加任务 {request.Name}。";
            await RefreshCoreAsync();
            succeeded = true;
        });
        return succeeded;
    }

    [RelayCommand]
    private void EditConfig() => Process.Start(new ProcessStartInfo
    {
        FileName = _paths.ConfigPath,
        UseShellExecute = true,
    });

    [RelayCommand]
    private void OpenLogs()
    {
        var directory = Path.Combine(_paths.RootDirectory, "logs");
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
        });
    }

    internal async Task SaveSettingsAsync()
    {
        await _settings.SaveAsync(new GuiSettings
        {
            AutoStartMissingOnLaunch = StartMissingOnLaunch,
        });
    }

    partial void OnSelectedTaskChanged(TaskRowViewModel? value)
    {
        _ = value;
        NotifyCommandStates();
    }

    partial void OnSearchTextChanged(string value)
    {
        _ = value;
        RefreshFilter();
    }

    partial void OnSelectedStatusFilterChanged(FilterOption value)
    {
        _ = value;
        RefreshFilter();
    }

    partial void OnSelectedTypeFilterChanged(FilterOption value)
    {
        _ = value;
        RefreshFilter();
    }

    partial void OnStartMissingOnLaunchChanged(bool value) => _ = SaveSettingsAsync();

    private bool CanOperateSelectedTask() => SelectedTask is not null && !IsBusy;

    private bool CanRunGlobalCommand() => !IsBusy;

    private async Task RunTaskCommandAsync(string command, string action)
    {
        var selected = SelectedTask;
        if (selected is null)
        {
            return;
        }

        await RunBusyAsync($"正在{action} {selected.Name}...", async () =>
        {
            var result = await _client.SendAsync<CommandResult>(
                command,
                selected.Name,
                timeout: TimeSpan.FromMinutes(2));
            StatusMessage = result.Succeeded
                ? $"{selected.Name} {action}成功。"
                : $"{selected.Name} {action}失败：{result.Message}";
            if (!result.Succeeded)
            {
                ErrorRequested?.Invoke(this, result.Message);
            }

            await RefreshCoreAsync();
        });
    }

    private async Task RunTaskMutationAsync(string command, string action)
    {
        var selected = SelectedTask;
        if (selected is null)
        {
            return;
        }

        await RunBusyAsync($"正在{action} {selected.Name}...", async () =>
        {
            _ = await _client.SendAsync<TaskSnapshot>(command, selected.Name);
            StatusMessage = $"{selected.Name} 已{action}。";
            await RefreshCoreAsync();
        });
    }

    private async Task RunBusyAsync(string busyMessage, Func<Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = busyMessage;
        NotifyCommandStates();
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            StatusMessage = $"操作失败：{exception.Message}";
            ErrorRequested?.Invoke(this, exception.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyCommandStates();
        }
    }

    private async Task RefreshCoreAsync()
    {
        var snapshots = await _client.SendAsync<TaskSnapshot[]>("list");
        ReplaceTasks(snapshots);
        await _cache.SaveAsync(snapshots);
    }

    private void ReplaceTasks(IReadOnlyList<TaskSnapshot> snapshots)
    {
        var selectedName = SelectedTask?.Name;
        Tasks.Clear();
        foreach (var snapshot in snapshots.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Tasks.Add(new TaskRowViewModel(snapshot));
        }

        TasksView.Refresh();
        SelectedTask = Tasks.FirstOrDefault(task =>
            string.Equals(task.Name, selectedName, StringComparison.OrdinalIgnoreCase))
            ?? Tasks.FirstOrDefault();
        UpdateCounts();
    }

    private bool FilterTask(object item)
    {
        if (item is not TaskRowViewModel task)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SelectedStatusFilter.Value)
            && !string.Equals(task.Status, SelectedStatusFilter.Value, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SelectedTypeFilter.Value)
            && !string.Equals(task.Type, SelectedTypeFilter.Value, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var query = SearchText.Trim();
        return task.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || task.Type.Contains(query, StringComparison.OrdinalIgnoreCase)
            || task.Reason.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || task.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RefreshFilter()
    {
        TasksView.Refresh();
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        var visible = TasksView.Cast<object>().OfType<TaskRowViewModel>().ToArray();
        IsEmpty = visible.Length == 0;
        var ok = visible.Count(task => task.Status == "OK");
        var fail = visible.Count(task => task.Status == "FAIL");
        var off = visible.Count(task => task.Status == "OFF");
        CountMessage = $"共 {visible.Length} 项  ·  OK {ok}  ·  FAIL {fail}  ·  OFF {off}";
    }

    private void NotifyCommandStates()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        EnableCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        ShowDetailCommand.NotifyCanExecuteChanged();
        RemoveTaskCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        DoctorCommand.NotifyCanExecuteChanged();
        StartMissingCommand.NotifyCanExecuteChanged();
    }
}
