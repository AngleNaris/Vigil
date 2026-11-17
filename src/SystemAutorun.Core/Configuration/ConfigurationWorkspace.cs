using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Configuration;

public interface IRevisionedResult { string Revision { get; } }

public sealed record ConfigurationWorkspaceSnapshot(
    string Revision,
    DateTimeOffset CapturedAt,
    AutorunConfiguration Configuration,
    ApplicationSettings Preferences) : IRevisionedResult;

public sealed record WorkspaceMutation<T>(T Value, string Revision, DateTimeOffset CapturedAt) : IRevisionedResult;

public sealed record ApplicationSettingsPatch(
    bool? AutoStartMissingOnLaunch = null,
    bool? AutoRecoverFailedTasks = null);

public sealed class ConfigurationConflictException(string expectedRevision, string actualRevision)
    : InvalidOperationException("Configuration changed. Reload the current revision before applying this change.")
{
    public string ExpectedRevision { get; } = expectedRevision;
    public string ActualRevision { get; } = actualRevision;
}

// Single mutation surface for GUI/CLI. The lease/CAS guarantee covers cooperating Agent
// clients; manual editors must reload and should not write while a mutation is in flight.
public sealed partial class ConfigurationWorkspace
{
    private readonly ProjectPaths _paths;
    private readonly ConfigurationLoader _loader = new();
    private readonly ConfigurationFileEditor _editor = new();
    private readonly ApplicationSettingsStore _settings;
    private readonly OverrideStore _overrides = new();

    public ConfigurationWorkspace(ProjectPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _settings = new ApplicationSettingsStore(paths.GuiSettingsPath);
    }

    public ProjectPaths Paths => _paths;

    public async Task<ConfigurationWorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await RootWriteLease.AcquireAsync(_paths, cancellationToken).ConfigureAwait(false);
        return await ReadWithLeaseAsync(lease, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConfigurationWorkspaceSnapshot> ReadWithLeaseAsync(RootWriteLease.Held lease, CancellationToken cancellationToken = default)
    {
        lease.Require(_paths);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var revision = await ReadRevisionAsync(cancellationToken).ConfigureAwait(false);
            var configuration = await _loader.LoadAsync(_paths.RootDirectory, cancellationToken).ConfigureAwait(false);
            var preferences = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
            var after = await ReadRevisionAsync(cancellationToken).ConfigureAwait(false);
            if (revision == after)
            {
                return new ConfigurationWorkspaceSnapshot(revision, DateTimeOffset.UtcNow, configuration, preferences);
            }
        }

        throw new IOException("Configuration is being changed outside Agent. Retry after the external edit finishes.");
    }

    public Task<WorkspaceMutation<ConfigurationMutationResult>> AddTaskAsync(
        AddTaskRequest request, string expectedRevision, CancellationToken cancellationToken = default) =>
        MutateAsync(expectedRevision, token => _editor.AddTaskAsync(_paths, request, token), cancellationToken);

    public Task<WorkspaceMutation<ConfigurationMutationResult>> UpdateTaskAsync(
        UpdateTaskRequest request, string expectedRevision, CancellationToken cancellationToken = default) =>
        MutateAsync(expectedRevision, token => _editor.UpdateTaskAsync(_paths, request, token), cancellationToken);

    public Task<WorkspaceMutation<ConfigurationMutationResult>> ReorderTasksAsync(
        ReorderTasksRequest request, string expectedRevision, CancellationToken cancellationToken = default) =>
        MutateAsync(expectedRevision, token => _editor.ReorderTasksAsync(_paths, request, token), cancellationToken);

    public Task<WorkspaceMutation<ConfigurationMutationResult>> RemoveTaskAsync(
        string name, string expectedRevision, CancellationToken cancellationToken = default) =>
        MutateAsync(expectedRevision, token => _editor.RemoveTaskAsync(_paths, name, token), cancellationToken);

    public Task<WorkspaceMutation<ConfigurationMutationResult>> SetExpectedRunningAsync(
        string name, bool enabled, string expectedRevision, CancellationToken cancellationToken = default) =>
        MutateAsync(expectedRevision,
            token => _editor.SetExpectedRunningAsync(_paths, name, enabled, token), cancellationToken);

    public Task<WorkspaceMutation<ConfigurationMutationResult>> SetAutomationAsync(
        string name, TaskAutomationPatch patch, string expectedRevision, CancellationToken cancellationToken = default) =>
        MutateAsync(expectedRevision,
            token => _editor.SetAutomationAsync(_paths, name, patch, token), cancellationToken);

    public Task<WorkspaceMutation<AutorunTask>> SetEnabledAsync(
        string name, bool enabled, string expectedRevision, CancellationToken cancellationToken = default) =>
        MutateAsync(expectedRevision, async token =>
        {
            var configuration = await _loader.LoadAsync(_paths.RootDirectory, token).ConfigureAwait(false);
            _ = configuration.GetTask(name);
            await _overrides.SetEnabledAsync(_paths.RootDirectory, name, enabled, token).ConfigureAwait(false);
            return (await _loader.LoadAsync(_paths.RootDirectory, token).ConfigureAwait(false)).GetTask(name);
        }, cancellationToken);

