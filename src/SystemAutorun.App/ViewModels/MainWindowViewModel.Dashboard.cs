using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemAutorun.App.Controls;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.App.ViewModels;

public partial class MainWindowViewModel
{
    private bool _dashboardInitialized;
    private DateTimeOffset _dashboardNow = DateTimeOffset.UtcNow;
    private int _dashboardRevision;

    [ObservableProperty]
    private int dashboardWindowHours;

    public bool IsRealtimeWindow => DashboardWindowHours == 0;
    public bool IsOneDayWindow => DashboardWindowHours == 24;
    public bool IsOneWeekWindow => DashboardWindowHours == 168;
    public bool IsOneMonthWindow => DashboardWindowHours == 720;
    public TimeSpan DashboardHealthDuration => DashboardWindowHours switch
    {
        24 => TimeSpan.FromHours(24),
        168 => TimeSpan.FromHours(168),
        720 => TimeSpan.FromHours(720),
        _ => TimeSpan.FromMinutes(5),
    };
    public bool IsSelectedTaskRunning => SelectedTask?.Status == "OK";
    public IAsyncRelayCommand DashboardPrimaryPowerCommand => IsSelectedTaskRunning ? StopCommand : StartCommand;
    public string DashboardPrimaryPowerIcon => IsSelectedTaskRunning ? "Stop" : "Play";
    public string DashboardPrimaryPowerLabel => IsSelectedTaskRunning ? "停止服务" : "启动服务";

    public DateTimeOffset DashboardNow => _dashboardNow;
    public int DashboardRevision => _dashboardRevision;
    public bool HasDashboardSelection => SelectedTask is not null;
    public string DashboardTitle => SelectedTask?.Name ?? "选择服务";
    public string DashboardIcon => TaskIcon(SelectedTask?.Type);
    public string DashboardEmptyTitle => IsEmpty ? EmptyStateTitle : "选择服务";
    public string DashboardEmptyDescription => IsEmpty && Tasks.Count > 0
        ? "调整条件，或清除筛选以显示全部服务。" : "从左侧选择，或添加一个工作负载。";
    public string DashboardEmptyActionLabel => IsEmpty ? EmptyStateActionLabel : "添加服务";
    public IRelayCommand DashboardEmptyActionCommand => IsEmpty ? EmptyStateActionCommand : AddProgramCommand;
    public string DashboardSelectedUptime => GetTaskUptime(SelectedTask);
    public string HostIcon => ResolveHostIcon(HostOs, SelectedHostTab?.Platform);
    public string DashboardCpuText => DashboardProcess is { CpuUsageAvailable: true } process
        ? $"{process.CpuUsagePercent:0.0}%" : "--";
    public string DashboardCpuMetaText => DashboardProcess is { } process
        ? process.ProcessCount > 1
            ? $"{process.ProcessCount} 进程 · PID {string.Join(", ", process.Processes.Take(2).Select(item => item.ProcessId))}{(process.Processes.Count > 2 ? "…" : string.Empty)}"
            : $"PID {string.Join(", ", process.Processes.Select(item => item.ProcessId))}"
        : SelectedTask?.Enabled == false ? "已禁用" : "无进程观测";
    public string DashboardWorkingSetText => DashboardProcess is { } process
        ? FormatBytes(process.WorkingSetBytes) : "--";
    public string DashboardMemoryPercentText => DashboardProcess is { } process
        && HostStatusAvailable && _lastLocalHostSnapshot is { TotalMemoryBytes: > 0 } host
        && Fresh(host.CapturedAt)
            ? $"{process.WorkingSetBytes * 100d / host.TotalMemoryBytes:0.0}%" : "--";
    public string DashboardMemoryMetaLabel => DashboardMemoryPercentText != "--"
        ? $"物理内存 {DashboardMemoryPercentText}"
        : "物理内存 --";
    public string DashboardSourceText => DashboardProcess is { } process
        ? $"进程观测 {process.CapturedAt.ToLocalTime():HH:mm:ss} · PID {string.Join(", ", process.Processes.Select(item => item.ProcessId))}"
        : "资源未知 · 尚未取得当前进程观测";
    public string DashboardUptimeHelp => SelectedTask?.Type == "windows" && ProcessFor(SelectedTask) is { } process
        && process.Processes.All(row => row.StartedAt is not null)
        ? "已观测匹配进程的本次启动时长；多个实例取最早启动时间。不是主机开机时长或连续健康时长。"
        : RuntimeFor(SelectedTask)?.ConsecutiveHealthySince is not null
            ? "显示连续被动健康观测时长，不是服务自身 uptime；断采、失败或已知重启会重置。未借用宿主进程启动时间。"
            : "尚无可验证的服务启动身份；不会用宿主进程或首次健康观测代替服务自身 uptime。";
    public string DashboardWindowLabel => DashboardWindowHours switch { 24 => "1 天", 168 => "1 周", 720 => "1 月", _ => "5 分钟" };
    public string DashboardSampleClock => _tasksAvailable && _monitoringSnapshot?.Tasks.ObservedAt is { } observed
        ? observed.ToLocalTime().ToString("HH:mm:ss") : "--:--:--";
    public string DashboardRefreshLabel => AutoRefresh ? _autoRefreshMilliseconds switch { 500 => "0.5s", 5000 => "5s", 10000 => "10s", _ => "1s" } : "暂停刷新";

