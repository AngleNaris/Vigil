using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ComboBox = System.Windows.Controls.ComboBox;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Models;

namespace SystemAutorun.App.Dialogs;

public partial class AutoRestartRuleWindow : ToolWindow
{
    private readonly AutoRestartRule _original;
    private readonly IReadOnlyList<AutorunTask> _tasks;
    private bool _loading;

    internal AutoRestartRule? ResultRule { get; private set; }

    internal AutoRestartRuleWindow(AutoRestartRule rule, IReadOnlyList<AutorunTask> tasks, bool isNew)
    {
        InitializeComponent();
        _original = rule;
        _tasks = tasks ?? [];

        Title = isNew ? "新建自动化规则" : $"编辑规则 · {rule.Name}";
        HeaderTitleText.Text = Title;
        HeaderIcon.Kind = isNew ? "Plus" : "Settings";
        TaskNameComboBox.ItemsSource = _tasks.Select(t => t.Name).ToArray();

        LoadRule(rule);
    }

    private void LoadRule(AutoRestartRule rule)
    {
        _loading = true;
        try
        {
            RuleNameTextBox.Text = rule.Name;
            RuleEnabledCheckBox.IsChecked = rule.Enabled;
            SelectComboTag(TargetKindComboBox, rule.TargetKind == AutoRestartTargetKind.ManagedTask ? "managed_task" : "process");
            SelectComboTag(ActionModeComboBox, rule.ActionMode switch
            {
                AutoRestartActionMode.RestartTask => "restart_task",
                AutoRestartActionMode.TerminateAndRelaunch => "terminate_and_relaunch",
                _ => "terminate_only",
            });
            TaskNameComboBox.SelectedItem = rule.TaskName;
            ProcessNameTextBox.Text = rule.ProcessName ?? string.Empty;
            CommandLineFilterTextBox.Text = rule.CommandLineContains ?? string.Empty;
            RequireElevationCheckBox.IsChecked = rule.RequireElevation;
            ExecutablePathTextBox.Text = rule.ExecutablePath ?? string.Empty;
            ArgumentsTextBox.Text = rule.Arguments ?? string.Empty;
            WorkingDirTextBox.Text = rule.WorkingDirectory ?? string.Empty;
            RelaunchFieldsPanel.IsExpanded = !string.IsNullOrWhiteSpace(rule.ExecutablePath)
                || !string.IsNullOrWhiteSpace(rule.Arguments)
                || !string.IsNullOrWhiteSpace(rule.WorkingDirectory);

            SelectComboTag(ScheduleKindComboBox, rule.ScheduleKind switch
            {
                AutoRestartScheduleKind.Interval => "interval",
                AutoRestartScheduleKind.Daily => "daily",
                _ => "none",
            });
            IntervalMinutesTextBox.Text = (rule.IntervalMinutes ?? AutoRestartRule.DefaultIntervalMinutes).ToString(CultureInfo.InvariantCulture);
            DailyTimeTextBox.Text = rule.DailyTime ?? AutoRestartRule.DefaultDailyTime;
            CpuThresholdTextBox.Text = rule.CpuPercentThreshold is > 0 ? rule.CpuPercentThreshold.Value.ToString("0.#", CultureInfo.InvariantCulture) : string.Empty;
            MemoryMbThresholdTextBox.Text = rule.MemoryMbThreshold is > 0 ? rule.MemoryMbThreshold.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
            HandleThresholdTextBox.Text = rule.HandleCountThreshold is > 0 ? rule.HandleCountThreshold.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
            InactiveHandleThresholdTextBox.Text = rule.InactiveHandleCountThreshold is > 0 ? rule.InactiveHandleCountThreshold.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
            SustainSecondsTextBox.Text = rule.SustainSeconds.ToString(CultureInfo.InvariantCulture);
            CooldownMinutesTextBox.Text = rule.CooldownMinutes.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _loading = false;
        }

        UpdateFieldVisibility();
    }

    private void TargetOrAction_Changed(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_loading) { return; }

        var isTask = SelectedComboTag(TargetKindComboBox) == "managed_task";
        _loading = true;
        try
        {
            if (isTask)
            {
                SelectComboTag(ActionModeComboBox, "restart_task");
            }
            else if (SelectedComboTag(ActionModeComboBox) == "restart_task")
            {
                SelectComboTag(ActionModeComboBox, "terminate_only");
            }
        }
        finally
        {
            _loading = false;
        }

