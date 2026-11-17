using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using YamlDotNet.RepresentationModel;

namespace SystemAutorun.Core.Configuration;

public sealed record AddWindowsTaskRequest(
    string Name,
    string Executable,
    IReadOnlyList<string> Arguments,
    string? Description = null,
    string? WorkingDirectory = null,
    int? StartupDelay = null,
    WindowMode WindowMode = WindowMode.Hidden,
    bool ExpectedRunning = true,
    bool? StartOnAppLaunch = null,
    bool? AutoRecover = null);

public sealed record AddTaskRequest(
    string Name,
    AutorunTaskType Type,
    string? Description = null,
    string? Executable = null,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    string? Distro = null,
    string? Service = null,
    string? Command = null,
    IReadOnlyList<string>? DependsOn = null,
    int? StartupDelay = null,
    HealthCheckType HealthType = HealthCheckType.Process,
    string? HealthPattern = null,
    string? HealthHost = null,
    int? HealthPort = null,
    int HealthTimeoutSeconds = 30,
    LaunchOptions? Launch = null,
    bool ExpectedRunning = false,
    bool? StartOnAppLaunch = null,
    bool? AutoRecover = null);

public sealed record TaskAutomationPatch(bool? StartOnAppLaunch = null, bool? AutoRecover = null);

public sealed record ConfigurationMutationResult(
    bool Succeeded,
    string Action,
    string TaskName,
    string BackupPath);

public sealed class ConfigurationFileEditor
{
    private static readonly SemaphoreSlim MutationLock = new(1, 1);
    private readonly ConfigurationLoader _loader;

    public ConfigurationFileEditor(ConfigurationLoader? loader = null)
    {
        _loader = loader ?? new ConfigurationLoader();
    }

    public async Task<ConfigurationMutationResult> AddWindowsTaskAsync(
        ProjectPaths paths,
        AddWindowsTaskRequest request,
        CancellationToken cancellationToken = default) =>
        await AddTaskAsync(
            paths,
            new AddTaskRequest(
                request.Name,
                AutorunTaskType.Windows,
                request.Description,
                request.Executable,
                request.Arguments,
                request.WorkingDirectory,
                StartupDelay: request.StartupDelay,
                HealthType: HealthCheckType.Process,
                HealthPattern: Path.GetFileName(request.Executable),
                Launch: new LaunchOptions(
                    RunAsMode.CurrentUser,
                    LaunchTrigger.Logon,
                    request.WindowMode,
                    ProcessLifecycle.Tracked),
                ExpectedRunning: request.ExpectedRunning,
                StartOnAppLaunch: request.StartOnAppLaunch,
                AutoRecover: request.AutoRecover),
            cancellationToken).ConfigureAwait(false);

