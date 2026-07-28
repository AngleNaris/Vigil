using System.Text.Json;
using System.Text.Json.Serialization;
using SystemAutorun.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SystemAutorun.Core.Configuration;

public sealed class ConfigurationLoader
{
    private static readonly JsonSerializerOptions OverrideJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public async Task<AutorunConfiguration> LoadAsync(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var root = Path.GetFullPath(rootDirectory);
        var configPath = Path.Combine(root, "autorun.yaml");
        if (!File.Exists(configPath))
        {
            throw new ConfigurationException($"Config file not found: {configPath}");
        }

        return await LoadFileAsync(configPath, root, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AutorunConfiguration> LoadFileAsync(
        string configPath,
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var root = Path.GetFullPath(rootDirectory);
        configPath = Path.GetFullPath(configPath);
        if (!File.Exists(configPath))
        {
            throw new ConfigurationException($"Config file not found: {configPath}");
        }

        ConfigDocument document;
        try
        {
            var yaml = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
            document = _deserializer.Deserialize<ConfigDocument>(yaml)
                ?? throw new ConfigurationException("Config is empty.");
        }
        catch (ConfigurationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ConfigurationException($"Failed to parse YAML: {exception.Message}", exception);
        }

        if (document.Version is not (1 or 2))
        {
            throw new ConfigurationException($"Unsupported config version: {document.Version}");
        }

        if (document.Settings is null || document.Tasks is null)
        {
            throw new ConfigurationException("Config must define settings and tasks.");
        }

        var overrides = await LoadOverridesAsync(root, cancellationToken).ConfigureAwait(false);
        var settings = MapSettings(document.Settings, root);
        var tasks = document.Tasks.Select(task => MapTask(task, settings, overrides)).ToArray();
        Validate(tasks);

        return new AutorunConfiguration(document.Version, settings, tasks, root);
    }

    private static AutorunSettings MapSettings(SettingsDocument document, string root)
    {
        var logDirectory = ExpandPath(document.LogDir, Path.Combine(root, "logs"), root);
        var backupDirectory = ExpandPath(document.BackupDir, Path.Combine(root, "backups"), root);
        var defaultDelay = document.DefaultStartupDelay ?? 5;
        if (defaultDelay < 0)
        {
            throw new ConfigurationException("settings.default_startup_delay must be non-negative.");
        }

        return new AutorunSettings(logDirectory, backupDirectory, defaultDelay);
    }

    private static AutorunTask MapTask(
        TaskDocument document,
        AutorunSettings settings,
        IReadOnlyDictionary<string, TaskOverride> overrides)
    {
        var name = Required(document.Name, "Task name is required.");
        var type = ParseTaskType(Required(document.Type, $"Task '{name}' type is required."));
        var enabled = overrides.TryGetValue(name, out var taskOverride)
            ? taskOverride.Enabled
            : document.Enabled ?? true;
        var delay = document.StartupDelay ?? settings.DefaultStartupDelay;
        if (delay < 0)
        {
            throw new ConfigurationException($"Task '{name}' startup_delay must be non-negative.");
        }

        var health = MapHealth(name, document.Health);
        var launch = MapLaunch(document.Launch, type);
        return new AutorunTask(
            name,
            type,
            enabled,
            document.Description?.Trim() ?? string.Empty,
            NormalizeOptionalPath(document.Exec),
            document.Args?.Where(value => value is not null).Select(value => value!).ToArray()
                ?? Array.Empty<string>(),
            NormalizeOptionalPath(document.WorkingDir),
            NormalizeOptional(document.Distro),
            NormalizeOptional(document.Service),
            NormalizeOptional(document.Command),
            document.DependsOn?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray()
                ?? Array.Empty<string>(),
            delay,
            health,
            launch);
    }

    private static HealthCheckDefinition MapHealth(string taskName, HealthDocument? document)
    {
        if (document is null)
        {
            throw new ConfigurationException($"Task '{taskName}' health is required.");
        }

        var type = ParseHealthType(Required(document.Type, $"Task '{taskName}' health.type is required."));
        var timeout = document.Timeout ?? 30;
        if (timeout <= 0)
        {
            throw new ConfigurationException($"Task '{taskName}' health.timeout must be positive.");
        }

        return new HealthCheckDefinition(
            type,
            NormalizeOptional(document.Pattern),
            NormalizeOptional(document.Host),
            document.Port,
            timeout,
            NormalizeOptional(document.Distro),
            NormalizeOptional(document.Service));
    }

    private static LaunchOptions MapLaunch(LaunchDocument? document, AutorunTaskType type)
    {
        var fallback = LaunchOptions.LegacyDefault(type);
        if (document is null)
        {
            return fallback;
        }

        return new LaunchOptions(
            ParseRunAs(document.RunAs, fallback.RunAs),
            ParseTrigger(document.Trigger, fallback.Trigger),
            ParseWindowMode(document.WindowMode, fallback.WindowMode),
            ParseLifecycle(document.Lifecycle, fallback.Lifecycle));
    }

    private static void Validate(IReadOnlyList<AutorunTask> tasks)
    {
        var taskMap = new Dictionary<string, AutorunTask>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            if (!taskMap.TryAdd(task.Name, task))
            {
                throw new ConfigurationException($"Duplicate task name: {task.Name}");
            }

            switch (task.Type)
            {
                case AutorunTaskType.Windows when string.IsNullOrWhiteSpace(task.Executable):
                    throw new ConfigurationException($"Windows task '{task.Name}' requires exec.");
                case AutorunTaskType.WslBoot when string.IsNullOrWhiteSpace(task.Distro):
                    throw new ConfigurationException($"WSL boot task '{task.Name}' requires distro.");
                case AutorunTaskType.WslSystemd when
                    string.IsNullOrWhiteSpace(task.Distro) || string.IsNullOrWhiteSpace(task.Service):
                    throw new ConfigurationException($"WSL systemd task '{task.Name}' requires distro and service.");
                case AutorunTaskType.WslCommand when
                    string.IsNullOrWhiteSpace(task.Distro) || string.IsNullOrWhiteSpace(task.Command):
                    throw new ConfigurationException($"WSL command task '{task.Name}' requires distro and command.");
            }

            ValidateHealth(task);
        }

        foreach (var task in tasks)
        {
            foreach (var dependency in task.DependsOn)
            {
                if (!taskMap.ContainsKey(dependency))
                {
                    throw new ConfigurationException(
                        $"Task '{task.Name}' depends on missing task '{dependency}'.");
                }
            }
        }

        DetectDependencyCycles(taskMap);
    }

    private static void ValidateHealth(AutorunTask task)
    {
        var health = task.Health;
        switch (health.Type)
        {
            case HealthCheckType.Process when string.IsNullOrWhiteSpace(health.Pattern):
                throw new ConfigurationException($"Task '{task.Name}' process health requires pattern.");
            case HealthCheckType.Tcp when string.IsNullOrWhiteSpace(health.Host) || health.Port is null:
                throw new ConfigurationException($"Task '{task.Name}' TCP health requires host and port.");
            case HealthCheckType.Tcp when health.Port is < 1 or > 65535:
                throw new ConfigurationException($"Task '{task.Name}' TCP health port is invalid.");
            case HealthCheckType.WslRunning when string.IsNullOrWhiteSpace(health.Distro ?? task.Distro):
                throw new ConfigurationException($"Task '{task.Name}' WSL health requires distro.");
            case HealthCheckType.SystemdActive when
                string.IsNullOrWhiteSpace(health.Distro ?? task.Distro)
                || string.IsNullOrWhiteSpace(health.Service ?? task.Service):
                throw new ConfigurationException($"Task '{task.Name}' systemd health requires distro and service.");
            case HealthCheckType.WslProcess when
                string.IsNullOrWhiteSpace(health.Distro ?? task.Distro) || string.IsNullOrWhiteSpace(health.Pattern):
                throw new ConfigurationException($"Task '{task.Name}' WSL process health requires distro and pattern.");
        }
    }

    private static void DetectDependencyCycles(IReadOnlyDictionary<string, AutorunTask> taskMap)
    {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string name)
        {
            if (visited.Contains(name))
            {
                return;
            }

            if (!visiting.Add(name))
            {
                throw new ConfigurationException($"Dependency cycle detected at task '{name}'.");
            }

            foreach (var dependency in taskMap[name].DependsOn)
            {
                Visit(dependency);
            }

            visiting.Remove(name);
            visited.Add(name);
        }

        foreach (var task in taskMap.Values)
        {
            Visit(task.Name);
        }
    }