    private ManagedProcessSnapshot? DashboardProcess => ProcessFor(SelectedTask);

    internal void InitializeDashboard()
    {
        if (_dashboardInitialized) { return; }
        _dashboardInitialized = true;
        _dashboardDisposed = false;
        PropertyChanged += DashboardPropertyChanged;
        Tasks.CollectionChanged += DashboardCollectionChanged;
        ManagedProcesses.CollectionChanged += DashboardCollectionChanged;
        if (TasksView is INotifyCollectionChanged view) { view.CollectionChanged += DashboardCollectionChanged; }
        TickDashboard();
    }

    internal void DetachDashboard()
    {
        CancelPresentationReads();
        StopDashboardHistory();
        CancelResourceRead(); _resourceHistory = null; _resourcePlots = Services.ResourcePlotSeries.Empty;
        if (!_dashboardInitialized) { return; }
        _dashboardInitialized = false;
        PropertyChanged -= DashboardPropertyChanged;
        Tasks.CollectionChanged -= DashboardCollectionChanged;
        ManagedProcesses.CollectionChanged -= DashboardCollectionChanged;
        if (TasksView is INotifyCollectionChanged view) { view.CollectionChanged -= DashboardCollectionChanged; }
    }

    internal void TickDashboard()
    {
        using var timing = Services.MotionTiming.Measure("dashboard.tick", this);
        _dashboardNow = DateTimeOffset.UtcNow;
        ExpireMonitoringGroups();
        ExpireRemoteObservations();
        if (!_presentationActive) { return; }
        UpdateResourceTrends();
        _dashboardRevision++;
        foreach (var property in DashboardProperties) { OnPropertyChanged(property); }
    }

    public string GetTaskUptime(TaskRowViewModel? task)
    {
        var runtime = RuntimeFor(task);
        if (task?.Type == "windows" && ProcessFor(task) is { } process
            && process.Processes.All(row => row.StartedAt is not null && row.StartedAt <= DashboardNow)
            && (runtime is { ProcessUptimeSource: "windows-process-observed-instances", ProcessStartedAt: { } observedStart }
                ? observedStart : process.StartedAt) is { } started && started <= DashboardNow)
        { return FormatDashboardDuration(DashboardNow - started); }
        if (task is { Enabled: true, Healthy: true, Snapshot.HealthObserved: true }
            && runtime is { ConsecutiveHealthSource: "consecutive-passive-health-observations", ConsecutiveHealthySince: { } since }
            && since <= DashboardNow)
        { return $"连续健康 {FormatDashboardDuration(DashboardNow - since)}"; }
        return "--";
    }

    private static string FormatDashboardDuration(TimeSpan elapsed) => FormatUptime(elapsed);

    public static string TaskIcon(string? type) => type switch
    {
        "windows" => "Windows",
        "windows_service" => "Service",
        "wsl_systemd" => "Database",
        "wsl_command" => "Code",
        "wsl_boot" => "Network",
        _ => "Service",
    };

