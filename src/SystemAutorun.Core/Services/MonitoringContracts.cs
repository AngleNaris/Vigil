using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Services;

public interface IRemoteHostReader
{
    Task<RemoteHostStatusSnapshot> ReadAsync(
        RemoteHostDefinition definition,
        CancellationToken cancellationToken = default);
}

public interface IRemoteHostStreamSource
{
    IAsyncEnumerable<RemoteHostStatusSnapshot> StreamAsync(
        RemoteHostDefinition definition,
        TimeSpan samplingInterval,
        CancellationToken cancellationToken = default);
}

public interface IRemoteHostAdaptiveStreamSource : IRemoteHostStreamSource
{
    IAsyncEnumerable<RemoteHostStatusSnapshot> StreamAdaptiveAsync(
        RemoteHostDefinition definition,
        TimeSpan initialSamplingInterval,
        IAsyncEnumerable<TimeSpan> samplingIntervalUpdates,
        CancellationToken cancellationToken = default);
}