    public async Task<ConfigurationMutationResult> AddTaskAsync(
        ProjectPaths paths,
        AddTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(request);
        await MutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = await _loader.LoadAsync(paths.RootDirectory, cancellationToken)
                .ConfigureAwait(false);
            var name = Required(request.Name, "Task name is required.");
            if (configuration.Tasks.Any(task =>
                string.Equals(task.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ConfigurationException($"Duplicate task name: {name}");
            }

            string? executable = null;
            string? workingDirectory = null;
            if (request.Type == AutorunTaskType.Windows)
            {
                executable = Path.GetFullPath(Required(
                    request.Executable,
                    "Executable path is required for a Windows task."));
                if (!File.Exists(executable))
                {
                    throw new FileNotFoundException("Executable does not exist.", executable);
                }

                workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                    ? Path.GetDirectoryName(executable)!
                    : Path.GetFullPath(request.WorkingDirectory);
            }

            var delay = request.StartupDelay ?? configuration.Settings.DefaultStartupDelay;
            if (delay < 0)
            {
                throw new ConfigurationException("startup_delay must be non-negative.");
            }

            if (request.HealthTimeoutSeconds <= 0)
            {
                throw new ConfigurationException("health.timeout must be positive.");
            }

            var healthPattern = string.IsNullOrWhiteSpace(request.HealthPattern)
                && request.HealthType == HealthCheckType.Process
                && executable is not null
                    ? Path.GetFileName(executable)
                    : NormalizeOptional(request.HealthPattern);
            var health = new HealthCheckDefinition(
                request.HealthType,
                healthPattern,
                NormalizeOptional(request.HealthHost),
                request.HealthPort,
                request.HealthTimeoutSeconds,
                NormalizeOptional(request.Distro),
                NormalizeOptional(request.Service));
            var launch = request.Launch ?? LaunchOptions.LegacyDefault(request.Type);
            var original = await SnapshotFile.ReadAllTextAsync(paths.ConfigPath, cancellationToken)
                .ConfigureAwait(false);
            var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var appendBase = PrepareTaskSequenceForAppend(original, out var wasEmpty);
            var candidate = appendBase.TrimEnd('\r', '\n')
                + newline
                + (wasEmpty ? string.Empty : newline)
                + BuildTaskBlock(
                    name,
                    request.Type,
                    executable,
                    request.Arguments ?? Array.Empty<string>(),
                    string.IsNullOrWhiteSpace(request.Description) ? name : request.Description.Trim(),
                    workingDirectory,
                    NormalizeOptional(request.Distro),
                    NormalizeOptional(request.Service),
                    NormalizeOptional(request.Command),
                    request.DependsOn?
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                        ?? Array.Empty<string>(),
                    delay,
                    health,
                    launch,
                    request.ExpectedRunning,
                    request.StartOnAppLaunch,
                    request.AutoRecover,
                    newline)
                + newline;
            return await CommitAsync(paths, original, candidate, "add", name, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            MutationLock.Release();
        }
    }

    private static string PrepareTaskSequenceForAppend(string yaml, out bool wasEmpty)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(yaml))
        {
            stream.Load(reader);
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root
            || !root.Children.TryGetValue(new YamlScalarNode("tasks"), out var tasksNode)
            || tasksNode is not YamlSequenceNode tasks)
        {
            throw new ConfigurationException("Config does not contain a tasks sequence.");
        }

        wasEmpty = tasks.Children.Count == 0;
        if (!wasEmpty)
        {
            return yaml;
        }

        var start = checked((int)tasks.Start.Index);
        var end = start;
        while (end < yaml.Length && yaml[end] != ']')
        {
            if (yaml[end] is not ('[' or ' ' or '\t' or '\r' or '\n'))
            {
                throw new ConfigurationException("Empty tasks sequence uses an unsupported YAML form.");
            }

            end++;
        }

        if (end >= yaml.Length)
        {
            throw new ConfigurationException("Empty tasks sequence is not terminated.");
        }

        var prefix = yaml[..start].TrimEnd(' ', '\t');
        var suffix = yaml[(end + 1)..];
        return string.Concat(prefix, suffix);
    }

