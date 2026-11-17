using SystemAutorun.App.Controls;
using SystemAutorun.Core.Models;

namespace SystemAutorun.App.Services;

/// <summary>Only received observations are retained. No timer-generated samples or persisted history.</summary>
internal sealed class ResourceTrendBuffer
{
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaximumGap = TimeSpan.FromSeconds(3);
    internal const int MaximumSeries = 8;
    internal const int MaximumSamples = 301;
    private readonly Dictionary<string, Series> _series = new(StringComparer.Ordinal);
    private long _touch;

    internal sealed class Series
    {
        internal List<(DateTimeOffset At, double? Cpu, double? Bytes, bool Break)> Samples { get; } = [];
        internal IReadOnlyList<PlotPoint> Cpu { get; private set; } = [];
        internal IReadOnlyList<PlotPoint> Memory { get; private set; } = [];
        internal string? ProcessIdentity { get; set; }
        internal bool PendingBreak { get; set; }
        internal long Touched { get; set; }

        internal void Publish()
        {
            Cpu = Samples.Select(sample => new PlotPoint(sample.At, sample.Cpu, BreakBefore: sample.Break)).ToArray();
            Memory = Samples.Select(sample => new PlotPoint(sample.At, sample.Bytes, BreakBefore: sample.Break)).ToArray();
        }
    }

    internal Series? Observe(string key, ManagedProcessSnapshot? snapshot, DateTimeOffset now, bool interrupted)
    {
        if (!_series.TryGetValue(key, out var series))
        {
            if (snapshot is null) { return null; }
            if (_series.Count >= MaximumSeries)
            { _series.Remove(_series.MinBy(pair => pair.Value.Touched).Key); }
            _series[key] = series = new Series();
        }
        series.Touched = ++_touch;
        var changed = series.Samples.RemoveAll(sample => sample.At < now - Window) > 0;
        series.PendingBreak |= interrupted;
        if (snapshot is null || snapshot.ProcessCount == 0 || snapshot.CapturedAt > now
            || snapshot.CapturedAt < now - MaximumGap)
        { series.PendingBreak = true; }
        else if (series.Samples.Count == 0 || snapshot.CapturedAt > series.Samples[^1].At)
        {
            var knownIdentity = snapshot.Processes.All(process => process.ProcessId > 0
                && process.StartedAt is { } started && started <= snapshot.CapturedAt);
            var identity = knownIdentity ? string.Join(";", snapshot.Processes.OrderBy(process => process.ProcessId)
                .Select(process => $"{process.ProcessId}:{process.StartedAt!.Value.UtcTicks}")) : null;
            var restart = identity is null || series.ProcessIdentity is null || series.ProcessIdentity != identity;
            var cpu = snapshot.CpuUsageAvailable && snapshot.Processes.All(process => double.IsFinite(process.CpuUsagePercent)
                && process.CpuUsagePercent is >= 0 and <= 100) && snapshot.CpuUsagePercent <= 100
                ? snapshot.CpuUsagePercent : (double?)null;
            var bytes = snapshot.Processes.All(process => process.WorkingSetBytes >= 0)
                ? snapshot.Processes.Sum(process => (double)process.WorkingSetBytes) : (double?)null;
            series.Samples.Add((snapshot.CapturedAt, cpu, bytes, series.PendingBreak || restart));
            series.ProcessIdentity = identity;
            series.PendingBreak = false;
            if (series.Samples.Count > MaximumSamples) { series.Samples.RemoveAt(0); }
            changed = true;
        }
        if (changed) { series.Publish(); }
        return series;
    }

    internal int SeriesCount => _series.Count;
    internal void Clear() => _series.Clear();
}
