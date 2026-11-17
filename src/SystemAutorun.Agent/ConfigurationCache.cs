using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Agent;

internal sealed class ConfigurationCache
{
    private readonly ConfigurationLoader _loader;
    private readonly ProjectPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AutorunConfiguration? _configuration;
    private string? _revision;

    internal ConfigurationCache(ConfigurationLoader loader, ProjectPaths paths)
    {
        _loader = loader;
        _paths = paths;
    }

    internal ProjectPaths Paths => _paths;

    internal async Task<AutorunConfiguration> GetAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var revision = await ReadRevisionAsync(cancellationToken).ConfigureAwait(false);
                if (!force && _configuration is not null && revision == _revision)
                {
                    return _configuration;
                }

                var configuration = await _loader.LoadAsync(_paths.RootDirectory, cancellationToken).ConfigureAwait(false);
                var after = await ReadRevisionAsync(cancellationToken).ConfigureAwait(false);
                if (revision == after)
                {
                    _configuration = configuration;
                    _revision = revision;
                    return configuration;
                }
            }

            throw new IOException("Configuration kept changing while Agent was reading it.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<string> ReadRevisionAsync(CancellationToken cancellationToken) =>
        ConfigurationRevision.ReadAsync(_paths, includePreferences: false, cancellationToken);
}
