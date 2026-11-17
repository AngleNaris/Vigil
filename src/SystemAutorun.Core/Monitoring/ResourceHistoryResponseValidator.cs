using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Monitoring;

public static class ResourceHistoryResponseValidator
{
    public static void Validate(ResourceHistoryResult? result, ResourceHistoryQuery query)
    {
        ResourceHistoryProjection.ValidateQuery(query, query.To);
        var cadence = query.Source == ResourceHistorySource.RecentSeconds ? 1 : 10;
        var count = query.Source == ResourceHistorySource.RecentSeconds ? query.BucketCount
            : Math.Min(query.BucketCount, Math.Max(1, (int)Math.Ceiling((query.To - query.From).TotalSeconds / cadence)));
        var width = query.Source == ResourceHistorySource.RecentSeconds ? 1 : (query.To - query.From).TotalSeconds / count;
        if (result is null || result.SchemaVersion != 1 || result.Source != query.Source
            || result.From != query.From || result.To != query.To
            || result.TaskName != query.TaskName || result.TaskIdentity != query.TaskIdentity || result.RemoteHostId != query.RemoteHostId
            || result.SourceIntervalSeconds != cadence || !double.IsFinite(result.BucketSeconds)
            || Math.Abs(result.BucketSeconds - width) > 0.000001
            || result.Points is null || result.Points.Count > count || result.Points.Count > ResourceHistoryProjection.MaximumPoints
            || query.Source == ResourceHistorySource.Durable && result.Points.Count != count
            || result.Warning?.Length > 1024 || result.RetainedFrom > query.To || result.LatestRecordAt > query.To
            || (result.RetainedFrom is null) != (result.LatestRecordAt is null) || result.RetainedFrom > result.LatestRecordAt)
        { throw new InvalidDataException("Resource history response does not match its requested target, bounds or source."); }
        var widthTicks = (query.To - query.From).Ticks / (double)count;
        DateTimeOffset? previous = null; var ordinal = 0;
        foreach (var point in result.Points)
        {
            var bucketFrom = query.From.AddTicks((long)(ordinal * widthTicks));
            var bucketTo = ordinal == count - 1 ? query.To : query.From.AddTicks((long)((ordinal + 1) * widthTicks));
            if (point is null || point.At < query.From || point.At > query.To || previous >= point.At
                || query.Source == ResourceHistorySource.Durable && (point.At < bucketFrom || point.At > bucketTo)
                || point.ContinuityId?.Length > 256 || point.SampleCount is < 0 or > MonitoringHistoryProjection.MaximumInputFrames
                || point.CpuPercent is { } cpu && (!double.IsFinite(cpu) || cpu is < 0 or > 100)
                || Invalid(point.MemoryBytes) || Invalid(point.DiskReadBytesPerSecond) || Invalid(point.DiskWriteBytesPerSecond)
                || Invalid(point.NetworkReceiveBytesPerSecond) || Invalid(point.NetworkSendBytesPerSecond))
            { throw new InvalidDataException("Resource history response contains an invalid or out-of-order sample."); }
            previous = point.At; ordinal++;
        }
    }

    public static void Validate(RecentResourceHistoryResult? result, ResourceHistoryQuery query)
    {
        if (result is null || result.SchemaVersion != 2 || query.Source != ResourceHistorySource.RecentSeconds
            || !Guid.TryParseExact(result.AgentInstanceId, "N", out _)
            || result.Source != ResourceHistorySource.RecentSeconds || result.SourceIntervalMilliseconds is not (500 or 1000)
            || !double.IsFinite(result.BucketSeconds) || Math.Abs(result.BucketSeconds - result.SourceIntervalMilliseconds / 1000d) > .000001)
        { throw new InvalidDataException("Recent resource response has an invalid schema or sampling interval."); }
        // Reuse target, bounds, order, finite-value and size validation; this temporary shape is not sent on the wire.
        Validate(new ResourceHistoryResult(1, result.Source, result.TaskName, result.TaskIdentity, result.From, result.To,
            1, 1, result.Points, result.RetainedFrom, result.LatestRecordAt, result.Truncated, result.Warning, result.RemoteHostId), query);
        DateTimeOffset? previousAt = null;
        foreach (var point in result.Points)
        {
            if (previousAt is { } prior && point.At - prior > TimeSpan.FromMilliseconds(result.SourceIntervalMilliseconds * 1.5)
                && !point.BreakBefore)
            { throw new InvalidDataException("Recent resource response joins a missing capture interval."); }
            previousAt = point.At;
            var known = point.CpuPercent is not null || point.MemoryBytes is not null || point.DiskReadBytesPerSecond is not null
                || point.DiskWriteBytesPerSecond is not null || point.NetworkReceiveBytesPerSecond is not null || point.NetworkSendBytesPerSecond is not null;
            if (point.SampleCount != (known ? 1 : 0)
                || known && (result.RetainedFrom is null || result.LatestRecordAt is null || point.At < result.RetainedFrom.Value || point.At > result.LatestRecordAt.Value))
            { throw new InvalidDataException("Recent resource response misrepresents capture count or actual retained timestamps."); }
        }
    }

    private static bool Invalid(double? value) => value is { } number && (!double.IsFinite(number) || number < 0);
}
