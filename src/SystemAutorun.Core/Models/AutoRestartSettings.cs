using System.Globalization;
using System.Text.Json.Serialization;

namespace SystemAutorun.Core.Models;

public enum AutoRestartTargetKind
{
    ManagedTask = 0,
    Process = 1,
}

public enum AutoRestartActionMode
{
    RestartTask = 0,
    TerminateOnly = 1,
    TerminateAndRelaunch = 2,
}

public enum AutoRestartScheduleKind
{
    None = 0,
    Interval = 1,
    Daily = 2,
}

public sealed record AutoRestartRule
{
    public const int DefaultIntervalMinutes = 1440;
    public const string DefaultDailyTime = "04:00";
    public const int DefaultSustainSeconds = 60;
    public const int DefaultCooldownMinutes = 30;

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("target_kind")]
    public AutoRestartTargetKind TargetKind { get; init; } = AutoRestartTargetKind.Process;

    [JsonPropertyName("task_name")]
    public string? TaskName { get; init; }

    [JsonPropertyName("process_name")]
    public string? ProcessName { get; init; }

    [JsonPropertyName("command_line_contains")]
    public string? CommandLineContains { get; init; }

    [JsonPropertyName("action_mode")]
    public AutoRestartActionMode ActionMode { get; init; } = AutoRestartActionMode.TerminateOnly;

    [JsonPropertyName("require_elevation")]
    public bool RequireElevation { get; init; }

    [JsonPropertyName("executable_path")]
    public string? ExecutablePath { get; init; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; init; }

    [JsonPropertyName("working_directory")]
    public string? WorkingDirectory { get; init; }

    [JsonPropertyName("schedule_kind")]
    public AutoRestartScheduleKind ScheduleKind { get; init; } = AutoRestartScheduleKind.None;

    [JsonPropertyName("interval_minutes")]
    public int? IntervalMinutes { get; init; }

    [JsonPropertyName("daily_time")]
    public string? DailyTime { get; init; }

    [JsonPropertyName("cpu_percent_threshold")]
    public double? CpuPercentThreshold { get; init; }

    [JsonPropertyName("memory_mb_threshold")]
    public int? MemoryMbThreshold { get; init; }

    [JsonPropertyName("handle_count_threshold")]
    public int? HandleCountThreshold { get; init; }

    [JsonPropertyName("inactive_handle_count_threshold")]
    public int? InactiveHandleCountThreshold { get; init; }

    [JsonPropertyName("sustain_seconds")]
    public int SustainSeconds { get; init; } = DefaultSustainSeconds;

    [JsonPropertyName("cooldown_minutes")]
    public int CooldownMinutes { get; init; } = DefaultCooldownMinutes;

    public bool HasScheduleTrigger => ScheduleKind != AutoRestartScheduleKind.None;

    public bool HasResourceTrigger =>
        CpuPercentThreshold is > 0
        || MemoryMbThreshold is > 0
        || HandleCountThreshold is > 0
        || InactiveHandleCountThreshold is > 0;

    public AutoRestartRule Normalize()
    {
        var targetKind = TargetKind;
        var actionMode = targetKind == AutoRestartTargetKind.ManagedTask
            ? AutoRestartActionMode.RestartTask
            : ActionMode == AutoRestartActionMode.RestartTask
                ? AutoRestartActionMode.TerminateOnly
                : ActionMode;
        var processName = targetKind == AutoRestartTargetKind.Process
            ? NormalizeProcessName(ProcessName)
            : null;
        var commandLine = targetKind == AutoRestartTargetKind.Process && !string.IsNullOrWhiteSpace(CommandLineContains)
            ? CommandLineContains.Trim()
            : null;
        var taskName = targetKind == AutoRestartTargetKind.ManagedTask && !string.IsNullOrWhiteSpace(TaskName)
            ? TaskName.Trim()
            : null;
        var requireElevation = targetKind == AutoRestartTargetKind.Process && RequireElevation;
        var relaunch = targetKind == AutoRestartTargetKind.Process
            && actionMode == AutoRestartActionMode.TerminateAndRelaunch;

        return this with
        {
            Id = Id?.Trim() ?? string.Empty,
            Name = Name?.Trim() ?? string.Empty,
            TargetKind = targetKind,
            TaskName = taskName,
            ProcessName = processName,
            CommandLineContains = commandLine,
            ActionMode = actionMode,
            RequireElevation = requireElevation,
            ExecutablePath = relaunch && !string.IsNullOrWhiteSpace(ExecutablePath) ? ExecutablePath.Trim() : null,
            Arguments = relaunch && !string.IsNullOrWhiteSpace(Arguments) ? Arguments.Trim() : null,
            WorkingDirectory = relaunch && !string.IsNullOrWhiteSpace(WorkingDirectory) ? WorkingDirectory.Trim() : null,
            IntervalMinutes = ScheduleKind == AutoRestartScheduleKind.Interval
                ? IntervalMinutes ?? DefaultIntervalMinutes
                : null,
            DailyTime = ScheduleKind == AutoRestartScheduleKind.Daily
                ? (string.IsNullOrWhiteSpace(DailyTime) ? DefaultDailyTime : DailyTime.Trim())
                : null,
            CpuPercentThreshold = CpuPercentThreshold is > 0 ? Math.Round(CpuPercentThreshold.Value, 1) : null,
            MemoryMbThreshold = MemoryMbThreshold is > 0 ? MemoryMbThreshold : null,
            HandleCountThreshold = HandleCountThreshold is > 0 ? HandleCountThreshold : null,
            InactiveHandleCountThreshold = InactiveHandleCountThreshold is > 0 ? InactiveHandleCountThreshold : null,
            SustainSeconds = Math.Clamp(SustainSeconds, 0, 3600),
            CooldownMinutes = Math.Clamp(CooldownMinutes, 1, 10080),
        };
    }

    public static string? NormalizeProcessName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        return trimmed;
    }
}

