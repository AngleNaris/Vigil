using SystemAutorun.App.Controls;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.App.ViewModels;

public partial class MainWindowViewModel
{
    private CancellationTokenSource? _historyCancellation;
    private bool _historyIsAutomatic;
    private string? _activeHistoryIdentity;
    private TimeSpan _activeHistoryDuration;
    private long _historyGeneration;
    private Task _dashboardHistoryTask = Task.CompletedTask;
    private MonitoringHistoryResult? _dashboardHistory;
    private DateTimeOffset _historyEnd = DateTimeOffset.UtcNow;
    private string _historyState = "尚未读取历史；斜纹为未知，不是正常。";
    private bool _historyReadFailed;
    private bool _dashboardDisposed;

    public bool IsHistoryLoading => _historyCancellation is { IsCancellationRequested: false } && _dashboardHistory is null;
    public DateTimeOffset DashboardHistoryEnd => LiveHealthOverlay() is { } live ? live.End : _historyEnd;
    public DateTimeOffset? DashboardHealthDataAt => LiveHealthOverlay() is { } live
        ? (_dashboardHistory?.LatestRecordAt is { } record && record > live.ObservedAt ? record : live.ObservedAt)
        : _dashboardHistory?.LatestRecordAt;
    public string DashboardHistoryHelp => $"最近 {DashboardWindowLabel}，{(DashboardHistoryEnd - DashboardHealthDuration).ToLocalTime():MM-dd HH:mm} 至 {DashboardHistoryEnd.ToLocalTime():MM-dd HH:mm}。{_historyState} 可用率仅依据有效健康记录计算。曲线为时间桶聚合读数，不跨断档连线。";
    public string DashboardHistoryNotice => _historyReadFailed ? "历史读取失败"
        : _monitoringSnapshot?.Persistence is { Available: false } ? "历史持久化不可用"
        : "";
    public string DashboardAvailabilityText => Percent(_dashboardHistory?.AvailabilityPercent);
    public string DashboardCoverageText => Percent(_dashboardHistory?.CoveragePercent);
    public IReadOnlyList<HealthInterval> DashboardHealthHistory
    {
        get
        {
            var intervals = _dashboardHistory?.Intervals
                .Select(row => new HealthInterval(row.From, row.To, row.Health == ObservationHealth.Fault ? "failed" : row.Health == ObservationHealth.Healthy ? "healthy" : "unknown"))
                .ToList() ?? [];
            if (LiveHealthOverlay() is { } live)
            {
                var windowFrom = live.End - DashboardHealthDuration;
                var liveFrom = live.ObservedAt < live.End.AddSeconds(-5) ? live.ObservedAt : live.End.AddSeconds(-5);
                if (liveFrom < windowFrom) { liveFrom = windowFrom; }
                if (live.End > liveFrom)
                {
                    intervals.Add(new HealthInterval(liveFrom, live.End, live.Healthy ? "healthy" : "failed"));
                }
            }
            return intervals;
        }
    }

    private (DateTimeOffset End, DateTimeOffset ObservedAt, bool Healthy)? LiveHealthOverlay()
    {
        if (DashboardHistoryMode || !_tasksAvailable || !ConfigurationMatchesMonitoring
            || CurrentHistoryIdentity is null
            || _historyState.StartsWith("已暂停刷新", StringComparison.Ordinal)
            || SelectedTask is not { Enabled: true, Snapshot.HealthObserved: true } task
            || _monitoringSnapshot?.Tasks.ObservedAt is not { } observedAt
            || !Fresh(observedAt))
        {
            return null;
        }
        if (_dashboardHistory is not { } history)
        {
            return null;
        }
        var faultTransition = !task.Healthy
            && (history.Intervals.Count == 0 || history.Intervals[^1].Health != ObservationHealth.Fault)
            && (history.LatestRecordAt is null || observedAt >= history.To.AddSeconds(-10));
        var recoveryTransition = task.Healthy
            && history.Intervals.Count > 0
            && history.Intervals[^1].Health == ObservationHealth.Fault
            && (history.LatestRecordAt is null || observedAt > history.LatestRecordAt);
        if (!faultTransition && !recoveryTransition)
        {
            return null;
        }
        var end = _historyEnd > observedAt ? _historyEnd
            : DashboardNow > observedAt ? DashboardNow
            : observedAt.AddSeconds(1);
        return (end, observedAt, task.Healthy);
    }
    public IReadOnlyList<PlotPoint> DashboardAvailabilityTrend => HistoryPoints(row => row.AvailabilityPercent);
    public IReadOnlyList<PlotPoint> DashboardCoverageTrend => HistoryPoints(row => row.CoveragePercent);
    public IReadOnlyList<PlotPoint> DashboardCpuTrend => HistoryPoints(row => row.CpuPercent, includeInterval: true);
    public IReadOnlyList<PlotPoint> DashboardMemoryTrend => HistoryPoints(row => row.MemoryPercent, includeInterval: true);
    internal Task DashboardHistoryTask => _dashboardHistoryTask;

