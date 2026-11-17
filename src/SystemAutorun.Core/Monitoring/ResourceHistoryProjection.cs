using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Monitoring;

public static class ResourceHistoryProjection
{
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(5);
    public const int MaximumPoints = 1000;

    public static void ValidateQuery(ResourceHistoryQuery query, DateTimeOffset now)
    {
        var host = query.TaskName is null && query.TaskIdentity is null && query.RemoteHostId is null;
        var remote = query.RemoteHostId is not null;
        if (!Enum.IsDefined(query.Source) || query.From >= query.To || query.To > now
            || query.To - query.From > (query.Source == ResourceHistorySource.RecentSeconds ? RecentWindow : MonitoringHistoryProjection.MaximumWindow)
            || query.BucketCount is < 1 or > MaximumPoints
            || remote && (query.TaskName is not null || query.TaskIdentity is not null
                || string.IsNullOrWhiteSpace(query.RemoteHostId) || query.RemoteHostId.Length > 256
                || query.RemoteHostId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            || !host && !remote && (string.IsNullOrWhiteSpace(query.TaskName) || query.TaskName.Length > 512
                || query.TaskIdentity is null || query.TaskIdentity.Length != 64
                || query.TaskIdentity.Any(character => !char.IsAsciiHexDigitLower(character))))
        { throw new ArgumentException("Invalid resource history target, window, source or point count."); }
    }

    public static ResourceHistoryResult ProjectRecent(IEnumerable<ResourceHistoryFrame> frames, ResourceHistoryQuery query,
        DateTimeOffset now, bool truncated = false, string? warning = null)
    {
        ValidateQuery(query, now);
        if (query.Source != ResourceHistorySource.RecentSeconds) { throw new ArgumentException("Expected recent resource source."); }
        var points = frames.Select(frame => RecentPoint(frame, query)).ToArray();
        return Result(points, query, 1, truncated, warning);
    }

    public static RecentResourceHistoryResult ProjectRecentPrecise(IEnumerable<ResourceHistoryFrame> frames, ResourceHistoryQuery query,
        DateTimeOffset now, int intervalMilliseconds, string agentInstanceId, bool truncated = false, string? warning = null)
    {
        if (!Guid.TryParseExact(agentInstanceId, "N", out _)) { throw new ArgumentException("Invalid Agent instance.", nameof(agentInstanceId)); }
        if (intervalMilliseconds is not (500 or 1000)) { throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds)); }
        var legacyShape = ProjectRecent(frames, query, now, truncated, warning);
        var points = legacyShape.Points.ToArray();
        var maximumContinuousGap = TimeSpan.FromMilliseconds(intervalMilliseconds * 1.5);
        for (var index = 1; index < points.Length; index++)
        {
            if (points[index].At - points[index - 1].At > maximumContinuousGap)
            { points[index] = points[index] with { BreakBefore = true }; }
        }
        return new(2, legacyShape.Source, legacyShape.TaskName, legacyShape.TaskIdentity, legacyShape.From, legacyShape.To,
            intervalMilliseconds, intervalMilliseconds / 1000d, points, legacyShape.RetainedFrom, legacyShape.LatestRecordAt,
            legacyShape.Truncated, legacyShape.Warning, agentInstanceId, legacyShape.RemoteHostId);
    }

    // Old clients accept only a one-second raw view. Select real captures, not averages; any
    // omitted unknown/identity boundary is carried onto the chosen point as a break.
    public static ResourceHistoryResult ProjectRecentCompatible(IEnumerable<ResourceHistoryFrame> frames, ResourceHistoryQuery query,
        DateTimeOffset now, bool truncated = false, string? warning = null)
    {
        ValidateQuery(query, now);
        var precise = ProjectRecentPrecise(frames, query with { BucketCount = MaximumPoints }, now, 500, Guid.Empty.ToString("N"), truncated, warning);
        var groups = precise.Points.GroupBy(point => (point.At - query.From).Ticks / TimeSpan.TicksPerSecond).ToArray();
        var projected = new List<ResourceHistoryPoint>(groups.Length);
        ResourceHistoryPoint? previous = null;
        foreach (var group in groups)
        {
            var points = group.ToArray(); var point = points[^1];
            var boundary = points.Any(item => item.BreakBefore || !Known(item))
                || points.Select(item => item.ContinuityId).Distinct(StringComparer.Ordinal).Count() > 1
                || PartialGap(points, item => item.CpuPercent) || PartialGap(points, item => item.MemoryBytes)
                || PartialGap(points, item => item.DiskReadBytesPerSecond) || PartialGap(points, item => item.DiskWriteBytesPerSecond)
                || PartialGap(points, item => item.NetworkReceiveBytesPerSecond) || PartialGap(points, item => item.NetworkSendBytesPerSecond)
                || previous is not null && (point.ContinuityId is null || previous.ContinuityId != point.ContinuityId);
            projected.Add(boundary ? point with { BreakBefore = true } : point);
            previous = point;
        }
        var limited = projected.Count > query.BucketCount;
        return new(1, query.Source, query.TaskName, query.TaskIdentity, query.From, query.To, 1, 1,
            projected.TakeLast(query.BucketCount).ToArray(), precise.RetainedFrom, precise.LatestRecordAt,
            precise.Truncated || limited, precise.Warning ?? (limited ? "Resource response point limit reached; older samples were omitted." : null),
            query.RemoteHostId);
    }

    public static ResourceHistoryResult ProjectDurable(IEnumerable<MonitoringHistoryFrame> frames, ResourceHistoryQuery query,
        DateTimeOffset now, bool truncated = false, string? warning = null, string? remoteHostIdentity = null)
    {
        ValidateQuery(query, now);
        if (query.Source != ResourceHistorySource.Durable) { throw new ArgumentException("Expected durable resource source."); }
        var input = frames.Take(MonitoringHistoryProjection.MaximumInputFrames + 1).ToArray();
        if (input.Length > MonitoringHistoryProjection.MaximumInputFrames)
        { truncated = true; warning ??= "Resource history record limit reached; omitted time is unknown."; }
        // Shared Store read has already ordered epochs. Last duplicate wins, never an old epoch.
        var points = input.Take(MonitoringHistoryProjection.MaximumInputFrames).Select(frame => DurablePoint(frame, query, remoteHostIdentity)).ToArray();
        return Result(points, query, 10, truncated, warning);
    }

    private static ResourceHistoryPoint RecentPoint(ResourceHistoryFrame frame, ResourceHistoryQuery query)
    {
        if (query.TaskName is null)
        { return HostPoint(frame.HostCapturedAt ?? frame.At, frame.Epoch, frame.Host); }
        var task = frame.Tasks.FirstOrDefault(row => row.TaskIdentity == query.TaskIdentity
            && string.Equals(row.TaskName, query.TaskName, StringComparison.OrdinalIgnoreCase));
        return task is null ? Empty(frame.At) : new(task.CapturedAt,
            task.ProcessIdentity is { } identity ? $"{frame.Epoch}:{identity}" : null,
            Cpu(task.CpuPercent), Nonnegative(task.WorkingSetBytes));
    }

    private static ResourceHistoryPoint DurablePoint(MonitoringHistoryFrame frame, ResourceHistoryQuery query, string? remoteHostIdentity)
    {
        if (query.RemoteHostId is not null)
        {
            var remote = frame.RemoteHosts?.FirstOrDefault(item =>
                string.Equals(item.HostId, query.RemoteHostId, StringComparison.OrdinalIgnoreCase)
                && (remoteHostIdentity is null || item.HostIdentity == remoteHostIdentity));
            return remote?.Host is null
                ? Empty(frame.ObservedAt)
                : HostPoint(frame.ObservedAt, $"{frame.AgentInstanceId}:{remote.HostIdentity}:{remote.SessionEpoch}", remote.Host);
        }
        if (query.TaskName is null) { return HostPoint(frame.ObservedAt, frame.AgentInstanceId, frame.Host); }
        var task = frame.Tasks.FirstOrDefault(row => row.TaskIdentity == query.TaskIdentity
            && string.Equals(row.TaskName, query.TaskName, StringComparison.OrdinalIgnoreCase));
        // Legacy bytes remain queryable, but unknown process boundaries are not asserted continuous.
        return task is null ? Empty(frame.ObservedAt) : new(frame.ObservedAt,
            task.ResourceIdentity is { } identity ? $"{frame.AgentInstanceId}:{identity}" : null,
            Cpu(task.CpuPercent), Nonnegative(task.WorkingSetBytes));
    }

    private static ResourceHistoryPoint HostPoint(DateTimeOffset at, string epoch, HostHistorySample? host) => host is null
        ? Empty(at) : new(at, epoch, Cpu(host.CpuPercent), Nonnegative(host.MemoryUsedBytes),
            Nonnegative(host.DiskReadBytesPerSecond), Nonnegative(host.DiskWriteBytesPerSecond),
            Nonnegative(host.NetworkReceiveBytesPerSecond), Nonnegative(host.NetworkSendBytesPerSecond));

    private static ResourceHistoryPoint Empty(DateTimeOffset at) => new(at, null, SampleCount: 0, BreakBefore: true);
    private static double? Nonnegative(double? value) => value is { } number && double.IsFinite(number) && number >= 0 ? number : null;
    private static double? Cpu(double? value) => value is >= 0 and <= 100 ? value : null;
    private static bool Known(ResourceHistoryPoint point) => point.CpuPercent is not null || point.MemoryBytes is not null
        || point.DiskReadBytesPerSecond is not null || point.DiskWriteBytesPerSecond is not null
        || point.NetworkReceiveBytesPerSecond is not null || point.NetworkSendBytesPerSecond is not null;

    private static ResourceHistoryResult Result(ResourceHistoryPoint[] input, ResourceHistoryQuery query,
        int cadence, bool truncated, string? warning)
    {
        var all = input.Where(point => point.At <= query.To).OrderBy(point => point.At).GroupBy(point => point.At)
            .Select(group => group.Last()).Select(point => Known(point) ? point : point with
            { ContinuityId = null, SampleCount = 0, BreakBefore = true }).ToArray();
        var known = all.Where(Known).ToArray();
        var selected = all.Where(point => point.At >= query.From && point.At <= query.To).ToArray();
        IReadOnlyList<ResourceHistoryPoint> output;
        var bucketSeconds = (double)cadence;
        if (query.Source == ResourceHistorySource.RecentSeconds)
        {
            if (selected.Length > query.BucketCount)
            { truncated = true; warning ??= "Resource response point limit reached; older samples were omitted."; }
            output = selected.TakeLast(query.BucketCount).ToArray();
        }
        else
        {
            // Never turn a 10-second durable sample into fabricated one-second points.
            var buckets = Math.Min(query.BucketCount, Math.Max(1, (int)Math.Ceiling((query.To - query.From).TotalSeconds / cadence)));
            var width = (query.To - query.From).Ticks / (double)buckets;
            bucketSeconds = width / TimeSpan.TicksPerSecond;
            var groups = selected.GroupBy(point => Math.Min(buckets - 1, (int)((point.At - query.From).Ticks / width)))
                .ToDictionary(group => group.Key, group => group.ToArray());
            var result = new List<ResourceHistoryPoint>(buckets);
            ResourceHistoryPoint? previous = null;
            for (var index = 0; index < buckets; index++)
            {
                // An unknown marker is not a capture. Center it to avoid colliding with the preceding
                // bucket's last real capture at a fractional-tick boundary.
                var at = query.From.AddTicks((long)((index + .5) * width));
                if (!groups.TryGetValue(index, out var points)) { result.Add(Empty(at)); previous = null; continue; }
                var epochs = points.Select(point => point.ContinuityId).Distinct(StringComparer.Ordinal).ToArray();
                var boundary = epochs.Length != 1 || epochs[0] is null || points.Any(point => !Known(point))
                    || PartialGap(points, point => point.CpuPercent) || PartialGap(points, point => point.MemoryBytes)
                    || PartialGap(points, point => point.DiskReadBytesPerSecond) || PartialGap(points, point => point.DiskWriteBytesPerSecond)
                    || PartialGap(points, point => point.NetworkReceiveBytesPerSecond) || PartialGap(points, point => point.NetworkSendBytesPerSecond)
                    || previous is null && index > 0
                    || previous is not null && (points[0].At - previous.At > TimeSpan.FromSeconds(15)
                        || points[0].ContinuityId != previous.ContinuityId)
                    || points.Zip(points.Skip(1)).Any(pair => pair.Second.At - pair.First.At > TimeSpan.FromSeconds(15));
                previous = points[^1];
                result.Add(new(points[^1].At, boundary ? null : epochs[0],
                    Mean(points, point => point.CpuPercent), Mean(points, point => point.MemoryBytes),
                    Mean(points, point => point.DiskReadBytesPerSecond), Mean(points, point => point.DiskWriteBytesPerSecond),
                    Mean(points, point => point.NetworkReceiveBytesPerSecond), Mean(points, point => point.NetworkSendBytesPerSecond),
                    points.Count(Known), boundary));
            }
            output = result;
        }
        return new(1, query.Source, query.TaskName, query.TaskIdentity, query.From, query.To, cadence, bucketSeconds,
            output, known.FirstOrDefault()?.At, known.LastOrDefault()?.At, truncated, warning, query.RemoteHostId);
    }

    private static bool PartialGap(ResourceHistoryPoint[] points, Func<ResourceHistoryPoint, double?> selector) =>
        points.Any(point => selector(point) is null) && points.Any(point => selector(point) is not null);

    private static double? Mean(IEnumerable<ResourceHistoryPoint> points, Func<ResourceHistoryPoint, double?> selector)
    {
        double mean = 0; var count = 0;
        foreach (var point in points)
        { if (selector(point) is { } value) { mean += (value - mean) / ++count; } }
        return count > 0 && double.IsFinite(mean) ? mean : null;
    }
}
