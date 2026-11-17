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