    public Task<WorkspaceMutation<ApplicationSettings>> SaveSettingsAsync(
        ApplicationSettings settings, string expectedRevision, CancellationToken cancellationToken = default) =>
        MutateAsync(expectedRevision, token => SaveSettingsCoreAsync(settings, token), cancellationToken);

    public Task<WorkspaceMutation<ApplicationSettings>> PatchSettingsAsync(
        ApplicationSettingsPatch patch, string expectedRevision, CancellationToken cancellationToken = default) =>
        MutateAsync(expectedRevision, async token =>
        {
            ArgumentNullException.ThrowIfNull(patch);
            var current = await _settings.LoadAsync(token).ConfigureAwait(false);
            return await SaveSettingsCoreAsync(current with
            {
                AutoStartMissingOnLaunch = patch.AutoStartMissingOnLaunch ?? current.AutoStartMissingOnLaunch,
                AutoRecoverFailedTasks = patch.AutoRecoverFailedTasks ?? current.AutoRecoverFailedTasks,
            }, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<WorkspaceMutation<ApplicationSettings>> UpsertRemoteHostAsync(
        RemoteHostDefinition host, string expectedRevision, CancellationToken cancellationToken = default) =>
        WriteRemoteHostAsync(host, expectedRevision, create: null, cancellationToken);

    public Task<WorkspaceMutation<ApplicationSettings>> AddRemoteHostAsync(
        RemoteHostDefinition host, string expectedRevision, CancellationToken cancellationToken = default) =>
        WriteRemoteHostAsync(host, expectedRevision, create: true, cancellationToken);

    public Task<WorkspaceMutation<ApplicationSettings>> UpdateRemoteHostAsync(
        RemoteHostDefinition host, string expectedRevision, CancellationToken cancellationToken = default) =>
        WriteRemoteHostAsync(host, expectedRevision, create: false, cancellationToken);

    private Task<WorkspaceMutation<ApplicationSettings>> WriteRemoteHostAsync(
        RemoteHostDefinition host, string expectedRevision, bool? create, CancellationToken cancellationToken) =>
        MutateAsync(expectedRevision, async token =>
        {
            RemoteHostDefinitionValidator.Validate(host);
            var current = await _settings.LoadAsync(token).ConfigureAwait(false);
            var hosts = current.RemoteHosts.ToList();
            var index = hosts.FindIndex(candidate => string.Equals(candidate.Id, host.Id, StringComparison.OrdinalIgnoreCase));
            if (create == true && index >= 0) { throw new ArgumentException($"Remote host ID '{host.Id}' already exists; use host update."); }
            if (create == false && index < 0) { throw new KeyNotFoundException($"Remote host ID '{host.Id}' does not exist; use host add."); }
            if (index < 0)
            {
                hosts.Add(host);
            }
            else
            {
                hosts[index] = host;
            }

            return await SaveSettingsCoreAsync(current with { RemoteHosts = hosts }, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<WorkspaceMutation<ApplicationSettings>> RemoveRemoteHostAsync(
        string id, string expectedRevision, CancellationToken cancellationToken = default) =>
        MutateAsync(expectedRevision, async token =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            var current = await _settings.LoadAsync(token).ConfigureAwait(false);
            var hosts = current.RemoteHosts.Where(host => !string.Equals(host.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (hosts.Length == current.RemoteHosts.Count)
            {
                throw new KeyNotFoundException($"Remote host not found: {id}");
            }

            return await SaveSettingsCoreAsync(current with { RemoteHosts = hosts }, token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<ApplicationSettings> SaveSettingsCoreAsync(ApplicationSettings settings, CancellationToken token)
    {
        await _settings.SaveAsync(settings, token).ConfigureAwait(false);
        return await _settings.LoadAsync(token).ConfigureAwait(false);
    }

    private async Task<WorkspaceMutation<T>> MutateAsync<T>(
        string expectedRevision, Func<CancellationToken, Task<T>> mutation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        await using var lease = await RootWriteLease.AcquireAsync(_paths, cancellationToken).ConfigureAwait(false);
        var current = await ReadRevisionAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(expectedRevision, current, StringComparison.Ordinal))
        {
            throw new ConfigurationConflictException(expectedRevision, current);
        }

        var result = await mutation(cancellationToken).ConfigureAwait(false);
        var revision = await ReadRevisionAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceMutation<T>(result, revision, DateTimeOffset.UtcNow);
    }

    private Task<string> ReadRevisionAsync(CancellationToken cancellationToken) =>
        ConfigurationRevision.ReadAsync(_paths, cancellationToken: cancellationToken);
}
