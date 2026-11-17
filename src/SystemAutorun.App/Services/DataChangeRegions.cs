using SystemAutorun.App.Controls;

namespace SystemAutorun.App.Services;

/// <summary>Data-space differences only. Screen position, axis scale and clock labels are not observations.</summary>
internal static class DataChangeRegions
{
    internal static bool[] Trend(IReadOnlyList<PlotPoint> before, IReadOnlyList<PlotPoint> current,
        DateTimeOffset? previousRecord, DateTimeOffset? latestRecord)
    {
        var changed = new bool[current.Count];
        var last = before.LastOrDefault(point => Known(point.Value));
        if (last is null) { return changed; } // First valid load is a baseline, not an update.
        var old = new Dictionary<(DateTimeOffset, DateTimeOffset?, DateTimeOffset?), PlotPoint>();
        foreach (var point in before) { old[(point.At, point.From, point.To)] = point; }
        for (var index = 0; index < current.Count; index++)
        {
            var point = current[index];
            if (!Known(point.Value)) { continue; }
            if (old.TryGetValue((point.At, point.From, point.To), out var previous))
            { changed[index] = point.Value != previous.Value; continue; }
            if (point.From is { } from && point.To is { } to)
            {
                // Rolling bucket bounds are not stable identities. Only the bucket receiving a real
                // new capture may flash; panning/rebucketing old records must not recolor the graph.
                changed[index] = latestRecord is { } latest && previousRecord is { } prior && latest > prior
                    && latest >= from && latest < to && point.Value != last.Value;
            }
            else if (point.At > last.At)
            { changed[index] = point.Value != last.Value; }
            else if (before.Count > 0 && point.At >= before[0].At)
            {
                var neighbor = before.LastOrDefault(row => row.At <= point.At && Known(row.Value));
                changed[index] = neighbor is not null && neighbor.Value != point.Value;
            }
        }
        return changed;
    }

    internal static IReadOnlyList<HealthInterval> Health(IReadOnlyList<HealthInterval> before, IReadOnlyList<HealthInterval> current,
        DateTimeOffset previousEnd, DateTimeOffset from, DateTimeOffset end)
    {
        if (before.Count == 0 || from >= end) { return []; }
        var events = new List<Edge> { new(from, -1, false, false), new(end, -1, false, false) };
        Add(before, false, previousEnd); Add(current, true, end);
        if (previousEnd > from && previousEnd < end) { events.Add(new(previousEnd, -1, false, false)); }
        events.Sort((left, right) => left.At.CompareTo(right.At));
        var oldActive = new SortedSet<int>(); var newActive = new SortedSet<int>();
        var result = new List<HealthInterval>();
        var oldTail = "unknown";
        foreach (var row in before)
        { if (row.From < previousEnd && row.To >= previousEnd && row.State is "healthy" or "failed") oldTail = row.State; }
        var offset = 0;
        while (offset < events.Count)
        {
            var at = events[offset].At;
            while (offset < events.Count && events[offset].At == at)
            {
                var edge = events[offset++]; if (edge.Index < 0) { continue; }
                var active = edge.Current ? newActive : oldActive;
                if (edge.Start) { active.Add(edge.Index); } else { active.Remove(edge.Index); }
            }
            if (offset == events.Count) { break; }
            var to = events[offset].At;
            var previous = at >= previousEnd ? oldTail : oldActive.Count > 0 ? before[oldActive.Max].State : "unknown";
            var state = newActive.Count > 0 ? current[newActive.Max].State : "unknown";
            if (state == previous || to <= at) { continue; }
            if (result.Count > 0 && result[^1].To == at && result[^1].State == state)
            { result[^1] = result[^1] with { To = to }; }
            else { result.Add(new(at, to, state)); }
        }
        return result;

        void Add(IReadOnlyList<HealthInterval> rows, bool isCurrent, DateTimeOffset limit)
        {
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index]; if (row.State is not ("healthy" or "failed")) { continue; }
                var left = row.From > from ? row.From : from;
                var right = row.To < limit ? row.To : limit; if (right > end) { right = end; }
                if (right <= left) { continue; }
                events.Add(new(left, index, isCurrent, true)); events.Add(new(right, index, isCurrent, false));
            }
        }
    }
    private static bool Known(double? number) => number is >= 0 && double.IsFinite(number.Value);
    private readonly record struct Edge(DateTimeOffset At, int Index, bool Current, bool Start);
}
