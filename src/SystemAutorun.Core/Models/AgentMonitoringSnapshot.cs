namespace SystemAutorun.Core.Models;

// Availability is evaluated by Agent when returning the envelope. A retained Data value
// with Available=false is diagnostic cache, never a current successful observation.
public sealed record MonitoringDataGroup<T>(
    bool Available,
    DateTimeOffset? ObservedAt,
    DateTimeOffset? AttemptedAt,
    T? Data,
    string? Error) where T : class;

public sealed record MonitoringPersistenceStatus(
    bool Available,
    DateTimeOffset? LastWrittenAt,
    string? Error,
    long MaximumBytes,
    int MaximumRetentionDays);

public sealed record TaskRuntimeObservation(
    string TaskName,
    string TaskIdentity,
    DateTimeOffset ObservedAt,
    DateTimeOffset? ProcessStartedAt,
    string ProcessUptimeSource,
    string? ProcessIdentity,
    DateTimeOffset? ConsecutiveHealthySince,
    string ConsecutiveHealthSource);

public sealed record AgentMonitoringSnapshot(
    int SchemaVersion,
    string RootId,
    string InstanceId,
    int ProcessId,
    string Version,
    DateTimeOffset StartedAt,
    DateTimeOffset HeartbeatAt,
    DateTimeOffset ReturnedAt,
    string? ConfigurationRevision,
    int SamplingIntervalSeconds,
    MonitoringDataGroup<IReadOnlyList<TaskSnapshot>> Tasks,
    MonitoringDataGroup<IReadOnlyList<ManagedProcessSnapshot>> Processes,
    MonitoringDataGroup<HostStatusSnapshot> Host,
    IReadOnlyList<TaskRuntimeObservation> Runtime,
    MonitoringPersistenceStatus Persistence,
    int? ResourceSamplingIntervalMilliseconds = null,
    int? RecentResourceSchemaVersion = null,
    int? HealthConfigurationGeneration = null,
    long? HealthClockEpoch = null,
    IReadOnlyList<RemoteHostStatusSnapshot>? RemoteHosts = null);
