using SystemAutorun.App.Controls;
using SystemAutorun.Core.Models;

namespace SystemAutorun.App.Services;

internal sealed record ResourcePlotSeries(IReadOnlyList<PlotPoint> Cpu, IReadOnlyList<PlotPoint> Memory,
    IReadOnlyList<PlotPoint> DiskRead, IReadOnlyList<PlotPoint> DiskWrite,
    IReadOnlyList<PlotPoint> Receive, IReadOnlyList<PlotPoint> Send)
{
    internal static readonly ResourcePlotSeries Empty = new([], [], [], [], [], []);
    internal static ResourcePlotSeries From(ResourceHistoryResult result) => From(result.Source, result.From, result.To, result.Points,
        TimeSpan.FromSeconds(result.SourceIntervalSeconds * 1.5));
    internal static ResourcePlotSeries From(RecentResourceHistoryResult result) => From(result.Source, result.From, result.To, result.Points,
        TimeSpan.FromMilliseconds(result.SourceIntervalMilliseconds * 1.5));
    private static ResourcePlotSeries From(ResourceHistorySource source, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<ResourceHistoryPoint> points, TimeSpan maximumGap) => new(
        Project(source, from, to, points, point => point.CpuPercent, maximumGap), Project(source, from, to, points, point => point.MemoryBytes, maximumGap),
        Project(source, from, to, points, point => point.DiskReadBytesPerSecond, maximumGap), Project(source, from, to, points, point => point.DiskWriteBytesPerSecond, maximumGap),
        Project(source, from, to, points, point => point.NetworkReceiveBytesPerSecond, maximumGap), Project(source, from, to, points, point => point.NetworkSendBytesPerSecond, maximumGap));

    private static PlotPoint[] Project(ResourceHistorySource source, DateTimeOffset windowFrom, DateTimeOffset windowTo,
        IReadOnlyList<ResourceHistoryPoint> points, Func<ResourceHistoryPoint, double?> value, TimeSpan maximumGap)
    {
        var output = new PlotPoint[points.Count]; string? previous = null; DateTimeOffset? previousAt = null;
        for (var index = 0; index < output.Length; index++)
        {
            var point = points[index];
            var boundary = point.BreakBefore || point.ContinuityId is null || previous is null || previous != point.ContinuityId
                || source == ResourceHistorySource.RecentSeconds && previousAt is { } last && point.At - last > maximumGap;
            DateTimeOffset? from = null, to = null; var at = point.At;
            if (source == ResourceHistorySource.Durable)
            {
                var buckets = points.Count;
                var width = (windowTo - windowFrom).Ticks / (double)buckets;
                // Durable replies contain one entry per bucket, including unknown ones.
                // Use its ordinal: a truncated fractional-tick boundary must not slip into the preceding bucket.
                var bucket = index;
                from = windowFrom.AddTicks((long)(bucket * width));
                to = bucket == buckets - 1 ? windowTo : windowFrom.AddTicks((long)((bucket + 1) * width));
                at = from.Value + (to.Value - from.Value) / 2;
            }
            output[index] = new(at, value(point), from, to, boundary);
            previous = point.ContinuityId; previousAt = point.At;
        }
        return output;
    }
}
