using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Services;

public interface IStartupRegistrationReader
{
    Task<StartupRegistrationData> ReadAsync(string kind, CancellationToken cancellationToken = default);
}
