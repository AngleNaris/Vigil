using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Monitoring;

public static class MonitoringHistoryProjection
{
    public static readonly TimeSpan SamplingInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(30);
    public const int MaximumInputFrames = 500_000;
    public const int MaximumIntervals = 8192;

    public static MonitoringHistoryResult Project(IEnumerable<MonitoringHistoryFrame> frames,
        MonitoringHistoryQuery query, DateTimeOffset now, string activeInstanceId,
        bool truncated = false, string? warning = null)
    {
        ValidateQuery(query, now);
        var bounded = frames.Take(MaximumInputFrames + 1).ToArray();
        if (bounded.Length > MaximumInputFrames) { truncated = true; }
        var samples = bounded.Take(MaximumInputFrames)
            .Where(frame => frame.SchemaVersion == 1 && frame.ObservedAt >= query.From - SamplingInterval && frame.ObservedAt <= query.To)
            .Select(frame => new Point(frame.ObservedAt, frame.AgentInstanceId,
                (frame.Tasks ?? []).LastOrDefault(task => task is not null
                    && string.Equals(task.TaskName, query.TaskName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(task.TaskIdentity, query.TaskIdentity, StringComparison.Ordinal))
                ?? new TaskHistorySample(query.TaskName, query.TaskIdentity, ObservationHealth.Unknown)))
            .OrderBy(point => point.At).GroupBy(point => point.At).Select(group => group.Last()).ToArray();
        var spans = new List<Span>();
        for (var i = 0; i < samples.Length; i++)
        {
            var point = samples[i];
            var end = point.At;
            // Hold only up to one sampling interval. Never bridge a restart/offline epoch.
            if (i + 1 < samples.Length && samples[i + 1].InstanceId == point.InstanceId)
            {
                end = Min(samples[i + 1].At, point.At + SamplingInterval);
            }
            else if (i == samples.Length - 1 && point.InstanceId == activeInstanceId)
            {
                end = Min(query.To, point.At + SamplingInterval);
            }
            var start = Max(query.From, point.At);
            end = Min(query.To, end);
            if (point.Sample.ValidUntil is { } validUntil) { end = Min(end, validUntil); }
            if (end > start) { spans.Add(new Span(start, end, point.Sample)); }
        }

        var intervals = new List<HealthHistoryInterval>();
        foreach (var span in spans.Where(span => span.Sample.Health is ObservationHealth.Healthy or ObservationHealth.Fault))
        {
            if (intervals.Count > 0 && intervals[^1] is { } last && last.To == span.From && last.Health == span.Sample.Health)
            {
                intervals[^1] = last with { To = span.To };
            }
            else { intervals.Add(new HealthHistoryInterval(span.From, span.To, span.Sample.Health)); }
        }
        if (intervals.Count > MaximumIntervals)
        {
            intervals = intervals.TakeLast(MaximumIntervals).ToList();
            truncated = true;
            warning = "Timeline interval limit reached; older unreturned intervals must be rendered unknown.";
        }
        var total = Aggregate(spans, 0, spans.Count, query.From, query.To);
        var buckets = new List<MonitoringHistoryBucket>();
        var bucketTicks = Math.Max(1L, ((query.To - query.From).Ticks + query.BucketCount - 1) / query.BucketCount);
        var cursor = 0;
        for (var start = query.From; start < query.To;)
        {
            var end = Min(query.To, start + TimeSpan.FromTicks(Math.Min(bucketTicks, (query.To - start).Ticks)));
            while (cursor < spans.Count && spans[cursor].To <= start) { cursor++; }
            var endExclusive = cursor;
            while (endExclusive < spans.Count && spans[endExclusive].From < end) { endExclusive++; }
            // A span can cross several buckets; only the expiry loop advances cursor.
            buckets.Add(Aggregate(spans, cursor, endExclusive, start, end));
            start = end;
        }
        var retainedFrom = spans.FirstOrDefault(span => span.Sample.Health is ObservationHealth.Healthy or ObservationHealth.Fault
            || span.Sample.CpuPercent is { } cpu && double.IsFinite(cpu) && cpu is >= 0 and <= 100
            || span.Sample.WorkingSetBytes is >= 0)?.From;
        return new MonitoringHistoryResult(1, query.TaskName, query.TaskIdentity, query.From, query.To,
            samples.Length > 0 ? samples[^1].At : null, retainedFrom,
            total.HealthySeconds, total.FaultSeconds, total.UnknownSeconds, total.AvailabilityPercent,
            total.CoveragePercent, intervals, buckets, truncated, warning);
    }

    internal static void ValidateQuery(MonitoringHistoryQuery query, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query.TaskName);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.TaskIdentity);
        if (query.To <= query.From || query.To - query.From > MaximumWindow || query.To > now)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "History must end by now and span at most 30 days.");
        }
        if (query.BucketCount is < 1 or > 1000) { throw new ArgumentOutOfRangeException(nameof(query.BucketCount)); }
    }

    private static MonitoringHistoryBucket Aggregate(IReadOnlyList<Span> spans, int first, int endExclusive, DateTimeOffset from, DateTimeOffset to)
    {
        double healthy = 0, fault = 0, cpuWeighted = 0, cpuSeconds = 0, memoryWeighted = 0, memorySeconds = 0;
        double workingWeighted = 0, workingSeconds = 0;
        for (var index = first; index < endExclusive; index++)
        {
            var span = spans[index];
            var seconds = (Min(to, span.To) - Max(from, span.From)).TotalSeconds;
            if (seconds <= 0) { continue; }
            if (span.Sample.Health == ObservationHealth.Healthy) { healthy += seconds; }
            else if (span.Sample.Health == ObservationHealth.Fault) { fault += seconds; }
            if (span.Sample.CpuPercent is { } cpu && double.IsFinite(cpu) && cpu is >= 0 and <= 100)
            {
                cpuWeighted += cpu * seconds;
                cpuSeconds += seconds;
            }
            if (span.Sample.WorkingSetBytes is { } working && working >= 0)
            {
                workingWeighted += working * seconds; workingSeconds += seconds;
                if (span.Sample.PhysicalMemoryBytes is { } physical && physical > 0)
                {
                    memoryWeighted += working * 100d / physical * seconds;
                    memorySeconds += seconds;
                }
            }
        }
        var duration = (to - from).TotalSeconds;
        var known = healthy + fault;
        return new MonitoringHistoryBucket(from, to, healthy, fault, Math.Max(0, duration - known),
            known > 0 ? Math.Clamp(healthy * 100d / known, 0, 100) : null, Math.Clamp(known * 100d / duration, 0, 100),
            cpuSeconds > 0 ? cpuWeighted / cpuSeconds : null,
            memorySeconds > 0 ? memoryWeighted / memorySeconds : null,
            workingSeconds > 0 ? workingWeighted / workingSeconds : null);
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) => first < second ? first : second;
    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;
    private sealed record Point(DateTimeOffset At, string InstanceId, TaskHistorySample Sample);
    private sealed record Span(DateTimeOffset From, DateTimeOffset To, TaskHistorySample Sample);
}
