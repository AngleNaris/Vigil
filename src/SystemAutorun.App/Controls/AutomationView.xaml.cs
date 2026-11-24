using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;
using UserControl = System.Windows.Controls.UserControl;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Models;

namespace SystemAutorun.App.Controls;

public partial class AutomationView : UserControl
{
    private static readonly TimeSpan TrendWindow = TimeSpan.FromMinutes(5);

    private sealed class RuleRowItem(AutoRestartRule rule) : INotifyPropertyChanged
    {
        private AutoRestartRule _rule = rule;
        private bool _hasAlert;
        private bool _globalEnabled;

        public AutoRestartRule Rule
        {
            get => _rule;
            set
            {
                _rule = value;
                NotifyAll();
            }
        }

        public bool HasAlert
        {
            get => _hasAlert;
            set
            {
                if (_hasAlert == value) { return; }
                _hasAlert = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlertVisibility)));
            }
        }

        public bool GlobalEnabled
        {
            get => _globalEnabled;
            set
            {
                if (_globalEnabled == value) { return; }
                _globalEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabledRule)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Badge)));
            }
        }

        public string Id => _rule.Id;
        public string Name => string.IsNullOrWhiteSpace(_rule.Name) ? _rule.Id : _rule.Name;
        public bool IsEnabledRule => _rule.Enabled && _globalEnabled;
        public string Badge => !_rule.Enabled ? "手动" : _globalEnabled ? "自动" : "暂停";
        public Visibility AlertVisibility => _hasAlert ? Visibility.Visible : Visibility.Collapsed;
        public string Summary => _rule.TargetKind == AutoRestartTargetKind.ManagedTask
            ? $"服务 · {_rule.TaskName}"
            : string.IsNullOrWhiteSpace(_rule.CommandLineContains)
                ? $"进程 · {_rule.ProcessName}.exe"
                : $"进程 · {_rule.ProcessName}.exe ({_rule.CommandLineContains})";

        public void NotifyAll()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabledRule)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Badge)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlertVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ObservableCollection<RuleRowItem> _items = [];
    private Func<Task<ConfigurationWorkspaceSnapshot>>? _loadWorkspace;
    private Func<AutoRestartSettings, ConfigurationWorkspaceSnapshot, Task<ConfigurationWorkspaceSnapshot>>? _saveSettings;
    private Func<Task<AutoRestartStatusSnapshot?>>? _readStatus;
    private Func<string, Task<AutoRestartRunResult>>? _runNow;
    private Func<bool, Task<bool>>? _toggleMaintenance;
    private ConfigurationWorkspaceSnapshot? _snapshot;
    private IReadOnlyList<AutorunTask> _tasks = [];
    private AutoRestartStatusSnapshot? _status;
    private bool _globalEnabled;
    private RuleRowItem? _activeItem;
    private bool _busy;
    private bool _loadedOnce;
    private bool _updatingSelection;
    private int _mutationGeneration;

    public AutomationView()
    {
        InitializeComponent();
        RuleList.ItemsSource = _items;
        RenderCurrentSelection(playTransition: false);
    }

    internal void Bind(
        Func<Task<ConfigurationWorkspaceSnapshot>> loadWorkspace,
        Func<AutoRestartSettings, ConfigurationWorkspaceSnapshot, Task<ConfigurationWorkspaceSnapshot>> saveSettings,
        Func<Task<AutoRestartStatusSnapshot?>> readStatus,
        Func<string, Task<AutoRestartRunResult>> runNow,
        Func<bool, Task<bool>> toggleMaintenance)
    {
        _loadWorkspace = loadWorkspace;
        _saveSettings = saveSettings;
        _readStatus = readStatus;
        _runNow = runNow;
        _toggleMaintenance = toggleMaintenance;
    }

    internal void PlayEntranceAnimation()
    {
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (IsVisible && AutomationRoot.IsLoaded)
            {
                LineMotion.Play(AutomationRoot);
            }
        }));
    }

    internal void LoadDirectForTest(
        AutoRestartSettings settings,
        IReadOnlyList<AutorunTask> tasks,
        AutoRestartStatusSnapshot? status,
        Func<AutoRestartSettings, Task>? save = null,
        Func<string, Task<AutoRestartRunResult>>? runNow = null,
        Func<bool, Task<bool>>? toggleMaintenance = null)
    {
        _tasks = tasks ?? [];
        _status = status;
        _loadedOnce = true;
        _mutationGeneration++;
        _saveSettings = save is null
            ? null
            : async (draft, snap) =>
            {
                await save(draft);
                var updated = snap with { Preferences = snap.Preferences with { AutoRestartSettings = draft } };
                _snapshot = updated;
                return updated;
            };
        _runNow = runNow;
        _toggleMaintenance = toggleMaintenance;
        _readStatus = () => Task.FromResult(_status);
        _snapshot = new ConfigurationWorkspaceSnapshot(
            new string('0', 64),
            DateTimeOffset.UtcNow,
            new AutorunConfiguration(1, new AutorunSettings("logs", "backups", 0), _tasks, Environment.CurrentDirectory),
            new ApplicationSettings { AutoRestartSettings = settings });
        _loadWorkspace = () => Task.FromResult(_snapshot);

        ApplyLoadedSettings(settings, focusTaskName: null);
        RenderCurrentSelection(playTransition: false);
    }

    internal async Task RefreshAsync(string? focusTaskName = null, bool forceReloadConfig = false)
    {
        if (_loadWorkspace is null) { return; }
        var generation = _mutationGeneration;
        try
        {
            var statusTask = _readStatus is not null ? _readStatus() : Task.FromResult<AutoRestartStatusSnapshot?>(null);
            if (!_loadedOnce || forceReloadConfig)
            {
                var workspace = await _loadWorkspace();
                _status = await statusTask;
                if (generation == _mutationGeneration)
                {
                    _snapshot = workspace;
                    _tasks = workspace.Configuration.Tasks;
                    _loadedOnce = true;
                    ApplyLoadedSettings(workspace.Preferences.AutoRestartSettings, focusTaskName);
                }
            }
            else
            {
                _status = await statusTask;
                SyncRowAlertsFromStatus();
                if (!string.IsNullOrWhiteSpace(focusTaskName))
                {
                    await FocusOrCreateTaskRuleAsync(focusTaskName);
                }
            }

            RenderCurrentSelection(playTransition: false);
        }
        catch (Exception exception)
        {
            LastResultNoticeText.Text = $"读取失败：{AgentErrorMessages.Format(exception)}";
        }
    }

    private void ApplyLoadedSettings(AutoRestartSettings? settings, string? focusTaskName)
    {
        var previousId = _activeItem?.Id;
        var normalized = (settings ?? new AutoRestartSettings()).CloneNormalized();
        _globalEnabled = normalized.Enabled;

        _updatingSelection = true;
        try
        {
            RebuildAddFromTaskSubmenu();
            _items.Clear();
            foreach (var rule in normalized.Rules)
            {
                _items.Add(new RuleRowItem(rule) { GlobalEnabled = _globalEnabled });
            }
            SyncRowAlertsFromStatus();

            RuleRowItem? target = null;
            if (!string.IsNullOrWhiteSpace(focusTaskName))
            {
                target = _items.FirstOrDefault(i =>
                    i.Rule.TargetKind == AutoRestartTargetKind.ManagedTask
                    && string.Equals(i.Rule.TaskName, focusTaskName, StringComparison.OrdinalIgnoreCase));
            }

            target ??= !string.IsNullOrWhiteSpace(previousId)
                ? _items.FirstOrDefault(i => string.Equals(i.Id, previousId, StringComparison.OrdinalIgnoreCase))
                : null;
            target ??= _items.FirstOrDefault();

            _activeItem = target;
            RuleList.SelectedItem = target;
        }
        finally
        {
            _updatingSelection = false;
        }

        if (!string.IsNullOrWhiteSpace(focusTaskName) && _activeItem?.Rule.TaskName != focusTaskName)
        {
            _ = FocusOrCreateTaskRuleAsync(focusTaskName);
        }
    }

    private void SyncRowAlertsFromStatus()
    {
        if (_status is null) { return; }
        foreach (var item in _items)
        {
            item.GlobalEnabled = _globalEnabled;
            var rs = _status.Rules.FirstOrDefault(r => string.Equals(r.RuleId, item.Id, StringComparison.OrdinalIgnoreCase));
            item.HasAlert = rs is not null && (rs.BreachedSinceUtc is not null || rs.LastSucceeded == false);
        }
    }

    private void RebuildAddFromTaskSubmenu()
    {
        AddFromTaskMenuItem.Items.Clear();
        if (_tasks.Count == 0)
        {
            AddFromTaskMenuItem.Items.Add(new MenuItem { Header = "（暂无已纳管服务）", IsEnabled = false });
            return;
        }

        foreach (var task in _tasks)
        {
            var item = new MenuItem { Header = task.Name, Tag = task.Name };
            item.Click += async (_, _) => await FocusOrCreateTaskRuleAsync(task.Name);
            AddFromTaskMenuItem.Items.Add(item);
        }
    }

    private async Task FocusOrCreateTaskRuleAsync(string taskName)
    {
        var existing = _items.FirstOrDefault(i =>
            i.Rule.TargetKind == AutoRestartTargetKind.ManagedTask
            && string.Equals(i.Rule.TaskName, taskName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RuleList.SelectedItem = existing;
            return;
        }

        var draft = new AutoRestartRule
        {
            Id = GenerateUniqueId($"task-{taskName}"),
            Name = $"服务重启 ({taskName})",
            Enabled = false,
            TargetKind = AutoRestartTargetKind.ManagedTask,
            TaskName = taskName,
            ActionMode = AutoRestartActionMode.RestartTask,
            ScheduleKind = AutoRestartScheduleKind.Daily,
            DailyTime = "04:00",
            SustainSeconds = 60,
            CooldownMinutes = 30,
        }.Normalize();

        await OpenRuleEditorAsync(draft, isNew: true);
    }

    private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_updatingSelection) { return; }
        var selected = RuleList.SelectedItem as RuleRowItem;
        var changed = !ReferenceEquals(_activeItem, selected);
        _activeItem = selected;
        RenderCurrentSelection(playTransition: changed);
    }

    private void RuleList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _busy) { return; }
        var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is RuleRowItem item)
        {
            RuleList.SelectedItem = item;
            _ = OpenRuleEditorAsync(item.Rule, isNew: false);
            e.Handled = true;
        }
    }

    private void RuleList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is RuleRowItem item)
        {
            RuleList.SelectedItem = item;
            ContextToggleAutoItem.Header = item.Rule.Enabled ? "停用自动触发（改为仅手动）" : "启用自动触发";
        }
    }

    private void AddRuleMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) { return; }
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void MoreMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) { return; }
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void RuleMoreMenu_Opened(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_activeItem is { } active)
        {
            MoreToggleAutoMenuItem.Header = active.Rule.Enabled ? "停用自动触发（改为仅手动）" : "启用自动触发";
        }
        MoreGlobalWatchMenuItem.IsChecked = _globalEnabled;
        if (_status is null)
        {
            MoreMaintenanceMenuItem.Header = "提权维护通道：未检测";
            MoreMaintenanceMenuItem.IsEnabled = false;
        }
        else if (_status.IsAgentElevated)
        {
            MoreMaintenanceMenuItem.Header = "提权维护通道：Agent 已处于管理员权限";
            MoreMaintenanceMenuItem.IsEnabled = false;
        }
        else if (_status.MaintenanceTaskInstalled)
        {
            MoreMaintenanceMenuItem.Header = "卸载提权维护通道…";
            MoreMaintenanceMenuItem.IsEnabled = !_busy;
        }
        else
        {
            MoreMaintenanceMenuItem.Header = "启用提权维护通道（一次 UAC）…";
            MoreMaintenanceMenuItem.IsEnabled = !_busy;
        }
    }

    private async void AddCustomRule_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var draft = new AutoRestartRule
        {
            Id = GenerateUniqueId("rule"),
            Name = $"进程维护 {_items.Count + 1}",
            Enabled = false,
            TargetKind = AutoRestartTargetKind.Process,
            ProcessName = "notepad",
            ActionMode = AutoRestartActionMode.TerminateAndRelaunch,
            ScheduleKind = AutoRestartScheduleKind.None,
            SustainSeconds = 60,
            CooldownMinutes = 30,
        }.Normalize();
        await OpenRuleEditorAsync(draft, isNew: true);
    }

    private async void AddPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string presetId }) { return; }
        var existing = _items.FirstOrDefault(i => string.Equals(i.Id, presetId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RuleList.SelectedItem = existing;
            return;
        }

        var preset = AutoRestartSettings.CreateBuiltInPresets()
            .FirstOrDefault(p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));
        if (preset is null) { return; }

        await PersistRulesAsync([.. _items.Select(i => i.Rule), preset], _globalEnabled, selectRuleId: preset.Id);
    }

    private async void ImportAllPresets_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var merged = _items.Select(i => i.Rule).ToList();
        string? firstAdded = null;
        foreach (var preset in AutoRestartSettings.CreateBuiltInPresets())
        {
            if (merged.Any(r => string.Equals(r.Id, preset.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            merged.Add(preset);
            firstAdded ??= preset.Id;
        }

        await PersistRulesAsync(merged, _globalEnabled, selectRuleId: firstAdded ?? _activeItem?.Id);
    }

    private async void EditRule_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_activeItem is null || _busy) { return; }
        await OpenRuleEditorAsync(_activeItem.Rule, isNew: false);
    }

    private async Task OpenRuleEditorAsync(AutoRestartRule rule, bool isNew)
    {
        var owner = Window.GetWindow(this);
        var dialog = new AutoRestartRuleWindow(rule, _tasks, isNew) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.ResultRule is not { } edited)
        {
            return;
        }

        var updatedList = _items.Select(i => i.Rule).ToList();
        var index = updatedList.FindIndex(r => string.Equals(r.Id, edited.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            updatedList[index] = edited;
        }
        else
        {
            updatedList.Add(edited);
        }

        // Enabling a rule automatically activates background watching so the user is never stuck by a hidden switch.
        var nextGlobal = _globalEnabled || edited.Enabled;
        await PersistRulesAsync(updatedList, nextGlobal, selectRuleId: edited.Id);
    }

    private async void ToggleAuto_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_activeItem is null || _busy) { return; }

        var current = _activeItem.Rule;
        var nextEnabled = !current.Enabled;
        var updated = current with { Enabled = nextEnabled };
        var rules = _items.Select(i => ReferenceEquals(i, _activeItem) ? updated : i.Rule).ToArray();
        var nextGlobal = _globalEnabled || nextEnabled;

        await PersistRulesAsync(rules, nextGlobal, selectRuleId: current.Id);
    }

    private async void ToggleGlobalWatch_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_busy) { return; }
        await PersistRulesAsync(_items.Select(i => i.Rule).ToArray(), !_globalEnabled, selectRuleId: _activeItem?.Id);
    }

    private async void RunOnce_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_activeItem is null || _runNow is null || _busy) { return; }

        var rule = _activeItem.Rule;
        SetBusy(true);
        try
        {
            LastResultNoticeText.Text = $"正在执行「{rule.Name}」…";
            var result = await _runNow(rule.Id);
            if (_readStatus is not null)
            {
                _status = await _readStatus();
                SyncRowAlertsFromStatus();
            }

            RenderCurrentSelection(playTransition: false);
            LastResultNoticeText.Text = result.Succeeded
                ? $"已执行一次：{result.Message}"
                : $"执行失败：{result.Message}";
            if (!result.Succeeded)
            {
                MessageDialog.ShowError(Window.GetWindow(this), "立即执行未完成", result.Message);
            }
        }
        catch (Exception exception)
        {
            MessageDialog.ShowError(Window.GetWindow(this), "立即执行失败", AgentErrorMessages.Format(exception));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_activeItem is null || _busy) { return; }
        var target = _activeItem;
        if (!MessageDialog.ShowConfirm(Window.GetWindow(this), "删除自动化规则", $"确定删除规则「{target.Name}」吗？正在运行的进程不会被停止。"))
        {
            return;
        }

        var remaining = _items.Where(i => !ReferenceEquals(i, target)).Select(i => i.Rule).ToArray();
        await PersistRulesAsync(remaining, _globalEnabled, selectRuleId: remaining.FirstOrDefault()?.Id);
    }

    private async void MaintenanceAction_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_toggleMaintenance is null || _busy || _status is null || _status.IsAgentElevated) { return; }

        var install = !_status.MaintenanceTaskInstalled;
        SetBusy(true);
        try
        {
            var ok = await _toggleMaintenance(install);
            if (ok && _readStatus is not null)
            {
                _status = await _readStatus();
                RenderCurrentSelection(playTransition: false);
            }
        }
        catch (Exception exception)
        {
            MessageDialog.ShowError(Window.GetWindow(this), "提权维护通道操作失败", AgentErrorMessages.Format(exception));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task PersistRulesAsync(IReadOnlyList<AutoRestartRule> rules, bool globalEnabled, string? selectRuleId)
    {
        if (_saveSettings is null || _snapshot is null)
        {
            return;
        }

        SetBusy(true);
        _mutationGeneration++;
        try
        {
            var draft = new AutoRestartSettings
            {
                Enabled = globalEnabled,
                Rules = rules.Select(r => r.Normalize()).ToArray(),
            }.CloneNormalized();
            AutoRestartSettingsValidator.Validate(draft);

            _snapshot = await _saveSettings(draft, _snapshot);
            _globalEnabled = draft.Enabled;
            if (_readStatus is not null)
            {
                _status = await _readStatus();
            }

            _updatingSelection = true;
            try
            {
                _items.Clear();
                foreach (var rule in draft.Rules)
                {
                    _items.Add(new RuleRowItem(rule) { GlobalEnabled = _globalEnabled });
                }
                SyncRowAlertsFromStatus();
                _activeItem = !string.IsNullOrWhiteSpace(selectRuleId)
                    ? _items.FirstOrDefault(i => string.Equals(i.Id, selectRuleId, StringComparison.OrdinalIgnoreCase)) ?? _items.FirstOrDefault()
                    : _items.FirstOrDefault();
                RuleList.SelectedItem = _activeItem;
            }
            finally
            {
                _updatingSelection = false;
            }

            RenderCurrentSelection(playTransition: false);
        }
        catch (Exception exception)
        {
            MessageDialog.ShowError(Window.GetWindow(this), "保存规则失败", AgentErrorMessages.Format(exception));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RenderCurrentSelection(bool playTransition)
    {
        RuleCountText.Text = $"{_items.Count}条规则";
        var hasRule = _activeItem is not null;
        SidebarEmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = hasRule ? Visibility.Collapsed : Visibility.Visible;
        RuleDashboardGrid.Visibility = hasRule ? Visibility.Visible : Visibility.Collapsed;

        if (_activeItem is not { } item)
        {
            return;
        }

        var rule = item.Rule;
        var rs = _status?.Rules.FirstOrDefault(r => string.Equals(r.RuleId, rule.Id, StringComparison.OrdinalIgnoreCase));

        // Scope DataChangePulse & SurfaceMotion transition to the active rule
        SurfaceMotion.SetTransitionKey(RuleDetailSurface, rule.Id);
        DataChangePulse.SetContext(RuleDetailSurface, rule.Id);
        if (playTransition && IsVisible && RuleDetailSurface.IsLoaded)
        {
            LineMotion.Transition(RuleDetailSurface);
        }

        // Header
        HeaderIcon.Kind = rule.TargetKind == AutoRestartTargetKind.ManagedTask
            ? "Service"
            : rule.RequireElevation ? "Shield" : "Windows";
        HeaderTitleText.Text = item.Name;
        HeaderRuleSummaryText.Text = BuildRuleConditionSummary(rule);

        // Crystal-clear Auto-Trigger Switch state (use native DashboardHeaderChoice Tag=True active styling so hover text transitions to white cleanly)
        var autoActive = rule.Enabled && _globalEnabled;
        AutoToggleIcon.Kind = autoActive ? "Pause" : "Play";
        AutoToggleText.Text = !rule.Enabled
            ? "自动：已停用"
            : _globalEnabled ? "自动：已启用" : "自动：全局暂停";
        AutoToggleButton.Tag = autoActive;

        // Row 1: State & Last Trigger Summary
        var stateLabel = BuildStateLabel(rule, rs, _globalEnabled);
        StateReadoutText.Text = stateLabel;
        DataChangePulse.SetValue(StateReadoutText, stateLabel);

        LastResultNoticeText.Text = BuildLastRunSummary(rs);

        // Row 2: 2x2 Live Telemetry Readouts + Peaks + Thresholds + Animated Plots
        var cpuText = rs?.LiveCpuPercent is { } cpu ? $"{cpu:0.0}%" : "--";
        var cpuPeak = rs?.PeakCpuPercent is { } pCpu ? $"峰值 {pCpu:0.0}%" : "峰值 --";
        SurfaceMotion.SetRollingText(CpuReadout, cpuText);
        DataChangePulse.SetValue(CpuReadout, cpuText);
        CpuPeakText.Text = rule.CpuPercentThreshold is > 0 ? $"{cpuPeak} · 阈值 {rule.CpuPercentThreshold:0.#}%" : cpuPeak;
        DataChangePulse.SetValue(CpuPeakText, CpuPeakText.Text);

        var memText = rs?.LiveWorkingSetBytes is { } mem ? FormatBytes(mem) : "--";
        var memPeak = rs?.PeakWorkingSetBytes is { } pMem ? $"峰值 {FormatBytes(pMem)}" : "峰值 --";
        SurfaceMotion.SetRollingText(MemoryReadout, memText);
        DataChangePulse.SetValue(MemoryReadout, memText);
        MemoryPeakText.Text = rule.MemoryMbThreshold is > 0 ? $"{memPeak} · 阈值 {rule.MemoryMbThreshold} MB" : memPeak;
        DataChangePulse.SetValue(MemoryPeakText, MemoryPeakText.Text);

        var handlesText = rs?.LiveHandleCount is { } h ? h.ToString("N0", CultureInfo.CurrentCulture) : "--";
        var handlesMeta = rs?.PeakHandleCount is { } pH ? $"峰值 {pH:N0}" : "峰值 --";
        if (rule.HandleCountThreshold is > 0)
        {
            handlesMeta += $" · 阈值 {rule.HandleCountThreshold:N0}";
        }
        else if (rs?.LiveGdiHandleCount is not null || rs?.LiveUserHandleCount is not null)
        {
            handlesMeta += $" · GDI {rs.LiveGdiHandleCount ?? 0} / USER {rs.LiveUserHandleCount ?? 0}";
        }
        SurfaceMotion.SetRollingText(HandlesReadout, handlesText);
        DataChangePulse.SetValue(HandlesReadout, handlesText);
        HandlesPeakText.Text = handlesMeta;
        DataChangePulse.SetValue(HandlesPeakText, handlesMeta);

        var inactiveText = rs?.LiveInactiveHandleCount is { } ih ? ih.ToString("N0", CultureInfo.CurrentCulture) : "--";
        var inactiveMeta = rs?.PeakInactiveHandleCount is { } pIh ? $"峰值 {pIh:N0}" : "峰值 --";
        if (rule.InactiveHandleCountThreshold is > 0)
        {
            inactiveMeta += $" · 阈值 {rule.InactiveHandleCountThreshold:N0}";
        }
        else if (rs is not null)
        {
            inactiveMeta += rs.LiveThreadCount is > 0
                ? $" · {rs.MatchedProcessCount} 实例 · {rs.LiveThreadCount} 线程"
                : $" · {rs.MatchedProcessCount} 实例";
        }
        SurfaceMotion.SetRollingText(InactiveHandlesReadout, inactiveText);
        DataChangePulse.SetValue(InactiveHandlesReadout, inactiveText);
        InactiveHandlesPeakText.Text = inactiveMeta;
        DataChangePulse.SetValue(InactiveHandlesPeakText, inactiveMeta);

        // Plots (with live sweep, dynamic scale, hover readout, and threshold reference line)
        RenderPlots(rule, rs);
    }

    private void RenderPlots(AutoRestartRule rule, AutoRestartRuleStatus? rs)
    {
        var now = _status?.CapturedAt ?? DateTimeOffset.UtcNow;
        var samples = rs?.Samples ?? [];
        var endAt = samples.Count > 0 && samples[^1].CapturedAt > now ? samples[^1].CapturedAt : now;
        var scaleKey = $"automation:{rule.Id}";

        var cpuPoints = new List<PlotPoint>(samples.Count);
        var memPoints = new List<PlotPoint>(samples.Count);
        var handlePoints = new List<PlotPoint>(samples.Count);
        var inactivePoints = new List<PlotPoint>(samples.Count);
        var intervals = new List<HealthInterval>(samples.Count);

        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            cpuPoints.Add(new PlotPoint(s.CapturedAt, s.CpuPercent));
            memPoints.Add(new PlotPoint(s.CapturedAt, s.WorkingSetBytes));
            handlePoints.Add(new PlotPoint(s.CapturedAt, s.HandleCount));
            inactivePoints.Add(new PlotPoint(s.CapturedAt, s.InactiveHandleCount));

            var nextAt = i + 1 < samples.Count
                ? samples[i + 1].CapturedAt
                : s.CapturedAt.AddSeconds(10);
            if (nextAt > endAt) { nextAt = endAt; }
            if (nextAt <= s.CapturedAt) { nextAt = s.CapturedAt.AddSeconds(2); }
            var state = s.Breaching ? "failed" : s.MatchedProcessCount > 0 ? "healthy" : "unknown";
            intervals.Add(new HealthInterval(s.CapturedAt, nextAt, state));
        }

        RuleHealthPlot.ScaleKey = scaleKey;
        RuleHealthPlot.EndAt = endAt;
        RuleHealthPlot.Intervals = intervals;
        RuleHealthPlot.DataAt = endAt;

        CpuPlot.ScaleKey = scaleKey;
        CpuPlot.WindowDuration = TrendWindow;
        CpuPlot.EndAt = endAt;
        CpuPlot.Threshold = rule.CpuPercentThreshold;
        CpuPlot.Points = cpuPoints;
        CpuPlot.DataAt = endAt;

        MemoryPlot.ScaleKey = scaleKey;
        MemoryPlot.WindowDuration = TrendWindow;
        MemoryPlot.EndAt = endAt;
        MemoryPlot.Threshold = rule.MemoryMbThreshold is > 0 ? rule.MemoryMbThreshold.Value * 1024d * 1024d : null;
        MemoryPlot.Points = memPoints;
        MemoryPlot.DataAt = endAt;

        HandlesPlot.ScaleKey = scaleKey;
        HandlesPlot.WindowDuration = TrendWindow;
        HandlesPlot.EndAt = endAt;
        HandlesPlot.Threshold = rule.HandleCountThreshold;
        HandlesPlot.Points = handlePoints;
        HandlesPlot.DataAt = endAt;

        InactiveHandlesPlot.ScaleKey = scaleKey;
        InactiveHandlesPlot.WindowDuration = TrendWindow;
        InactiveHandlesPlot.EndAt = endAt;
        InactiveHandlesPlot.Threshold = rule.InactiveHandleCountThreshold;
        InactiveHandlesPlot.Points = inactivePoints;
        InactiveHandlesPlot.DataAt = endAt;
    }

    private static string BuildRuleConditionSummary(AutoRestartRule rule)
    {
        var parts = new List<string>(5);
        parts.Add(rule.TargetKind == AutoRestartTargetKind.ManagedTask
            ? $"服务 {rule.TaskName}"
            : string.IsNullOrWhiteSpace(rule.CommandLineContains)
                ? $"{rule.ProcessName}.exe"
                : $"{rule.ProcessName}.exe ({rule.CommandLineContains})");

        if (rule.ScheduleKind == AutoRestartScheduleKind.Daily && !string.IsNullOrWhiteSpace(rule.DailyTime))
        {
            parts.Add($"每日 {rule.DailyTime}");
        }
        else if (rule.ScheduleKind == AutoRestartScheduleKind.Interval && rule.IntervalMinutes is > 0)
        {
            parts.Add($"每 {rule.IntervalMinutes}m");
        }

        if (rule.CpuPercentThreshold is > 0) { parts.Add($"CPU ≥ {rule.CpuPercentThreshold:0.#}%"); }
        if (rule.MemoryMbThreshold is > 0) { parts.Add($"内存 ≥ {rule.MemoryMbThreshold} MB"); }
        if (rule.HandleCountThreshold is > 0) { parts.Add($"句柄 ≥ {rule.HandleCountThreshold:N0}"); }
        if (rule.InactiveHandleCountThreshold is > 0) { parts.Add($"不活跃句柄 ≥ {rule.InactiveHandleCountThreshold:N0}"); }
        if (parts.Count == 1) { parts.Add("仅手动触发"); }
        return string.Join(" · ", parts);
    }

    private static string BuildStateLabel(AutoRestartRule rule, AutoRestartRuleStatus? rs, bool globalEnabled)
    {
        if (!rule.Enabled) { return "仅手动（自动触发已停用）"; }
        if (!globalEnabled) { return "全局巡检已暂停"; }
        if (rs is null) { return "等待后台采样"; }
        if (rs.CooldownUntilUtc is { } cd && cd > DateTimeOffset.UtcNow)
        {
            var remain = Math.Max(1, (int)Math.Ceiling((cd - DateTimeOffset.UtcNow).TotalMinutes));
            return $"冷却中（剩余 {remain}m）";
        }
        if (rs.BreachedSinceUtc is { } since)
        {
            var elapsed = Math.Max(0, (int)(DateTimeOffset.UtcNow - since).TotalSeconds);
            return $"阈值超标蓄力 {elapsed}s / {rule.SustainSeconds}s";
        }
        return rs.MatchedProcessCount > 0 ? $"自动巡检中（{rs.MatchedProcessCount} 个目标）" : "自动巡检中（未发现进程）";
    }

    private static string BuildLastRunSummary(AutoRestartRuleStatus? rs)
    {
        if (rs?.LastTriggeredAt is not { } at) { return string.Empty; }
        var state = rs.LastSucceeded == true ? "成功" : "失败";
        return $"上次 {at.ToLocalTime():MM-dd HH:mm} · {rs.LastTriggerReason} ({state})";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        AutoToggleButton.IsEnabled = !busy;
        RunOnceButton.IsEnabled = !busy;
        RuleMoreButton.IsEnabled = !busy;
        AddRuleMenuButton.IsEnabled = !busy;
    }

    private string GenerateUniqueId(string prefix)
    {
        var sanitized = new string(prefix.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (string.IsNullOrWhiteSpace(sanitized)) { sanitized = "rule"; }
        if (sanitized.Length > 48) { sanitized = sanitized[..48]; }
        var candidate = sanitized;
        var suffix = 2;
        while (_items.Any(i => string.Equals(i.Id, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{sanitized}-{suffix++}";
        }
        return candidate;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null and not T)
        {
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return current as T;
    }
}