    public static string ResolveHostIcon(string? osDescription, RemoteHostPlatform? platform = null)
    {
        var text = osDescription?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            if (text.Contains("Windows", StringComparison.OrdinalIgnoreCase)) { return "Windows"; }
            if (text.Contains("macOS", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Mac OS", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Darwin", StringComparison.OrdinalIgnoreCase)) { return "MacOs"; }
            if (text.Contains("Linux", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Debian", StringComparison.OrdinalIgnoreCase)
                || text.Contains("CentOS", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Fedora", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Alpine", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Red Hat", StringComparison.OrdinalIgnoreCase)
                || text.Contains("RHEL", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Rocky", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Alma", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Arch", StringComparison.OrdinalIgnoreCase)
                || text.Contains("SUSE", StringComparison.OrdinalIgnoreCase)) { return "Linux"; }
        }
        return platform switch
        {
            RemoteHostPlatform.Windows => "Windows",
            RemoteHostPlatform.Linux => "Linux",
            _ => "Host",
        };
    }

    private ManagedProcessSnapshot? ProcessFor(TaskRowViewModel? task)
    {
        if (!IsLocalHostSelected || task?.Type is not ("windows" or "windows_service")
            || ManagedProcessStatusText.StartsWith("常驻进程读取失败", StringComparison.Ordinal)) { return null; }
        var process = ManagedProcesses.FirstOrDefault(item => string.Equals(item.TaskName, task.Name, StringComparison.OrdinalIgnoreCase))?.Snapshot;
        return process is { ProcessCount: > 0 }
            && string.Equals(process.Type, task.Type, StringComparison.Ordinal)
            && process.TaskIdentity == MonitoringTaskIdentity.For(task.Snapshot)
            && Fresh(process.CapturedAt) ? process : null;
    }

    private bool Fresh(DateTimeOffset capturedAt) => DashboardNow - capturedAt is var age
        && age >= TimeSpan.FromSeconds(-5)
        && (age <= TimeSpan.FromSeconds(30) || InLocalResumeGrace);

    [RelayCommand]
    private void SelectDashboardWindow(string? hours)
    {
        if (int.TryParse(hours, out var value) && value is 0 or 24 or 168 or 720) { DashboardWindowHours = value; }
    }

    [RelayCommand]
    private void SelectDashboardType(string? value)
    {
        var filter = TypeFilters.FirstOrDefault(item => string.Equals(item.Value, value ?? "", StringComparison.Ordinal));
        if (filter is not null) { SelectedTypeFilter = filter; }
    }

    partial void OnDashboardWindowHoursChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(DashboardHistoryMode));
        OnPropertyChanged(nameof(IsRealtimeWindow));
        OnPropertyChanged(nameof(IsOneDayWindow));
        OnPropertyChanged(nameof(IsOneWeekWindow));
        OnPropertyChanged(nameof(IsOneMonthWindow));
        OnPropertyChanged(nameof(DashboardHealthDuration));
        OnPropertyChanged(nameof(DashboardHealthWindowLabel));
        OnPropertyChanged(nameof(DashboardWindowLabel));
        TickDashboard(); NotifyResourceTrends(); _ = QueueDashboardHistoryAsync();
    }

    private void DashboardCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        _ = sender; _ = args;
        // ApplyMonitoringGroups ticks once when finished; do not tick per row every second.
        if (!_applyingMonitoring) { TickDashboard(); }
    }

    private void DashboardPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        _ = sender;
        if (args.PropertyName is nameof(SelectedTask) or nameof(ManagedProcessStatusText)
            or nameof(HostStatusAvailable) or nameof(HostUpdatedText) or nameof(AutoRefresh)
            or nameof(IsBusy) or nameof(SelectedHostTab) or nameof(IsEmpty)
            or nameof(EmptyStateTitle) or nameof(EmptyStateActionLabel)) { TickDashboard(); }
    }

    private static readonly string[] DashboardProperties =
    [
        nameof(DashboardNow), nameof(DashboardRevision), nameof(HasDashboardSelection),
        nameof(IsSelectedTaskRunning), nameof(DashboardPrimaryPowerCommand), nameof(DashboardPrimaryPowerIcon), nameof(DashboardPrimaryPowerLabel),
        nameof(DashboardTitle), nameof(DashboardIcon), nameof(HostIcon), nameof(DashboardSelectedUptime),
        nameof(DashboardEmptyTitle), nameof(DashboardEmptyDescription),
        nameof(DashboardEmptyActionLabel), nameof(DashboardEmptyActionCommand),
        nameof(DashboardCpuText), nameof(DashboardCpuMetaText), nameof(DashboardWorkingSetText), nameof(DashboardMemoryPercentText), nameof(DashboardMemoryMetaLabel), nameof(DashboardResourceCpuText),
        nameof(DashboardSourceText), nameof(DashboardUptimeHelp), nameof(DashboardWindowLabel),
        nameof(DashboardHistoryHelp), nameof(DashboardHistoryNotice), nameof(DashboardSampleClock), nameof(DashboardRefreshLabel),
        nameof(HostResourceCpuText), nameof(HostMemoryUsedText), nameof(HostDiskReadText), nameof(HostDiskWriteText), nameof(HostReceiveText), nameof(HostSendText),
    ];
}