    private static string Percent(double? value) => value is { } number && double.IsFinite(number) && number is >= 0 and <= 100 ? $"{number:0.0}%" : "--";
    private IReadOnlyList<PlotPoint> HistoryPoints(Func<MonitoringHistoryBucket, double?> value, bool includeInterval = false) => _dashboardHistory?.Buckets
        .Select(row => new PlotPoint(row.From + (row.To - row.From) / 2, value(row) is { } number
            && double.IsFinite(number) && number is >= 0 and <= 100 ? number : null, includeInterval ? row.From : null, includeInterval ? row.To : null)).ToArray() ?? [];

    private string? CurrentHistoryIdentity => IsLocalHostSelected && _configuration.Revision is { } revision
        && _monitoringConfiguration is { } configuration && configuration.Revision == revision
        && SelectedTask is { Enabled: true } selected
        && configuration.Configuration.Tasks.Any(task =>
            MonitoringTaskIdentity.For(task) == MonitoringTaskIdentity.For(selected.Snapshot)) == true
            ? MonitoringTaskIdentity.For(selected.Snapshot) : null;

    private Task QueueDashboardHistoryAsync(bool automatic = false)
    {
        if (_applyingMonitoring || _dashboardDisposed) { return _dashboardHistoryTask; }
        var identity = CurrentHistoryIdentity;
        var targetDuration = DashboardHealthDuration;
        if (automatic && _historyCancellation is { IsCancellationRequested: false } && !_historyIsAutomatic
            && identity is not null && identity == _activeHistoryIdentity
            && Math.Abs((targetDuration - _activeHistoryDuration).TotalSeconds) < 1)
        {
            return _dashboardHistoryTask;
        }
        var generation = ++_historyGeneration;
        _historyCancellation?.Cancel();
        _historyCancellation = null; _historyIsAutomatic = false;
        // R106: a periodic refresh of the same service and window keeps the shown history until the
        // new reply replaces it, so the band and trends do not blank every 10 s. Only a different
        // service, window or a lost identity clears it immediately.
        var sameView = identity is not null && _dashboardHistory is { } shown && shown.TaskIdentity == identity
            && Math.Abs(((shown.To - shown.From) - targetDuration).TotalSeconds) < 1;
        if (!sameView)
        {
            _dashboardHistory = null; _historyReadFailed = false;
            _historyEnd = DateTimeOffset.UtcNow;
            _historyState = identity is null ? "尚无当前服务的追踪身份；历史未知。" : "正在读取历史；斜纹为未知。";
            NotifyHistoryChanged();
        }
        if (identity is null || SelectedTask is not { } selected || !_presentationActive || IsBusy)
        {
            _activeHistoryIdentity = null; _activeHistoryDuration = TimeSpan.Zero;
            return _dashboardHistoryTask = Task.CompletedTask;
        }
        var end = DateTimeOffset.UtcNow;
        var bucketCount = DashboardHistoryMode ? 120 : 30;
        var query = new MonitoringHistoryQuery(selected.Name, identity, end - targetDuration, end, bucketCount);
        var cancellation = new CancellationTokenSource();
        _historyCancellation = cancellation; _historyIsAutomatic = automatic;
        _activeHistoryIdentity = identity; _activeHistoryDuration = targetDuration;
        if (_dashboardHistory is null) { OnPropertyChanged(nameof(IsHistoryLoading)); }
        return _dashboardHistoryTask = LoadDashboardHistoryAsync(query, generation, cancellation);
    }