        UpdateFieldVisibility();
    }

    private void ScheduleKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_loading) { return; }
        UpdateFieldVisibility();
    }

    private void UpdateFieldVisibility()
    {
        var isTask = SelectedComboTag(TargetKindComboBox) == "managed_task";
        var actionTag = SelectedComboTag(ActionModeComboBox);
        var scheduleTag = SelectedComboTag(ScheduleKindComboBox);

        ManagedTaskPanel.Visibility = isTask ? Visibility.Visible : Visibility.Collapsed;
        ProcessTargetPanel.Visibility = isTask ? Visibility.Collapsed : Visibility.Visible;
        ActionModeComboBox.IsEnabled = !isTask;
        RelaunchFieldsPanel.Visibility = !isTask && actionTag == "terminate_and_relaunch" ? Visibility.Visible : Visibility.Collapsed;
        RequireElevationCheckBox.Visibility = !isTask ? Visibility.Visible : Visibility.Collapsed;

        IntervalPanel.Visibility = scheduleTag == "interval" ? Visibility.Visible : Visibility.Collapsed;
        DailyTimePanel.Visibility = scheduleTag == "daily" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ValidationText.Visibility = Visibility.Collapsed;
        try
        {
            var built = BuildRuleFromFields();
            AutoRestartSettingsValidator.ValidateRule(built);
            ResultRule = built;
            DialogResult = true;
            Close();
        }
        catch (Exception exception)
        {
            ValidationText.Text = exception.Message;
            ValidationText.Visibility = Visibility.Visible;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        DialogResult = false;
        Close();
    }

    private AutoRestartRule BuildRuleFromFields()
    {
        var isTask = SelectedComboTag(TargetKindComboBox) == "managed_task";
        var actionMode = isTask
            ? AutoRestartActionMode.RestartTask
            : SelectedComboTag(ActionModeComboBox) == "terminate_and_relaunch"
                ? AutoRestartActionMode.TerminateAndRelaunch
                : AutoRestartActionMode.TerminateOnly;
        var scheduleKind = SelectedComboTag(ScheduleKindComboBox) switch
        {
            "interval" => AutoRestartScheduleKind.Interval,
            "daily" => AutoRestartScheduleKind.Daily,
            _ => AutoRestartScheduleKind.None,
        };

        int? intervalMinutes = null;
        if (scheduleKind == AutoRestartScheduleKind.Interval)
        {
            if (!int.TryParse(IntervalMinutesTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInterval))
            {
                throw new ArgumentException("定时间隔分钟数必须是整数。");
            }
            intervalMinutes = parsedInterval;
        }

        double? cpu = ParseOptionalDouble(CpuThresholdTextBox.Text, "CPU 阈值");
        int? memMb = ParseOptionalInt(MemoryMbThresholdTextBox.Text, "内存阈值 (MB)");
        int? handles = ParseOptionalInt(HandleThresholdTextBox.Text, "总句柄阈值");
        int? inactiveHandles = ParseOptionalInt(InactiveHandleThresholdTextBox.Text, "不活跃句柄阈值");
        var sustain = ParseRequiredInt(SustainSecondsTextBox.Text, "持续超标秒数", AutoRestartRule.DefaultSustainSeconds);
        var cooldown = ParseRequiredInt(CooldownMinutesTextBox.Text, "冷却间隔分钟", AutoRestartRule.DefaultCooldownMinutes);

        return new AutoRestartRule
        {
            Id = _original.Id,
            Name = RuleNameTextBox.Text.Trim(),
            Enabled = RuleEnabledCheckBox.IsChecked == true,
            TargetKind = isTask ? AutoRestartTargetKind.ManagedTask : AutoRestartTargetKind.Process,
            TaskName = isTask ? TaskNameComboBox.SelectedItem as string : null,
            ProcessName = isTask ? null : ProcessNameTextBox.Text.Trim(),
            CommandLineContains = isTask ? null : NullIfBlank(CommandLineFilterTextBox.Text),
            ActionMode = actionMode,
            RequireElevation = !isTask && RequireElevationCheckBox.IsChecked == true,
            ExecutablePath = !isTask && actionMode == AutoRestartActionMode.TerminateAndRelaunch ? NullIfBlank(ExecutablePathTextBox.Text) : null,
            Arguments = !isTask && actionMode == AutoRestartActionMode.TerminateAndRelaunch ? NullIfBlank(ArgumentsTextBox.Text) : null,
            WorkingDirectory = !isTask && actionMode == AutoRestartActionMode.TerminateAndRelaunch ? NullIfBlank(WorkingDirTextBox.Text) : null,
            ScheduleKind = scheduleKind,
            IntervalMinutes = intervalMinutes,
            DailyTime = scheduleKind == AutoRestartScheduleKind.Daily ? DailyTimeTextBox.Text.Trim() : null,
            CpuPercentThreshold = cpu,
            MemoryMbThreshold = memMb,
            HandleCountThreshold = handles,
            InactiveHandleCountThreshold = inactiveHandles,
            SustainSeconds = sustain,
            CooldownMinutes = cooldown,
        }.Normalize();
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double? ParseOptionalDouble(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"{label} 格式无效，请输入数字。");
        }
        return value <= 0 ? null : value;
    }

    private static int? ParseOptionalInt(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"{label} 格式无效，请输入整数。");
        }
        return value <= 0 ? null : value;
    }

    private static int ParseRequiredInt(string text, string label, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) { return fallback; }
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"{label} 格式无效，请输入整数。");
        }
        return value;
    }

    private static void SelectComboTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private static string SelectedComboTag(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
}
