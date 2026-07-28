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
    int? StartupDelay = null);

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

            var executable = Path.GetFullPath(Required(request.Executable, "Executable path is required."));
            if (!File.Exists(executable))
            {
                throw new FileNotFoundException("Executable does not exist.", executable);
            }

            var delay = request.StartupDelay ?? configuration.Settings.DefaultStartupDelay;
            if (delay < 0)
            {
                throw new ConfigurationException("startup_delay must be non-negative.");
            }

            var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? Path.GetDirectoryName(executable)!
                : Path.GetFullPath(request.WorkingDirectory);
            var original = await File.ReadAllTextAsync(paths.ConfigPath, cancellationToken)
                .ConfigureAwait(false);
            var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var candidate = original.TrimEnd('\r', '\n')
                + newline
                + newline
                + BuildWindowsTaskBlock(
                    name,
                    executable,
                    request.Arguments,
                    string.IsNullOrWhiteSpace(request.Description) ? name : request.Description.Trim(),
                    workingDirectory,
                    delay,
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

            var original = await File.ReadAllTextAsync(paths.ConfigPath, cancellationToken)
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
            var endIndex = checked((int)mappingNode.End.Index);
            end = endIndex >= yaml.Length ? yaml.Length : FindLineEnd(yaml, endIndex);
        }

        return yaml.Remove(start, end - start);
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
            var latest = await File.ReadAllTextAsync(paths.ConfigPath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(latest, original, StringComparison.Ordinal))
            {
                throw new IOException("autorun.yaml changed while the edit was being prepared.");
            }

            await WriteDurableTextAsync(backupPath, original, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, paths.ConfigPath, overwrite: true);
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

    private static string BuildWindowsTaskBlock(
        string name,
        string executable,
        IReadOnlyList<string> arguments,
        string description,
        string workingDirectory,
        int delay,
        string newline)
    {
        var lines = new List<string>
        {
            $"  - name: {Quote(name)}",
            "    type: windows",
            "    enabled: true",
            $"    description: {Quote(description)}",
            $"    exec: {Quote(executable)}",
        };
        if (arguments.Count > 0)
        {
            lines.Add($"    args: [{string.Join(", ", arguments.Select(Quote))}]");
        }

        lines.Add($"    working_dir: {Quote(workingDirectory)}");
        lines.Add($"    startup_delay: {delay}");
        lines.Add("    health:");
        lines.Add("      type: process");
        lines.Add($"      pattern: {Quote(Path.GetFileName(executable))}");
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

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
