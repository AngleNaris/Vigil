namespace SystemAutorun.Core.Models;

public enum ObservationHealth { Unknown, Healthy, Fault }

// No command lines, environment, probe output, or executable paths are persisted here.
public sealed record TaskHistorySample(
    string TaskName,
    string TaskIdentity,
    ObservationHealth Health,
    double? CpuPercent = null,
    long? WorkingSetBytes = null,
    long? PhysicalMemoryBytes = null,
    DateTimeOffset? ValidUntil = null,
    string? ResourceIdentity = null);

public sealed record HostHistorySample(
    double? CpuPercent = null,
    long? MemoryUsedBytes = null,
    long? PhysicalMemoryBytes = null,
    double? DiskReadBytesPerSecond = null,
    double? DiskWriteBytesPerSecond = null,
    double? NetworkReceiveBytesPerSecond = null,
    double? NetworkSendBytesPerSecond = null);

public sealed record RemoteHostHistorySample(
    string HostId,
    string HostIdentity,
    long SessionEpoch = 0,
    HostHistorySample? Host = null);

public sealed record MonitoringHistoryFrame(
    int SchemaVersion,
    string RootId,
    string AgentInstanceId,
    DateTimeOffset ObservedAt,
    IReadOnlyList<TaskHistorySample> Tasks,
    long EpochSequence = 0,
    HostHistorySample? Host = null,
    IReadOnlyList<RemoteHostHistorySample>? RemoteHosts = null);

public sealed record MonitoringHistoryQuery(
    string TaskName,
    string TaskIdentity,
    DateTimeOffset From,
    DateTimeOffset To,
    int BucketCount = 120);

public sealed record HealthHistoryInterval(DateTimeOffset From, DateTimeOffset To, ObservationHealth Health);

public sealed record MonitoringHistoryBucket(
    DateTimeOffset From,
    DateTimeOffset To,
    double HealthySeconds,
    double FaultSeconds,
    double UnknownSeconds,
    double? AvailabilityPercent,
    double CoveragePercent,
    double? CpuPercent,
    double? MemoryPercent,
    double? WorkingSetBytes = null);

public sealed record MonitoringHistoryResult(
    int SchemaVersion,
    string TaskName,
    string TaskIdentity,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset? LatestRecordAt,
    DateTimeOffset? RetainedFrom,
    double HealthySeconds,
    double FaultSeconds,
    double UnknownSeconds,
    double? AvailabilityPercent,
    double CoveragePercent,
    IReadOnlyList<HealthHistoryInterval> Intervals,
    IReadOnlyList<MonitoringHistoryBucket> Buckets,
    bool Truncated,
    string? Warning);