public sealed record AutoRestartSettings
{
    public const int MaximumRules = 64;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("rules")]
    public IReadOnlyList<AutoRestartRule> Rules { get; init; } = [];

    public AutoRestartSettings CloneNormalized() =>
        this with
        {
            Rules = (Rules ?? []).Where(rule => rule is not null).Select(rule => rule.Normalize()).ToArray(),
        };

    public static IReadOnlyList<AutoRestartRule> CreateBuiltInPresets() =>
    [
        new AutoRestartRule
        {
            Id = "dwm-compositor",
            Name = "桌面合成器 (dwm.exe)",
            Enabled = false,
            TargetKind = AutoRestartTargetKind.Process,
            ProcessName = "dwm",
            ActionMode = AutoRestartActionMode.TerminateOnly,
            RequireElevation = true,
            ScheduleKind = AutoRestartScheduleKind.Daily,
            DailyTime = "04:00",
            CpuPercentThreshold = 7.0,
            SustainSeconds = 90,
            CooldownMinutes = 60,
        },
        new AutoRestartRule
        {
            Id = "explorer-factory-zombies",
            Name = "僵尸资源管理器 (explorer /factory)",
            Enabled = false,
            TargetKind = AutoRestartTargetKind.Process,
            ProcessName = "explorer",
            CommandLineContains = "/factory",
            ActionMode = AutoRestartActionMode.TerminateOnly,
            RequireElevation = false,
            ScheduleKind = AutoRestartScheduleKind.Interval,
            IntervalMinutes = 360,
            CooldownMinutes = 30,
        },
        new AutoRestartRule
        {
            Id = "dopus-handles",
            Name = "Directory Opus (dopus.exe)",
            Enabled = false,
            TargetKind = AutoRestartTargetKind.Process,
            ProcessName = "dopus",
            ActionMode = AutoRestartActionMode.TerminateAndRelaunch,
            RequireElevation = false,
            ScheduleKind = AutoRestartScheduleKind.None,
            HandleCountThreshold = 20000,
            SustainSeconds = 60,
            CooldownMinutes = 60,
        },
    ];
}

public static class AutoRestartElevatedPolicy
{
    // Only protect Vigil's own executables and Windows kernel pseudo-processes.
    // Users are free to target any Windows process without an arbitrary allowlist.
    private static readonly HashSet<string> ProtectedSelfProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system",
        "idle",
        "vigil",
        "vigil.agent",
        "vigil.elevated",
        "systemautorun.app",
        "systemautorun.agent",
        "systemautorun.elevated",
        "systemautorun.cli",
    };

    public static bool IsCriticalDeniedProcess(string? processName)
    {
        var normalized = AutoRestartRule.NormalizeProcessName(processName);
        return string.IsNullOrWhiteSpace(normalized) || ProtectedSelfProcesses.Contains(normalized);
    }

    public static bool IsAllowedElevatedTarget(string? processName, string? commandLineContains)
    {
        _ = commandLineContains;
        return !IsCriticalDeniedProcess(processName);
    }
}

