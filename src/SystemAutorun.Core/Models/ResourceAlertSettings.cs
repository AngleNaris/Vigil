using System.Text.Json.Serialization;

namespace SystemAutorun.Core.Models;

public sealed record HostAlertOverride
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("cpu_percent")]
    public double? CpuPercent { get; init; }

    [JsonPropertyName("memory_percent")]
    public double? MemoryPercent { get; init; }

    [JsonPropertyName("storage_percent")]
    public double? StoragePercent { get; init; }

    [JsonPropertyName("cpu_sustain_seconds")]
    public int? CpuSustainSeconds { get; init; }

    public bool IsDefault =>
        Enabled is null
        && CpuPercent is null
        && MemoryPercent is null
        && StoragePercent is null
        && CpuSustainSeconds is null;
}

public sealed record TaskAlertOverride
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("cpu_percent")]
    public double? CpuPercent { get; init; }

    [JsonPropertyName("memory_percent")]
    public double? MemoryPercent { get; init; }

    [JsonPropertyName("memory_mb")]
    public int? MemoryMb { get; init; }

    [JsonPropertyName("cpu_sustain_seconds")]
    public int? CpuSustainSeconds { get; init; }

    public bool IsDefault =>
        Enabled is null
        && CpuPercent is null
        && MemoryPercent is null
        && MemoryMb is null
        && CpuSustainSeconds is null;
}

public sealed record EffectiveHostAlertThresholds(
    bool Enabled,
    double CpuPercent,
    double CpuResetPercent,
    TimeSpan CpuSustainDuration,
    double MemoryPercent,
    double StoragePercent);

public sealed record EffectiveTaskAlertThresholds(
    bool Enabled,
    double CpuPercent,
    double CpuResetPercent,
    TimeSpan CpuSustainDuration,
    double MemoryPercent,
    long MemoryBytes);

