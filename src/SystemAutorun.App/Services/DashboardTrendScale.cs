namespace SystemAutorun.App.Services;

internal readonly record struct TrendScale(double Minimum, double Maximum)
{
    internal static TrendScale Fit(IEnumerable<double> source, bool bytes)
    {
        var values = source.Where(value => double.IsFinite(value) && value >= 0).ToArray();
        if (values.Length == 0) { return new(0, bytes ? 16 * 1024 * 1024 : .1); }
        var low = values.Min(); var high = values.Max();
        if (!bytes) { return new(0, Math.Min(100, Nice(Math.Max(.1, high * 1.15)))); }
        // A non-zero memory axis is explicit in the labels. Never magnify tiny allocator noise.
        var span = Nice(Math.Max(16 * 1024 * 1024, Math.Max(high * .1, (high - low) * 1.3)));
        var step = span / 4;
        var minimum = Math.Max(0, Math.Floor((low - span * .15) / step) * step);
        var maximum = Math.Ceiling(Math.Max(minimum + span, high + span * .15) / step) * step;
        return new(minimum, maximum);
    }

    internal static TrendScale FitRate(IEnumerable<double> source)
    {
        var high = source.Where(value => double.IsFinite(value) && value >= 0).DefaultIfEmpty(0).Max();
        return new(0, Nice(Math.Max(1, high <= double.MaxValue / 1.15 ? high * 1.15 : high)));
    }

    internal static double Nice(double value)
    {
        var unit = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var leading = value / unit;
        var rounded = (leading <= 1 ? 1 : leading <= 2 ? 2 : leading <= 5 ? 5 : 10) * unit;
        return double.IsFinite(rounded) ? rounded : double.MaxValue;
    }
}

internal sealed class StableTrendScale
{
    private TrendScale? _current;
    private DateTimeOffset _heldAt;

    internal TrendScale Update(IEnumerable<double> values, bool bytes, DateTimeOffset at, bool rate = false)
    {
        var desired = rate ? TrendScale.FitRate(values) : TrendScale.Fit(values, bytes);
        if (_current is not { } current || at < _heldAt)
        { _heldAt = at; return (_current = desired).Value; }
        if (desired.Minimum < current.Minimum || desired.Maximum > current.Maximum)
        {
            _heldAt = at;
            return (_current = new TrendScale(Math.Min(desired.Minimum, current.Minimum), Math.Max(desired.Maximum, current.Maximum))).Value;
        }
        if (at - _heldAt >= TimeSpan.FromSeconds(30)
            && desired.Maximum - desired.Minimum <= (current.Maximum - current.Minimum) * .65)
        { _heldAt = at; _current = desired; }
        return _current.Value;
    }

    internal void Reset() => _current = null;
}
