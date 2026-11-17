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

public sealed record ReorderTasksRequest(IReadOnlyList<string> OrderedNames);

public sealed record UpdateTaskRequest(string OriginalName, AddTaskRequest Task);

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
            var spec = PrepareTaskSpec(configuration, request);
            if (configuration.Tasks.Any(task =>
                string.Equals(task.Name, spec.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ConfigurationException($"Duplicate task name: {spec.Name}");
            }

            var original = await SnapshotFile.ReadAllTextAsync(paths.ConfigPath, cancellationToken)
                .ConfigureAwait(false);
            var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var appendBase = PrepareTaskSequenceForAppend(original, out var wasEmpty);
            var candidate = appendBase.TrimEnd('\r', '\n')
                + newline
                + (wasEmpty ? string.Empty : newline)
                + BuildTaskBlock(spec, enabled: true, newline)
                + newline;
            return await CommitAsync(paths, original, candidate, "add", spec.Name, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            MutationLock.Release();
        }
    }

    public async Task<ConfigurationMutationResult> UpdateTaskAsync(
        ProjectPaths paths,
        UpdateTaskRequest update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(update.Task);
        var originalName = Required(update.OriginalName, "Original task name is required.");
        await MutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = await _loader.LoadAsync(paths.RootDirectory, cancellationToken)
                .ConfigureAwait(false);
            var existing = configuration.GetTask(originalName);
            var spec = PrepareTaskSpec(configuration, update.Task);
            if (configuration.Tasks.Any(task =>
                !string.Equals(task.Name, existing.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(task.Name, spec.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ConfigurationException($"Duplicate task name: {spec.Name}");
            }

            var original = await SnapshotFile.ReadAllTextAsync(paths.ConfigPath, cancellationToken)
                .ConfigureAwait(false);
            var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var candidate = ReplaceTaskBlock(original, existing.Name, BuildTaskBlock(spec, existing.Enabled, newline));
            var result = await CommitAsync(paths, original, candidate, "update", spec.Name, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(existing.Name, spec.Name, StringComparison.OrdinalIgnoreCase))
            {
                var overrides = new OverrideStore();
                await overrides.RemoveAsync(paths.RootDirectory, existing.Name, cancellationToken)
                    .ConfigureAwait(false);
                if (!existing.Enabled)
                {
                    await overrides.SetEnabledAsync(paths.RootDirectory, spec.Name, false, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return result;
        }
        finally
        {
            MutationLock.Release();
        }
    }

    public async Task<ConfigurationMutationResult> ReorderTasksAsync(
        ProjectPaths paths,
        ReorderTasksRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.OrderedNames);
        await MutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = await _loader.LoadAsync(paths.RootDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (configuration.Tasks.Count <= 1)
            {
                return new ConfigurationMutationResult(true, "reorder", string.Empty, string.Empty);
            }

            var byName = configuration.Tasks.ToDictionary(task => task.Name, StringComparer.OrdinalIgnoreCase);
            var desired = new List<string>(configuration.Tasks.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in request.OrderedNames)
            {
                if (!string.IsNullOrWhiteSpace(name)
                    && byName.TryGetValue(name.Trim(), out var task)
                    && seen.Add(task.Name))
                {
                    desired.Add(task.Name);
                }
            }

            foreach (var task in configuration.Tasks)
            {
                if (seen.Add(task.Name))
                {
                    desired.Add(task.Name);
                }
            }

            if (desired.SequenceEqual(configuration.Tasks.Select(task => task.Name), StringComparer.OrdinalIgnoreCase))
            {
                return new ConfigurationMutationResult(true, "reorder", string.Empty, string.Empty);
            }

            var original = await SnapshotFile.ReadAllTextAsync(paths.ConfigPath, cancellationToken)
                .ConfigureAwait(false);
            var candidate = ReorderTaskBlocks(original, desired);
            return await CommitAsync(paths, original, candidate, "reorder", "tasks", cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            MutationLock.Release();
        }
    }

    private static PreparedTaskSpec PrepareTaskSpec(AutorunConfiguration configuration, AddTaskRequest request)
    {
        var name = Required(request.Name, "Task name is required.");
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
        var dependsOn = request.DependsOn?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<string>();
        var description = string.IsNullOrWhiteSpace(request.Description) ? name : request.Description.Trim();
        return new PreparedTaskSpec(
            name,
            request.Type,
            executable,
            request.Arguments ?? Array.Empty<string>(),
            description,
            workingDirectory,
            NormalizeOptional(request.Distro),
            NormalizeOptional(request.Service),
            NormalizeOptional(request.Command),
            dependsOn,
            delay,
            health,
            launch,
            request.ExpectedRunning,
            request.StartOnAppLaunch,
            request.AutoRecover);
    }

    private sealed record PreparedTaskSpec(
        string Name,
        AutorunTaskType Type,
        string? Executable,
        IReadOnlyList<string> Arguments,
        string Description,
        string? WorkingDirectory,
        string? Distro,
        string? Service,
        string? Command,
        IReadOnlyList<string> DependsOn,
        int Delay,
        HealthCheckDefinition Health,
        LaunchOptions Launch,
        bool ExpectedRunning,
        bool? StartOnAppLaunch,
        bool? AutoRecover);

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
        var (root, tasks) = LoadTasksSequence(yaml);
        var (start, end, _) = FindTaskSpan(yaml, tasks, taskName);
        var result = yaml.Remove(start, end - start);
        if (tasks.Children.Count == 1)
        {
            var key = root.Children.Keys.OfType<YamlScalarNode>().Single(node => node.Value == "tasks");
            var colon = result.IndexOf(':', checked((int)key.End.Index));
            result = result.Insert(colon + 1, " []");
        }

        return result;
    }

    internal static string ReplaceTaskBlock(string yaml, string taskName, string replacementBlock)
    {
        var (_, tasks) = LoadTasksSequence(yaml);
        var (start, end, _) = FindTaskSpan(yaml, tasks, taskName);
        var existingLength = yaml[start..end].TrimEnd('\r', '\n').Length;
        return yaml.Remove(start, existingLength).Insert(start, replacementBlock);
    }

    internal static string ReorderTaskBlocks(string yaml, IReadOnlyList<string> orderedNames)
    {
        var (_, tasks) = LoadTasksSequence(yaml);
        var spans = new List<(string Name, int Start, int ContentEnd)>(tasks.Children.Count);
        for (var i = 0; i < tasks.Children.Count; i++)
        {
            if (tasks.Children[i] is not YamlMappingNode mapping
                || !mapping.Children.TryGetValue(new YamlScalarNode("name"), out var nameNode)
                || nameNode is not YamlScalarNode scalar
                || string.IsNullOrWhiteSpace(scalar.Value))
            {
                throw new ConfigurationException("All tasks must define scalar names to reorder.");
            }

            var (start, end, _) = GetTaskSpanAt(yaml, tasks, i);
            var contentLength = yaml[start..end].TrimEnd('\r', '\n').Length;
            spans.Add((scalar.Value, start, start + contentLength));
        }

        var blocksByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var span in spans)
        {
            blocksByName[span.Name] = yaml[span.Start..span.ContentEnd];
        }

        var newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var separator = newline;
        if (spans.Count >= 2)
        {
            var between = yaml[spans[0].ContentEnd..spans[1].Start];
            if (!string.IsNullOrEmpty(between))
            {
                separator = between;
            }
        }

        var reordered = orderedNames.Select(name => blocksByName[name]).ToArray();
        var totalStart = spans[0].Start;
        var totalEnd = spans[^1].ContentEnd;
        return yaml.Remove(totalStart, totalEnd - totalStart).Insert(totalStart, string.Join(separator, reordered));
    }

    private static (YamlMappingNode Root, YamlSequenceNode Tasks) LoadTasksSequence(string yaml)
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

        return (root, tasks);
    }

    private static (int Start, int End, YamlMappingNode Node) FindTaskSpan(
        string yaml,
        YamlSequenceNode tasks,
        string taskName)
    {
        var matches = tasks.Children
            .OfType<YamlMappingNode>()
            .Where(mapping => mapping.Children.TryGetValue(new YamlScalarNode("name"), out var nameNode)
                && string.Equals(((YamlScalarNode)nameNode).Value, taskName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ConfigurationException($"Expected one task block for '{taskName}', found {matches.Length}.");
        }

        var taskIndex = tasks.Children.IndexOf(matches[0]);
        return GetTaskSpanAt(yaml, tasks, taskIndex);
    }

    private static (int Start, int End, YamlMappingNode Node) GetTaskSpanAt(
        string yaml,
        YamlSequenceNode tasks,
        int taskIndex)
    {
        var mappingNode = (YamlMappingNode)tasks.Children[taskIndex];
        var start = FindLineStart(yaml, checked((int)mappingNode.Start.Index));
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
                throw new ConfigurationException("Removing or editing this task requires a block-style task entry.");
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

        return (start, end, mappingNode);
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

    private static string BuildTaskBlock(PreparedTaskSpec spec, bool enabled, string newline)
    {
        var lines = new List<string>
        {
            $"  - name: {Quote(spec.Name)}",
            $"    type: {spec.Type.ToKey()}",
            $"    enabled: {enabled.ToString().ToLowerInvariant()}",
            $"    expected_running: {spec.ExpectedRunning.ToString().ToLowerInvariant()}",
            $"    description: {Quote(spec.Description)}",
        };

        if (spec.StartOnAppLaunch is { } starts) { lines.Add($"    start_on_app_launch: {starts.ToString().ToLowerInvariant()}"); }
        if (spec.AutoRecover is { } recovers) { lines.Add($"    auto_recover: {recovers.ToString().ToLowerInvariant()}"); }

        switch (spec.Type)
        {
            case AutorunTaskType.Windows:
                lines.Add($"    exec: {Quote(Required(spec.Executable, "Executable path is required."))}");
                if (spec.Arguments.Count > 0)
                {
                    lines.Add($"    args: [{string.Join(", ", spec.Arguments.Select(Quote))}]");
                }

                lines.Add($"    working_dir: {Quote(Required(spec.WorkingDirectory, "Working directory is required."))}");
                break;
            case AutorunTaskType.WindowsService:
                lines.Add($"    service: {Quote(Required(spec.Service, "Windows service name is required."))}");
                break;
            case AutorunTaskType.WslBoot:
                lines.Add($"    distro: {Quote(Required(spec.Distro, "WSL distro is required."))}");
                break;
            case AutorunTaskType.WslSystemd:
                lines.Add($"    distro: {Quote(Required(spec.Distro, "WSL distro is required."))}");
                lines.Add($"    service: {Quote(Required(spec.Service, "systemd service is required."))}");
                break;
            case AutorunTaskType.WslCommand:
                lines.Add($"    distro: {Quote(Required(spec.Distro, "WSL distro is required."))}");
                lines.Add($"    command: {Quote(Required(spec.Command, "WSL command is required."))}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(spec.Type), spec.Type, null);
        }

        if (spec.DependsOn.Count > 0)
        {
            lines.Add($"    depends_on: [{string.Join(", ", spec.DependsOn.Select(Quote))}]");
        }

        lines.Add($"    startup_delay: {spec.Delay}");
        lines.Add("    launch:");
        lines.Add($"      run_as: {spec.Launch.RunAs.ToKey()}");
        lines.Add($"      trigger: {spec.Launch.Trigger.ToKey()}");
        lines.Add($"      window_mode: {spec.Launch.WindowMode.ToKey()}");
        lines.Add($"      lifecycle: {spec.Launch.Lifecycle.ToKey()}");
        lines.Add("    health:");
        lines.Add($"      type: {spec.Health.Type.ToKey()}");
        switch (spec.Health.Type)
        {
            case HealthCheckType.Process:
                lines.Add($"      pattern: {Quote(Required(spec.Health.Pattern, "Process health pattern is required."))}");
                break;
            case HealthCheckType.Tcp:
                lines.Add($"      host: {Quote(Required(spec.Health.Host, "TCP health host is required."))}");
                lines.Add($"      port: {spec.Health.Port
                    ?? throw new ArgumentException("TCP health port is required.")}");
                break;
            case HealthCheckType.WindowsService:
                lines.Add($"      service: {Quote(Required(
                    spec.Health.Service ?? spec.Service,
                    "Windows service health name is required."))}");
                break;
            case HealthCheckType.WslRunning:
                lines.Add($"      distro: {Quote(Required(spec.Health.Distro ?? spec.Distro, "WSL health distro is required."))}");
                break;
            case HealthCheckType.SystemdActive:
                lines.Add($"      distro: {Quote(Required(spec.Health.Distro ?? spec.Distro, "systemd health distro is required."))}");
                lines.Add($"      service: {Quote(Required(spec.Health.Service ?? spec.Service, "systemd health service is required."))}");
                break;
            case HealthCheckType.WslProcess:
                lines.Add($"      distro: {Quote(Required(spec.Health.Distro ?? spec.Distro, "WSL process distro is required."))}");
                lines.Add($"      pattern: {Quote(Required(spec.Health.Pattern, "WSL process pattern is required."))}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(spec.Health.Type), spec.Health.Type, null);
        }

        lines.Add($"      timeout: {spec.Health.TimeoutSeconds}");
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