public sealed record ResourceAlertSettings
{
    public const double DefaultHostCpuPercent = 85.0;
    public const double DefaultHostMemoryPercent = 85.0;
    public const double DefaultHostStoragePercent = 90.0;
    public const double DefaultProcessCpuPercent = 85.0;
    public const double DefaultProcessMemoryPercent = 25.0;
    public const int DefaultProcessMemoryMb = 2048;
    public const int DefaultCpuSustainSeconds = 60;
    public const int DefaultNotificationCooldownMinutes = 10;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("cpu_sustain_seconds")]
    public int CpuSustainSeconds { get; init; } = DefaultCpuSustainSeconds;

    [JsonPropertyName("notification_cooldown_minutes")]
    public int NotificationCooldownMinutes { get; init; } = DefaultNotificationCooldownMinutes;

    [JsonPropertyName("host_cpu_percent")]
    public double HostCpuPercent { get; init; } = DefaultHostCpuPercent;

    [JsonPropertyName("host_memory_percent")]
    public double HostMemoryPercent { get; init; } = DefaultHostMemoryPercent;

    [JsonPropertyName("host_storage_percent")]
    public double HostStoragePercent { get; init; } = DefaultHostStoragePercent;

    [JsonPropertyName("process_cpu_percent")]
    public double ProcessCpuPercent { get; init; } = DefaultProcessCpuPercent;

    [JsonPropertyName("process_memory_percent")]
    public double ProcessMemoryPercent { get; init; } = DefaultProcessMemoryPercent;

    [JsonPropertyName("process_memory_mb")]
    public int ProcessMemoryMb { get; init; } = DefaultProcessMemoryMb;

    [JsonPropertyName("host_overrides")]
    public IReadOnlyDictionary<string, HostAlertOverride> HostOverrides { get; init; } =
        new Dictionary<string, HostAlertOverride>(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("task_overrides")]
    public IReadOnlyDictionary<string, TaskAlertOverride> TaskOverrides { get; init; } =
        new Dictionary<string, TaskAlertOverride>(StringComparer.OrdinalIgnoreCase);

    public HostAlertOverride? FindHostOverride(string hostId) =>
        TryFind(HostOverrides, hostId);

    public TaskAlertOverride? FindTaskOverride(string taskName) =>
        TryFind(TaskOverrides, taskName);

    public EffectiveHostAlertThresholds ResolveHost(string hostId)
    {
        var rule = FindHostOverride(hostId);
        var enabled = rule?.Enabled ?? Enabled;
        var cpu = ClampPercent(rule?.CpuPercent ?? HostCpuPercent);
        var reset = Math.Max(0, cpu - 5.0);
        var sustain = TimeSpan.FromSeconds(Math.Clamp(rule?.CpuSustainSeconds ?? CpuSustainSeconds, 0, 3600));
        var memory = ClampPercent(rule?.MemoryPercent ?? HostMemoryPercent);
        var storage = ClampPercent(rule?.StoragePercent ?? HostStoragePercent);
        return new EffectiveHostAlertThresholds(enabled, cpu, reset, sustain, memory, storage);
    }

    public EffectiveTaskAlertThresholds ResolveTask(string taskName)
    {
        var rule = FindTaskOverride(taskName);
        var enabled = rule?.Enabled ?? Enabled;
        var cpu = ClampPercent(rule?.CpuPercent ?? ProcessCpuPercent);
        var reset = Math.Max(0, cpu - 5.0);
        var sustain = TimeSpan.FromSeconds(Math.Clamp(rule?.CpuSustainSeconds ?? CpuSustainSeconds, 0, 3600));
        var memoryPercent = ClampPercent(rule?.MemoryPercent ?? ProcessMemoryPercent);
        var memoryMb = Math.Clamp(rule?.MemoryMb ?? ProcessMemoryMb, 0, 1_048_576);
        var memoryBytes = memoryMb * 1024L * 1024L;
        return new EffectiveTaskAlertThresholds(enabled, cpu, reset, sustain, memoryPercent, memoryBytes);
    }

    public ResourceAlertSettings CloneNormalized()
    {
        var hosts = new Dictionary<string, HostAlertOverride>(StringComparer.OrdinalIgnoreCase);
        if (HostOverrides is not null)
        {
            foreach (var (key, value) in HostOverrides)
            {
                if (!string.IsNullOrWhiteSpace(key) && value is not null && !value.IsDefault)
                {
                    hosts[key.Trim()] = value;
                }
            }
        }

        var tasks = new Dictionary<string, TaskAlertOverride>(StringComparer.OrdinalIgnoreCase);
        if (TaskOverrides is not null)
        {
            foreach (var (key, value) in TaskOverrides)
            {
                if (!string.IsNullOrWhiteSpace(key) && value is not null && !value.IsDefault)
                {
                    tasks[key.Trim()] = value;
                }
            }
        }

        return this with
        {
            HostOverrides = hosts,
            TaskOverrides = tasks,
        };
    }

    private static double ClampPercent(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? 100.0 : Math.Clamp(value, 1.0, 100.0);

    private static TValue? TryFind<TValue>(IReadOnlyDictionary<string, TValue>? dictionary, string key)
        where TValue : class
    {
        if (dictionary is null || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (dictionary.TryGetValue(key, out var exact))
        {
            return exact;
        }

        foreach (var pair in dictionary)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }
}

public static class ResourceAlertSettingsValidator
{
    public static void Validate(ResourceAlertSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidatePercent(settings.HostCpuPercent, "alert_settings.host_cpu_percent");
        ValidatePercent(settings.HostMemoryPercent, "alert_settings.host_memory_percent");
        ValidatePercent(settings.HostStoragePercent, "alert_settings.host_storage_percent");
        ValidatePercent(settings.ProcessCpuPercent, "alert_settings.process_cpu_percent");
        ValidatePercent(settings.ProcessMemoryPercent, "alert_settings.process_memory_percent");
        ValidateSustainSeconds(settings.CpuSustainSeconds, "alert_settings.cpu_sustain_seconds");
        ValidateNotificationCooldownMinutes(settings.NotificationCooldownMinutes, "alert_settings.notification_cooldown_minutes");
        ValidateMemoryMb(settings.ProcessMemoryMb, "alert_settings.process_memory_mb");

        if (settings.HostOverrides is not null)
        {
            var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, rule) in settings.HostOverrides)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new ArgumentException("alert_settings.host_overrides contains an empty host key.");
                }

                if (!seenHosts.Add(key.Trim()))
                {
                    throw new ArgumentException($"alert_settings.host_overrides contains duplicate host key: {key}");
                }

                if (rule is null)
                {
                    throw new ArgumentException($"alert_settings.host_overrides[{key}] must not be null.");
                }

                if (rule.CpuPercent is { } cpu) { ValidatePercent(cpu, $"alert_settings.host_overrides[{key}].cpu_percent"); }
                if (rule.MemoryPercent is { } mem) { ValidatePercent(mem, $"alert_settings.host_overrides[{key}].memory_percent"); }
                if (rule.StoragePercent is { } storage) { ValidatePercent(storage, $"alert_settings.host_overrides[{key}].storage_percent"); }
                if (rule.CpuSustainSeconds is { } sustain) { ValidateSustainSeconds(sustain, $"alert_settings.host_overrides[{key}].cpu_sustain_seconds"); }
            }
        }

        if (settings.TaskOverrides is not null)
        {
            var seenTasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, rule) in settings.TaskOverrides)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new ArgumentException("alert_settings.task_overrides contains an empty task key.");
                }

                if (!seenTasks.Add(key.Trim()))
                {
                    throw new ArgumentException($"alert_settings.task_overrides contains duplicate task key: {key}");
                }

                if (rule is null)
                {
                    throw new ArgumentException($"alert_settings.task_overrides[{key}] must not be null.");
                }

                if (rule.CpuPercent is { } cpu) { ValidatePercent(cpu, $"alert_settings.task_overrides[{key}].cpu_percent"); }
                if (rule.MemoryPercent is { } mem) { ValidatePercent(mem, $"alert_settings.task_overrides[{key}].memory_percent"); }
                if (rule.MemoryMb is { } mb) { ValidateMemoryMb(mb, $"alert_settings.task_overrides[{key}].memory_mb"); }
                if (rule.CpuSustainSeconds is { } sustain) { ValidateSustainSeconds(sustain, $"alert_settings.task_overrides[{key}].cpu_sustain_seconds"); }
            }
        }
    }

    private static void ValidatePercent(double value, string field)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 1 || value > 100)
        {
            throw new ArgumentException($"{field} must be between 1 and 100.");
        }
    }

    private static void ValidateNotificationCooldownMinutes(int value, string field)
    {
        if (value < 0 || value > 1440)
        {
            throw new ArgumentException($"{field} must be between 0 and 1440.");
        }
    }

    private static void ValidateSustainSeconds(int value, string field)
    {
        if (value < 0 || value > 3600)
        {
            throw new ArgumentException($"{field} must be between 0 and 3600 seconds.");
        }
    }

    private static void ValidateMemoryMb(int value, string field)
    {
        if (value < 0 || value > 1_048_576)
        {
            throw new ArgumentException($"{field} must be between 0 and 1048576 MB.");
        }
    }
}
