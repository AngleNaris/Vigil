using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Services;

public interface IStartupSnapshotReader
{
    Task<StartupManagementSnapshot> ReadAsync(AutorunConfiguration configuration, StartupListQuery query,
        CancellationToken cancellationToken = default);
}
