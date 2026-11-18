using SystemAutorun.App.Controls;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.App.ViewModels;

public partial class MainWindowViewModel
{
    private sealed record ResourceObservation(DateTimeOffset To, DateTimeOffset? RetainedFrom, DateTimeOffset? LatestRecordAt, bool Truncated, string? Warning);
    private ResourceObservation? _resourceHistory;
    private ResourcePlotSeries _resourcePlots = ResourcePlotSeries.Empty;
    private CancellationTokenSource? _resourceCancellation;
    private Task _resourceHistoryTask = Task.CompletedTask;
    private long _resourceGeneration;
    private string? _resourceViewKey;
    private DateTimeOffset _resourceReadAt = DateTimeOffset.MinValue;
    private bool _resourceTargetIsHost;
    private bool _resourceForcePending, _resourceReadFailed;
    private string _resourceState = "尚未读取Agent资源轨迹。";

    public bool DashboardHistoryMode => DashboardWindowHours > 0;

    public bool IsResourceLoading => _resourceHistory is null
        && _resourceCancellation is { } current
        && (!current.IsCancellationRequested || _resourceForcePending);

    internal Task ResourceHistoryTask => AwaitLatestResourceHistoryAsync();
    private async Task AwaitLatestResourceHistoryAsync()
    {
        while (true)
        {
            var current = _resourceHistoryTask; await current;
            if (ReferenceEquals(current, _resourceHistoryTask)) { return; }
        }
    }
    public string DashboardHealthWindowLabel => DashboardWindowHours switch { 24 => "最近1天", 168 => "最近1周", 720 => "最近1月", _ => "实时" };
    public string DashboardResourceCaption => _resourceReadFailed ? "资源轨迹不可用"
        : DashboardHistoryMode ? DashboardHealthWindowLabel
        : "最近5分钟";
    public string DashboardResourceEmptyText => _resourceViewKey is null ? "此目标无资源轨迹" : _resourceReadFailed ? "资源轨迹不可用" : "此时段无有效采样";
    public string DashboardResourceHelp => $"{_resourceState} Agent独立采集，未选中或GUI关闭不停止。"
        + (DashboardHistoryMode ? "历史仅按有效样本求均值，不表示整桶连续覆盖；未知和身份边界不连线。" : "仅实际捕获点；断采、进程重启及配置边界不连线。")
        + " 内存始终为工作集字节；主读数仍为当前观测。";
    public IReadOnlyList<PlotPoint> DashboardResourceCpu => _resourceTargetIsHost ? [] : _resourcePlots.Cpu;
    public IReadOnlyList<PlotPoint> DashboardResourceMemory => _resourceTargetIsHost ? [] : _resourcePlots.Memory;
    public IReadOnlyList<PlotPoint> DashboardResourceDiskRead => _resourceTargetIsHost ? [] : _resourcePlots.DiskRead;
    public IReadOnlyList<PlotPoint> DashboardResourceDiskWrite => _resourceTargetIsHost ? [] : _resourcePlots.DiskWrite;
    public IReadOnlyList<PlotPoint> DashboardResourceReceive => _resourceTargetIsHost ? [] : _resourcePlots.Receive;
    public IReadOnlyList<PlotPoint> DashboardResourceSend => _resourceTargetIsHost ? [] : _resourcePlots.Send;
    public bool DashboardResourceMemoryBytes => true;
    public TimeSpan DashboardResourceDuration => DashboardHistoryMode ? TimeSpan.FromHours(DashboardWindowHours) : ResourceHistoryProjection.RecentWindow;
    public DateTimeOffset DashboardResourceEnd => _resourceHistory?.To ?? DashboardNow;
    public DateTimeOffset? DashboardResourceDataAt => _resourceHistory?.LatestRecordAt;
    public string DashboardResourceScaleKey => _resourceViewKey ?? "unavailable";
    public string? DashboardDataPulseContext => _resourceViewKey;
    public string DashboardResourceCpuText => DashboardProcess is { CpuUsageAvailable: true } process && process.CpuUsagePercent is > 0 and < 1
        ? process.CpuUsagePercent < .01 ? "<0.01%" : $"{process.CpuUsagePercent:0.00}%" : DashboardCpuText;

