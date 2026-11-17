using System.Globalization;
using CommunityToolkit.Mvvm.Input;

namespace SystemAutorun.App.ViewModels;

public partial class MainWindowViewModel
{
    private int _autoRefreshMilliseconds = 1000;
    public int AutoRefreshMilliseconds => _autoRefreshMilliseconds;
    internal TimeSpan AutoRefreshInterval => TimeSpan.FromMilliseconds(_autoRefreshMilliseconds);
    public bool CanRefreshAtHalfSecond => _monitoringSnapshot is
        { RecentResourceSchemaVersion: 2, ResourceSamplingIntervalMilliseconds: 500 };
    public string RefreshHalfSecondLabel => CanRefreshAtHalfSecond ? "0.5 秒" : _monitoringSnapshot is null ? "0.5 秒（后台未知）" : "0.5 秒（待更新后台）";
    public string HalfSecondRefreshHelp => CanRefreshAtHalfSecond
        ? "后台已支持半秒真实采样；界面按所选间隔读取。"
        : _monitoringSnapshot is null ? "尚未连接后台，无法确认半秒采样能力。"
        : _monitoringSnapshot.ResourceSamplingIntervalMilliseconds == 1000 ? "当前后台仍按1秒采样；更新后台后可使用。"
        : "当前后台未提供半秒采样能力；更新后台后可使用。";
    public string AutoRefreshMenuLabel => AutoRefresh ? $"自动刷新 · {RefreshIntervalText}" : "自动刷新 · 已暂停";
    public string RefreshIntervalText => _autoRefreshMilliseconds switch { 500 => "0.5 秒", 5000 => "5 秒", 10000 => "10 秒", _ => "1 秒" };
    public bool RefreshHalfSecondSelected => _autoRefreshMilliseconds == 500;
    public bool RefreshOneSecondSelected => _autoRefreshMilliseconds == 1000;
    public bool RefreshFiveSecondsSelected => _autoRefreshMilliseconds == 5000;
    public bool RefreshTenSecondsSelected => _autoRefreshMilliseconds == 10000;

    [RelayCommand(CanExecute = nameof(CanSelectAutoRefreshInterval))]
    private void SelectAutoRefreshInterval(string? milliseconds)
    {
        if (!CanSelectAutoRefreshInterval(milliseconds)) { return; }
        SetAutoRefreshInterval(int.Parse(milliseconds!, CultureInfo.InvariantCulture));
    }

    private bool CanSelectAutoRefreshInterval(string? milliseconds) =>
        int.TryParse(milliseconds, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value is (500 or 1000 or 5000 or 10000) && (value != 500 || CanRefreshAtHalfSecond);

    private void SetAutoRefreshInterval(int milliseconds)
    {
        if (_autoRefreshMilliseconds == milliseconds)
        {
            OnPropertyChanged(nameof(RefreshHalfSecondSelected)); OnPropertyChanged(nameof(RefreshOneSecondSelected));
            OnPropertyChanged(nameof(RefreshFiveSecondsSelected)); OnPropertyChanged(nameof(RefreshTenSecondsSelected));
            return;
        }
        _autoRefreshMilliseconds = milliseconds;
        foreach (var property in new[] { nameof(AutoRefreshMilliseconds), nameof(AutoRefreshMenuLabel), nameof(RefreshIntervalText),
            nameof(RefreshHalfSecondSelected), nameof(RefreshOneSecondSelected), nameof(RefreshFiveSecondsSelected), nameof(RefreshTenSecondsSelected),
            nameof(DashboardRefreshLabel) }) { OnPropertyChanged(property); }
        // Do not cancel an in-flight read or query durable history just because the GUI cadence changed.
        if (!DashboardHistoryMode)
        {
            _resourceReadAt = DateTimeOffset.MinValue;
            if (AutoRefresh && _resourceCancellation is null) { UpdateResourceTrends(); }
        }
    }

    private void RefreshCapabilityChanged()
    {
        if (!CanRefreshAtHalfSecond && _autoRefreshMilliseconds == 500) { SetAutoRefreshInterval(1000); }
        OnPropertyChanged(nameof(CanRefreshAtHalfSecond)); OnPropertyChanged(nameof(HalfSecondRefreshHelp)); OnPropertyChanged(nameof(RefreshHalfSecondLabel));
        SelectAutoRefreshIntervalCommand.NotifyCanExecuteChanged();
    }
}
