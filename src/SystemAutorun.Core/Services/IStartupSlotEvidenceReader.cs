using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Services;

// Fixed source mapping belongs to the adapter. No path/identity/write payload is accepted.
// Cancellation may stop a caller's wait; completion MUST mean native work and cleanup ended.
public interface IStartupSlotEvidenceReader
{
    Task<StartupSlotEvidenceRead> ReadAsync(string backupId, CancellationToken cancellationToken = default);
}