    public async Task<ConfigurationMutationResult> RemoveTaskAsync(
        ProjectPaths paths,
        string taskName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        taskName = Required(taskName, "Task name is required.");
        await MutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = await _loader.LoadAsync(paths.RootDirectory, cancellationToken)
                .ConfigureAwait(false);
            _ = configuration.GetTask(taskName);
            var dependents = configuration.Tasks
                .Where(task => task.DependsOn.Contains(taskName, StringComparer.OrdinalIgnoreCase))
                .Select(task => task.Name)
                .ToArray();
            if (dependents.Length > 0)
            {
                throw new ConfigurationException(
                    $"Task '{taskName}' is required by: {string.Join(", ", dependents)}");
            }

            var original = await SnapshotFile.ReadAllTextAsync(paths.ConfigPath, cancellationToken)
                .ConfigureAwait(false);
            var candidate = RemoveTaskBlock(original, taskName);
            var result = await CommitAsync(paths, original, candidate, "remove", taskName, cancellationToken)
                .ConfigureAwait(false);
            await new OverrideStore().RemoveAsync(paths.RootDirectory, taskName, cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        finally
        {
            MutationLock.Release();
        }
    }

    public async Task<ConfigurationMutationResult> SetExpectedRunningAsync(
        ProjectPaths paths,
        string taskName,
        bool expectedRunning,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        taskName = Required(taskName, "Task name is required.");
        await MutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = await _loader.LoadAsync(paths.RootDirectory, cancellationToken)
                .ConfigureAwait(false);
            _ = configuration.GetTask(taskName);
            var original = await SnapshotFile.ReadAllTextAsync(paths.ConfigPath, cancellationToken)
                .ConfigureAwait(false);
            var candidate = SetTaskScalar(
                original,
                taskName,
                "expected_running",
                expectedRunning ? "true" : "false");
            return await CommitAsync(
                paths,
                original,
                candidate,
                expectedRunning ? "monitor" : "unmonitor",
                taskName,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            MutationLock.Release();
        }
    }

    public async Task<ConfigurationMutationResult> SetAutomationAsync(
        ProjectPaths paths, string taskName, TaskAutomationPatch patch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.StartOnAppLaunch is null && patch.AutoRecover is null)
        { throw new ArgumentException("At least one task automation choice is required.", nameof(patch)); }
        taskName = Required(taskName, "Task name is required.");
        await MutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = (await _loader.LoadAsync(paths.RootDirectory, cancellationToken).ConfigureAwait(false)).GetTask(taskName);
            var original = await SnapshotFile.ReadAllTextAsync(paths.ConfigPath, cancellationToken).ConfigureAwait(false);
            var candidate = original;
            if (patch.StartOnAppLaunch is { } starts)
            { candidate = SetTaskScalar(candidate, taskName, "start_on_app_launch", starts ? "true" : "false"); }
            if (patch.AutoRecover is { } recovers)
            { candidate = SetTaskScalar(candidate, taskName, "auto_recover", recovers ? "true" : "false"); }
            return await CommitAsync(paths, original, candidate, "automation", taskName, cancellationToken).ConfigureAwait(false);
        }
        finally { MutationLock.Release(); }
    }

    internal static string RemoveTaskBlock(string yaml, string taskName)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(yaml))
        {
            stream.Load(reader);
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root
            || !root.Children.TryGetValue(new YamlScalarNode("tasks"), out var tasksNode)
            || tasksNode is not YamlSequenceNode tasks)
        {
            throw new ConfigurationException("Config does not contain a tasks sequence.");
        }

        var matches = tasks.Children
            .OfType<YamlMappingNode>()
            .Where(mapping => mapping.Children.TryGetValue(new YamlScalarNode("name"), out var nameNode)
                && string.Equals(((YamlScalarNode)nameNode).Value, taskName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ConfigurationException($"Expected one task block for '{taskName}', found {matches.Length}.");
        }

        var mappingNode = matches[0];
        var start = FindLineStart(yaml, checked((int)mappingNode.Start.Index));
        var taskIndex = tasks.Children.IndexOf(mappingNode);
        int end;
        if (taskIndex >= 0 && taskIndex + 1 < tasks.Children.Count)
        {
            end = FindLineStart(yaml, checked((int)tasks.Children[taskIndex + 1].Start.Index));
        }
        else
        {
            // RepresentationModel.End is the mapping-start event's end, not the
            // complete block's end. Using it deletes only the name line.
            var firstLineEnd = FindLineEnd(yaml, start);
            var firstLine = yaml[start..firstLineEnd];
            var indentation = firstLine.TakeWhile(character => character is ' ' or '\t').Count();
            if (!firstLine.AsSpan(indentation).StartsWith("-", StringComparison.Ordinal)
                || yaml[checked((int)mappingNode.Start.Index)] == '{')
            {
                throw new ConfigurationException("Removing this task requires a block-style task entry.");
            }

            end = firstLineEnd;
            while (end < yaml.Length)
            {
                var nextEnd = FindLineEnd(yaml, end);
                var line = yaml[end..nextEnd];
                if (!string.IsNullOrWhiteSpace(line)
                    && line.TakeWhile(character => character is ' ' or '\t').Count() <= indentation)
                {
                    break;
                }

                end = nextEnd;
            }
        }

        var result = yaml.Remove(start, end - start);
        if (tasks.Children.Count == 1)
        {
            var key = root.Children.Keys.OfType<YamlScalarNode>().Single(node => node.Value == "tasks");
            var colon = result.IndexOf(':', checked((int)key.End.Index));
            result = result.Insert(colon + 1, " []");
        }

        return result;
    }

    internal static string SetTaskScalar(
        string yaml,
        string taskName,
        string field,
        string value)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(yaml))
        {
            stream.Load(reader);
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root
            || !root.Children.TryGetValue(new YamlScalarNode("tasks"), out var tasksNode)
            || tasksNode is not YamlSequenceNode tasks)
        {
            throw new ConfigurationException("Config does not contain a tasks sequence.");
        }

        var matches = tasks.Children
            .OfType<YamlMappingNode>()
            .Where(mapping => mapping.Children.TryGetValue(new YamlScalarNode("name"), out var nameNode)
                && string.Equals(((YamlScalarNode)nameNode).Value, taskName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ConfigurationException($"Expected one task block for '{taskName}', found {matches.Length}.");
        }

        var mapping = matches[0];
        var existing = mapping.Children.FirstOrDefault(pair =>
            pair.Key is YamlScalarNode key
            && string.Equals(key.Value, field, StringComparison.OrdinalIgnoreCase));
        if (existing.Key is YamlScalarNode existingKey)
        {
            var lineStart = FindLineStart(yaml, checked((int)existingKey.Start.Index));
            var lineEnd = FindLineEnd(yaml, checked((int)existing.Value.End.Index));
            var contentEnd = lineEnd;
            while (contentEnd > lineStart && yaml[contentEnd - 1] is '\r' or '\n')
            {
                contentEnd--;
            }

            var indent = yaml[lineStart..checked((int)existingKey.Start.Index)];
            var lineEnding = yaml[contentEnd..lineEnd];
            return yaml[..lineStart]
                + $"{indent}{field}: {value}{lineEnding}"
                + yaml[lineEnd..];
        }

        var anchor = mapping.Children
            .Select(pair => pair.Key)
            .OfType<YamlScalarNode>()
            .FirstOrDefault(key => string.Equals(key.Value, "enabled", StringComparison.OrdinalIgnoreCase))
            ?? mapping.Children
                .Select(pair => pair.Key)
                .OfType<YamlScalarNode>()
                .FirstOrDefault(key => string.Equals(key.Value, "type", StringComparison.OrdinalIgnoreCase))
            ?? mapping.Children
                .Select(pair => pair.Key)
                .OfType<YamlScalarNode>()
                .First();
        var anchorStart = checked((int)anchor.Start.Index);
        var anchorLineStart = FindLineStart(yaml, anchorStart);
        var anchorLineEnd = FindLineEnd(yaml, checked((int)mapping.Children[anchor].End.Index));
        var newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var separator = anchorLineEnd > anchorLineStart && yaml[anchorLineEnd - 1] == '\n'
            ? string.Empty
            : newline;
        var indentPrefix = yaml[anchorLineStart..anchorStart];
        return yaml.Insert(
            anchorLineEnd,
            $"{separator}{indentPrefix}{field}: {value}{newline}");
    }

    private async Task<ConfigurationMutationResult> CommitAsync(
        ProjectPaths paths,
        string original,
        string candidate,
        string action,
        string taskName,
        CancellationToken cancellationToken)
    {
        var configDirectory = Path.GetDirectoryName(paths.ConfigPath)!;
        var tempPath = Path.Combine(configDirectory, $".autorun.yaml.{Guid.NewGuid():N}.tmp");
        var configuration = await _loader.LoadAsync(paths.RootDirectory, cancellationToken)
            .ConfigureAwait(false);
        var backupDirectory = Path.Combine(configuration.Settings.BackupDirectory, "config");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(
            backupDirectory,
            $"{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}-{action}-{SanitizeFileName(taskName)}-autorun.yaml");
        try
        {
            await WriteDurableTextAsync(tempPath, candidate, cancellationToken).ConfigureAwait(false);
            _ = await _loader.LoadFileAsync(tempPath, paths.RootDirectory, cancellationToken)
                .ConfigureAwait(false);
            var latest = await SnapshotFile.ReadAllTextAsync(paths.ConfigPath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(latest, original, StringComparison.Ordinal))
            {
                throw new IOException("autorun.yaml changed while the edit was being prepared.");
            }

            await WriteDurableTextAsync(backupPath, original, cancellationToken).ConfigureAwait(false);
            SnapshotFile.Publish(tempPath, paths.ConfigPath);
            return new ConfigurationMutationResult(true, action, taskName, backupPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static async Task WriteDurableTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        var bytes = new UTF8Encoding(false).GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static string BuildTaskBlock(
        string name,
        AutorunTaskType type,
        string? executable,
        IReadOnlyList<string> arguments,
        string description,
        string? workingDirectory,
        string? distro,
        string? service,
        string? command,
        IReadOnlyList<string> dependsOn,
        int delay,
        HealthCheckDefinition health,
        LaunchOptions launch,
        bool expectedRunning,
        bool? startOnAppLaunch,
        bool? autoRecover,
        string newline)
    {
        var lines = new List<string>
        {
            $"  - name: {Quote(name)}",
            $"    type: {type.ToKey()}",
            "    enabled: true",
            $"    expected_running: {expectedRunning.ToString().ToLowerInvariant()}",
            $"    description: {Quote(description)}",
        };

        if (startOnAppLaunch is { } starts) { lines.Add($"    start_on_app_launch: {starts.ToString().ToLowerInvariant()}"); }
        if (autoRecover is { } recovers) { lines.Add($"    auto_recover: {recovers.ToString().ToLowerInvariant()}"); }

        switch (type)
        {
            case AutorunTaskType.Windows:
                lines.Add($"    exec: {Quote(Required(executable, "Executable path is required."))}");
                if (arguments.Count > 0)
                {
                    lines.Add($"    args: [{string.Join(", ", arguments.Select(Quote))}]");
                }

                lines.Add($"    working_dir: {Quote(Required(workingDirectory, "Working directory is required."))}");
                break;
            case AutorunTaskType.WindowsService:
                lines.Add($"    service: {Quote(Required(service, "Windows service name is required."))}");
                break;
            case AutorunTaskType.WslBoot:
                lines.Add($"    distro: {Quote(Required(distro, "WSL distro is required."))}");
                break;
            case AutorunTaskType.WslSystemd:
                lines.Add($"    distro: {Quote(Required(distro, "WSL distro is required."))}");
                lines.Add($"    service: {Quote(Required(service, "systemd service is required."))}");
                break;
            case AutorunTaskType.WslCommand:
                lines.Add($"    distro: {Quote(Required(distro, "WSL distro is required."))}");
                lines.Add($"    command: {Quote(Required(command, "WSL command is required."))}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }

        if (dependsOn.Count > 0)
        {
            lines.Add($"    depends_on: [{string.Join(", ", dependsOn.Select(Quote))}]");
        }

        lines.Add($"    startup_delay: {delay}");
        lines.Add("    launch:");
        lines.Add($"      run_as: {launch.RunAs.ToKey()}");
        lines.Add($"      trigger: {launch.Trigger.ToKey()}");
        lines.Add($"      window_mode: {launch.WindowMode.ToKey()}");
        lines.Add($"      lifecycle: {launch.Lifecycle.ToKey()}");
        lines.Add("    health:");
        lines.Add($"      type: {health.Type.ToKey()}");
        switch (health.Type)
        {
            case HealthCheckType.Process:
                lines.Add($"      pattern: {Quote(Required(health.Pattern, "Process health pattern is required."))}");
                break;
            case HealthCheckType.Tcp:
                lines.Add($"      host: {Quote(Required(health.Host, "TCP health host is required."))}");
                lines.Add($"      port: {health.Port
                    ?? throw new ArgumentException("TCP health port is required.")}");
                break;
            case HealthCheckType.WindowsService:
                lines.Add($"      service: {Quote(Required(
                    health.Service ?? service,
                    "Windows service health name is required."))}");
                break;
            case HealthCheckType.WslRunning:
                lines.Add($"      distro: {Quote(Required(health.Distro ?? distro, "WSL health distro is required."))}");
                break;
            case HealthCheckType.SystemdActive:
                lines.Add($"      distro: {Quote(Required(health.Distro ?? distro, "systemd health distro is required."))}");
                lines.Add($"      service: {Quote(Required(health.Service ?? service, "systemd health service is required."))}");
                break;
            case HealthCheckType.WslProcess:
                lines.Add($"      distro: {Quote(Required(health.Distro ?? distro, "WSL process distro is required."))}");
                lines.Add($"      pattern: {Quote(Required(health.Pattern, "WSL process pattern is required."))}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(health.Type), health.Type, null);
        }

        lines.Add($"      timeout: {health.TimeoutSeconds}");
        return string.Join(newline, lines);
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static int FindLineStart(string value, int index)
    {
        var newline = value.LastIndexOf('\n', Math.Clamp(index, 0, value.Length - 1));
        return newline < 0 ? 0 : newline + 1;
    }

    private static int FindLineEnd(string value, int index)
    {
        var newline = value.IndexOf('\n', Math.Clamp(index, 0, value.Length));
        return newline < 0 ? value.Length : newline + 1;
    }

    private static string Required(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(message) : value.Trim();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