public static class AutoRestartSettingsValidator
{
    public static void Validate(AutoRestartSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Rules is null)
        {
            throw new ArgumentException("auto_restart_settings.rules must be an array.");
        }

        if (settings.Rules.Count > AutoRestartSettings.MaximumRules)
        {
            throw new ArgumentException($"auto_restart_settings.rules must not exceed {AutoRestartSettings.MaximumRules} entries.");
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawRule in settings.Rules)
        {
            if (rawRule is null)
            {
                throw new ArgumentException("auto_restart_settings.rules must not contain null entries.");
            }

            ValidateRule(rawRule);
            if (!seenIds.Add(rawRule.Id.Trim()))
            {
                throw new ArgumentException($"Duplicate auto-restart rule id: {rawRule.Id}");
            }
        }
    }

    public static void ValidateRule(AutoRestartRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var id = rule.Id?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            throw new ArgumentException("规则 ID 必须由 1~64 位英文字母、数字、-、_ 或 . 组成。");
        }

        var name = rule.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
        {
            throw new ArgumentException($"规则 '{id}' 的名称不能为空且不能超过 120 个字符。");
        }

        if (rule.TargetKind == AutoRestartTargetKind.ManagedTask)
        {
            if (string.IsNullOrWhiteSpace(rule.TaskName))
            {
                throw new ArgumentException($"规则 '{name}' 必须指定目标服务名称。");
            }

            if (rule.ActionMode != AutoRestartActionMode.RestartTask)
            {
                throw new ArgumentException($"已纳管服务规则 '{name}' 的动作模式必须为 restart_task。");
            }

            if (rule.RequireElevation)
            {
                throw new ArgumentException($"已纳管服务规则 '{name}' 不支持启用系统提权维护通道。");
            }
        }
        else if (rule.TargetKind == AutoRestartTargetKind.Process)
        {
            var processName = AutoRestartRule.NormalizeProcessName(rule.ProcessName);
            if (string.IsNullOrWhiteSpace(processName)
                || processName.Length > 128
                || processName.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            {
                throw new ArgumentException($"规则 '{name}' 的进程名无效（只需填写进程名，如 dwm 或 dopus.exe，不要包含路径）。");
            }

            if (AutoRestartElevatedPolicy.IsCriticalDeniedProcess(processName))
            {
                throw new ArgumentException($"不能对 Vigil 自身或内核保留进程 '{processName}' 配置自动重启。");
            }

            if (rule.CommandLineContains is { Length: > 256 })
            {
                throw new ArgumentException($"规则 '{name}' 的命令行过滤条件不能超过 256 个字符。");
            }

            if (rule.ActionMode == AutoRestartActionMode.RestartTask)
            {
                throw new ArgumentException($"自定义进程规则 '{name}' 不能使用 restart_task 动作模式。");
            }

            if (rule.ActionMode == AutoRestartActionMode.TerminateAndRelaunch)
            {
                if (!string.IsNullOrWhiteSpace(rule.ExecutablePath))
                {
                    var exe = rule.ExecutablePath.Trim();
                    if (exe.Length > 512 || !Path.IsPathFullyQualified(exe))
                    {
                        throw new ArgumentException($"规则 '{name}' 的重启程序路径必须是完整绝对路径。");
                    }
                }

                if (rule.Arguments is { Length: > 1024 })
                {
                    throw new ArgumentException($"规则 '{name}' 的启动参数过长（上限 1024 字符）。");
                }

                if (!string.IsNullOrWhiteSpace(rule.WorkingDirectory) && !Path.IsPathFullyQualified(rule.WorkingDirectory.Trim()))
                {
                    throw new ArgumentException($"规则 '{name}' 的工作目录必须是完整绝对路径。");
                }
            }
        }
        else
        {
            throw new ArgumentException($"规则 '{name}' 的目标类型无效。");
        }

        switch (rule.ScheduleKind)
        {
            case AutoRestartScheduleKind.None:
                break;
            case AutoRestartScheduleKind.Interval:
                if (rule.IntervalMinutes is null or < 5 or > 43200)
                {
                    throw new ArgumentException($"规则 '{name}' 的定时间隔必须在 5 到 43200 分钟（30 天）之间。");
                }
                break;
            case AutoRestartScheduleKind.Daily:
                if (!TryParseDailyTime(rule.DailyTime, out _))
                {
                    throw new ArgumentException($"规则 '{name}' 的每日定点时间格式无效，请使用 HH:mm（例如 04:00）。");
                }
                break;
            default:
                throw new ArgumentException($"规则 '{name}' 的定时类型无效。");
        }

        if (rule.CpuPercentThreshold is { } cpu && (double.IsNaN(cpu) || double.IsInfinity(cpu) || cpu < 0 || cpu > 100))
        {
            throw new ArgumentException($"规则 '{name}' 的 CPU 阈值必须在 0 到 100 之间。");
        }

        if (rule.MemoryMbThreshold is { } mem && (mem < 0 || mem > 1_048_576))
        {
            throw new ArgumentException($"规则 '{name}' 的内存阈值必须在 0 到 1048576 MB 之间。");
        }

        if (rule.HandleCountThreshold is { } handles && (handles < 0 || handles > 1_000_000))
        {
            throw new ArgumentException($"规则 '{name}' 的句柄数阈值必须在 0 到 1000000 之间。");
        }

        if (rule.InactiveHandleCountThreshold is { } inactiveHandles && (inactiveHandles < 0 || inactiveHandles > 1_000_000))
        {
            throw new ArgumentException($"规则 '{name}' 的不活跃句柄阈值必须在 0 到 1000000 之间。");
        }

        if (rule.SustainSeconds < 0 || rule.SustainSeconds > 3600)
        {
            throw new ArgumentException($"规则 '{name}' 的持续时间必须在 0 到 3600 秒之间。");
        }

        if (rule.CooldownMinutes < 1 || rule.CooldownMinutes > 10080)
        {
            throw new ArgumentException($"规则 '{name}' 的冷却时间必须在 1 到 10080 分钟（7 天）之间。");
        }
    }

    public static bool TryParseDailyTime(string? value, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return TimeOnly.TryParseExact(value.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }
}

