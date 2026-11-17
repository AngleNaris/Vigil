using System.Text.Json;
using System.Text.Json.Serialization;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Configuration;

public sealed class ApplicationSettingsStore
{
    private const int MaximumSettingsBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _path;
    private readonly object _saveSync = new();
    private Task _pendingSave = Task.CompletedTask;

    public ApplicationSettingsStore(string path) => _path = Path.GetFullPath(path);

    public async Task<ApplicationSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = SnapshotFile.OpenRead(_path);
            if (stream.Length > MaximumSettingsBytes)
            {
                throw new ConfigurationException("Application settings exceed the 1 MiB limit.");
            }

            var settings = await JsonSerializer.DeserializeAsync<ApplicationSettings>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new ConfigurationException("Application settings must be a JSON object, not null.");
            Validate(settings);
            return settings;
        }
        catch (FileNotFoundException)
        {
            return new ApplicationSettings();
        }
        catch (JsonException exception)
        {
            // Corruption is not an empty settings file: do not silently erase hosts on save.
            throw new ConfigurationException($"Invalid application settings: {exception.Message}", exception);
        }
    }

    public Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);
        var snapshot = settings with
        {
            RemoteHosts = settings.RemoteHosts.ToArray(),
            LogSources = settings.LogSources.Select(source => source with
            {
                AdditionalSettings = source.AdditionalSettings?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()),
            }).ToArray(),
            AdditionalSettings = settings.AdditionalSettings?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()),
        };
        lock (_saveSync)
        {
            var save = SaveAfterAsync(_pendingSave, snapshot, cancellationToken);
            _pendingSave = save;
            return save;
        }
    }

    private async Task SaveAfterAsync(Task previousSave, ApplicationSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            await previousSave.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The earlier caller observes its failure; later saves must not be poisoned.
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        if (bytes.Length > MaximumSettingsBytes)
        {
            throw new ConfigurationException("Application settings exceed the 1 MiB limit.");
        }

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            SnapshotFile.Publish(temporary, _path);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void Validate(ApplicationSettings settings)
    {
        Logging.LogSourcePolicy.ValidateDefinitions(settings.LogSources);
        if (settings.RemoteHosts is null)
        {
            throw new ConfigurationException("remote_hosts must be an array.");
        }

        if (settings.AdditionalSettings?.Keys.Any(key =>
            string.Equals(key, "auto_start_missing_on_launch", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "auto_recover_failed_tasks", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "remote_hosts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "log_sources", StringComparison.OrdinalIgnoreCase)) == true)
        {
            throw new ConfigurationException("Additional settings must not shadow built-in settings.");
        }

        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in settings.RemoteHosts)
        {
            if (host is null)
            {
                throw new ConfigurationException("remote_hosts must not contain null entries.");
            }

            RemoteHostDefinitionValidator.Validate(host);
            if (!identities.Add(host.Id))
            {
                throw new ConfigurationException($"Duplicate remote host id: {host.Id}");
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }
}