    public IReadOnlyList<PlotPoint> HostResourceCpu => _resourceTargetIsHost ? _resourcePlots.Cpu : [];
    public IReadOnlyList<PlotPoint> HostResourceMemory => _resourceTargetIsHost ? _resourcePlots.Memory : [];
    public IReadOnlyList<PlotPoint> HostResourceDiskRead => _resourceTargetIsHost ? _resourcePlots.DiskRead : [];
    public IReadOnlyList<PlotPoint> HostResourceDiskWrite => _resourceTargetIsHost ? _resourcePlots.DiskWrite : [];
    public IReadOnlyList<PlotPoint> HostResourceReceive => _resourceTargetIsHost ? _resourcePlots.Receive : [];
    public IReadOnlyList<PlotPoint> HostResourceSend => _resourceTargetIsHost ? _resourcePlots.Send : [];
    public string HostResourceHelp => IsRemoteHostSelected
        ? $"{_resourceState} 远程四图共享时段；内存为使用字节，磁盘为物理磁盘真实读写B/s，网络为非回环活跃接口增量合计。未知留空；历史为有效样本均值。"
        : $"{_resourceState} 主机四图共享时段；内存为使用字节，磁盘为PhysicalDisk(_Total)真实读写B/s。"
            + "网络为活跃非回环/隧道IPv4接口增量合计，含虚拟接口，可能重复计流量，不等于唯一物理链路或互联网流量。未知留空；历史为有效样本均值。";
    public string HostResourceCpuText => HostStatusAvailable ? CpuUsageText : "--";
    public string HostMemoryUsedText => !HostStatusAvailable ? "--"
        : IsLocalHostSelected && _lastLocalHostSnapshot is { TotalMemoryBytes: > 0 } host
            ? FormatBytes(Math.Max(0, host.TotalMemoryBytes - host.AvailableMemoryBytes))
            : IsRemoteHostSelected && SelectedHostTab?.RemoteSnapshot is { Online: true, TotalMemoryBytes: > 0 } remote
                ? FormatBytes(remote.MemoryUsedBytes)
                : "--";
    public string HostDiskReadText => HostRate(IsLocalHostSelected ? _lastLocalHostSnapshot?.DiskReadBytesPerSecond : SelectedHostTab?.RemoteSnapshot?.DiskReadBytesPerSecond);
    public string HostDiskWriteText => HostRate(IsLocalHostSelected ? _lastLocalHostSnapshot?.DiskWriteBytesPerSecond : SelectedHostTab?.RemoteSnapshot?.DiskWriteBytesPerSecond);
    public string HostReceiveText => HostRate(IsLocalHostSelected ? _lastLocalHostSnapshot?.NetworkReceiveBytesPerSecond : SelectedHostTab?.RemoteSnapshot?.NetworkReceiveBytesPerSecond);
    public string HostSendText => HostRate(IsLocalHostSelected ? _lastLocalHostSnapshot?.NetworkSendBytesPerSecond : SelectedHostTab?.RemoteSnapshot?.NetworkSendBytesPerSecond);
    private string HostRate(double? value) => HostStatusAvailable ? TaskRate(value) : "--";
    private static string TaskRate(double? value) => value is { } rate && double.IsFinite(rate) && rate >= 0
        ? FormatResourceRate(rate) : "--";
    private static string FormatResourceRate(double rate)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s", "PB/s"];
        var unit = 0; while (rate >= 1024 && unit < units.Length - 1) { rate /= 1024; unit++; }
        return $"{rate:G3} {units[unit]}";
    }

    partial void OnAutoRefreshChanged(bool value)
    {
        OnPropertyChanged(nameof(AutoRefreshMenuLabel)); OnPropertyChanged(nameof(DashboardRefreshLabel));
        if (!value)
        {
            _localResumeGraceUntilUtc = null;
            _remoteResumeGraceUntilUtc = null;
            _resourceForcePending = false; CancelResourceRead(); CancelLiveRead();
            if (_refreshIsAutomatic) { _refreshCancellation?.Cancel(); }
            if (_historyIsAutomatic)
            {
                _historyGeneration++; _historyCancellation?.Cancel();
                if (_dashboardHistory is null)
                { _historyState = "已暂停刷新；当前历史未知。"; _historyReadFailed = false; NotifyHistoryChanged(); }
                else { OnPropertyChanged(nameof(IsHistoryLoading)); }
            }
        }
        else { UpdateResourceTrends(force: true); }
    }

    private string? ResourceViewKey => IsOverviewPage
        ? IsLocalHostSelected
            ? $"{_monitoringSnapshot?.InstanceId}:host:{DashboardHistoryMode}:{(DashboardHistoryMode ? DashboardWindowHours : 0)}"
            : SelectedHostTab is { IsLocal: false, Definition: { Enabled: true } remote }
                ? $"{_monitoringSnapshot?.InstanceId}:remote:{MonitoringRemoteHostIdentity.For(remote)}:{DashboardHistoryMode}:{(DashboardHistoryMode ? DashboardWindowHours : 0)}"
                : null
        : IsLocalHostSelected && IsWorkloadsPage && CurrentHistoryIdentity is { } identity
            ? $"{_monitoringSnapshot?.InstanceId}:task:{identity}:{DashboardHistoryMode}:{(DashboardHistoryMode ? DashboardWindowHours : 0)}" : null;

    internal void RefreshResourcesIfDue() => UpdateResourceTrends();

    private void UpdateResourceTrends(bool force = false)
    {
        if (_applyingMonitoring || _dashboardDisposed || !_dashboardInitialized) { return; }
        var key = ResourceViewKey; var changed = key != _resourceViewKey;
        if (changed || force) { _resourceForcePending = true; }
        if (force && !changed) { CancelResourceRead(); _resourceReadAt = DateTimeOffset.MinValue; }
        if (changed)
        {
            CancelResourceRead(); _resourceViewKey = key; _resourceHistory = null;
            _resourcePlots = ResourcePlotSeries.Empty; _resourceReadAt = DateTimeOffset.MinValue;
            _resourceTargetIsHost = IsOverviewPage; _resourceReadFailed = false;
            _resourceState = key is null ? "当前目标资源轨迹不可用。" : "正在读取Agent资源轨迹。";
            NotifyResourceTrends();
        }
        if (key is null || !_presentationActive || IsBusy || _resourceCancellation is not null || (!AutoRefresh && !_resourceForcePending)) { return; }
        var now = DateTimeOffset.UtcNow;
        var interval = DashboardHistoryMode ? TimeSpan.FromSeconds(10) : AutoRefreshInterval;
        if (!_resourceForcePending && now >= _resourceReadAt && now - _resourceReadAt < interval) { return; }
        _resourceForcePending = false; _resourceReadAt = now;
        var source = DashboardHistoryMode ? ResourceHistorySource.Durable : ResourceHistorySource.RecentSeconds;
        var precise = source == ResourceHistorySource.RecentSeconds && _monitoringSnapshot is
            { RecentResourceSchemaVersion: 2, ResourceSamplingIntervalMilliseconds: 500 or 1000 };
        var host = IsOverviewPage;
        var remoteHostId = IsRemoteHostSelected ? SelectedHostTab?.Definition?.Id : null;
        var query = new ResourceHistoryQuery(now - DashboardResourceDuration, now, precise ? 601 : 301,
            host ? null : SelectedTask?.Name, host ? null : CurrentHistoryIdentity, source, remoteHostId);
        var cancellation = new CancellationTokenSource(DashboardHistoryMode ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(3));
        _resourceCancellation = cancellation;
        if (_resourceHistory is null) { OnPropertyChanged(nameof(IsResourceLoading)); }
        _resourceHistoryTask = ReadResourceHistoryAsync(query, key, _resourceGeneration, cancellation, precise,
            _monitoringSnapshot?.InstanceId, _monitoringSnapshot?.ResourceSamplingIntervalMilliseconds);
    }

    private async Task ReadResourceHistoryAsync(ResourceHistoryQuery query, string key, long generation, CancellationTokenSource cancellation, bool precise,
        string? expectedInstanceId, int? expectedIntervalMilliseconds)
    {
        try
        {
            ResourcePlotSeries plots; ResourceObservation observation;
            if (precise)
            {
                var result = await _monitoring.ReadRecentResourcesAsync(query, cancellation.Token);
                if (!ResourceReplyCurrent(key, generation, cancellation)) { return; }
                ResourceHistoryResponseValidator.Validate(result, query);
                if (!string.Equals(result.AgentInstanceId, expectedInstanceId, StringComparison.Ordinal)
                    || result.SourceIntervalMilliseconds != expectedIntervalMilliseconds)
                { throw new InvalidDataException("资源响应的后台实例或采样间隔与监测快照不一致。"); }
                plots = ResourcePlotSeries.From(result);
                observation = new(result.To, result.RetainedFrom, result.LatestRecordAt, result.Truncated, result.Warning);
            }
            else
            {
                var result = await _monitoring.ReadResourceHistoryAsync(query, cancellation.Token);
                if (!ResourceReplyCurrent(key, generation, cancellation)) { return; }
                ResourceHistoryResponseValidator.Validate(result, query);
                plots = ResourcePlotSeries.From(result);
                observation = new(result.To, result.RetainedFrom, result.LatestRecordAt, result.Truncated, result.Warning);
            }
            // Stable lists keep unchanged samples from replaying geometry/feedback on every poll.
            _resourcePlots = new(Keep(_resourcePlots.Cpu, plots.Cpu), Keep(_resourcePlots.Memory, plots.Memory),
                Keep(_resourcePlots.DiskRead, plots.DiskRead), Keep(_resourcePlots.DiskWrite, plots.DiskWrite),
                Keep(_resourcePlots.Receive, plots.Receive), Keep(_resourcePlots.Send, plots.Send));
            _resourceHistory = observation; _resourceReadFailed = false;
            _resourceState = observation.RetainedFrom is { } first
                ? $"实际最早记录 {first.ToLocalTime():MM-dd HH:mm:ss}，最近有效记录 {observation.LatestRecordAt?.ToLocalTime():MM-dd HH:mm:ss}。"
                : "此来源尚无有效记录；不补造缺失数据。";
            if (observation.Truncated) { _resourceState += " 记录受限或截断。"; }
            if (observation.Warning is { Length: > 0 } warning) { _resourceState += " " + warning; }
            if (query.Source == ResourceHistorySource.RecentSeconds)
            {
                if (query.TaskIdentity is { Length: > 0 } taskId)
                {
                    var thresholds = _alertSettings.ResolveTask(query.TaskName ?? string.Empty);
                    if (thresholds.Enabled)
                    {
                        SeedHighCpuFromRecentPlot("task:" + taskId, plots.Cpu, query.To,
                            thresholds.CpuPercent, thresholds.CpuResetPercent, thresholds.CpuSustainDuration);
                    }
                }
                else if (query.RemoteHostId is { Length: > 0 } remoteId)
                {
                    var thresholds = _alertSettings.ResolveHost(remoteId);
                    if (thresholds.Enabled)
                    {
                        SeedHighCpuFromRecentPlot("host:remote:" + remoteId, plots.Cpu, query.To,
                            thresholds.CpuPercent, thresholds.CpuResetPercent, thresholds.CpuSustainDuration);
                    }
                }
                else
                {
                    var thresholds = _alertSettings.ResolveHost("local");
                    if (thresholds.Enabled)
                    {
                        SeedHighCpuFromRecentPlot("host:local", plots.Cpu, query.To,
                            thresholds.CpuPercent, thresholds.CpuResetPercent, thresholds.CpuSustainDuration);
                    }
                }
                UpdateResourceAlerts();
            }
            NotifyResourceTrends();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_dashboardDisposed && _presentationActive && generation == _resourceGeneration && key == ResourceViewKey)
            { _resourceReadFailed = true; _resourceState = "资源轨迹查询超时；未收到新的有效数据。"; NotifyResourceTrends(); }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (ResourceReplyCurrent(key, generation, cancellation))
            {
                // Keep same-target last known samples with their original timestamps; never fill from snapshots/health.
                _resourceReadFailed = true; _resourceState = $"资源轨迹不可用：{exception.Message} 未收到新的有效数据。"; NotifyResourceTrends();
            }
        }
        finally
        {
            var wasCurrent = ReferenceEquals(_resourceCancellation, cancellation);
            if (wasCurrent) { _resourceCancellation = null; }
            cancellation.Dispose();
            if (wasCurrent && !_dashboardDisposed) { OnPropertyChanged(nameof(IsResourceLoading)); }
            if (wasCurrent && _resourceForcePending && !_dashboardDisposed && _presentationActive) { UpdateResourceTrends(); }
        }
    }

    private bool ResourceReplyCurrent(string key, long generation, CancellationTokenSource cancellation) =>
        !cancellation.IsCancellationRequested && !_dashboardDisposed && _presentationActive && generation == _resourceGeneration && key == ResourceViewKey;
    private static IReadOnlyList<PlotPoint> Keep(IReadOnlyList<PlotPoint> before, IReadOnlyList<PlotPoint> after) => before.SequenceEqual(after) ? before : after;
    private void CancelResourceRead() { _resourceGeneration++; _resourceCancellation?.Cancel(); OnPropertyChanged(nameof(IsResourceLoading)); }

    private void NotifyHostResourceReadouts()
    {
        foreach (var property in new[] { nameof(HostResourceCpuText), nameof(HostMemoryUsedText),
            nameof(HostDiskReadText), nameof(HostDiskWriteText), nameof(HostReceiveText), nameof(HostSendText) })
        { OnPropertyChanged(property); }
    }

    private void NotifyResourceTrends()
    {
        foreach (var property in new[] { nameof(DashboardResourceCpu), nameof(DashboardResourceMemory),
            nameof(DashboardResourceDiskRead), nameof(DashboardResourceDiskWrite), nameof(DashboardResourceReceive), nameof(DashboardResourceSend),
            nameof(DashboardResourceEnd),
            nameof(DashboardResourceDuration), nameof(DashboardResourceMemoryBytes), nameof(DashboardResourceScaleKey), nameof(DashboardResourceDataAt), nameof(DashboardDataPulseContext),
            nameof(DashboardHealthWindowLabel), nameof(DashboardResourceCaption), nameof(DashboardResourceEmptyText),
            nameof(DashboardResourceHelp), nameof(DashboardResourceCpuText),
            nameof(DashboardDiskReadText), nameof(DashboardDiskWriteText), nameof(DashboardReceiveText), nameof(DashboardSendText),
            nameof(HostResourceCpu), nameof(HostResourceMemory),
            nameof(HostResourceDiskRead), nameof(HostResourceDiskWrite), nameof(HostResourceReceive), nameof(HostResourceSend),
            nameof(HostResourceHelp), nameof(HostResourceCpuText), nameof(HostMemoryUsedText), nameof(HostDiskReadText), nameof(HostDiskWriteText), nameof(HostReceiveText), nameof(HostSendText),
            nameof(IsResourceLoading) })
        { OnPropertyChanged(property); }
    }
}
