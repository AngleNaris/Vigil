using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Logging;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Agent;

internal sealed class AgentLogService(ConfigurationWorkspace workspace)
{
    private readonly LogPageReader _reader = new();

    internal async Task<LogSourcesSnapshot> SourcesAsync(CancellationToken cancellationToken)
    {
        var snapshot = await workspace.ReadAsync(cancellationToken).ConfigureAwait(false);
        var sources = new List<LogSourceInfo>(); var bytes = 0;
        foreach (var source in Sources(snapshot))
        {
            var size = JsonSerializer.SerializeToUtf8Bytes(source, AgentProtocol.JsonOptions).Length;
            if (sources.Count == 256 || bytes + size > 256 * 1024) { break; }
            sources.Add(source); bytes += size;
        }
        var total = snapshot.Configuration.Tasks.Count + snapshot.Preferences.LogSources.Count + 2;
        return new LogSourcesSnapshot(1, snapshot.Revision, DateTimeOffset.UtcNow, sources, total, sources.Count < total);
    }

    internal async Task<LogPage> ReadAsync(LogQuery query, CancellationToken cancellationToken)
    {
        LogPageReader.Validate(query);
        var snapshot = await workspace.ReadAsync(cancellationToken).ConfigureAwait(false);
        var id = query.Source ?? (query.Task is null ? "control:agent" : TaskSourceId(snapshot.Configuration.GetTask(query.Task).Name));
        var source = Sources(snapshot).FirstOrDefault(candidate => candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Log source not configured: {id}");
        if (query.Task is not null && !string.Equals(query.Task, source.TaskName, StringComparison.OrdinalIgnoreCase))
        { throw new ArgumentException("The selected log source is not associated with the requested task."); }
        return await _reader.ReadAsync(source, query, workspace.Paths.RootDirectory, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<LogSourceInfo> Sources(ConfigurationWorkspaceSnapshot snapshot)
    {
        var configuration = snapshot.Configuration;
        yield return new("control:agent", "file", "control", null, Path.Combine(configuration.Settings.LogDirectory, "agent.log"));
        yield return new("control:agent-fatal", "file", "control", null, Path.Combine(configuration.RootDirectory, "agent-fatal.log"));
        foreach (var definition in snapshot.Preferences.LogSources)
        {
            var missingTask = definition.TaskName is not null && !configuration.Tasks.Any(task => task.Name.Equals(definition.TaskName, StringComparison.OrdinalIgnoreCase));
            string? path = null;
            var reason = missingTask ? "task_not_found" : definition.Kind != "file" ? "provider_unsupported" : null;
            if (definition.Kind == "file")
            {
                try { path = LogSourcePolicy.ResolveFilePath(definition.Path!, configuration.RootDirectory); }
                catch (ArgumentException) { reason = "invalid_source_path"; }
            }
            yield return new(definition.Id, definition.Kind, "business", definition.TaskName, path, reason);
        }
        var categories = configuration.Tasks.GroupBy(task => FileLog.CategoryFileName(task.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var task in configuration.Tasks)
        {
            var category = FileLog.CategoryFileName(task.Name);
            var path = Path.Combine(configuration.Settings.LogDirectory, category);
            var ambiguous = categories[category] > 1 || category.Equals("agent.log", StringComparison.OrdinalIgnoreCase)
                || path.Equals(Path.Combine(configuration.RootDirectory, "agent-fatal.log"), StringComparison.OrdinalIgnoreCase);
            yield return new(TaskSourceId(task.Name), "file", "control", task.Name, path,
                ambiguous ? "control_category_ambiguous" : null);
        }
    }

    private static string TaskSourceId(string name) => "control:task:" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant())))[..20];
}
