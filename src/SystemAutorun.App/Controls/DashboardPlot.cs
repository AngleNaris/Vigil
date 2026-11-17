using System.Globalization;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace SystemAutorun.App.Controls;

public sealed record PlotPoint(DateTimeOffset At, double? Value, DateTimeOffset? From = null, DateTimeOffset? To = null, bool BreakBefore = false);
public sealed record HealthInterval(DateTimeOffset From, DateTimeOffset To, string State);

/// <summary>
/// Time-positioned rectangular/linear plots. Missing observations remain missing;
/// this control never invents a series to make an empty dashboard look populated.
/// </summary>
public sealed partial class DashboardPlot : FrameworkElement
{
    internal const double SweepDurationMilliseconds = 620;
    private static readonly FrameworkPropertyMetadata RenderMetadata =
        new(null, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(string), typeof(DashboardPlot),
        new FrameworkPropertyMetadata("Trend", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<PlotPoint>), typeof(DashboardPlot), RenderMetadata);
    public static readonly DependencyProperty IntervalsProperty = DependencyProperty.Register(
        nameof(Intervals), typeof(IReadOnlyList<HealthInterval>), typeof(DashboardPlot),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WindowHoursProperty = DependencyProperty.Register(
        nameof(WindowHours), typeof(int), typeof(DashboardPlot),
        new FrameworkPropertyMetadata(24, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EndAtProperty = DependencyProperty.Register(
        nameof(EndAt), typeof(DateTimeOffset), typeof(DashboardPlot),
        new FrameworkPropertyMetadata(DateTimeOffset.UnixEpoch, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WindowDurationProperty = DependencyProperty.Register(
        nameof(WindowDuration), typeof(TimeSpan), typeof(DashboardPlot), new FrameworkPropertyMetadata(TimeSpan.Zero, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BytesProperty = DependencyProperty.Register(
        nameof(Bytes), typeof(bool), typeof(DashboardPlot), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
            (target, _) => ((DashboardPlot)target)._scale.Reset()));
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(DashboardPlot), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PlotLeftInsetProperty = DependencyProperty.Register(
        nameof(PlotLeftInset), typeof(double), typeof(DashboardPlot), new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsRender), ValidInset);
    public static readonly DependencyProperty PlotRightInsetProperty = DependencyProperty.Register(
        nameof(PlotRightInset), typeof(double), typeof(DashboardPlot), new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsRender), ValidInset);
    public static readonly DependencyProperty ScaleKeyProperty = DependencyProperty.Register(
        nameof(ScaleKey), typeof(string), typeof(DashboardPlot), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender,
            (target, _) => ((DashboardPlot)target)._scale.Reset()));
    public static readonly DependencyProperty SecondaryPointsProperty = DependencyProperty.Register(
        nameof(SecondaryPoints), typeof(IReadOnlyList<PlotPoint>), typeof(DashboardPlot), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RateProperty = DependencyProperty.Register(
        nameof(Rate), typeof(bool), typeof(DashboardPlot), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
            (target, _) => ((DashboardPlot)target)._scale.Reset()));
    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(DashboardPlot), new FrameworkPropertyMetadata("此时段无有效采样", FrameworkPropertyMetadataOptions.AffectsRender));
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }
    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
        nameof(IsLoading), typeof(bool), typeof(DashboardPlot), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsLoading { get => (bool)GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }
    public static readonly DependencyProperty PrimaryLabelProperty = DependencyProperty.Register(
        nameof(PrimaryLabel), typeof(string), typeof(DashboardPlot), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SecondaryLabelProperty = DependencyProperty.Register(
        nameof(SecondaryLabel), typeof(string), typeof(DashboardPlot), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    private readonly Services.StableTrendScale _scale = new();
    private static readonly CultureInfo ZhCnCulture = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly Typeface MonoTypeface = new(new System.Windows.Media.FontFamily("Cascadia Code, Consolas, Microsoft YaHei, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly DashStyle GridDashStyle = CreateFrozenDashStyle();
    private readonly OverlayVisual _overlayVisual = new();
    private PlotPoint[] _visiblePoints = [], _visibleSecondary = [];
    private (Color Color, Pen Pen)? _framePenCache, _guidePenCache, _tickPenCache, _primaryPenCache, _secondaryPenCache, _hatchPenCache, _baselinePenCache;
    private (Color Color, LinearGradientBrush Brush)? _primaryAreaCache, _secondaryAreaCache;
    private HealthCellsCacheKey _cachedHealthCellsKey;
    private DrawingGroup? _cachedHealthCells;
    private HealthChromeCacheKey _cachedHealthChromeKey;
    private DrawingGroup? _cachedHealthChrome;
    private TrendDataCacheKey _cachedTrendDataKey;
    private bool _trendDataPrepared;
    private bool _cachedTrendHasValues;
    private Rect _cachedTrendDataBounds;
    private TrendChromeCacheKey _cachedTrendChromeKey;
    private DrawingGroup? _cachedTrendChrome;
    private TrendSeriesCacheKey _cachedTrendSeriesKey;
    private DrawingGroup? _cachedTrendSeries;
    private Point? _cachedPrimaryTailPoint;
    private EmptyLoadingCacheKey _cachedEmptyLoadingKey;
    private DrawingGroup? _cachedEmptyLoadingChrome;

    private readonly record struct HealthCellsCacheKey(
        double Width,
        double Height,
        DateTimeOffset EndAt,
        TimeSpan Duration,
        IReadOnlyList<HealthInterval>? IntervalsRef,
        int IntervalsCount,
        HealthInterval? FirstInterval,
        HealthInterval? LastInterval,
        Color UnknownColor,
        Color HatchColor,
        Color HealthyColor,
        Color FailedColor);

    private readonly record struct HealthChromeCacheKey(
        double Width,
        double Height,
        DateTimeOffset From,
        TimeSpan Duration,
        string CultureName,
        double PixelsPerDip,
        Color BaselineColor,
        Color HintColor);

    private readonly record struct TrendDataCacheKey(
        double Width,
        double Height,
        double LeftInset,
        double RightInset,
        DateTimeOffset EndAt,
        TimeSpan Duration,
        bool Bytes,
        bool Rate,
        string ScaleKey,
        IReadOnlyList<PlotPoint>? PointsRef,
        int PointsCount,
        PlotPoint? FirstPoint,
        PlotPoint? LastPoint,
        IReadOnlyList<PlotPoint>? SecondaryRef,
        int SecondaryCount,
        PlotPoint? FirstSecondary,
        PlotPoint? LastSecondary);

    private readonly record struct TrendChromeCacheKey(
        Rect PlotBounds,
        double Width,
        double Height,
        string Caption,
        double ScaleMin,
        double ScaleMax,
        bool Bytes,
        bool Rate,
        string CultureName,
        double PixelsPerDip,
        Color SubtleColor,
        Color StrongColor,
        Color HintColor);

    private readonly record struct TrendSeriesCacheKey(
        Color AccentColor,
        Color SecondaryColor);

    private readonly record struct EmptyLoadingCacheKey(
        Rect Bounds,
        double Width,
        double Height,
        string Caption,
        string CultureName,
        double PixelsPerDip,
        Color SubtleColor,
        Color StrongColor,
        Color HintColor);
    public IReadOnlyList<PlotPoint>? SecondaryPoints { get => (IReadOnlyList<PlotPoint>?)GetValue(SecondaryPointsProperty); set => SetValue(SecondaryPointsProperty, value); }
    public bool Rate { get => (bool)GetValue(RateProperty); set => SetValue(RateProperty, value); }
    public string PrimaryLabel { get => (string)GetValue(PrimaryLabelProperty); set => SetValue(PrimaryLabelProperty, value); }
    public string SecondaryLabel { get => (string)GetValue(SecondaryLabelProperty); set => SetValue(SecondaryLabelProperty, value); }
    private Rect _plot;
    private TimeSpan Duration => Mode == "Health"
        ? (WindowHours > 0 ? TimeSpan.FromHours(WindowHours) : TimeSpan.FromMinutes(5))
        : (WindowDuration > TimeSpan.Zero ? WindowDuration : TimeSpan.FromHours(Math.Max(1, WindowHours)));
    public TimeSpan WindowDuration { get => (TimeSpan)GetValue(WindowDurationProperty); set => SetValue(WindowDurationProperty, value); }
    public bool Bytes { get => (bool)GetValue(BytesProperty); set => SetValue(BytesProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public double PlotLeftInset { get => (double)GetValue(PlotLeftInsetProperty); set => SetValue(PlotLeftInsetProperty, value); }
    public double PlotRightInset { get => (double)GetValue(PlotRightInsetProperty); set => SetValue(PlotRightInsetProperty, value); }
    internal Rect PlotBounds => _plot;
    public string ScaleKey { get => (string)GetValue(ScaleKeyProperty); set => SetValue(ScaleKeyProperty, value); }

    public string Mode { get => (string)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public IReadOnlyList<PlotPoint>? Points { get => (IReadOnlyList<PlotPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public IReadOnlyList<HealthInterval>? Intervals { get => (IReadOnlyList<HealthInterval>?)GetValue(IntervalsProperty); set => SetValue(IntervalsProperty, value); }
    public int WindowHours { get => (int)GetValue(WindowHoursProperty); set => SetValue(WindowHoursProperty, value); }
    public DateTimeOffset EndAt { get => (DateTimeOffset)GetValue(EndAtProperty); set => SetValue(EndAtProperty, value); }

    public DashboardPlot()
    {
        ClipToBounds = true; SnapsToDevicePixels = true;
        AddVisualChild(_overlayVisual);
        InitializePulse();
        InitializeHover();
        using var overlay = _overlayVisual.RenderOpen();
        overlay.DrawDrawing(_pulseLayer);
        overlay.DrawDrawing(_hoverLayer);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? _overlayVisual : throw new ArgumentOutOfRangeException(nameof(index));

    private sealed class OverlayVisual : DrawingVisual
    {
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnRender(DrawingContext context)
    {
        using var timing = Services.MotionTiming.Measure(Mode == "Health" ? "plot.health" : "plot.trend", this);
        base.OnRender(context);
        if (ActualWidth < 2 || ActualHeight < 2) { return; }
        // Empty chart regions must receive pointer movement too, including genuine sampling gaps.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Mode == "Health") { DrawHealth(context); }
        else { DrawTrend(context); }
    }

    private static DashStyle CreateFrozenDashStyle()
    {
        var style = new DashStyle([2, 3], 0);
        style.Freeze();
        return style;
    }

    private static Color BrushColor(Brush brush) => brush is SolidColorBrush solid ? solid.Color : Colors.Gray;

    private static Pen CachedPen(ref (Color Color, Pen Pen)? cache, Brush brush, double thickness, double opacity = 1d, bool round = false, DashStyle? dashStyle = null)
    {
        var color = BrushColor(brush);
        if (opacity < 1d)
        {
            color = Color.FromArgb((byte)Math.Clamp(Math.Round(color.A * opacity), 0, 255), color.R, color.G, color.B);
        }
        if (cache is { } hit && hit.Color == color)
        {
            return hit.Pen;
        }
        var frozen = new SolidColorBrush(color);
        frozen.Freeze();
        var pen = new Pen(frozen, thickness);
        if (round)
        {
            pen.LineJoin = PenLineJoin.Round;
            pen.StartLineCap = PenLineCap.Round;
            pen.EndLineCap = PenLineCap.Round;
        }
        if (dashStyle is not null)
        {
            pen.DashStyle = dashStyle;
        }
        pen.Freeze();
        cache = (color, pen);
        return pen;
    }

    private static LinearGradientBrush? CachedAreaBrush(ref (Color Color, LinearGradientBrush Brush)? cache, Brush strokeBrush, double topAlpha, double bottomAlpha)
    {
        if (strokeBrush is not SolidColorBrush solid) { return null; }
        var color = solid.Color;
        if (cache is { } hit && hit.Color == color)
        {
            return hit.Brush;
        }
        if (CreateAreaBrush(strokeBrush, topAlpha, bottomAlpha) is not { } gradient) { return null; }
        cache = (color, gradient);
        return gradient;
    }

    private Brush Brush(string key, string fallback) => TryFindResource(key) as Brush
        ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));

    internal readonly record struct HealthCellSpan(double Left, double Right, string State);

    internal static List<HealthCellSpan> ResolveHealthCellSpans(IReadOnlyList<HealthCellSpan> raw)
    {
        var active = new List<HealthCellSpan>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var next = raw[i];
            if (next.Right <= next.Left + 1e-9) { continue; }
            for (var j = active.Count - 1; j >= 0; j--)
            {
                var prior = active[j];
                if (prior.Right <= next.Left + 1e-9 || prior.Left >= next.Right - 1e-9) { continue; }
                active.RemoveAt(j);
                if (prior.Left < next.Left - 1e-9)
                {
                    active.Add(new HealthCellSpan(prior.Left, next.Left, prior.State));
                }
                if (prior.Right > next.Right + 1e-9)
                {
                    active.Add(new HealthCellSpan(next.Right, prior.Right, prior.State));
                }
            }
            if (next.State is "healthy" or "failed")
            {
                active.Add(next);
            }
        }
        return active;
    }

    private void InvalidatePlotCaches()
    {
        _cachedHealthCells = null;
        _cachedHealthChrome = null;
        _trendDataPrepared = false;
        _cachedTrendChrome = null;
        _cachedTrendSeries = null;
        _cachedEmptyLoadingChrome = null;
        _framePenCache = null;
        _guidePenCache = null;
        _tickPenCache = null;
        _primaryPenCache = null;
        _secondaryPenCache = null;
        _hatchPenCache = null;
        _baselinePenCache = null;
        _primaryAreaCache = null;
        _secondaryAreaCache = null;
    }

    private void DrawHealth(DrawingContext context)
    {
        var height = Math.Max(12, ActualHeight - 30);
        var width = ActualWidth;
        var duration = Duration;
        var from = EndAt - duration;
        var unknown = Brush("Semantic.Brush.Selection", "#202026");
        var hatchBrush = Brush("Semantic.Brush.BorderStrong", "#3B3B45");
        var healthy = Brush("Semantic.Brush.HistoryHealthy", "#43434E");
        var failed = Brush("Semantic.Brush.StatusFailed", "#E11D48");
        var intervals = Intervals;
        var intervalsCount = intervals?.Count ?? 0;
        var cellsKey = new HealthCellsCacheKey(
            width,
            height,
            EndAt,
            duration,
            intervals,
            intervalsCount,
            intervalsCount > 0 ? intervals![0] : null,
            intervalsCount > 0 ? intervals![intervalsCount - 1] : null,
            BrushColor(unknown),
            BrushColor(hatchBrush),
            BrushColor(healthy),
            BrushColor(failed));
        if (_cachedHealthCells is null || _cachedHealthCellsKey != cellsKey)
        {
            var seconds = Math.Max(1e-6, duration.TotalSeconds);
            var count = Math.Max(1, (int)(width / 9));
            var stride = width / count;
            var hatch = CachedPen(ref _hatchPenCache, hatchBrush, 1);
            var cellWidth = Math.Max(1, stride - 3);
            var cells = new List<HealthCellSpan>?[count];
            if (intervals is not null)
            {
                // Partition [from, EndAt] contiguously across all count cells so there is never an inter-cell blind spot.
                foreach (var interval in intervals)
                {
                    var u0 = Math.Max(0, (interval.From - from).TotalSeconds / seconds * count);
                    var u1 = Math.Min(count, (interval.To - from).TotalSeconds / seconds * count);
                    if (u1 <= u0) { continue; }
                    var first = Math.Clamp((int)u0, 0, count - 1);
                    var last = Math.Clamp((int)Math.Ceiling(u1) - 1, 0, count - 1);
                    for (var index = first; index <= last; index++)
                    {
                        var localLeft = Math.Max(0, u0 - index);
                        var localRight = Math.Min(1, u1 - index);
                        if (localRight > localLeft)
                        {
                            (cells[index] ??= []).Add(new HealthCellSpan(localLeft, localRight, interval.State));
                        }
                    }
                }
            }

            var cellsGroup = new DrawingGroup();
            using (var dc = cellsGroup.Open())
            {
                for (var index = 0; index < count; index++)
                {
                    var rect = new Rect(index * stride, 0, cellWidth, height);
                    dc.DrawRectangle(unknown, null, rect);
                    var cellClip = new RectangleGeometry(rect);
                    cellClip.Freeze();
                    dc.PushClip(cellClip);
                    for (double y = -8; y < height + 8; y += 8)
                    {
                        dc.DrawLine(hatch, new Point(rect.Left, y + 6), new Point(rect.Right, y));
                    }
                    if (cells[index] is { } rawSpans)
                    {
                        var resolved = ResolveHealthCellSpans(rawSpans);
                        var anyFailed = false;
                        for (var i = 0; i < resolved.Count; i++)
                        {
                            if (resolved[i].State == "failed" && resolved[i].Right - resolved[i].Left > 1e-9)
                            {
                                anyFailed = true;
                                break;
                            }
                        }
                        if (anyFailed)
                        {
                            dc.DrawRectangle(failed, null, rect);
                        }
                        else
                        {
                            for (var i = 0; i < resolved.Count; i++)
                            {
                                var span = resolved[i];
                                var left = rect.Left + span.Left * cellWidth;
                                var spanWidth = (span.Right - span.Left) * cellWidth;
                                if (spanWidth > 0)
                                {
                                    dc.DrawRectangle(healthy, null, new Rect(left, 0, spanWidth, height));
                                }
                            }
                        }
                    }
                    dc.Pop();
                }
            }
            cellsGroup.Freeze();
            _cachedHealthCells = cellsGroup;
            _cachedHealthCellsKey = cellsKey;
        }

        var progress = EffectiveSweepProgress;
        var clipping = progress < 0.999;
        if (progress > 0.001)
        {
            if (clipping)
            {
                var sweepClip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, width * progress), height));
                sweepClip.Freeze();
                context.PushClip(sweepClip);
            }
            context.DrawDrawing(_cachedHealthCells);
            if (clipping) { context.Pop(); }
        }

        var baselineBrush = Brush("Semantic.Brush.BorderSubtle", "#2B2B33");
        var hintBrush = Brush("Semantic.Brush.TextHint", "#8B8B94");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var culture = CultureInfo.CurrentCulture;
        var chromeKey = new HealthChromeCacheKey(
            width,
            height,
            from,
            duration,
            culture.Name,
            dpi,
            BrushColor(baselineBrush),
            BrushColor(hintBrush));
        if (_cachedHealthChrome is null || _cachedHealthChromeKey != chromeKey)
        {
            var chromeGroup = new DrawingGroup();
            using (var dc = chromeGroup.Open())
            {
                dc.DrawLine(CachedPen(ref _baselinePenCache, baselineBrush, 1),
                    new Point(0, height + 7), new Point(width, height + 7));
                var tickCount = duration < TimeSpan.FromHours(1) ? 5 : 6;
                var labelFormat = duration <= TimeSpan.FromHours(24) ? "HH:mm" : "MM-dd";
                for (var index = 0; index <= tickCount; index++)
                {
                    var at = (from + TimeSpan.FromTicks((long)Math.Round(duration.Ticks * (index / (double)tickCount)))).ToLocalTime();
                    var label = Text(at.ToString(labelFormat, culture), 11.5);
                    var x = Math.Clamp(width * index / tickCount - label.Width / 2, 0, Math.Max(0, width - label.Width));
                    dc.DrawText(label, new Point(x, height + 12));
                }
            }
            chromeGroup.Freeze();
            _cachedHealthChrome = chromeGroup;
            _cachedHealthChromeKey = chromeKey;
        }

        context.DrawDrawing(_cachedHealthChrome);
        if (IsLoadingSweepVisible)
        {
            DrawLoadingSweep(context, 0, height + 6, width);
        }
    }

    private static PlotPoint[] FilterVisible(IReadOnlyList<PlotPoint>? source, DateTimeOffset from, DateTimeOffset to)
    {
        if (source is null || source.Count == 0) { return []; }
        var list = new List<PlotPoint>(source.Count);
        var ordered = true;
        var previousAt = DateTimeOffset.MinValue;
        for (var index = 0; index < source.Count; index++)
        {
            var point = source[index];
            if (point.At < from || point.At > to) { continue; }
            if (point.At < previousAt) { ordered = false; }
            previousAt = point.At;
            list.Add(point);
        }
        if (list.Count == 0) { return []; }
        if (!ordered) { list.Sort(static (a, b) => a.At.CompareTo(b.At)); }
        return list.ToArray();
    }

    private static double[] CollectValidValues(PlotPoint[] primary, PlotPoint[] secondary)
    {
        var values = new List<double>(primary.Length + secondary.Length);
        for (var index = 0; index < primary.Length; index++)
        {
            if (Valid(primary[index].Value)) { values.Add(primary[index].Value!.Value); }
        }
        for (var index = 0; index < secondary.Length; index++)
        {
            if (Valid(secondary[index].Value)) { values.Add(secondary[index].Value!.Value); }
        }
        return values.Count == 0 ? [] : values.ToArray();
    }

    internal void EnsureTrendDataPrepared()
    {
        var points = Points;
        var secondaryPoints = SecondaryPoints;
        var pointsCount = points?.Count ?? 0;
        var secondaryCount = secondaryPoints?.Count ?? 0;
        var dataKey = new TrendDataCacheKey(
            ActualWidth,
            ActualHeight,
            PlotLeftInset,
            PlotRightInset,
            EndAt,
            Duration,
            Bytes,
            Rate,
            ScaleKey,
            points,
            pointsCount,
            pointsCount > 0 ? points![0] : null,
            pointsCount > 0 ? points![pointsCount - 1] : null,
            secondaryPoints,
            secondaryCount,
            secondaryCount > 0 ? secondaryPoints![0] : null,
            secondaryCount > 0 ? secondaryPoints![secondaryCount - 1] : null);

        if (!_trendDataPrepared || _cachedTrendDataKey != dataKey)
        {
            var windowStart = EndAt - Duration;
            _visiblePoints = FilterVisible(points, windowStart, EndAt);
            _visibleSecondary = FilterVisible(secondaryPoints, windowStart, EndAt);
            var values = CollectValidValues(_visiblePoints, _visibleSecondary);
            _cachedTrendDataBounds = TrendBounds();
            _cachedTrendHasValues = values.Length > 0;
            if (_cachedTrendHasValues)
            {
                _plot = _cachedTrendDataBounds;
                _renderScale = _scale.Update(values, Bytes, EndAt, Rate);
            }
            else
            {
                _plot = IsLoading ? _cachedTrendDataBounds : Rect.Empty;
            }
            _cachedTrendChrome = null;
            _cachedTrendSeries = null;
            _cachedEmptyLoadingChrome = null;
            _cachedTrendDataKey = dataKey;
            _trendDataPrepared = true;
        }
    }

    private void DrawTrend(DrawingContext context)
    {
        EnsureTrendDataPrepared();

        var secondaryPoints = SecondaryPoints;
        var bounds = _cachedTrendDataBounds;
        var captionText = secondaryPoints is null ? Caption : string.Empty;
        var subtleBrush = Brush("Semantic.Brush.BorderSubtle", "#2B2B33");
        var strongBrush = Brush("Semantic.Brush.BorderStrong", "#3B3B45");
        var hintBrush = Brush("Semantic.Brush.TextHint", "#8B8B94");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (!_cachedTrendHasValues)
        {
            if (IsLoading)
            {
                _plot = bounds;
                var loadingKey = new EmptyLoadingCacheKey(
                    bounds,
                    ActualWidth,
                    ActualHeight,
                    captionText,
                    CultureInfo.CurrentCulture.Name,
                    dpi,
                    BrushColor(subtleBrush),
                    BrushColor(strongBrush),
                    BrushColor(hintBrush));
                if (_cachedEmptyLoadingChrome is null || _cachedEmptyLoadingKey != loadingKey)
                {
                    var emptyLoading = new DrawingGroup();
                    using (var dc = emptyLoading.Open())
                    {
                        DrawTrendCaption(dc, bounds, captionText);
                        DrawTelemetryGrid(dc);
                        var label = Text("正在载入…", 12);
                        dc.DrawText(label, new Point(bounds.Left + 8, bounds.Top + Math.Max(4, (bounds.Height - label.Height) / 2)));
                    }
                    emptyLoading.Freeze();
                    _cachedEmptyLoadingChrome = emptyLoading;
                    _cachedEmptyLoadingKey = loadingKey;
                }
                context.DrawDrawing(_cachedEmptyLoadingChrome);
                DrawLoadingSweep(context, _plot.Left, _plot.Bottom - 1.5, _plot.Width);
                return;
            }
            _plot = Rect.Empty;
            DrawTrendCaption(context, bounds, captionText);
            var emptyLabel = Text(EmptyText, 12);
            context.DrawText(emptyLabel, new Point(bounds.Left, Math.Max(20, (ActualHeight - emptyLabel.Height) / 2)));
            if (_loadingFinishing)
            {
                DrawLoadingSweep(context, bounds.Left, bounds.Bottom - 1.5, bounds.Width);
            }
            return;
        }

        _plot = bounds;
        var scale = _renderScale;
        var chromeKey = new TrendChromeCacheKey(
            _plot,
            ActualWidth,
            ActualHeight,
            captionText,
            scale.Minimum,
            scale.Maximum,
            Bytes,
            Rate,
            CultureInfo.CurrentCulture.Name,
            dpi,
            BrushColor(subtleBrush),
            BrushColor(strongBrush),
            BrushColor(hintBrush));
        if (_cachedTrendChrome is null || _cachedTrendChromeKey != chromeKey)
        {
            var chromeGroup = new DrawingGroup();
            using (var dc = chromeGroup.Open())
            {
                DrawTrendCaption(dc, bounds, captionText);
                DrawTelemetryGrid(dc);
                dc.DrawText(Text(ScaleLabel(scale.Maximum, scale.Maximum), 11), new Point(_plot.Left, 2));
                dc.DrawText(Text(ScaleLabel(scale.Minimum, scale.Maximum), 11), new Point(_plot.Left, _plot.Bottom + 5));
            }
            chromeGroup.Freeze();
            _cachedTrendChrome = chromeGroup;
            _cachedTrendChromeKey = chromeKey;
        }
        context.DrawDrawing(_cachedTrendChrome);

        var accentBrush = Brush("Semantic.Brush.Accent", "#E11D48");
        var secondaryBrush = Brush("Semantic.Brush.ChartSecondary", "#9E334D");
        var progress = EffectiveSweepProgress;
        var clipping = progress < 0.999;
        if (progress > 0.001)
        {
            var seriesKey = new TrendSeriesCacheKey(BrushColor(accentBrush), BrushColor(secondaryBrush));
            if (_cachedTrendSeries is null || _cachedTrendSeriesKey != seriesKey)
            {
                var seriesGroup = new DrawingGroup();
                Point? tailPoint;
                using (var dc = seriesGroup.Open())
                {
                    if (_visibleSecondary.Length > 0)
                    {
                        DrawSeries(dc, _visibleSecondary, scale,
                            CachedPen(ref _secondaryPenCache, secondaryBrush, 1.05, round: true),
                            CachedAreaBrush(ref _secondaryAreaCache, secondaryBrush, 0.10, 0.0), out _);
                    }
                    DrawSeries(dc, _visiblePoints, scale,
                        CachedPen(ref _primaryPenCache, accentBrush, 1.15, round: true),
                        CachedAreaBrush(ref _primaryAreaCache, accentBrush, 0.22, 0.01), out tailPoint);
                }
                seriesGroup.Freeze();
                _cachedTrendSeries = seriesGroup;
                _cachedPrimaryTailPoint = tailPoint;
                _cachedTrendSeriesKey = seriesKey;
            }

            if (clipping)
            {
                var sweepClip = new RectangleGeometry(new Rect(_plot.Left - 1.5, _plot.Top - 3, _plot.Width * progress + 1.5, _plot.Height + 6));
                sweepClip.Freeze();
                context.PushClip(sweepClip);
            }
            context.DrawDrawing(_cachedTrendSeries);
            if (!clipping && _cachedPrimaryTailPoint is { } tail)
            {
                context.DrawRectangle(accentBrush, null, new Rect(tail.X - 1.3, tail.Y - 1.3, 2.6, 2.6));
            }
            if (clipping)
            {
                context.Pop();
                if (progress > 0.005 && _visiblePoints.Length > 0
                    && FindSweepLeadPoint(_visiblePoints, scale, _plot.Left + _plot.Width * progress) is { } lead)
                {
                    context.DrawRectangle(accentBrush, null, new Rect(lead.X - 1.4, lead.Y - 1.4, 2.8, 2.8));
                }
            }
        }
        if (IsLoadingSweepVisible)
        {
            DrawLoadingSweep(context, _plot.Left, _plot.Bottom - 1.5, _plot.Width);
        }
    }

    private void DrawTrendCaption(DrawingContext context, Rect bounds, string captionText)
    {
        if (string.IsNullOrEmpty(captionText)) { return; }
        var caption = Text(captionText, 11);
        caption.MaxTextWidth = Math.Max(1, bounds.Width);
        caption.MaxLineCount = 1;
        caption.Trimming = TextTrimming.CharacterEllipsis;
        context.DrawText(caption, new Point(Math.Max(bounds.Left, bounds.Right - caption.Width), 2));
    }

    private Point? FindSweepLeadPoint(IReadOnlyList<PlotPoint> points, Services.TrendScale scale, double revealRight)
    {
        var span = Math.Max(1e-9, scale.Maximum - scale.Minimum);
        Point? previousPoint = null;
        PlotPoint? previousData = null;
        Point? rightmostVisible = null;
        for (var index = 0; index < points.Count; index++)
        {
            var data = points[index];
            if (!Valid(data.Value))
            {
                previousPoint = null;
                previousData = null;
                continue;
            }
            var current = new Point(_plot.Left + Position(data.At) / ActualWidth * _plot.Width,
                _plot.Bottom - (data.Value!.Value - scale.Minimum) / span * _plot.Height);
            if (previousPoint is { } prevPt && previousData is not null
                && CanConnect(previousData, data, Services.ResourceTrendBuffer.MaximumGap)
                && prevPt.X <= revealRight && current.X >= revealRight && current.X > prevPt.X)
            {
                var t = (revealRight - prevPt.X) / (current.X - prevPt.X);
                return new Point(revealRight, prevPt.Y + (current.Y - prevPt.Y) * t);
            }
            if (current.X <= revealRight) { rightmostVisible = current; }
            previousPoint = current;
            previousData = data;
        }
        return rightmostVisible is { } pt && revealRight - pt.X <= 6 ? pt : null;
    }

    private void DrawTelemetryGrid(DrawingContext context)
    {
        var subtleBrush = Brush("Semantic.Brush.BorderSubtle", "#2B2B33");
        var framePen = CachedPen(ref _framePenCache, subtleBrush, 1, opacity: 0.55);
        context.DrawLine(framePen, new Point(_plot.Left, _plot.Top), new Point(_plot.Right, _plot.Top));
        context.DrawLine(framePen, new Point(_plot.Left, _plot.Bottom), new Point(_plot.Right, _plot.Bottom));

        var midY = _plot.Top + _plot.Height / 2;
        var guide = CachedPen(ref _guidePenCache, subtleBrush, 1, dashStyle: GridDashStyle);
        context.DrawLine(guide, new Point(_plot.Left, midY), new Point(_plot.Right, midY));

        if (_plot.Width >= 24 && _plot.Height >= 16)
        {
            var tickBrush = Brush("Semantic.Brush.BorderStrong", "#3B3B45");
            var tickPen = CachedPen(ref _tickPenCache, tickBrush, 1);
            const double tick = 4;
            context.DrawLine(tickPen, new Point(_plot.Left, _plot.Top), new Point(_plot.Left + tick, _plot.Top));
            context.DrawLine(tickPen, new Point(_plot.Left, _plot.Top), new Point(_plot.Left, _plot.Top + tick));
            context.DrawLine(tickPen, new Point(_plot.Right - tick, _plot.Top), new Point(_plot.Right, _plot.Top));
            context.DrawLine(tickPen, new Point(_plot.Right, _plot.Top), new Point(_plot.Right, _plot.Top + tick));
            context.DrawLine(tickPen, new Point(_plot.Left, _plot.Bottom), new Point(_plot.Left + tick, _plot.Bottom));
            context.DrawLine(tickPen, new Point(_plot.Left, _plot.Bottom - tick), new Point(_plot.Left, _plot.Bottom));
            context.DrawLine(tickPen, new Point(_plot.Right - tick, _plot.Bottom), new Point(_plot.Right, _plot.Bottom));
            context.DrawLine(tickPen, new Point(_plot.Right, _plot.Bottom - tick), new Point(_plot.Right, _plot.Bottom));
        }
    }

    internal static LinearGradientBrush? CreateAreaBrush(Brush strokeBrush, double topAlpha, double bottomAlpha)
    {
        if (strokeBrush is not SolidColorBrush solid) { return null; }
        var c = solid.Color;
        var top = Color.FromArgb((byte)Math.Clamp(Math.Round(topAlpha * 255), 0, 255), c.R, c.G, c.B);
        var bottom = Color.FromArgb((byte)Math.Clamp(Math.Round(bottomAlpha * 255), 0, 255), c.R, c.G, c.B);
        var gradient = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(0, 1));
        gradient.Freeze();
        return gradient;
    }

    private Rect TrendBounds()
    {
        var left = Math.Min(PlotLeftInset, Math.Max(0, ActualWidth - 1));
        var right = Math.Min(PlotRightInset, Math.Max(0, ActualWidth - left - 1));
        return new Rect(left, 26, Math.Max(1, ActualWidth - left - right), Math.Max(1, ActualHeight - 55));
    }

    private void DrawSeries(DrawingContext context, IReadOnlyList<PlotPoint> points, Services.TrendScale scale, Pen pen, Brush? areaBrush, out Point? tailPoint)
    {
        tailPoint = null;
        var runs = new List<List<Point>>();
        List<Point>? currentRun = null;
        PlotPoint? previous = null;
        var span = Math.Max(1e-9, scale.Maximum - scale.Minimum);
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (!Valid(point.Value)) { previous = null; currentRun = null; continue; }
            var end = new Point(_plot.Left + Position(point.At) / ActualWidth * _plot.Width,
                _plot.Bottom - (point.Value!.Value - scale.Minimum) / span * _plot.Height);
            if (previous is not null && currentRun is not null && CanConnect(previous, point, Services.ResourceTrendBuffer.MaximumGap))
            {
                currentRun.Add(end);
            }
            else
            {
                currentRun = [end];
                runs.Add(currentRun);
                context.DrawRectangle(pen.Brush, null, new Rect(end.X - 1, end.Y - 1, 2, 2));
            }
            previous = point;
        }

        StreamGeometry? areaGeometry = null;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            StreamGeometryContext? areaPath = null;
            if (areaBrush is not null && runs.Count > 0)
            {
                areaGeometry = new StreamGeometry();
                areaPath = areaGeometry.Open();
            }
            try
            {
                for (var r = 0; r < runs.Count; r++)
                {
                    var rendered = DecimateRun(runs[r], _plot.Left, _plot.Width, _plot.Height);
                    path.BeginFigure(rendered[0], false, false);
                    for (var index = 1; index < rendered.Count; index++)
                    {
                        path.LineTo(rendered[index], true, false);
                    }
                    if (areaPath is not null && rendered.Count > 1 && rendered[^1].X > rendered[0].X)
                    {
                        areaPath.BeginFigure(new Point(rendered[0].X, _plot.Bottom), true, true);
                        for (var index = 0; index < rendered.Count; index++)
                        {
                            areaPath.LineTo(rendered[index], false, false);
                        }
                        areaPath.LineTo(new Point(rendered[^1].X, _plot.Bottom), false, false);
                    }
                }
            }
            finally
            {
                areaPath?.Close();
            }
        }
        if (areaGeometry is not null && areaBrush is not null)
        {
            areaGeometry.Freeze();
            context.DrawGeometry(areaBrush, null, areaGeometry);
        }
        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);

        if (runs.Count > 0)
        {
            tailPoint = runs[^1][^1];
        }
    }

    private static IReadOnlyList<Point> DecimateRun(List<Point> run, double plotLeft, double plotWidth, double plotHeight)
    {
        if (run.Count <= 6 || plotWidth <= 4 || run.Count <= plotWidth * 0.65)
        {
            return run;
        }
        const double bucketWidth = 1.35;
        var reduced = new List<Point>((int)(plotWidth / bucketWidth) + 8);
        var index = 0;
        while (index < run.Count)
        {
            var bucket = (int)Math.Floor((run[index].X - plotLeft) / bucketWidth);
            var end = index + 1;
            while (end < run.Count && (int)Math.Floor((run[end].X - plotLeft) / bucketWidth) == bucket)
            {
                end++;
            }
            var count = end - index;
            if (count == 1)
            {
                reduced.Add(run[index]);
            }
            else
            {
                var minIdx = index;
                var maxIdx = index;
                var sumX = 0d;
                var sumY = 0d;
                for (var i = index; i < end; i++)
                {
                    var pt = run[i];
                    sumX += pt.X;
                    sumY += pt.Y;
                    if (pt.Y < run[minIdx].Y) { minIdx = i; }
                    if (pt.Y > run[maxIdx].Y) { maxIdx = i; }
                }
                if (index == 0) { reduced.Add(run[0]); }
                var spread = run[maxIdx].Y - run[minIdx].Y;
                if (spread <= Math.Max(1.5, plotHeight * 0.045))
                {
                    if (index > 0 && end < run.Count)
                    {
                        reduced.Add(new Point(sumX / count, sumY / count));
                    }
                }
                else
                {
                    var firstExtreme = Math.Min(minIdx, maxIdx);
                    var secondExtreme = Math.Max(minIdx, maxIdx);
                    if (firstExtreme > 0 && firstExtreme < run.Count - 1) { reduced.Add(run[firstExtreme]); }
                    if (secondExtreme != firstExtreme && secondExtreme > 0 && secondExtreme < run.Count - 1) { reduced.Add(run[secondExtreme]); }
                }
                if (end == run.Count) { reduced.Add(run[^1]); }
            }
            index = end;
        }
        if (reduced.Count < 4) { return reduced; }
        var smoothed = new Point[reduced.Count];
        smoothed[0] = reduced[0];
        smoothed[^1] = reduced[^1];
        var prominenceThreshold = Math.Max(3.0, plotHeight * 0.14);
        for (var i = 1; i < reduced.Count - 1; i++)
        {
            var prev = reduced[i - 1];
            var curr = reduced[i];
            var next = reduced[i + 1];
            var isPeak = (curr.Y < prev.Y && curr.Y < next.Y) || (curr.Y > prev.Y && curr.Y > next.Y);
            var prominence = Math.Max(Math.Abs(curr.Y - prev.Y), Math.Abs(curr.Y - next.Y));
            if (isPeak && prominence >= prominenceThreshold)
            {
                smoothed[i] = curr;
            }
            else
            {
                smoothed[i] = new Point(curr.X, prev.Y * 0.22 + curr.Y * 0.56 + next.Y * 0.22);
            }
        }
        return smoothed;
    }

    internal static bool CanConnect(PlotPoint previous, PlotPoint point, TimeSpan maximumGap) =>
        !point.BreakBefore && Valid(previous.Value) && Valid(point.Value) && point.At > previous.At
        && (previous.To is { } to && point.From is { } from ? to == from : point.At - previous.At <= maximumGap);

    private static bool Valid(double? value) => value is >= 0 && double.IsFinite(value.Value);
    private static bool ValidInset(object value) => value is double inset && double.IsFinite(inset) && inset >= 0;
    private string ScaleLabel(double value, double upper)
    {
        return MetricLabel(value, upper, Bytes, Rate);
    }

    internal static string MetricLabel(double value, double upper, bool bytes, bool rate)
    {
        if (!bytes && !rate) { return $"{value:0.##}%"; }
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        var unit = 0; var divisor = 1d;
        while (upper / divisor >= 1024 && unit < units.Length - 1) { divisor *= 1024; unit++; }
        var number = value / divisor;
        return $"{number.ToString(number >= 1e6 ? "G3" : "0.#", CultureInfo.CurrentCulture)} {units[unit]}{(rate ? "/s" : "")}";
    }

    private void DrawLoadingSweep(DrawingContext context, double left, double top, double width)
    {
        if (width <= 8) { return; }
        var accent = Brush("Semantic.Brush.Accent", "#E11D48");
        var barWidth = Math.Clamp(width * 0.28, 28, Math.Min(120, width));
        var phase = CanAnimateSweep ? Math.Clamp(LoadingPhase, 0d, 1d) : 0.5d;
        var x = left + Math.Max(0, width - barWidth) * phase;
        if (_loadingFinishing && phase > 0.88d)
        {
            var faded = accent.CloneCurrentValue();
            faded.Opacity *= Math.Clamp((1d - phase) / 0.12d, 0.15d, 1d);
            faded.Freeze();
            accent = faded;
        }
        context.DrawRectangle(accent, null, new Rect(x, top, barWidth, 2));
    }

    private double Position(DateTimeOffset at) => (at - (EndAt - Duration)).TotalSeconds / Duration.TotalSeconds * ActualWidth;
    private FormattedText Text(string value, double size) => new(value, ZhCnCulture,
        System.Windows.FlowDirection.LeftToRight, MonoTypeface, size,
        Brush("Semantic.Brush.TextHint", "#8B8B94"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
