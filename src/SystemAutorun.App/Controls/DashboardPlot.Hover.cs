using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SystemAutorun.App.Services;
using Brush = System.Windows.Media.Brush;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace SystemAutorun.App.Controls;

public sealed partial class DashboardPlot
{
    internal sealed record HoverSample(DateTimeOffset At, DateTimeOffset? From, DateTimeOffset? To, double? Primary, double? Secondary);
    internal sealed record HealthHoverSample(DateTimeOffset At, string State, DateTimeOffset? From, DateTimeOffset? To);
    private readonly DrawingGroup _hoverLayer = new();
    private readonly DrawingGroup _hoverLine = new(), _hoverLabels = new();
    private readonly ScaleTransform _hoverScale = new(1, 0);
    private readonly DataChangePulse.ClockLease _hoverClock = new();
    private long _hoverGeneration;
    private DispatcherOperation? _hoverPending;
    private Point? _hoverPosition;
    private double _lastHoverLineX = double.NaN;
    private string? _lastHoverText;
    private (string Text, double WidthConstraint, bool IsLight, string AccentPreference, double Dpi) _cachedHoverKey;
    private (FormattedText Text, Brush Brush, string? Icon)[]? _cachedHoverLines;
    private bool _hoverInitialized;
    private bool _hoverThemeHooked;
    internal DrawingGroup HoverLayer => _hoverLayer;
    internal DrawingGroup HoverLine => _hoverLine;
    internal DrawingGroup HoverLabels => _hoverLabels;
    internal double HoverLineLength => _hoverScale.ScaleY;
    internal bool HoverAnimationActive => _hoverClock.Active;
    internal HoverSample? CurrentHoverSample { get; private set; }
    internal string? CurrentHoverText { get; private set; }

    private void InitializeHover()
    {
        _hoverLine.Transform = _hoverScale;
        _hoverLayer.Children.Add(_hoverLine); _hoverLayer.Children.Add(_hoverLabels);
        _hoverInitialized = true;
        Loaded += (_, args) =>
        {
            if (!ReferenceEquals(args.OriginalSource, this) || _hoverThemeHooked) { return; }
            ThemeService.Changed += HoverThemeChanged; _hoverThemeHooked = true;
        };
        Unloaded += (_, args) =>
        {
            if (!ReferenceEquals(args.OriginalSource, this)) { return; }
            if (_hoverThemeHooked) { ThemeService.Changed -= HoverThemeChanged; _hoverThemeHooked = false; }
            ClearHover();
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { ClearHover(); } };
    }

