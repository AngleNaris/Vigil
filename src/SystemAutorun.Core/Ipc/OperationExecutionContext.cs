using System.Text.Json.Serialization;
using SystemAutorun.Core.Infrastructure;

namespace SystemAutorun.Core.Ipc;

// Live Journal callback metadata, NOT confirmation, authentication or native authority.
// No public constructor, setter, Dispose or Windows owner. This is not a wire contract.
public sealed class OperationExecutionContext
{
    private readonly string _physicalRoot;
    private int _ended;

    public string OperationId { get; }
    public string Fingerprint { get; }
    public string OwnerInstanceId { get; }
    public DateTimeOffset AcceptedAt { get; }
    public bool IsActive => Volatile.Read(ref _ended) == 0;

    // Do not accidentally persist the original request through response serialization.
    // The owned JsonElement clone remains readable as historical data after End, not authority.
    [JsonIgnore]
    public AgentRequest Request { get; }

    internal OperationExecutionContext(string physicalRoot, string operationId, string fingerprint,
        string ownerInstanceId, DateTimeOffset acceptedAt, AgentRequest snapshot)
    {
        _physicalRoot = physicalRoot; OperationId = operationId; Fingerprint = fingerprint;
        OwnerInstanceId = ownerInstanceId; AcceptedAt = acceptedAt; Request = snapshot;
    }

    // A point-in-time namespace/lifetime check, NOT a directory-object or native CAS.
    // It cannot revoke an already-created owner or make fire-and-forget work safe.
    public void RequireActiveFor(ProjectPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        RequireActive();
        if (!string.Equals(PhysicalDirectory.Resolve(paths.RootDirectory), _physicalRoot, StringComparison.Ordinal))
        { throw new ArgumentException("Execution context belongs to a different physical root namespace.", nameof(paths)); }
        RequireActive();
    }

    private void RequireActive()
    {
        if (!IsActive) { throw new InvalidOperationException("The Journal execute callback has ended."); }
    }

    internal void End() => Interlocked.Exchange(ref _ended, 1);
}