public sealed record AutoRestartMetricSample(
    DateTimeOffset CapturedAt,
    double? CpuPercent,
    long? WorkingSetBytes,
    int? HandleCount,
    int? InactiveHandleCount,
    int MatchedProcessCount,
    bool Breaching);

public sealed record AutoRestartRuleStatus(
    string RuleId,
    string Name,
    bool Enabled,
    int MatchedProcessCount,
    double? LiveCpuPercent,
    long? LiveWorkingSetBytes,
    int? LiveHandleCount,
    DateTimeOffset? BreachedSinceUtc,
    DateTimeOffset? LastTriggeredAt,
    string? LastTriggerReason,
    bool? LastSucceeded,
    string? LastMessage,
    DateTimeOffset? NextScheduledAt,
    DateTimeOffset? CooldownUntilUtc,
    double? PeakCpuPercent = null,
    long? PeakWorkingSetBytes = null,
    int? PeakHandleCount = null,
    int? LiveInactiveHandleCount = null,
    int? PeakInactiveHandleCount = null,
    int? LiveThreadCount = null,
    int? LiveGdiHandleCount = null,
    int? LiveUserHandleCount = null,
    IReadOnlyList<AutoRestartMetricSample>? Samples = null);

public sealed record AutoRestartStatusSnapshot(
    bool Enabled,
    bool IsAgentElevated,
    bool MaintenanceTaskInstalled,
    string? MaintenanceTaskError,
    DateTimeOffset CapturedAt,
    IReadOnlyList<AutoRestartRuleStatus> Rules);

public sealed record AutoRestartRunResult(
    string RuleId,
    bool Succeeded,
    string Message,
    int AffectedProcessCount,
    bool UsedElevation,
    DateTimeOffset ExecutedAt);

public sealed record ElevatedMaintenanceRequest(
    string RequestId,
    string ProcessName,
    string? CommandLineContains,
    DateTimeOffset RequestedAtUtc);

public sealed record ElevatedMaintenanceResult(
    string RequestId,
    bool Succeeded,
    string Message,
    int TerminatedCount,
    DateTimeOffset CompletedAtUtc);