    private async Task LoadDashboardHistoryAsync(MonitoringHistoryQuery query, long generation, CancellationTokenSource cancellation)
    {
        try
        {
            var result = await _monitoring.ReadHistoryAsync(query, cancellation.Token);
            // A canceled transport may still return a result. Selection/window/generation
            // are checked again; no late reply is allowed to become another service's history.
            if (!HistoryRequestIsCurrent(query, generation) || cancellation.IsCancellationRequested) { return; }
            if (result.SchemaVersion != 1 || result.TaskIdentity != query.TaskIdentity
                || !string.Equals(result.TaskName, query.TaskName, StringComparison.OrdinalIgnoreCase)
                || result.From != query.From || result.To != query.To || result.Buckets.Count > 1000
                || result.Intervals.Any(row => row.From < query.From || row.To > query.To || row.To <= row.From)
                || result.Buckets.Any(row => row.From < query.From || row.To > query.To || row.To <= row.From))
            { throw new InvalidDataException("历史响应身份、窗口或区间无效。"); }
            _dashboardHistory = result; _historyReadFailed = false;
            _historyEnd = result.To;
            _historyState = result.LatestRecordAt is null ? "没有历史记录；整个窗口未知。"
                : $"最近记录 {result.LatestRecordAt.Value.ToLocalTime():MM-dd HH:mm:ss}；斜纹为未知。";
            if (result.Truncated) { _historyState += " 历史已截断，未返回区间仍为未知。"; }
            if (!string.IsNullOrWhiteSpace(result.Warning)) { _historyState += $" {result.Warning}"; }
            if (_monitoringSnapshot?.Persistence is { Available: false } persistence)
            { _historyState += $" 历史持久化不可用：{persistence.Error ?? "unknown"}。"; }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (HistoryRequestIsCurrent(query, generation))
            { _dashboardHistory = null; _historyReadFailed = true; _historyState = $"历史读取失败：{exception.Message}；当前历史未知。"; }
        }
        finally
        {
            if (ReferenceEquals(_historyCancellation, cancellation)) { _historyCancellation = null; _historyIsAutomatic = false; }
            cancellation.Dispose();
            if (generation == _historyGeneration && !_dashboardDisposed) { NotifyHistoryChanged(); }
        }
    }

    private bool HistoryRequestIsCurrent(MonitoringHistoryQuery query, long generation) => !_dashboardDisposed
        && generation == _historyGeneration && CurrentHistoryIdentity == query.TaskIdentity
        && Math.Abs(((query.To - query.From) - DashboardHealthDuration).TotalSeconds) < 1;

    private void NotifyHistoryChanged()
    {
        using var timing = Services.MotionTiming.Measure("history.publish", this);
        foreach (var property in new[] { nameof(IsHistoryLoading), nameof(DashboardHistoryEnd), nameof(DashboardHistoryHelp), nameof(DashboardHistoryNotice),
            nameof(DashboardAvailabilityText), nameof(DashboardCoverageText), nameof(DashboardHealthHistory), nameof(DashboardHealthDataAt),
            nameof(DashboardAvailabilityTrend), nameof(DashboardCoverageTrend), nameof(DashboardCpuTrend), nameof(DashboardMemoryTrend) })
        { OnPropertyChanged(property); }
        if (DashboardHistoryMode) { NotifyResourceTrends(); }
    }

    private void InvalidateDashboardHistory(string reason)
    {
        _historyGeneration++;
        _historyCancellation?.Cancel();
        _historyCancellation = null;
        _dashboardHistory = null; _historyReadFailed = false;
        _historyEnd = DateTimeOffset.UtcNow;
        _historyState = reason;
        NotifyHistoryChanged();
    }

    private void StopDashboardHistory()
    {
        _dashboardDisposed = true;
        _historyGeneration++;
        _historyCancellation?.Cancel();
        _historyCancellation = null;
    }
}
