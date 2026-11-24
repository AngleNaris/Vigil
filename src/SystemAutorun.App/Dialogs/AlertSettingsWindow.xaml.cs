using System.ComponentModel;
using System.Globalization;
using System.Windows.Controls;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Models;

namespace SystemAutorun.App.Dialogs;

public partial class AlertSettingsWindow : ToolWindow
{
    private enum TargetKind
    {
        Global,
        Host,
        Task,
    }

    private sealed class TargetItem(TargetKind kind, string key, string targetId, string title, string subtitle, string icon, string badge)
        : INotifyPropertyChanged
    {
        private string _badge = badge;
        public TargetKind Kind { get; } = kind;
        public string Key { get; } = key;
        public string TargetId { get; } = targetId;
        public string Title { get; } = title;
        public string Subtitle { get; } = subtitle;
        public string Icon { get; } = icon;

        public string Badge
        {
            get => _badge;
            set
            {
                if (!string.Equals(_badge, value, StringComparison.Ordinal))
                {
                    _badge = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Badge)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly Func<ResourceAlertSettings, Task>? _save;
    private readonly List<TargetItem> _items = [];
    private ResourceAlertSettings _draft;
    private TargetItem? _activeItem;
    private bool _loadingFields;
    private bool _revertingSelection;
    private bool _saving;

    internal AlertSettingsWindow(
        ResourceAlertSettings settings,
        IReadOnlyList<RemoteHostDefinition> remoteHosts,
        IReadOnlyList<AutorunTask> tasks,
        Func<ResourceAlertSettings, Task>? save = null,
        string? focusTargetKey = null)
    {
        InitializeComponent();
        _draft = (settings ?? new ResourceAlertSettings()).CloneNormalized();
        _save = save;

        BuildTargetItems(remoteHosts ?? [], tasks ?? []);
        TargetList.ItemsSource = _items;

        var initial = !string.IsNullOrWhiteSpace(focusTargetKey)
            ? _items.FirstOrDefault(item => string.Equals(item.Key, focusTargetKey, StringComparison.OrdinalIgnoreCase))
            : null;
        TargetList.SelectedItem = initial ?? _items[0];
        if (TargetList.SelectedItem is not null)
        {
            TargetList.ScrollIntoView(TargetList.SelectedItem);
        }
    }

    internal ResourceAlertSettings SavedSettings { get; private set; } = new();

    private void BuildTargetItems(
        IReadOnlyList<RemoteHostDefinition> remoteHosts,
        IReadOnlyList<AutorunTask> tasks)
    {
        _items.Clear();
        _items.Add(new TargetItem(
            TargetKind.Global,
            "global",
            "global",
            "全局默认阈值",
            "未单独覆盖的主机与服务将自动沿用以下默认阈值。",
            "Settings",
            _draft.Enabled ? "启用" : "停用"));

        _items.Add(new TargetItem(
            TargetKind.Host,
            "host:local",
            "local",
            $"{Environment.MachineName} (本机)",
            "自定义本机主机的 CPU、内存与磁盘占用告警阈值。",
            "Windows",
            HostBadge("local")));

        foreach (var host in remoteHosts)
        {
            _items.Add(new TargetItem(
                TargetKind.Host,
                "host:" + host.Id,
                host.Id,
                host.Name,
                $"远程主机 · {host.UserName}@{host.Host}:{host.Port}",
                host.Platform == RemoteHostPlatform.Linux ? "Linux" : "Windows",
                HostBadge(host.Id)));
        }

        foreach (var task in tasks)
        {
            _items.Add(new TargetItem(
                TargetKind.Task,
                "task:" + task.Name,
                task.Name,
                task.Name,
                $"工作负载 ({task.Type}) · 独立设置此服务及其子进程的告警比例",
                "Service",
                TaskBadge(task.Name)));
        }
    }

    private string HostBadge(string hostId)
    {
        var rule = _draft.FindHostOverride(hostId);
        if (rule?.Enabled == false) { return "已禁用"; }
        return rule is not null && !rule.IsDefault ? "自定义" : "默认";
    }

    private string TaskBadge(string taskName)
    {
        var rule = _draft.FindTaskOverride(taskName);
        if (rule?.Enabled == false) { return "已禁用"; }
        return rule is not null && !rule.IsDefault ? "自定义" : "默认";
    }

    private void RefreshAllBadges()
    {
        foreach (var item in _items)
        {
            item.Badge = item.Kind switch
            {
                TargetKind.Global => _draft.Enabled ? "启用" : "停用",
                TargetKind.Host => HostBadge(item.TargetId),
                TargetKind.Task => TaskBadge(item.TargetId),
                _ => item.Badge,
            };
        }
    }

    private void TargetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_revertingSelection) { return; }
        if (TargetList.SelectedItem is not TargetItem next || ReferenceEquals(next, _activeItem))
        {
            return;
        }

        if (_activeItem is not null)
        {
            try
            {
                CommitActiveTargetToDraft();
            }
            catch (ArgumentException exception)
            {
                _revertingSelection = true;
                try { TargetList.SelectedItem = _activeItem; }
                finally { _revertingSelection = false; }
                MessageDialog.ShowError(this, "阈值输入无效", exception.Message);
                return;
            }
        }

        _activeItem = next;
        RenderTarget(next);
    }

    private void RenderTarget(TargetItem item)
    {
        _loadingFields = true;
        try
        {
            SelectedTargetTitle.Text = item.Title;
            SelectedTargetSubtitle.Text = item.Subtitle;
            GlobalEditorPanel.Visibility = item.Kind == TargetKind.Global ? Visibility.Visible : Visibility.Collapsed;
            HostOverridePanel.Visibility = item.Kind == TargetKind.Host ? Visibility.Visible : Visibility.Collapsed;
            TaskOverridePanel.Visibility = item.Kind == TargetKind.Task ? Visibility.Visible : Visibility.Collapsed;

            switch (item.Kind)
            {
                case TargetKind.Global:
                    GlobalEnabledCheckBox.IsChecked = _draft.Enabled;
                    GlobalHostCpuTextBox.Text = FormatPercent(_draft.HostCpuPercent);
                    GlobalHostMemTextBox.Text = FormatPercent(_draft.HostMemoryPercent);
                    GlobalHostStorageTextBox.Text = FormatPercent(_draft.HostStoragePercent);
                    GlobalProcCpuTextBox.Text = FormatPercent(_draft.ProcessCpuPercent);
                    GlobalProcMemTextBox.Text = FormatPercent(_draft.ProcessMemoryPercent);
                    GlobalProcMemMbTextBox.Text = _draft.ProcessMemoryMb.ToString(CultureInfo.InvariantCulture);
                    GlobalSustainTextBox.Text = _draft.CpuSustainSeconds.ToString(CultureInfo.InvariantCulture);
                    GlobalCooldownTextBox.Text = _draft.NotificationCooldownMinutes.ToString(CultureInfo.InvariantCulture);
                    break;

                case TargetKind.Host:
                {
                    var rule = _draft.FindHostOverride(item.TargetId);
                    var effective = _draft.ResolveHost(item.TargetId);
                    var hasCustom = rule is not null
                        && (rule.CpuPercent is not null
                            || rule.MemoryPercent is not null
                            || rule.StoragePercent is not null
                            || rule.CpuSustainSeconds is not null);
                    HostEnabledCheckBox.IsChecked = effective.Enabled;
                    HostCustomCheckBox.IsChecked = hasCustom;
                    HostCpuTextBox.Text = FormatPercent(effective.CpuPercent);
                    HostMemTextBox.Text = FormatPercent(effective.MemoryPercent);
                    HostStorageTextBox.Text = FormatPercent(effective.StoragePercent);
                    HostSustainTextBox.Text = ((int)effective.CpuSustainDuration.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                    UpdateOverrideFieldState();
                    break;
                }

                case TargetKind.Task:
                {
                    var rule = _draft.FindTaskOverride(item.TargetId);
                    var effective = _draft.ResolveTask(item.TargetId);
                    var hasCustom = rule is not null
                        && (rule.CpuPercent is not null
                            || rule.MemoryPercent is not null
                            || rule.MemoryMb is not null
                            || rule.CpuSustainSeconds is not null);
                    TaskEnabledCheckBox.IsChecked = effective.Enabled;
                    TaskCustomCheckBox.IsChecked = hasCustom;
                    TaskCpuTextBox.Text = FormatPercent(effective.CpuPercent);
                    TaskMemTextBox.Text = FormatPercent(effective.MemoryPercent);
                    TaskMemMbTextBox.Text = (effective.MemoryBytes / (1024L * 1024L)).ToString(CultureInfo.InvariantCulture);
                    TaskSustainTextBox.Text = ((int)effective.CpuSustainDuration.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                    UpdateOverrideFieldState();
                    break;
                }
            }
        }
        finally
        {
            _loadingFields = false;
        }
    }

    private void OverrideCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_loadingFields || _activeItem is null) { return; }

        if (_activeItem.Kind == TargetKind.Host && HostCustomCheckBox.IsChecked != true)
        {
            _loadingFields = true;
            try
            {
                HostCpuTextBox.Text = FormatPercent(_draft.HostCpuPercent);
                HostMemTextBox.Text = FormatPercent(_draft.HostMemoryPercent);
                HostStorageTextBox.Text = FormatPercent(_draft.HostStoragePercent);
                HostSustainTextBox.Text = _draft.CpuSustainSeconds.ToString(CultureInfo.InvariantCulture);
            }
            finally
            {
                _loadingFields = false;
            }
        }
        else if (_activeItem.Kind == TargetKind.Task && TaskCustomCheckBox.IsChecked != true)
        {
            _loadingFields = true;
            try
            {
                TaskCpuTextBox.Text = FormatPercent(_draft.ProcessCpuPercent);
                TaskMemTextBox.Text = FormatPercent(_draft.ProcessMemoryPercent);
                TaskMemMbTextBox.Text = _draft.ProcessMemoryMb.ToString(CultureInfo.InvariantCulture);
                TaskSustainTextBox.Text = _draft.CpuSustainSeconds.ToString(CultureInfo.InvariantCulture);
            }
            finally
            {
                _loadingFields = false;
            }
        }

        UpdateOverrideFieldState();
    }

    private void UpdateOverrideFieldState()
    {
        HostCustomCheckBox.IsEnabled = HostEnabledCheckBox.IsChecked == true;
        HostFieldsGrid.IsEnabled = HostEnabledCheckBox.IsChecked == true && HostCustomCheckBox.IsChecked == true;
        TaskCustomCheckBox.IsEnabled = TaskEnabledCheckBox.IsChecked == true;
        TaskFieldsGrid.IsEnabled = TaskEnabledCheckBox.IsChecked == true && TaskCustomCheckBox.IsChecked == true;
    }

    private void CommitActiveTargetToDraft()
    {
        if (_activeItem is null) { return; }
        switch (_activeItem.Kind)
        {
            case TargetKind.Global:
                _draft = (_draft with
                {
                    Enabled = GlobalEnabledCheckBox.IsChecked == true,
                    HostCpuPercent = ParsePercent(GlobalHostCpuTextBox.Text, "全局主机 CPU 告警比例"),
                    HostMemoryPercent = ParsePercent(GlobalHostMemTextBox.Text, "全局主机内存告警比例"),
                    HostStoragePercent = ParsePercent(GlobalHostStorageTextBox.Text, "全局主机磁盘告警比例"),
                    ProcessCpuPercent = ParsePercent(GlobalProcCpuTextBox.Text, "全局服务 CPU 告警比例"),
                    ProcessMemoryPercent = ParsePercent(GlobalProcMemTextBox.Text, "全局服务内存占比告警"),
                    ProcessMemoryMb = ParseMemoryMb(GlobalProcMemMbTextBox.Text, "全局服务内存上限"),
                    CpuSustainSeconds = ParseSustainSeconds(GlobalSustainTextBox.Text, "全局 CPU 持续时间"),
                    NotificationCooldownMinutes = ParseCooldownMinutes(GlobalCooldownTextBox.Text, "告警通知冷却间隔"),
                }).CloneNormalized();
                break;

            case TargetKind.Host:
            {
                var hosts = new Dictionary<string, HostAlertOverride>(_draft.HostOverrides, StringComparer.OrdinalIgnoreCase);
                var enabled = HostEnabledCheckBox.IsChecked == true;
                var custom = HostCustomCheckBox.IsChecked == true;
                bool? enabledOverride = enabled == _draft.Enabled ? null : enabled;
                HostAlertOverride rule;
                if (custom)
                {
                    rule = new HostAlertOverride
                    {
                        Enabled = enabledOverride,
                        CpuPercent = ParsePercent(HostCpuTextBox.Text, "主机 CPU 告警比例"),
                        MemoryPercent = ParsePercent(HostMemTextBox.Text, "主机内存告警比例"),
                        StoragePercent = ParsePercent(HostStorageTextBox.Text, "主机磁盘告警比例"),
                        CpuSustainSeconds = ParseSustainSeconds(HostSustainTextBox.Text, "主机 CPU 持续时间"),
                    };
                }
                else
                {
                    rule = new HostAlertOverride { Enabled = enabledOverride };
                }

                if (rule.IsDefault)
                {
                    hosts.Remove(_activeItem.TargetId);
                }
                else
                {
                    hosts[_activeItem.TargetId] = rule;
                }

                _draft = (_draft with { HostOverrides = hosts }).CloneNormalized();
                break;
            }

            case TargetKind.Task:
            {
                var tasks = new Dictionary<string, TaskAlertOverride>(_draft.TaskOverrides, StringComparer.OrdinalIgnoreCase);
                var enabled = TaskEnabledCheckBox.IsChecked == true;
                var custom = TaskCustomCheckBox.IsChecked == true;
                bool? enabledOverride = enabled == _draft.Enabled ? null : enabled;
                TaskAlertOverride rule;
                if (custom)
                {
                    rule = new TaskAlertOverride
                    {
                        Enabled = enabledOverride,
                        CpuPercent = ParsePercent(TaskCpuTextBox.Text, "服务 CPU 告警比例"),
                        MemoryPercent = ParsePercent(TaskMemTextBox.Text, "服务内存占比告警"),
                        MemoryMb = ParseMemoryMb(TaskMemMbTextBox.Text, "服务内存上限"),
                        CpuSustainSeconds = ParseSustainSeconds(TaskSustainTextBox.Text, "服务 CPU 持续时间"),
                    };
                }
                else
                {
                    rule = new TaskAlertOverride { Enabled = enabledOverride };
                }

                if (rule.IsDefault)
                {
                    tasks.Remove(_activeItem.TargetId);
                }
                else
                {
                    tasks[_activeItem.TargetId] = rule;
                }

                _draft = (_draft with { TaskOverrides = tasks }).CloneNormalized();
                break;
            }
        }

        RefreshAllBadges();
    }

    private void ResetDefault_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _draft = new ResourceAlertSettings();
        RefreshAllBadges();
        if (_activeItem is not null)
        {
            RenderTarget(_activeItem);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_saving) { return; }

        try
        {
            CommitActiveTargetToDraft();
            ResourceAlertSettingsValidator.Validate(_draft);
            SavedSettings = _draft;
            if (_save is not null)
            {
                _saving = true;
                SaveButton.IsEnabled = false;
                ResetDefaultButton.IsEnabled = false;
                try
                {
                    await _save(_draft);
                }
                finally
                {
                    _saving = false;
                    if (IsPresentationActive)
                    {
                        SaveButton.IsEnabled = true;
                        ResetDefaultButton.IsEnabled = true;
                    }
                }
            }

            if (IsPresentationActive)
            {
                try
                {
                    DialogResult = true;
                }
                catch (InvalidOperationException)
                {
                    Close();
                }
            }
        }
        catch (Exception exception)
        {
            if (!IsPresentationActive) { return; }
            MessageDialog.ShowError(
                this,
                exception is ArgumentException ? "报警阈值配置无效" : "报警阈值未保存",
                exception is ArgumentException ? exception.Message : AgentErrorMessages.Format(exception));
        }
    }

    private static string FormatPercent(double value) =>
        value.ToString("0.#", CultureInfo.InvariantCulture);

    private static double ParsePercent(string text, string label)
    {
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value) || double.IsInfinity(value) || value < 1 || value > 100)
        {
            throw new ArgumentException($"{label} 必须是 1 到 100 之间的数字。");
        }

        return Math.Round(value, 1);
    }

    private static int ParseMemoryMb(string text, string label)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < 0 || value > 1_048_576)
        {
            throw new ArgumentException($"{label} 必须是 0 到 1048576 之间的整数。");
        }

        return value;
    }

    private static int ParseSustainSeconds(string text, string label)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < 0 || value > 3600)
        {
            throw new ArgumentException($"{label} 必须是 0 到 3600 之间的整数。");
        }

        return value;
    }

    private static int ParseCooldownMinutes(string text, string label)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < 0 || value > 1440)
        {
            throw new ArgumentException($"{label} 必须是 0 到 1440 之间的整数。");
        }

        return value;
    }
}
