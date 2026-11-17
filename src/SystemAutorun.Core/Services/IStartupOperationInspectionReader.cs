using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Services;

// One explicit ID only. Completion means observation and cleanup have actually ended.
public interface IStartupOperationInspectionReader
{
    Task<StartupOperationInspectionSnapshot> ReadAsync(string operationId, CancellationToken cancellationToken = default);
}
