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
    private DateTime _configWriteTime;
    private DateTime _overridesWriteTime;

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
            var configWriteTime = GetWriteTime(_paths.ConfigPath);
            var overridesWriteTime = GetWriteTime(_paths.OverridesPath);
            if (!force
                && _configuration is not null
                && configWriteTime == _configWriteTime
                && overridesWriteTime == _overridesWriteTime)
            {
                return _configuration;
            }

            _configuration = await _loader.LoadAsync(_paths.RootDirectory, cancellationToken)
                .ConfigureAwait(false);
            _configWriteTime = configWriteTime;
            _overridesWriteTime = overridesWriteTime;
            return _configuration;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static DateTime GetWriteTime(string path) =>
        File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
}