    private static async Task<IReadOnlyDictionary<string, TaskOverride>> LoadOverridesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, "autorun.overrides.json");
        if (!File.Exists(path))
        {
            return new Dictionary<string, TaskOverride>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var values = await JsonSerializer.DeserializeAsync<Dictionary<string, TaskOverride>>(
                stream,
                OverrideJsonOptions,
                cancellationToken).ConfigureAwait(false);
            return values is null
                ? new Dictionary<string, TaskOverride>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, TaskOverride>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, TaskOverride>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string ExpandPath(string? configured, string fallback, string root)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
        value = Environment.ExpandEnvironmentVariables(value);
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
    }

    private static string? NormalizeOptionalPath(string? value)
    {
        var normalized = NormalizeOptional(value);
        return normalized is null ? null : Environment.ExpandEnvironmentVariables(normalized);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Required(string? value, string message) =>
        NormalizeOptional(value) ?? throw new ConfigurationException(message);

    private static AutorunTaskType ParseTaskType(string value) => value.ToLowerInvariant() switch
    {
        "windows" => AutorunTaskType.Windows,
        "wsl_boot" => AutorunTaskType.WslBoot,
        "wsl_systemd" => AutorunTaskType.WslSystemd,
        "wsl_command" => AutorunTaskType.WslCommand,
        _ => throw new ConfigurationException($"Unsupported task type: {value}"),
    };

    private static HealthCheckType ParseHealthType(string value) => value.ToLowerInvariant() switch
    {
        "process" => HealthCheckType.Process,
        "tcp" => HealthCheckType.Tcp,
        "wsl_running" => HealthCheckType.WslRunning,
        "systemd_active" => HealthCheckType.SystemdActive,
        "wsl_process" => HealthCheckType.WslProcess,
        _ => throw new ConfigurationException($"Unsupported health type: {value}"),
    };

    private static RunAsMode ParseRunAs(string? value, RunAsMode fallback) =>
        value?.ToLowerInvariant() switch
        {
            null or "" => fallback,
            "current_user" or "user" => RunAsMode.CurrentUser,
            "system" => RunAsMode.System,
            _ => throw new ConfigurationException($"Unsupported launch.run_as: {value}"),
        };

    private static LaunchTrigger ParseTrigger(string? value, LaunchTrigger fallback) =>
        value?.ToLowerInvariant() switch
        {
            null or "" => fallback,
            "manual" => LaunchTrigger.Manual,
            "logon" => LaunchTrigger.Logon,
            "boot" => LaunchTrigger.Boot,
            _ => throw new ConfigurationException($"Unsupported launch.trigger: {value}"),
        };

    private static WindowMode ParseWindowMode(string? value, WindowMode fallback) =>
        value?.ToLowerInvariant() switch
        {
            null or "" => fallback,
            "normal" => WindowMode.Normal,
            "minimized" => WindowMode.Minimized,
            "hidden" => WindowMode.Hidden,
            "no_console" => WindowMode.NoConsole,
            "tray" => WindowMode.Tray,
            _ => throw new ConfigurationException($"Unsupported launch.window_mode: {value}"),
        };

    private static ProcessLifecycle ParseLifecycle(string? value, ProcessLifecycle fallback) =>
        value?.ToLowerInvariant() switch
        {
            null or "" => fallback,
            "detached" => ProcessLifecycle.Detached,
            "tracked" => ProcessLifecycle.Tracked,
            "service" => ProcessLifecycle.Service,
            _ => throw new ConfigurationException($"Unsupported launch.lifecycle: {value}"),
        };

    private sealed class ConfigDocument
    {
        public int Version { get; init; }

        public SettingsDocument? Settings { get; init; }

        public List<TaskDocument>? Tasks { get; init; }
    }

    private sealed class SettingsDocument
    {
        public string? LogDir { get; init; }

        public string? BackupDir { get; init; }

        public int? DefaultStartupDelay { get; init; }
    }

    private sealed class TaskDocument
    {
        public string? Name { get; init; }

        public string? Type { get; init; }

        public bool? Enabled { get; init; }

        public string? Description { get; init; }

        public string? Exec { get; init; }

        public List<string?>? Args { get; init; }

        public string? WorkingDir { get; init; }

        public int? StartupDelay { get; init; }

        public string? Distro { get; init; }

        public string? Service { get; init; }

        public string? Command { get; init; }

        public List<string>? DependsOn { get; init; }

        public HealthDocument? Health { get; init; }

        public LaunchDocument? Launch { get; init; }
    }

    private sealed class HealthDocument
    {
        public string? Type { get; init; }

        public string? Pattern { get; init; }

        public string? Host { get; init; }

        public int? Port { get; init; }

        public int? Timeout { get; init; }

        public string? Distro { get; init; }

        public string? Service { get; init; }
    }

    private sealed class LaunchDocument
    {
        public string? RunAs { get; init; }

        public string? Trigger { get; init; }

        public string? WindowMode { get; init; }

        public string? Lifecycle { get; init; }
    }

    private sealed class TaskOverride
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; }
    }
}
