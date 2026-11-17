namespace SystemAutorun.Core.Models;

public enum ResourceHistorySource { RecentSeconds, Durable }

// Both target fields and RemoteHostId null select the local host. This is deliberately independent of health history.
public sealed record ResourceHistoryQuery(DateTimeOffset From, DateTimeOffset To, int BucketCount = 301,
    string? TaskName = null, string? TaskIdentity = null, ResourceHistorySource Source = ResourceHistorySource.RecentSeconds,
    string? RemoteHostId = null);

// SampleCount counts captures with at least one known metric, not per-metric coverage.
public sealed record ResourceHistoryPoint(DateTimeOffset At, string? ContinuityId,
    double? CpuPercent = null, double? MemoryBytes = null,
    double? DiskReadBytesPerSecond = null, double? DiskWriteBytesPerSecond = null,
    double? NetworkReceiveBytesPerSecond = null, double? NetworkSendBytesPerSecond = null,
    int SampleCount = 1, bool BreakBefore = false);

public sealed record ResourceHistoryResult(int SchemaVersion, ResourceHistorySource Source,
    string? TaskName, string? TaskIdentity, DateTimeOffset From, DateTimeOffset To,
    int SourceIntervalSeconds, double BucketSeconds, IReadOnlyList<ResourceHistoryPoint> Points,
    DateTimeOffset? RetainedFrom, DateTimeOffset? LatestRecordAt, bool Truncated, string? Warning,
    string? RemoteHostId = null);

// Opt-in recent-resource schema; milliseconds describe the real Agent sampling target, not GUI polling.
public sealed record RecentResourceHistoryResult(int SchemaVersion, ResourceHistorySource Source,
    string? TaskName, string? TaskIdentity, DateTimeOffset From, DateTimeOffset To,
    int SourceIntervalMilliseconds, double BucketSeconds, IReadOnlyList<ResourceHistoryPoint> Points,
    DateTimeOffset? RetainedFrom, DateTimeOffset? LatestRecordAt, bool Truncated, string? Warning,
    string AgentInstanceId, string? RemoteHostId = null);

// Compact memory-only captures. No command lines, paths, NICs or full process/host snapshots.
public sealed record ResourceTaskSample(string TaskName, string TaskIdentity, DateTimeOffset CapturedAt,
    string? ProcessIdentity, double? CpuPercent = null, long? WorkingSetBytes = null);
public sealed record ResourceHistoryFrame(DateTimeOffset At, string Epoch,
    IReadOnlyList<ResourceTaskSample> Tasks, HostHistorySample? Host = null, DateTimeOffset? HostCapturedAt = null);
