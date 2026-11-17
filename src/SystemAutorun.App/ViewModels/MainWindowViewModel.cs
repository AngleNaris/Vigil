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
using SystemAutorun.Core.Services;

namespace SystemAutorun.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public const string OverviewPage = "overview";
    public const string WorkloadsPage = "workloads";
    public const string ManagedProcessesPage = "managed-processes";
    public const string ExceptionLogsPage = "exception-logs";

    private readonly AgentClient _client;
    private readonly NativeCacheStore _cache;
    private readonly AgentConfigurationClient _configuration;
    private readonly ProjectPaths _paths;
    private readonly Func<ProcessStartInfo, Process?> _openLogDirectory;
    private Task _settingsSaveTask = Task.CompletedTask;
    private int _settingsGeneration;
    private int _startSettingsVersion;
    private int _recoverySettingsVersion;
    private readonly IRemoteHostReader _remoteHostReader;
    private readonly HashSet<string> _knownFailures =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, NetworkStatusSnapshot> _lastNetworks =
        new Dictionary<string, NetworkStatusSnapshot>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<RemoteHostDefinition> _remoteHostDefinitions =
        Array.Empty<RemoteHostDefinition>();
    private HostStatusSnapshot? _lastLocalHostSnapshot;
    private DateTimeOffset? _lastHostCapture;
    private bool _notificationsEnabled;
    private bool _loadingSettings;
    private CancellationTokenSource? _refreshCancellation;
    private bool _nextRefreshAutomatic, _refreshIsAutomatic;
    private bool _presentationActive = true;
    private DateTimeOffset? _localResumeGraceUntilUtc;
    private DateTimeOffset? _remoteResumeGraceUntilUtc;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool isRefreshing;

    internal bool IsPresentationActive => _presentationActive;
    internal bool InLocalResumeGrace => _localResumeGraceUntilUtc is { } until && DateTimeOffset.UtcNow < until;
    internal bool InRemoteResumeGrace => _remoteResumeGraceUntilUtc is { } until && DateTimeOffset.UtcNow < until;

    internal void SetPresentationActive(bool active)
    {
        if (_presentationActive == active) { return; }
        _presentationActive = active;
        if (!active)
        {
            _localResumeGraceUntilUtc = null;
            _remoteResumeGraceUntilUtc = null;
            CancelPresentationReads();
        }
        else
        {
            if (_dashboardInitialized && AutoRefresh && !_dashboardDisposed)
            {
                var until = DateTimeOffset.UtcNow.AddSeconds(5);
                _localResumeGraceUntilUtc = until;
                _remoteResumeGraceUntilUtc = until;
            }
            TickDashboard();
        }
    }

    private void CancelPresentationReads()
    {
        _refreshCancellation?.Cancel();
        CancelLiveRead();
        _resourceForcePending = false; CancelResourceRead();
        CancelRemoteRefresh();
        _historyGeneration++;
        _historyCancellation?.Cancel();
        _historyCancellation = null;
    }

    [ObservableProperty]
    private TaskRowViewModel? selectedTask;

    [ObservableProperty]
    private ManagedProcessRowViewModel? selectedManagedProcess;

    [ObservableProperty]
    private ExceptionLogRowViewModel? selectedExceptionLog;

    [ObservableProperty]
    private HostTabViewModel? selectedHostTab;

    [ObservableProperty]
    private string currentPage = OverviewPage;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string managedProcessSearchText = string.Empty;

    [ObservableProperty]
    private string exceptionLogSearchText = string.Empty;

    [ObservableProperty]
    private FilterOption selectedStatusFilter;

    [ObservableProperty]
    private FilterOption selectedTypeFilter;

    [ObservableProperty]
    private FilterOption selectedExceptionLevelFilter;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartMissingCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleTaskAppLaunchCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleTaskAutoRecoverCommand))]
    private bool isBusy;

    [ObservableProperty]
    private bool autoRefresh = true;

    [ObservableProperty]
    private bool startMissingOnLaunch;

    [ObservableProperty]
    private bool autoRecoverFailedTasks;

    [ObservableProperty]
    private bool isSelfStartupEnabled;

    [ObservableProperty]
    private string statusMessage = "就绪。";

    [ObservableProperty]
    private string countMessage = string.Empty;

    [ObservableProperty]
    private bool isEmpty = true;

    [ObservableProperty]
    private int totalTaskCount;

    [ObservableProperty]
    private int okTaskCount;

    [ObservableProperty]
    private int failedTaskCount;

    [ObservableProperty]
    private int disabledTaskCount;

    [ObservableProperty]
    private string emptyStateTitle = "暂无启动任务";

    [ObservableProperty]
    private string emptyStateActionLabel = "添加任务";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostMemoryPercentText))]
    private bool hostStatusAvailable;

    [ObservableProperty]
    private string hostName = Environment.MachineName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostIcon))]
    private string hostOs = "正在读取 Windows 状态...";

    [ObservableProperty]
    private string hostUptime = "--";

    [ObservableProperty]
    private string hostLogicalProcessorsText = "--";

    [ObservableProperty]
    private string hostProcessCountText = "--";

    [ObservableProperty]
    private string hostThreadCountText = "--";

    [ObservableProperty]
    private string hostConnectionText = string.Empty;

    [ObservableProperty]
    private double cpuUsagePercent;

    [ObservableProperty]
    private string cpuUsageText = "--";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostMemoryPercentText))]
    private double memoryUsagePercent;

    [ObservableProperty]
    private string memoryUsageText = "--";

    [ObservableProperty]
    private double storageUsagePercent;

    [ObservableProperty]
    private string storageUsageText = "--";

    [ObservableProperty]
    private string storageDetail = "正在读取磁盘状态...";

    // R109: whole-machine used / total across every ready volume.
    [ObservableProperty]
    private string storageCapacityText = "--";

    [ObservableProperty]
    private string networkAddress = "--";

    [ObservableProperty]
    private string networkRateText = "--";

    [ObservableProperty]
    private string networkDetail = "正在读取网络状态...";

    [ObservableProperty]
    private string hostUpdatedText = "尚未刷新";

    [ObservableProperty]
    private string serverStatus = "OFF";

    [ObservableProperty]
    private string serverStatusDetail = "正在连接本机 Agent。";

    [ObservableProperty]
    private int managedProcessCount;

    [ObservableProperty]
    private int healthyManagedProcessCount;

    [ObservableProperty]
    private int failedManagedProcessCount;

    [ObservableProperty]
    private string managedProcessStatusText = "尚未读取常驻进程。";

    [ObservableProperty]
    private int exceptionLogCount;

    [ObservableProperty]
    private int criticalLogCount;

    [ObservableProperty]
    private int errorLogCount;

    [ObservableProperty]
    private int warningLogCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExceptionLogEmptyText))]
    private string exceptionLogStatusText = "尚未读取异常日志。";

    public string ExceptionLogEmptyText => ExceptionLogStatusText.StartsWith("异常日志读取失败", StringComparison.Ordinal)
        || ExceptionLogStatusText.StartsWith("尚未读取", StringComparison.Ordinal)
            ? ExceptionLogStatusText : "当前没有异常日志";

    internal MainWindowViewModel(
        AgentClient client,
        NativeCacheStore cache,
        AgentConfigurationClient configuration,
        ProjectPaths paths,
        IRemoteHostReader remoteHostReader,
        IDashboardMonitoringClient? monitoring = null,
        Func<ProcessStartInfo, Process?>? openLogDirectory = null)
    {
        _client = client;
        _cache = cache;
        _configuration = configuration;
        _paths = paths;
        _openLogDirectory = openLogDirectory ?? (info => Process.Start(info));
        _remoteHostReader = remoteHostReader;
        _monitoring = monitoring ?? new DashboardMonitoringClient(client);

        StatusFilters =
        [
            new FilterOption("全部状态", string.Empty),
            new FilterOption("正常 OK", "OK"),
            new FilterOption("异常 FAIL", "FAIL"),
            new FilterOption("禁用 OFF", "OFF"),
        ];
        TypeFilters =
        [
            new FilterOption("全部类型", string.Empty),
            new FilterOption("Windows", "windows"),
            new FilterOption("Windows 服务", "windows_service"),
            new FilterOption("WSL Boot", "wsl_boot"),
            new FilterOption("WSL systemd", "wsl_systemd"),
            new FilterOption("WSL command", "wsl_command"),
        ];
        ExceptionLevelFilters =
        [
            new FilterOption("全部级别", string.Empty),
            new FilterOption("严重", "CRITICAL"),
            new FilterOption("错误", "ERROR"),
            new FilterOption("警告", "WARNING"),
        ];
        selectedStatusFilter = StatusFilters[0];
        selectedTypeFilter = TypeFilters[0];
        selectedExceptionLevelFilter = ExceptionLevelFilters[0];

        TasksView = CollectionViewSource.GetDefaultView(Tasks);
        TasksView.Filter = FilterTask;
        ManagedProcessesView = CollectionViewSource.GetDefaultView(ManagedProcesses);
        ManagedProcessesView.Filter = FilterManagedProcess;
        ExceptionLogsView = CollectionViewSource.GetDefaultView(ExceptionLogs);
        ExceptionLogsView.Filter = FilterExceptionLog;

        var localHost = new HostTabViewModel();
        Hosts.Add(localHost);
        selectedHostTab = localHost;
    }

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = new();

    public ObservableCollection<ManagedProcessRowViewModel> ManagedProcesses { get; } = new();

    public ObservableCollection<ExceptionLogRowViewModel> ExceptionLogs { get; } = new();

    public ObservableCollection<HostTabViewModel> Hosts { get; } = new();

    public ObservableCollection<DriveStatusRowViewModel> Drives { get; } = new();

    public ObservableCollection<NetworkStatusRowViewModel> Networks { get; } = new();

    public ICollectionView TasksView { get; }

    public ICollectionView ManagedProcessesView { get; }

    public ICollectionView ExceptionLogsView { get; }

    public IReadOnlyList<FilterOption> StatusFilters { get; }

    public IReadOnlyList<FilterOption> TypeFilters { get; }

    public IReadOnlyList<FilterOption> ExceptionLevelFilters { get; }

    public IReadOnlyList<RemoteHostDefinition> RemoteHostDefinitions => _remoteHostDefinitions;

    public bool IsLocalHostSelected => SelectedHostTab?.IsLocal != false;

    public bool IsRemoteHostSelected => !IsLocalHostSelected;

    public bool IsOverviewPage => CurrentPage == OverviewPage;

    public bool IsWorkloadsPage => CurrentPage == WorkloadsPage;

    public bool IsManagedProcessesPage => CurrentPage == ManagedProcessesPage;

    public bool IsExceptionLogsPage => CurrentPage == ExceptionLogsPage;

    /// <summary>R105: the host and exception-log tabs share the embedded monitoring view.</summary>
    public bool IsMonitoringPage => IsOverviewPage || IsExceptionLogsPage;

    public IRelayCommand EmptyStateActionCommand => Tasks.Count == 0
        ? AddProgramCommand
        : ClearFiltersCommand;

    public event EventHandler<string>? ErrorRequested;

    public event EventHandler<DoctorReport>? DoctorRequested;

    public event EventHandler<TaskStatusDetail>? DetailRequested;

    public event EventHandler? StartupManagerRequested;

    public event EventHandler? AddProgramRequested;

    public event EventHandler? RemoteHostsRequested;

    public event EventHandler<string?>? RemoteHostDialogRequested;

    public event EventHandler<string>? AlertRequested;

    public Func<string, bool>? ConfirmRequested { get; set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var cacheTask = _cache.LoadAsync(cancellationToken);
        _loadingSettings = true;
        try
        {
            _ = await ReloadConfigurationCoreAsync(cancellationToken);
        }
        catch
        {
            _ = cacheTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw;
        }
        finally
        {
            _loadingSettings = false;
        }

        var cached = await cacheTask.ConfigureAwait(true);
        if (cached.Count > 0)
        {
            ReplaceTasks(cached.Select(item => Unobserved(item, "cached-observation", "缓存不是当前观测，正在等待后台采样。" )).ToArray(), notifyFailures: false);
            StatusMessage = "已显示缓存，正在刷新。";
        }

        await RefreshAsync();
        _notificationsEnabled = true;
        await CheckAppLaunchTasksAsync(cancellationToken);
    }

    internal void RequestAutomaticFullRefresh()
    {
        if (!AutoRefresh || !RefreshCommand.CanExecute(null)) { return; }
        _nextRefreshAutomatic = true;
        try { RefreshCommand.Execute(null); }
        finally { _nextRefreshAutomatic = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        var automatic = _nextRefreshAutomatic; _nextRefreshAutomatic = false;
        if (!CanRefresh() || _dashboardDisposed || automatic && !AutoRefresh) { return; }
        using var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation; _refreshIsAutomatic = automatic;
        _lastFullRefreshAt = DateTimeOffset.UtcNow;
        CancelLiveRead();
        IsRefreshing = true;
        try
        {
            try
            {
                // Let this short revision read finish through the shared configuration gate.
                // Canceling it for a click would invalidate the cursor needed by that mutation.
                using var readBudget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                _ = await ReloadConfigurationCoreAsync(readBudget.Token);
            }
            catch (Exception exception)
            {
                if (_dashboardDisposed || cancellation.IsCancellationRequested) { return; }
                MarkMonitoringUnavailable(exception is OperationCanceledException
                    ? "后台配置读取超时或不可用" : exception.Message);
                InvalidateDashboardHistory("配置不可用；当前历史未知，未沿用旧读数。");
                InvalidateRemoteObservations("后台配置不可用；远程观测未知，请刷新重试。");
                StatusMessage = LocalMonitoringRefreshMessage;
                return;
            }
            while (!_dashboardDisposed && SelectedHostTab is { } selected)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (selected.IsLocal || !_presentationActive)
                {
                    // Hidden windows retain local passive observations and tray alerts only.
                    await RefreshDashboardCoreAsync(reloadConfiguration: false, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (_presentationActive && !ReferenceEquals(SelectedHostTab, selected)) { continue; }
                    if (!_dashboardDisposed) { StatusMessage = LocalMonitoringRefreshMessage; }
                    return;
                }
                var completedForSelection = await RefreshRemoteHostCoreAsync(selected);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_dashboardDisposed) { return; }
                if (!completedForSelection || !ReferenceEquals(SelectedHostTab, selected)) { continue; }
                StatusMessage = selected.Status == "OK"
                    ? $"远程主机 {selected.Name} 已刷新。"
                    : $"远程主机 {selected.Name} 不可用。";
                return;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_dashboardDisposed && !cancellation.IsCancellationRequested)
            { StatusMessage = $"状态读取失败：{AgentErrorMessages.Format(exception)}"; }
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation)) { _refreshCancellation = null; _refreshIsAutomatic = false; }
            IsRefreshing = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void Navigate(string? page)
    {
        if (string.IsNullOrWhiteSpace(page) || !CanNavigate(page))
        {
            return;
        }

        CurrentPage = page;
    }

    [RelayCommand(CanExecute = nameof(CanRunGlobalCommand))]
    private void ManageRemoteHosts()
    {
        RemoteHostsRequested?.Invoke(this, EventArgs.Empty);
        RemoteHostDialogRequested?.Invoke(this, null);
    }

    private bool CanEditRemoteHost(HostTabViewModel? tab) => !IsBusy && (tab ?? SelectedHostTab) is { IsLocal: false };

    [RelayCommand(CanExecute = nameof(CanEditRemoteHost))]
    private void EditRemoteHost(HostTabViewModel? tab)
    {
        var target = tab ?? SelectedHostTab;
        if (target is null || target.IsLocal) { return; }
        RemoteHostDialogRequested?.Invoke(this, target.Id);
    }

    [RelayCommand(CanExecute = nameof(CanEditRemoteHost))]
    private async Task RemoveRemoteHostAsync(HostTabViewModel? tab)
    {
        var target = tab ?? SelectedHostTab;
        if (target is null || target.IsLocal
            || !(ConfirmRequested?.Invoke($"确定删除远程服务器“{target.Name}”吗？") ?? false))
        {
            return;
        }

        await RunBusyAsync($"正在删除远程服务器 {target.Name}...", async () =>
        {
            var original = await PrepareRemoteHostsEditAsync();
            var updated = original.Preferences.RemoteHosts
                .Where(item => !string.Equals(item.Id, target.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            await ApplyRemoteHostsAsync(updated, original);
            StatusMessage = $"已删除远程服务器 {target.Name}。";
        });
    }

    internal async Task ReorderRemoteHostsAsync(IReadOnlyList<string> orderedRemoteIds)
    {
        ArgumentNullException.ThrowIfNull(orderedRemoteIds);
        if (IsBusy)
        {
            RebuildHostTabs();
            return;
        }

        try
        {
            var original = await PrepareRemoteHostsEditAsync();
            var byId = original.Preferences.RemoteHosts.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            var reordered = new List<RemoteHostDefinition>(original.Preferences.RemoteHosts.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in orderedRemoteIds)
            {
                if (byId.TryGetValue(id, out var definition) && seen.Add(definition.Id))
                {
                    reordered.Add(definition);
                }
            }
            foreach (var definition in original.Preferences.RemoteHosts)
            {
                if (seen.Add(definition.Id))
                {
                    reordered.Add(definition);
                }
            }
            if (reordered.SequenceEqual(original.Preferences.RemoteHosts))
            {
                return;
            }
            await ApplyRemoteHostsAsync(reordered, original);
        }
        catch (Exception exception)
        {
            RebuildHostTabs();
            var message = AgentErrorMessages.Format(exception);
            StatusMessage = $"调整服务器顺序失败：{message}";
            ErrorRequested?.Invoke(this, message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOperateSelectedTask))]
    private async Task StartAsync() => await RunTaskCommandAsync("start", "启动");

    [RelayCommand(CanExecute = nameof(CanOperateSelectedTask))]
    private async Task StopAsync() => await RunTaskCommandAsync("stop", "停止");

    [RelayCommand(CanExecute = nameof(CanOperateSelectedTask))]
    private async Task RestartAsync() => await RunTaskCommandAsync("restart", "重启");

    [RelayCommand(CanExecute = nameof(CanOperateSelectedTask))]
    private async Task EnableAsync() => await RunTaskMutationAsync(true, "启用");

    [RelayCommand(CanExecute = nameof(CanOperateSelectedTask))]
    private async Task DisableAsync() => await RunTaskMutationAsync(false, "禁用");

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

    [RelayCommand(CanExecute = nameof(CanRunLocalCommand))]
    private async Task DoctorAsync()
    {
        await RunBusyAsync("正在执行系统诊断...", async () =>
        {
            var report = await _client.SendAsync<DoctorReport>("doctor", timeout: TimeSpan.FromSeconds(60));
            DoctorRequested?.Invoke(this, report);
            StatusMessage = report.AllOk ? "系统诊断通过。" : $"系统诊断发现 {report.Failed} 项问题。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanStartMissing))]
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
            await RefreshDashboardCoreAsync();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRunLocalCommand))]
    private void OpenStartupManager() => StartupManagerRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(CanRunLocalCommand))]
    private void AddProgram() => AddProgramRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        SelectedStatusFilter = StatusFilters[0];
        SelectedTypeFilter = TypeFilters[0];
    }

    [RelayCommand]
    private void FilterByStatus(string? status)
    {
        var target = StatusFilters.FirstOrDefault(option =>
            string.Equals(option.Value, status, StringComparison.OrdinalIgnoreCase))
            ?? StatusFilters[0];
        SelectedStatusFilter = ReferenceEquals(SelectedStatusFilter, target)
            ? StatusFilters[0]
            : target;
    }

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
            _ = await _configuration.MutateAsync<ConfigurationMutationResult>("task-remove", selected.Name);
            await RefreshAfterConfigurationChangeAsync($"{selected.Name} 已移出配置；现有进程未停止。");
        });
    }

    internal async Task<bool> AddWindowsTaskAsync(AddWindowsTaskRequest request)
    {
        return await AddTaskAsync(new AddTaskRequest(
            request.Name,
            AutorunTaskType.Windows,
            request.Description,
            request.Executable,
            request.Arguments,
            request.WorkingDirectory,
            StartupDelay: request.StartupDelay,
            HealthType: HealthCheckType.Process,
            HealthPattern: Path.GetFileName(request.Executable),
            Launch: new LaunchOptions(
                RunAsMode.CurrentUser,
                LaunchTrigger.Logon,
                request.WindowMode,
                ProcessLifecycle.Tracked),
            ExpectedRunning: request.ExpectedRunning,
            StartOnAppLaunch: request.StartOnAppLaunch,
            AutoRecover: request.AutoRecover));
    }

    internal string? PendingAddOperationId { get; private set; }

    internal async Task<bool> AddTaskAsync(AddTaskRequest request)
    {
        if (PendingAddOperationId is { } pending)
        { throw new InvalidOperationException($"上次添加结果未知，输入已保留。请查询原操作 {pending}，不要重复提交或换 ID 重试。"); }
        if (IsBusy)
        { throw new InvalidOperationException("正在处理其他操作，输入已保留；待当前操作完成后再保存。"); }
        var succeeded = false;
        await RunBusyAsync($"正在添加 {request.Name}...", async () =>
        {
            try { _ = await _configuration.MutateAsync<ConfigurationMutationResult>("task-add", payload: request); }
            catch (AgentOutcomeUnknownException exception)
            { PendingAddOperationId = exception.OperationId; throw; }
            succeeded = true;
            await RefreshAfterConfigurationChangeAsync($"已添加工作负载 {request.Name}。");
        });
        return succeeded;
    }

    internal async Task<ConfigurationWorkspaceSnapshot> PrepareRemoteHostsEditAsync()
    {
        await FlushSettingsAsync();
        return await ReloadConfigurationCoreAsync();
    }

    internal async Task ApplyRemoteHostsAsync(
        IReadOnlyList<RemoteHostDefinition> definitions, ConfigurationWorkspaceSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
        {
            RemoteHostDefinitionValidator.Validate(definition);
        }

        var duplicateId = definitions
            .GroupBy(definition => definition.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
        {
            throw new ArgumentException($"远程主机 ID 重复：{duplicateId.Key}");
        }

        await FlushSettingsAsync();
        try
        {
            _ = await _configuration.MutateAsync<ApplicationSettings>("settings-set",
                payload: original.Preferences with { RemoteHosts = definitions.ToArray() },
                expectedRevision: original.Revision);
        }
        catch
        {
            _settingsGeneration++;
            try { _ = await ReloadConfigurationCoreAsync(); } catch (Exception) { }
            throw;
        }
        // An acknowledged commit is not undone by a subsequent read failure.
        try
        {
            _ = await ReloadConfigurationCoreAsync();
            StatusMessage = $"已保存 {_remoteHostDefinitions.Count} 台远程主机。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"远程主机已保存，但状态重读失败；请刷新核对：{exception.Message}";
        }
    }

    [RelayCommand]
    private void EditConfig() => Process.Start(new ProcessStartInfo
    {
        FileName = _paths.ConfigPath,
        UseShellExecute = true,
    });

    internal async Task FlushSettingsAsync()
    {
        Task observed;
        do
        {
            observed = _settingsSaveTask;
            await observed;
        } while (!ReferenceEquals(observed, _settingsSaveTask));
    }

    partial void OnSelectedTaskChanged(TaskRowViewModel? value)
    {
        _ = value;
        NotifyCommandStates();
        if (!_applyingMonitoring)
        {
            if (_dashboardInitialized) { TickDashboard(); }
            _ = QueueDashboardHistoryAsync();
        }
    }

    partial void OnSelectedHostTabChanged(HostTabViewModel? value)
    {
        OnPropertyChanged(nameof(IsLocalHostSelected));
        OnPropertyChanged(nameof(IsRemoteHostSelected));
        OnPropertyChanged(nameof(HostIcon));
        NavigateCommand.NotifyCanExecuteChanged();
        EditRemoteHostCommand.NotifyCanExecuteChanged();
        RemoveRemoteHostCommand.NotifyCanExecuteChanged();
        NotifyCommandStates();
        _ = QueueDashboardHistoryAsync();
        CancelLiveRead();
        CancelRemoteRefresh();

        if (value is null)
        {
            return;
        }

        if (!value.IsLocal && CurrentPage != OverviewPage)
        {
            CurrentPage = OverviewPage;
        }

        if (value.IsLocal)
        {
            if (_lastLocalHostSnapshot is not null) { ApplyHostStatus(_lastLocalHostSnapshot); }
            else { ClearLocalHostObservation("等待本机后台观测。"); }
        }
        else if (value.Definition?.Enabled == true
            && value.RemoteSnapshot is { Online: true } cached
            && IsRemoteObservationFresh(cached.CapturedAt, allowResumeGrace: true))
        {
            ApplyRemoteHostStatus(cached);
        }
        else
        {
            // A non-fresh or unverified tab stays unknown until the backend replies.
            SetRemoteUnavailable(value, value.Definition?.Enabled == true
                ? "等待当前主机的后台观测。" : "远程主机已禁用。");
        }

        UpdateServerStatus();
        if (!_loadingSettings && !IsBusy && RefreshCommand.CanExecute(null))
        {
            RefreshCommand.Execute(null);
        }
    }

    partial void OnCurrentPageChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsOverviewPage));
        OnPropertyChanged(nameof(IsWorkloadsPage));
        OnPropertyChanged(nameof(IsManagedProcessesPage));
        OnPropertyChanged(nameof(IsExceptionLogsPage));
        OnPropertyChanged(nameof(IsMonitoringPage));
        TickDashboard();
    }

    partial void OnSearchTextChanged(string value)
    {
        _ = value;
        RefreshTaskFilter();
    }

    partial void OnManagedProcessSearchTextChanged(string value)
    {
        _ = value;
        ManagedProcessesView.Refresh();
    }

    partial void OnExceptionLogSearchTextChanged(string value)
    {
        _ = value;
        ExceptionLogsView.Refresh();
    }

    partial void OnSelectedStatusFilterChanged(FilterOption value)
    {
        _ = value;
        RefreshTaskFilter();
    }

    partial void OnSelectedTypeFilterChanged(FilterOption value)
    {
        _ = value;
        RefreshTaskFilter();
    }

    partial void OnSelectedExceptionLevelFilterChanged(FilterOption value)
    {
        _ = value;
        ExceptionLogsView.Refresh();
    }

    partial void OnStartMissingOnLaunchChanged(bool value)
    {
        if (!_loadingSettings)
        {
            _startSettingsVersion++;
            QueueSettingsPatch(new ApplicationSettingsPatch(AutoStartMissingOnLaunch: value));
        }
    }

    partial void OnAutoRecoverFailedTasksChanged(bool value)
    {
        if (!_loadingSettings)
        {
            _recoverySettingsVersion++;
            QueueSettingsPatch(new ApplicationSettingsPatch(AutoRecoverFailedTasks: value));
        }
    }

    private void QueueSettingsPatch(ApplicationSettingsPatch patch) =>
        _settingsSaveTask = SaveSettingsWithFeedbackAsync(_settingsSaveTask, patch, _settingsGeneration);

    private async Task SaveSettingsWithFeedbackAsync(Task previous, ApplicationSettingsPatch patch, int generation)
    {
        await previous;
        if (generation != _settingsGeneration) { return; }
        try
        {
            _ = await _configuration.MutateAsync<ApplicationSettings>("settings-patch", payload: patch);
        }
        catch (Exception exception)
        {
            // Drop later queued intents based on the same stale UI instead of rebasing
            // them onto another frontend's revision without the user's review.
            _settingsGeneration++;
            try { _ = await ReloadConfigurationCoreAsync(); } catch (Exception) { }
            var message = AgentErrorMessages.Format(exception);
            StatusMessage = $"设置保存失败：{message}";
            ErrorRequested?.Invoke(this, message);
        }
    }

    private async Task<ConfigurationWorkspaceSnapshot> ReloadConfigurationCoreAsync(CancellationToken cancellationToken = default)
    {
        var startVersion = _startSettingsVersion;
        var recoveryVersion = _recoverySettingsVersion;
        var snapshot = await _configuration.ReadAsync(cancellationToken);
        if (_configuration.Revision != snapshot.Revision) { return snapshot; }
        ApplyMonitoringConfiguration(snapshot);
        var wasLoading = _loadingSettings;
        _loadingSettings = true;
        try
        {
            // A slow read must not replace a newer optimistic toggle while its save queues.
            if (startVersion == _startSettingsVersion) { StartMissingOnLaunch = snapshot.Preferences.AutoStartMissingOnLaunch; }
            if (recoveryVersion == _recoverySettingsVersion) { AutoRecoverFailedTasks = snapshot.Preferences.AutoRecoverFailedTasks; }
            if (!_remoteHostDefinitions.SequenceEqual(snapshot.Preferences.RemoteHosts))
            {
                _remoteHostDefinitions = snapshot.Preferences.RemoteHosts.ToArray();
                RebuildHostTabs();
            }
        }
        finally { _loadingSettings = wasLoading; }
        return snapshot;
    }

    private bool CanOperateSelectedTask() =>
        IsLocalHostSelected && SelectedTask is not null && !IsBusy;

    private bool CanRefresh() => !IsBusy && !IsRefreshing;

    private bool CanRunGlobalCommand() => !IsBusy;

    private bool CanRunLocalCommand() => IsLocalHostSelected && !IsBusy;

    private bool CanStartMissing() => IsLocalHostSelected && !IsBusy && Tasks.Count > 0;

    private bool CanNavigate(string? page) =>
        page == OverviewPage
        || IsLocalHostSelected
        && page is WorkloadsPage or ManagedProcessesPage or ExceptionLogsPage;

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

            await RefreshDashboardCoreAsync();
        });
    }

    private async Task RunTaskMutationAsync(bool enabled, string action)
    {
        var selected = SelectedTask;
        if (selected is null)
        {
            return;
        }

        await RunBusyAsync($"正在{action} {selected.Name}...", async () =>
        {
            _ = await _configuration.MutateAsync<AutorunTask>("task-enable", selected.Name, new { enabled });
            await RefreshAfterConfigurationChangeAsync($"{selected.Name} 已{action}。");
        });
    }

    private async Task RunBusyAsync(string busyMessage, Func<Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        CancelPresentationReads();
        StatusMessage = busyMessage;
        NotifyCommandStates();
        try
        {
            await FlushSettingsAsync();
            await operation();
        }
        catch (Exception exception)
        {
            _settingsGeneration++;
            try { _ = await ReloadConfigurationCoreAsync(); } catch (Exception) { }
            var message = AgentErrorMessages.Format(exception);
            StatusMessage = $"操作失败：{message}";
            ErrorRequested?.Invoke(this, message);
        }
        finally
        {
            IsBusy = false;
            NotifyCommandStates();
            await QueueDashboardHistoryAsync();
        }
    }

    private async Task RefreshAfterConfigurationChangeAsync(string committedMessage)
    {
        StatusMessage = committedMessage;
        try { await RefreshDashboardCoreAsync(); }
        catch (Exception exception)
        {
            // The mutation already has a durable acknowledgement. Do not invite a
            // duplicate add/remove merely because refreshing the display failed.
            StatusMessage = $"{committedMessage} 状态刷新失败，请刷新核对：{exception.Message}";
        }
    }

    private async Task RefreshDashboardCoreAsync(bool reloadConfiguration = true, CancellationToken cancellationToken = default)
    {
        await FlushSettingsAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (reloadConfiguration) { _ = await ReloadConfigurationCoreAsync(cancellationToken); }
        await Task.WhenAll(RefreshMonitoringCoreAsync(cancellationToken),
            _presentationActive ? RefreshExceptionLogsCoreAsync(cancellationToken) : Task.CompletedTask);
        // Refresh is read-only with respect to workloads. Recovery belongs exclusively to
        // the Agent worker; opening/refreshing/closing this view never resumes a paused task.
    }

    private async Task RefreshExceptionLogsCoreAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await _client.SendAsync<ExceptionLogSnapshot>(
                "exception-logs",
                timeout: TimeSpan.FromSeconds(20), cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var selectedKey = SelectedExceptionLog is null
                ? null
                : $"{SelectedExceptionLog.Entry.Timestamp:O}|{SelectedExceptionLog.Source}|{SelectedExceptionLog.Message}";
            // R106: unchanged entries keep their row (and selection); only new/removed ones move.
            // Identical lines are numbered in order; each key is computed exactly once per pass.
            static IReadOnlyList<string> Keys(IEnumerable<ExceptionLogEntry> entries)
            {
                var occurrence = new Dictionary<string, int>(StringComparer.Ordinal);
                return entries.Select(entry =>
                {
                    var key = $"{entry.Timestamp:O}|{entry.Source}|{entry.FileName}|{entry.Level}|{entry.Message}";
                    var seen = occurrence.GetValueOrDefault(key); occurrence[key] = seen + 1;
                    return seen == 0 ? key : $"{key}#{seen}";
                }).ToArray();
            }
            var desiredKeys = Keys(snapshot.Entries);
            var desired = snapshot.Entries.Select((entry, index) => (Key: desiredKeys[index], Entry: entry)).ToArray();
            var rowKeys = new Dictionary<ExceptionLogRowViewModel, string>(ReferenceEqualityComparer.Instance);
            var currentKeys = Keys(ExceptionLogs.Select(row => row.Entry));
            for (var index = 0; index < ExceptionLogs.Count; index++) { rowKeys[ExceptionLogs[index]] = currentKeys[index]; }
            Reconcile(ExceptionLogs, desired, item => item.Key, row => rowKeys[row], (_, _) => { }, item => new ExceptionLogRowViewModel(item.Entry));

            ExceptionLogsView.Refresh();
            SelectedExceptionLog = ExceptionLogs.FirstOrDefault(item =>
                $"{item.Entry.Timestamp:O}|{item.Source}|{item.Message}" == selectedKey)
                ?? ExceptionLogs.FirstOrDefault();
            ExceptionLogCount = snapshot.Entries.Count;
            CriticalLogCount = snapshot.Entries.Count(entry => entry.Level == ExceptionLogLevel.Critical);
            ErrorLogCount = snapshot.Entries.Count(entry => entry.Level == ExceptionLogLevel.Error);
            WarningLogCount = snapshot.Entries.Count(entry => entry.Level == ExceptionLogLevel.Warning);
            ExceptionLogStatusText =
                $"{snapshot.SourceCount} 个日志源 · 更新于 {snapshot.CapturedAt:HH:mm:ss}"
                + (snapshot.Truncated ? " · 已截断" : string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            ExceptionLogStatusText = $"异常日志读取失败：{exception.Message}";
        }
    }

    private async Task<bool> RefreshRemoteHostCoreAsync(HostTabViewModel tab)
    {
        if (tab.Definition is not { } definition || _dashboardDisposed) { return false; }
        if (!definition.Enabled)
        {
            SetRemoteUnavailable(tab, "远程主机已禁用。");
            UpdateResourceTrends(force: true);
            return true;
        }
        var hasFreshSnapshot = tab.RemoteSnapshot is { Online: true } current && IsRemoteObservationFresh(current.CapturedAt, allowResumeGrace: true);
        if (!hasFreshSnapshot)
        {
            SetRemoteUnavailable(tab, "正在查询当前主机的后台观测…");
        }
        CancelLiveRead();
        CancelRemoteRefresh();
        MarkSnapshotReadStarted();
        var generation = _remoteGeneration;
        var revision = _monitoringConfiguration?.Revision;
        using var cancellation = new CancellationTokenSource();
        _remoteCancellation = cancellation;
        try
        {
            if (revision is null || revision != _configuration.Revision)
            { throw new InvalidDataException("主机配置尚未同步，请刷新后重试。"); }
            var snapshot = await _remoteHostReader.ReadAsync(definition, cancellation.Token);
            if (!RemoteRequestIsCurrent(tab, generation) || cancellation.IsCancellationRequested) { return false; }
            if (revision != _configuration.Revision || revision != _monitoringConfiguration?.Revision)
            { throw new InvalidDataException("主机配置已变化，请刷新后重试。"); }
            if (!string.Equals(snapshot.Id, definition.Id, StringComparison.OrdinalIgnoreCase)
                || snapshot.Platform != definition.Platform || snapshot.Host != definition.Host
                || snapshot.Online && !RemoteObservationFresh(snapshot.CapturedAt))
            { throw new InvalidDataException("远程观测身份或采样时间无效。"); }
            _dashboardNow = DateTimeOffset.UtcNow;
            _remoteResumeGraceUntilUtc = null;
            tab.ApplyRemoteStatus(snapshot);
            ApplyRemoteHostStatus(snapshot);
            if (_monitoringSnapshot is null || !_refreshIsAutomatic || DateTimeOffset.UtcNow - _monitoringSnapshot.ReturnedAt >= FullRefreshInterval)
            {
                try
                {
                    var monitor = await _monitoring.ReadSnapshotAsync(cancellation.Token);
                    if (RemoteRequestIsCurrent(tab, generation) && !cancellation.IsCancellationRequested)
                    {
                        ApplyMonitoringSnapshot(monitor);
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { System.Diagnostics.Trace.TraceInformation("Background monitor snapshot check skipped on remote tab: {0}", exception.GetType().Name); }
            }
            UpdateResourceTrends(force: !_refreshIsAutomatic && _resourceCancellation is null);
            if (_resourceCancellation is not null) { await ResourceHistoryTask; }
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        catch (Exception exception)
        {
            if (!RemoteRequestIsCurrent(tab, generation)) { return false; }
            SetRemoteUnavailable(tab, $"后台远程观测不可用：{AgentErrorMessages.Format(exception)}");
            UpdateResourceTrends(force: false);
            return true;
        }
        finally
        {
            if (ReferenceEquals(_remoteCancellation, cancellation)) { _remoteCancellation = null; }
        }
    }

    private void ApplyHostStatus(HostStatusSnapshot snapshot)
    {
        HostName = snapshot.MachineName;
        HostOs = snapshot.OsDescription;
        HostUptime = FormatUptime(snapshot.Uptime);
        HostLogicalProcessorsText = snapshot.LogicalProcessorCount > 0
            ? $"{snapshot.LogicalProcessorCount} 逻辑核心"
            : "--";
        HostProcessCountText = $"{snapshot.ProcessCount} 进程";
        HostThreadCountText = $"{snapshot.ThreadCount} 线程";
        HostConnectionText = string.Empty;
        CpuUsagePercent = snapshot.CpuUsagePercent;
        CpuUsageText = $"{snapshot.CpuUsagePercent:0}%";
        MemoryUsagePercent = snapshot.MemoryUsedPercent;
        MemoryUsageText =
            $"{FormatBytes(snapshot.TotalMemoryBytes - snapshot.AvailableMemoryBytes)} / "
            + FormatBytes(snapshot.TotalMemoryBytes);
        ApplyStorage(snapshot.Drives);
        ApplyLocalNetworks(snapshot);
        HostUpdatedText = $"更新于 {snapshot.CapturedAt:HH:mm:ss}";
        HostStatusAvailable = true;
        NotifyHostResourceReadouts();
        UpdateResourceAlerts();
        UpdateServerStatus();
    }

    private void ApplyRemoteHostStatus(RemoteHostStatusSnapshot snapshot)
    {
        HostName = snapshot.MachineName;
        HostOs = snapshot.Online ? snapshot.OsDescription : snapshot.Error ?? "远程主机不可用";
        HostUptime = snapshot.Online ? FormatUptime(snapshot.Uptime) : "--";
        HostLogicalProcessorsText = snapshot.Online && snapshot.LogicalProcessorCount > 0
            ? $"{snapshot.LogicalProcessorCount} 逻辑核心"
            : "--";
        HostProcessCountText = snapshot.Online ? $"{snapshot.ProcessCount} 进程" : "--";
        HostThreadCountText = snapshot.Online && snapshot.ThreadCount > 0 ? $"{snapshot.ThreadCount} 线程" : "--";
        HostConnectionText = SelectedHostTab?.Definition is { } definition
            ? $"SSH · {definition.UserName}@{definition.Host}:{definition.Port}"
            : "SSH";
        CpuUsagePercent = snapshot.Online ? snapshot.CpuUsagePercent : 0;
        CpuUsageText = snapshot.Online ? $"{snapshot.CpuUsagePercent:0}%" : "--";
        MemoryUsagePercent = snapshot.Online ? snapshot.MemoryUsedPercent : 0;
        MemoryUsageText = snapshot.Online
            ? $"{FormatBytes(snapshot.TotalMemoryBytes - snapshot.AvailableMemoryBytes)} / "
                + FormatBytes(snapshot.TotalMemoryBytes)
            : "--";
        if (snapshot.Online) { ApplyStorage(snapshot.Drives); }
        else
        {
            Drives.Clear();
            StorageUsagePercent = 0;
            StorageUsageText = "--";
            StorageCapacityText = "--";
            StorageDetail = "远程存储观测不可用。";
        }

        NetworkAddress = snapshot.Online ? snapshot.NetworkAddress : "--";
        NetworkRateText = snapshot.Online
            ? snapshot.NetworkReceiveBytesPerSecond is { } rx && snapshot.NetworkSendBytesPerSecond is { } tx
                ? $"↓ {FormatResourceRate(rx)} · ↑ {FormatResourceRate(tx)}"
                : $"↓ {FormatBytes(snapshot.BytesReceived)} · ↑ {FormatBytes(snapshot.BytesSent)} 累计"
            : "--";
        if (snapshot.Online)
        {
            var desiredNetworks = snapshot.Networks is { Count: > 0 } nicList
                ? nicList
                : [new NetworkStatusSnapshot("远程汇总", snapshot.NetworkAddress, snapshot.BytesReceived, snapshot.BytesSent)];
            Reconcile(Networks, desiredNetworks, network => network.Name, row => row.Name,
                (row, network) => row.Update(network.Address, FormatBytes(network.BytesReceived), FormatBytes(network.BytesSent)),
                network => new NetworkStatusRowViewModel(network.Name, network.Address,
                    FormatBytes(network.BytesReceived), FormatBytes(network.BytesSent)));
            NetworkDetail = snapshot.Networks is { Count: > 0 } rows
                ? string.Join(Environment.NewLine, rows.Select(network => $"{network.Name} · {network.Address}"))
                : $"{snapshot.NetworkAddress} · SSH 只读探针";
        }
        else
        {
            Networks.Clear();
            NetworkDetail = snapshot.Error ?? "远程网络数据不可用。";
        }

        HostUpdatedText = snapshot.Online ? $"观测于 {snapshot.CapturedAt.ToLocalTime():HH:mm:ss}" : "尚无可用观测";
        HostStatusAvailable = snapshot.Online;
        NotifyHostResourceReadouts();
        UpdateResourceAlerts();
        UpdateServerStatus();
    }

    private void ApplyRemotePlaceholder(HostTabViewModel tab)
    {
        HostName = tab.Name;
        HostOs = tab.StatusDetail;
        HostUptime = "--";
        HostLogicalProcessorsText = "--";
        HostProcessCountText = "--";
        HostThreadCountText = "--";
        HostConnectionText = tab.Address;
        CpuUsagePercent = 0;
        CpuUsageText = "--";
        MemoryUsagePercent = 0;
        MemoryUsageText = "--";
        StorageUsagePercent = 0;
        StorageUsageText = "--";
        StorageCapacityText = "--";
        StorageDetail = "尚未读取远程存储状态。";
        NetworkAddress = tab.Address;
        NetworkRateText = "--";
        NetworkDetail = "尚未读取远程网络状态。";
        HostUpdatedText = tab.UpdatedText;
        Drives.Clear();
        Networks.Clear();
        HostStatusAvailable = false;
        NotifyHostResourceReadouts();
    }

    private void ApplyStorage(IReadOnlyList<DriveStatusSnapshot> snapshots)
    {
        Reconcile(Drives, snapshots, drive => drive.Name, row => row.Name,
            (row, drive) => row.Update(drive), drive => new DriveStatusRowViewModel(drive));

        // R109: the whole machine, not the busiest volume; per-volume bars stay in the list below.
        var total = snapshots.Where(drive => drive.TotalBytes > 0).Sum(drive => drive.TotalBytes);
        var used = snapshots.Where(drive => drive.TotalBytes > 0).Sum(drive => Math.Clamp(drive.TotalBytes - drive.FreeBytes, 0, drive.TotalBytes));
        var machinePercent = total <= 0 ? 0 : Math.Clamp(used * 100d / total, 0, 100);
        StorageUsagePercent = machinePercent;
        StorageUsageText = total <= 0 ? "--" : $"{machinePercent:0}%";
        StorageCapacityText = total <= 0 ? "无磁盘数据" : $"{FormatBytes(used)} / {FormatBytes(total)}";
        StorageDetail = snapshots.Count == 0
            ? "未检测到已就绪的固定磁盘。"
            : string.Join(
                Environment.NewLine,
                snapshots.Select(drive =>
                    $"{drive.Name} {drive.Label} · {FormatBytes(drive.FreeBytes)} 可用 / "
                    + $"{FormatBytes(drive.TotalBytes)}"));
    }

    private void ApplyLocalNetworks(HostStatusSnapshot snapshot)
    {
        Reconcile(Networks, snapshot.Networks, network => network.Name, row => row.Name,
            (row, network) => row.Update(network.Address, FormatBytes(network.BytesReceived), FormatBytes(network.BytesSent)),
            network => new NetworkStatusRowViewModel(network.Name, network.Address,
                FormatBytes(network.BytesReceived), FormatBytes(network.BytesSent)));

        var primaryNetwork = snapshot.Networks.FirstOrDefault();

        NetworkAddress = primaryNetwork?.Address ?? "未连接";
        NetworkDetail = snapshot.Networks.Count == 0
            ? "未检测到活动 IPv4 网络接口。"
            : string.Join(
                Environment.NewLine,
                snapshot.Networks.Select(network => $"{network.Name} · {network.Address}"));
        NetworkRateText = CalculateNetworkRate(snapshot);
    }

    private string CalculateNetworkRate(HostStatusSnapshot snapshot)
    {
        if (_lastHostCapture is null)
        {
            _lastHostCapture = snapshot.CapturedAt;
            _lastNetworks = snapshot.Networks.ToDictionary(
                item => item.Name,
                StringComparer.OrdinalIgnoreCase);
            return "等待速率样本";
        }

        // The GUI may read the same Agent sample twice; zero deltas are not a zero rate.
        if (snapshot.CapturedAt <= _lastHostCapture) { return NetworkRateText; }
        var seconds = Math.Max(0.1, (snapshot.CapturedAt - _lastHostCapture.Value).TotalSeconds);
        long received = 0;
        long sent = 0;
        foreach (var current in snapshot.Networks)
        {
            if (_lastNetworks.TryGetValue(current.Name, out var previous))
            {
                received += Math.Max(0, current.BytesReceived - previous.BytesReceived);
                sent += Math.Max(0, current.BytesSent - previous.BytesSent);
            }
        }

        _lastHostCapture = snapshot.CapturedAt;
        _lastNetworks = snapshot.Networks.ToDictionary(
            item => item.Name,
            StringComparer.OrdinalIgnoreCase);
        return $"↓ {FormatBytes((long)(received / seconds))}/s · "
            + $"↑ {FormatBytes((long)(sent / seconds))}/s";
    }

    private void ReplaceTasks(
        IReadOnlyList<TaskSnapshot> snapshots,
        bool notifyFailures)
    {
        var selectedName = SelectedTask?.Name;
        var previous = SelectedTask;
        // R106: reconcile by name instead of clearing, so a 10 s refresh never blanks the list.
        Reconcile(Tasks, snapshots.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            snapshot => snapshot.Name.ToUpperInvariant(), row => row.Name.ToUpperInvariant(),
            (row, snapshot) => row.Update(snapshot), snapshot => new TaskRowViewModel(snapshot));

        TasksView.Refresh();
        var visibleTasks = TasksView.Cast<TaskRowViewModel>().ToArray();
        SelectedTask = visibleTasks.FirstOrDefault(task =>
            string.Equals(task.Name, selectedName, StringComparison.OrdinalIgnoreCase))
            ?? visibleTasks.FirstOrDefault();
        // Same row object: SelectedTask did not change, but its snapshot did.
        if (SelectedTask is not null && ReferenceEquals(SelectedTask, previous)) { NotifyCommandStates(); TickDashboard(); }
        UpdateCounts();
        UpdateResourceAlerts();
        TrackFailures(notifyFailures);
    }

    /// <summary>Makes <paramref name="rows"/> equal to <paramref name="desired"/> by key with the fewest
    /// collection changes: existing rows are updated and moved, never recreated.</summary>
    private static void Reconcile<TRow, TItem>(ObservableCollection<TRow> rows, IReadOnlyList<TItem> desired,
        Func<TItem, string> itemKey, Func<TRow, string> rowKey, Action<TRow, TItem> update, Func<TItem, TRow> create)
    {
        var wanted = desired.Select(itemKey).ToHashSet(StringComparer.Ordinal);
        for (var index = rows.Count - 1; index >= 0; index--) { if (!wanted.Contains(rowKey(rows[index]))) { rows.RemoveAt(index); } }
        var existing = new Dictionary<string, TRow>(StringComparer.Ordinal);
        foreach (var row in rows) { existing.TryAdd(rowKey(row), row); }
        for (var index = 0; index < desired.Count; index++)
        {
            var item = desired[index];
            if (existing.TryGetValue(itemKey(item), out var row))
            {
                update(row, item);
                var current = rows.IndexOf(row);
                if (current != index) { rows.Move(current, index); }
            }
            else { rows.Insert(index, create(item)); }
        }
    }

    private void RebuildHostTabs()
    {
        var selectedId = SelectedHostTab?.Id ?? "local";
        Hosts.Clear();
        Hosts.Add(new HostTabViewModel());
        foreach (var definition in _remoteHostDefinitions)
        {
            Hosts.Add(new HostTabViewModel(definition));
        }

        SelectedHostTab = Hosts.FirstOrDefault(host =>
            string.Equals(host.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? Hosts[0];
        UpdateResourceAlerts();
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

    private bool FilterManagedProcess(object item)
    {
        if (item is not ManagedProcessRowViewModel process)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(ManagedProcessSearchText))
        {
            return true;
        }

        var query = ManagedProcessSearchText.Trim();
        return process.TaskName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || process.TypeLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || process.ProcessNames.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || process.Reason.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private bool FilterExceptionLog(object item)
    {
        if (item is not ExceptionLogRowViewModel log)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SelectedExceptionLevelFilter.Value)
            && !string.Equals(log.Level, SelectedExceptionLevelFilter.Value, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(ExceptionLogSearchText))
        {
            return true;
        }

        var query = ExceptionLogSearchText.Trim();
        return log.Source.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || log.Message.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || log.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RefreshTaskFilter()
    {
        TasksView.Refresh();
        if (SelectedTask is { } selected && !TasksView.Contains(selected)) { SelectedTask = null; }
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        var visible = TasksView.Cast<object>().OfType<TaskRowViewModel>().ToArray();
        IsEmpty = visible.Length == 0;
        TotalTaskCount = Tasks.Count;
        OkTaskCount = Tasks.Count(task => task.Status == "OK");
        FailedTaskCount = Tasks.Count(task => task.Status == "FAIL");
        DisabledTaskCount = Tasks.Count(task => task.Status == "OFF");
        EmptyStateTitle = Tasks.Count == 0 ? "暂无启动任务" : "没有匹配结果";
        EmptyStateActionLabel = Tasks.Count == 0 ? "添加任务" : "清除筛选";
        OnPropertyChanged(nameof(EmptyStateActionCommand));
        CountMessage = visible.Length == Tasks.Count
            ? $"{Tasks.Count} 项"
            : $"显示 {visible.Length} / {Tasks.Count} 项";
        StartMissingCommand.NotifyCanExecuteChanged();
        UpdateServerStatus();
    }

    private void UpdateServerStatus()
    {
        var selected = SelectedHostTab;
        if (selected is { IsLocal: false })
        {
            ServerStatus = selected.Status;
            ServerStatusDetail = selected.StatusDetail;
            return;
        }

        ServerStatus = FailedTaskCount > 0
            ? "FAIL"
            : !HostStatusAvailable
                ? "OFF"
                : "OK";
        ServerStatusDetail = FailedTaskCount > 0
            ? $"{FailedTaskCount} 个工作负载需要处理。"
            : !HostStatusAvailable
                ? "本机 Agent 或资源采样不可用。"
                : TotalTaskCount == 0
                    ? "主机在线，尚未配置工作负载。"
                    : $"{OkTaskCount} 个工作负载运行正常。";
        Hosts.FirstOrDefault(host => host.IsLocal)?.ApplyLocalStatus(
            ServerStatus,
            ServerStatusDetail,
            _lastLocalHostSnapshot?.CapturedAt);
    }

    private void TrackFailures(bool notify)
    {
        var current = Tasks
            .Where(task => task.Enabled && task.Snapshot.HealthObserved && !task.Healthy)
            .Select(task => task.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (notify)
        {
            var newlyFailed = current.Except(_knownFailures, StringComparer.OrdinalIgnoreCase).ToArray();
            if (newlyFailed.Length > 0)
            {
                AlertRequested?.Invoke(
                    this,
                    newlyFailed.Length == 1
                        ? $"工作负载 {newlyFailed[0]} 状态异常。"
                        : $"{newlyFailed.Length} 个工作负载出现异常。");
            }
        }

        _knownFailures.Clear();
        _knownFailures.UnionWith(current);
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime < TimeSpan.Zero) { uptime = TimeSpan.Zero; }
        return (uptime.Days > 0 ? $"{uptime.Days}天 " : "")
            + uptime.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return $"{display:0.#} {units[unit]}";
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
        AddProgramCommand.NotifyCanExecuteChanged();
        OpenStartupManagerCommand.NotifyCanExecuteChanged();
        ManageRemoteHostsCommand.NotifyCanExecuteChanged();
        EditRemoteHostCommand.NotifyCanExecuteChanged();
        RemoveRemoteHostCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsSelectedTaskRunning));
        OnPropertyChanged(nameof(DashboardPrimaryPowerCommand));
        OnPropertyChanged(nameof(DashboardPrimaryPowerIcon));
        OnPropertyChanged(nameof(DashboardPrimaryPowerLabel));
        NotifyTaskAutomation();
    }
}