    private void HoverThemeChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) { return; }
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => HoverThemeChanged(sender, args)));
            return;
        }
        InvalidatePlotCaches();
        _cachedHoverLines = null; _lastHoverText = null; _lastHoverLineX = double.NaN;
        InvalidateVisual();
        if (_hoverPosition is { } point && IsVisible && IsLoaded) { UpdateHover(point); }
    }

    private void HoverSourceChanged(DependencyProperty property)
    {
        if (!_hoverInitialized) { return; }
        if (property == DataChangePulse.ContextProperty || property == ScaleKeyProperty || property == ModeProperty || property == WindowDurationProperty || property == WindowHoursProperty
            || property == PlotLeftInsetProperty || property == PlotRightInsetProperty)
        { ClearHover(); return; }
        if (_hoverPosition is null || property != PointsProperty && property != SecondaryPointsProperty
            && property != IntervalsProperty && property != EndAtProperty && property != DataAtProperty
            && property != BytesProperty && property != RateProperty && property != CountProperty && property != ThresholdProperty) { return; }
        _hoverPending?.Abort();
        _hoverPending = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _hoverPending = null;
            if (_hoverPosition is { } point && IsVisible && IsLoaded) { UpdateHover(point); }
        }));
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        UpdateHover(args.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs args)
    {
        base.OnMouseLeave(args);
        ExitHover();
    }

    internal void ExitHover()
    {
        _hoverPending?.Abort(); _hoverPending = null; _hoverPosition = null;
        _lastHoverLineX = double.NaN; _lastHoverText = null;
        CurrentHoverSample = null; CurrentHoverText = null; _hoverLabels.Children.Clear();
        if (_hoverLine.Children.Count == 0) { ClearHover(); return; }
        AnimateHoverLine(0);
    }

    internal void ClearHover()
    {
        _hoverPending?.Abort(); _hoverPending = null; _hoverPosition = null;
        _lastHoverLineX = double.NaN; _lastHoverText = null;
        CurrentHoverSample = null; CurrentHoverText = null;
        ++_hoverGeneration; _hoverClock.Stop(); _hoverScale.ScaleY = 0;
        _hoverLine.Children.Clear(); _hoverLabels.Children.Clear();
    }

    private void HoverMotionChanged()
    {
        if (!_hoverInitialized) { return; }
        if (!IsLoaded || !IsVisible || Window.GetWindow(this)?.WindowState == WindowState.Minimized)
        { ClearHover(); return; }
        if (_pulseLifetime?.Animated != true)
        {
            if (_hoverPosition is null) { ClearHover(); }
            else { ++_hoverGeneration; _hoverClock.Stop(); _hoverScale.ScaleY = 1; }
        }
    }

    private void AnimateHoverLine(double target)
    {
        var from = Math.Clamp(_hoverScale.ScaleY, 0, 1);
        var generation = ++_hoverGeneration;
        _hoverClock.Stop(); _hoverScale.ScaleY = target;
        if (_pulseLifetime?.Animated != true || Math.Abs(target - from) < .001)
        { if (target == 0) { _hoverLine.Children.Clear(); } return; }
        var duration = TimeSpan.FromMilliseconds(135 * Math.Abs(target - from));
        _hoverClock.Start(_hoverScale, ScaleTransform.ScaleYProperty,
            new DoubleAnimation(from, target, new Duration(duration)) { FillBehavior = FillBehavior.Stop },
            () => { if (generation == _hoverGeneration && target == 0) { _hoverLine.Children.Clear(); } });
    }

    internal void UpdateHover(Point position)
    {
        if (ActualWidth < 2 || ActualHeight < 2 || !IsVisible) { ClearHover(); return; }
        var entering = _hoverPosition is null;
        _hoverPosition = position;
        var bounds = Mode == "Health" ? new Rect(0, 0, ActualWidth, Math.Max(12, ActualHeight - 30)) : TrendBounds();
        var ratio = Math.Clamp((position.X - bounds.Left) / bounds.Width, 0, 1);
        var at = EndAt - Duration + TimeSpan.FromTicks((long)(Duration.Ticks * ratio));
        // Bucket/health intervals are half-open; the rightmost pixel represents the instant just inside the window.
        if (ratio >= 1 && at > DateTimeOffset.MinValue) { at = at.AddTicks(-1); }
        var red = Brush("Semantic.Brush.Accent", "#E11D48");
        var main = Brush("Semantic.Brush.TextPrimary", "#E4E4E7");
        var secondary = Brush("Semantic.Brush.TextSecondary", "#A1A1AA");
        var hint = Brush("Semantic.Brush.TextHint", "#8B8B94");
        var borderStrong = Brush("Semantic.Brush.BorderStrong", "#303036");
        var windowBg = Brush("Semantic.Brush.WindowBackground", "#18181B");
        string header; string first; string? second = null; var lineX = bounds.Left + ratio * bounds.Width;
        if (Mode == "Health")
        {
            var count = Math.Max(1, (int)(bounds.Width / 9));
            var stride = bounds.Width / count;
            var cellWidth = Math.Max(1, stride - 3);
            var cellIndex = Math.Clamp((int)(position.X / stride), 0, count - 1);
            var cellFrac = Math.Clamp((position.X - cellIndex * stride) / cellWidth, 0, 1);
            var cellStart = EndAt - Duration + TimeSpan.FromTicks((long)Math.Round(Duration.Ticks * (cellIndex / (double)count)));
            var cellEnd = EndAt - Duration + TimeSpan.FromTicks((long)Math.Round(Duration.Ticks * ((cellIndex + 1) / (double)count)));
            at = cellStart + TimeSpan.FromTicks((long)((cellEnd - cellStart).Ticks * cellFrac));
            if (at >= cellEnd && at > cellStart) { at = cellEnd.AddTicks(-1); }
            if (at >= EndAt && at > DateTimeOffset.MinValue) { at = EndAt.AddTicks(-1); }
            lineX = Math.Clamp(cellIndex * stride + cellFrac * cellWidth, 0, bounds.Width);
            var state = ResolveHealthHover(Intervals, at, cellStart, cellEnd);
            CurrentHoverSample = null;
            header = HoverTime(state.At, state.From, state.To, false);
            first = state.State switch { "healthy" => "正常", "failed" => "故障", _ => "未知" };
        }
        else
        {
            EnsureTrendDataPrepared();
            var sample = CurrentHoverSample = ResolveHover(_visiblePoints, _visibleSecondary, at);
            if (sample is not null)
            {
                lineX = Math.Clamp(bounds.Left + Position(sample.At) / ActualWidth * bounds.Width, bounds.Left, bounds.Right);
                header = HoverTime(sample.At, sample.From, sample.To, sample.From is not null && sample.To is not null);
                first = HoverMetricLabel(sample.Primary, Bytes, Rate, Count);
                if (SecondaryPoints is not null) { second = HoverMetricLabel(sample.Secondary, Bytes, Rate, Count); }
            }
            else
            {
                header = HoverTime(at, null, null, false); first = "未采样";
                if (SecondaryPoints is not null) { second = "未采样"; }
            }
        }
        CurrentHoverText = second is null ? $"{header} · {first}" : $"{header} · {first} · {second}";
        if (!entering && Math.Abs(lineX - _lastHoverLineX) < 0.2
            && string.Equals(CurrentHoverText, _lastHoverText, StringComparison.Ordinal))
        {
            return;
        }
        _lastHoverLineX = lineX;
        _lastHoverText = CurrentHoverText;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var widthConstraint = Math.Max(1, Math.Round(ActualWidth - 24, 1));
        var cacheKey = (CurrentHoverText, widthConstraint, ThemeService.IsLight, ThemeService.AccentPreference, dpi);
        if (_cachedHoverLines is not { } lines || _cachedHoverKey != cacheKey)
        {
            lines = second is null
                ?
                [
                    (HoverText(header, hint), hint, null),
                    (HoverText(first, main), main, null),
                ]
                :
                [
                    (HoverText(header, hint), hint, null),
                    (HoverText(first, main), main, "Inbound"),
                    (HoverText(second, secondary), secondary, "Outbound"),
                ];
            _cachedHoverKey = cacheKey;
            _cachedHoverLines = lines;
        }
        var width = Math.Min(Math.Max(1, ActualWidth - 4), lines.Max(line => line.Text.Width + (line.Icon is null ? 0 : 15)) + 12);
        var height = lines.Sum(line => line.Text.Height) + 6;
        var left = lineX + width + 6 <= ActualWidth ? lineX + 4 : lineX - width - 4;
        left = Math.Clamp(left, 0, Math.Max(0, ActualWidth - width));
        var top = Math.Clamp(bounds.Top + 3, 0, Math.Max(0, ActualHeight - height));
        _hoverScale.CenterY = bounds.Top;
        var redLinePen = new Pen(red, 1); redLinePen.Freeze();
        var borderPen = new Pen(borderStrong, 1); borderPen.Freeze();
        using (var guide = _hoverLine.Open())
        {
            guide.DrawLine(redLinePen, new Point(lineX, bounds.Top), new Point(lineX, bounds.Bottom));
        }
        using var drawing = _hoverLabels.Open();
        drawing.DrawRectangle(windowBg, borderPen, new Rect(left, top, width, height));
        drawing.DrawRectangle(red, null, new Rect(left, top, 2, height));
        if (Mode != "Health" && CurrentHoverSample is { } hoverSample && !_plot.IsEmpty)
        {
            var span = Math.Max(1e-9, _renderScale.Maximum - _renderScale.Minimum);
            if (Valid(hoverSample.Secondary))
            {
                var sy = Math.Clamp(_plot.Bottom - (hoverSample.Secondary!.Value - _renderScale.Minimum) / span * _plot.Height, _plot.Top, _plot.Bottom);
                var secPen = new Pen(secondary, 1); secPen.Freeze();
                drawing.DrawRectangle(windowBg, secPen, new Rect(lineX - 1.75, sy - 1.75, 3.5, 3.5));
            }
            if (Valid(hoverSample.Primary))
            {
                var py = Math.Clamp(_plot.Bottom - (hoverSample.Primary!.Value - _renderScale.Minimum) / span * _plot.Height, _plot.Top, _plot.Bottom);
                var priPen = new Pen(red, 1.1); priPen.Freeze();
                drawing.DrawRectangle(windowBg, priPen, new Rect(lineX - 2, py - 2, 4, 4));
            }
        }
        var y = top + 3;
        foreach (var line in lines)
        {
            var x = left + 6;
            if (line.Icon is not null)
            {
                DrawHoverIcon(drawing, line.Icon, line.Brush, new Point(x, y + Math.Max(0, (line.Text.Height - 12) / 2)));
                x += 15;
            }
            drawing.DrawText(line.Text, new Point(x, y)); y += line.Text.Height;
        }
        if (entering) { AnimateHoverLine(1); }
    }

    private FormattedText HoverText(string text, Brush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            MonoTypeface, 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, ActualWidth - 24), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        return formatted;
    }

    private static void DrawHoverIcon(DrawingContext drawing, string kind, Brush brush, Point at)
    {
        drawing.PushTransform(new TranslateTransform(at.X, at.Y)); drawing.PushTransform(new ScaleTransform(.5, .5));
        drawing.DrawGeometry(null, new Pen(brush, 1.5), LineIcon.Glyph(kind));
        drawing.Pop(); drawing.Pop();
    }

    private static string HoverTime(DateTimeOffset at, DateTimeOffset? from, DateTimeOffset? to, bool average)
    {
        var start = from?.ToLocalTime(); var end = to?.ToLocalTime();
        if (start is null || end is null) { return at.ToLocalTime().ToString("MM-dd HH:mm:ss.fff", CultureInfo.CurrentCulture); }
        var range = start.Value.Date == end.Value.Date
            ? $"{start:MM-dd HH:mm:ss} — {end:HH:mm:ss}"
            : $"{start:MM-dd HH:mm:ss} — {end:MM-dd HH:mm:ss}";
        return average ? $"均值 · {range}" : range;
    }

    // Select a single temporal identity for both series. Never pair values from different samples
    // or claim that a missing interval contains an interpolated observation.
    internal static HoverSample? ResolveHover(IReadOnlyList<PlotPoint> primary, IReadOnlyList<PlotPoint> secondary, DateTimeOffset at)
    {
        var history = HasBuckets(primary) || HasBuckets(secondary);
        var primaryGap = !history && InGap(primary, at);
        var secondaryGap = !history && InGap(secondary, at);
        PlotPoint? anchor = null;
        var nearest = double.PositiveInfinity;
        if (!primaryGap) { Consider(primary); }
        if (!secondaryGap) { Consider(secondary); }
        var inConnectedSpan = !history && ((!primaryGap && InConnectedSpan(primary, at)) || (!secondaryGap && InConnectedSpan(secondary, at)));
        var maxHoverDistance = inConnectedSpan ? MaximumRenderGap.TotalSeconds : ResourceTrendBuffer.MaximumGap.TotalSeconds;
        if (anchor is null || !history && nearest > maxHoverDistance) { return null; }
        var selected = anchor;
        var first = primaryGap ? null : Match(primary); var second = secondaryGap ? null : Match(secondary);

        static bool HasBuckets(IReadOnlyList<PlotPoint> points)
        {
            for (var index = 0; index < points.Count; index++)
            { if (points[index] is { From: not null, To: not null }) { return true; } }
            return false;
        }
        static bool InGap(IReadOnlyList<PlotPoint> points, DateTimeOffset target)
        {
            for (var index = 1; index < points.Count; index++)
            {
                var previous = points[index - 1]; var next = points[index];
                if (previous.At < target && target < next.At)
                { return next.BreakBefore || !Valid(previous.Value) || !Valid(next.Value) || next.At - previous.At > MaximumRenderGap; }
            }
            return false;
        }
        static bool InConnectedSpan(IReadOnlyList<PlotPoint> points, DateTimeOffset target)
        {
            for (var index = 1; index < points.Count; index++)
            {
                var previous = points[index - 1]; var next = points[index];
                if (previous.At <= target && target <= next.At)
                { return !next.BreakBefore && Valid(previous.Value) && Valid(next.Value) && next.At - previous.At <= MaximumRenderGap; }
            }
            return false;
        }
        void Consider(IReadOnlyList<PlotPoint> points)
        {
            for (var index = 0; index < points.Count; index++)
            {
                var point = points[index];
                if (history && (point.From is not { } from || point.To is not { } to || from >= to || at < from || at >= to)) { continue; }
                if (!history && (point.From is not null || point.To is not null)) { continue; }
                var distance = Math.Abs((point.At - at).TotalSeconds);
                if (distance < nearest) { anchor = point; nearest = distance; }
            }
        }
        PlotPoint? Match(IReadOnlyList<PlotPoint> points)
        {
            for (var index = 0; index < points.Count; index++)
            {
                var point = points[index];
                if (history ? point.From == selected.From && point.To == selected.To
                    : point.From is null && point.To is null && point.At == selected.At) { return point; }
            }
            return null;
        }
        return new(selected.At, selected.From, selected.To, Valid(first?.Value) ? first!.Value : null, Valid(second?.Value) ? second!.Value : null);
    }

    internal static HealthHoverSample ResolveHealthHover(IReadOnlyList<HealthInterval>? intervals, DateTimeOffset at,
        DateTimeOffset? cellFrom = null, DateTimeOffset? cellTo = null)
    {
        HealthInterval? known = null; HealthInterval? unknown = null;
        if (intervals is not null)
        {
            foreach (var interval in intervals)
            {
                if (interval.From > at || at >= interval.To) { continue; }
                // DrawHealth paints known intervals over the unknown hatch, in input order.
                if (interval.State is "healthy" or "failed") { known = interval; }
                else { unknown = interval; }
            }
            if (known?.State != "failed" && cellFrom is { } cf && cellTo is { } ct && cf < ct)
            {
                var cellSeconds = (ct - cf).TotalSeconds;
                var raw = new List<HealthCellSpan>();
                HealthInterval? lastFailed = null;
                foreach (var interval in intervals)
                {
                    if (interval.To <= cf || interval.From >= ct) { continue; }
                    var localLeft = Math.Max(0, (interval.From - cf).TotalSeconds / cellSeconds);
                    var localRight = Math.Min(1, (interval.To - cf).TotalSeconds / cellSeconds);
                    if (localRight > localLeft)
                    {
                        raw.Add(new HealthCellSpan(localLeft, localRight, interval.State));
                        if (interval.State == "failed") { lastFailed = interval; }
                    }
                }
                if (raw.Count > 0 && lastFailed is not null)
                {
                    var resolved = ResolveHealthCellSpans(raw);
                    for (var i = 0; i < resolved.Count; i++)
                    {
                        if (resolved[i].State == "failed" && resolved[i].Right - resolved[i].Left > 1e-9)
                        {
                            return new(at, "failed", lastFailed.From, lastFailed.To);
                        }
                    }
                }
            }
        }
        var match = known ?? unknown;
        return new(at, known?.State ?? "unknown", match?.From, match?.To);
    }

    internal static string HoverMetricLabel(double? value, bool bytes, bool rate, bool count = false)
    {
        if (!Valid(value)) { return "未采样"; }
        var number = value!.Value;
        if (count) { return Math.Round(number).ToString("N0", CultureInfo.CurrentCulture); }
        if (!bytes && !rate) { return $"{number.ToString("0.0", CultureInfo.CurrentCulture)}%"; }
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        var unit = 0;
        while (number >= 1024 && unit < units.Length - 1) { number /= 1024; unit++; }
        var formatted = number.ToString(number >= 1e6 ? "0.0E+0" : "0.0", CultureInfo.CurrentCulture);
        return $"{formatted} {units[unit]}{(rate ? "/s" : "")}";
    }
}
