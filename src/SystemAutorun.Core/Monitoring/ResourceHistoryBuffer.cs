using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Monitoring;

// Agent-owned, memory-only; a restart deliberately starts an empty recent-resource history.
public sealed class ResourceHistoryBuffer
{
    private readonly object _gate = new();
    private readonly Queue<(ResourceHistoryFrame Frame, long Bytes)> _frames = new();
    private readonly int _maximumFrames, _maximumRows;
    private readonly long _maximumBytes;
    private long _bytes, _epoch;
    private int _rows;
    private DateTimeOffset? _lastAt, _prunedThrough, _resetAt;

    public ResourceHistoryBuffer(int maximumFrames = 601, long maximumBytes = 32L * 1024 * 1024, int maximumRows = 100_000)
    {
        if (maximumFrames is < 1 or > 601 || maximumBytes is < 512 or > 32L * 1024 * 1024 || maximumRows is < 1 or > 100_000)
        { throw new ArgumentOutOfRangeException(nameof(maximumFrames)); }
        _maximumFrames = maximumFrames; _maximumBytes = maximumBytes; _maximumRows = maximumRows;
    }

    public void Append(ResourceHistoryFrame frame)
    {
        lock (_gate)
        {
            if (_lastAt is { } previous && frame.At <= previous)
            {
                _frames.Clear(); _bytes = 0; _rows = 0; _prunedThrough = null; _resetAt = frame.At; _epoch++;
            }
            _lastAt = frame.At;
            frame = frame with { Epoch = $"{frame.Epoch}:{_epoch}" };
            var bytes = 256L + frame.Epoch.Length * 2L + frame.Tasks.Sum(row => 128L
                + row.TaskName.Length * 2L + row.TaskIdentity.Length * 2L + (row.ProcessIdentity?.Length ?? 0) * 2L);
            if (bytes > _maximumBytes || frame.Tasks.Count > _maximumRows)
            {
                // All-or-unknown, never silently take the first N enabled tasks.
                _frames.Clear(); _bytes = 0; _rows = 0; _prunedThrough = frame.At;
                frame = frame with { Tasks = [], Host = null, HostCapturedAt = null }; bytes = 256;
            }
            _frames.Enqueue((frame, bytes)); _bytes += bytes; _rows += frame.Tasks.Count;
            while (_frames.Count > 0)
            {
                var first = _frames.Peek();
                var pressure = _frames.Count > _maximumFrames || _bytes > _maximumBytes || _rows > _maximumRows;
                if (!pressure && first.Frame.At >= frame.At - ResourceHistoryProjection.RecentWindow) { break; }
                _frames.Dequeue(); _bytes -= first.Bytes; _rows -= first.Frame.Tasks.Count;
                if (pressure && first.Frame.At >= frame.At - ResourceHistoryProjection.RecentWindow) { _prunedThrough = first.Frame.At; }
            }
        }
    }

    public ResourceHistoryResult Query(ResourceHistoryQuery query, DateTimeOffset now)
    {
        var (frames, truncated, warning) = Snapshot(query, now);
        return ResourceHistoryProjection.ProjectRecent(frames, query, now, truncated, warning);
    }

    public ResourceHistoryResult QueryCompatible(ResourceHistoryQuery query, DateTimeOffset now)
    {
        var (frames, truncated, warning) = Snapshot(query, now);
        return ResourceHistoryProjection.ProjectRecentCompatible(frames, query, now, truncated, warning);
    }

    public RecentResourceHistoryResult QueryPrecise(ResourceHistoryQuery query, DateTimeOffset now, int intervalMilliseconds, string agentInstanceId)
    {
        var (frames, truncated, warning) = Snapshot(query, now);
        return ResourceHistoryProjection.ProjectRecentPrecise(frames, query, now, intervalMilliseconds, agentInstanceId, truncated, warning);
    }

    private (ResourceHistoryFrame[] Frames, bool Truncated, string? Warning) Snapshot(ResourceHistoryQuery query, DateTimeOffset now)
    {
        ResourceHistoryProjection.ValidateQuery(query, now);
        lock (_gate)
        {
            var frames = _frames.Select(item => item.Frame).ToArray();
            var truncated = _prunedThrough is { } pruned && query.From <= pruned || _resetAt is { } reset && query.From <= reset;
            var warning = _resetAt is { } clockAt && query.From <= clockAt ? "Resource clock changed; the recent ring was restarted." :
                truncated ? "Resource memory capacity shortened retention or rejected an oversized capture; omitted time is unknown." : null;
            return (frames, truncated, warning);
        }
    }
}
