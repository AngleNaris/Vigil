using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SystemAutorun.App.Services;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;

namespace SystemAutorun.App.Controls;

public sealed partial class DashboardPlot
{
    public static readonly DependencyProperty DataAtProperty = DependencyProperty.Register(
        nameof(DataAt), typeof(DateTimeOffset?), typeof(DashboardPlot), new PropertyMetadata(null));
    public DateTimeOffset? DataAt { get => (DateTimeOffset?)GetValue(DataAtProperty); set => SetValue(DataAtProperty, value); }
    internal static readonly DependencyProperty SweepProgressProperty = DependencyProperty.Register(
        nameof(SweepProgress), typeof(double), typeof(DashboardPlot),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    internal double SweepProgress { get => (double)GetValue(SweepProgressProperty); set => SetValue(SweepProgressProperty, value); }
    internal static readonly DependencyProperty LoadingPhaseProperty = DependencyProperty.Register(
        nameof(LoadingPhase), typeof(double), typeof(DashboardPlot),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    internal double LoadingPhase { get => (double)GetValue(LoadingPhaseProperty); set => SetValue(LoadingPhaseProperty, value); }
    private readonly DrawingGroup _pulseLayer = new() { Opacity = 0 };
    private readonly DataChangePulse.ClockLease _pulseClock = new();
    private DataChangePulse.Lifetime? _pulseLifetime;
    private DispatcherOperation? _pulsePending;
    private PlotPoint[] _beforePrimary = [], _beforeSecondary = [];
    private HealthInterval[] _beforeHealth = [];
    private DateTimeOffset _beforeEnd;
    private DateTimeOffset? _beforeRecord;
    private TrendScale _renderScale;
    private bool _pulseReady;
    internal bool DataPulseActive => _pulseClock.Active;
    internal int PulseGeometryBuilds { get; private set; }
    internal DrawingGroup PulseLayer => _pulseLayer;

    private void InitializePulse()
    {
        _pulseLifetime = DataChangePulse.Watch(this, ResetPulse);
        Loaded += OnPlotLoaded;
        IsVisibleChanged += OnPlotVisibleChanged;
    }

    private void OnPlotLoaded(object sender, RoutedEventArgs args)
    {
        if (!ReferenceEquals(args.OriginalSource, this)) { return; }
        _wasVisible = IsVisible;
        if (IsVisible) { TryStartPendingSweep(); SyncLoadingAnimation(); }
    }

    private void OnPlotVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        var visible = args.NewValue is true;
        if (!visible)
        {
            _wasVisible = false;
            _pendingDelayMs = 0;
            _sweepRequestTimestamp = 0;
            _entranceDelayMs = 0;
            _entranceStartTimestamp = 0;
            _healthDrewEmpty = false;
            var hiddenByWindow = IsHiddenOnlyByWindow();
            _hiddenWithWindow = hiddenByWindow;
            StopRangeSweep();
            StopLoadingAnimation();
            if (!hiddenByWindow) { _awaitingSeriesDraw = true; }
            return;
        }
        if (!_wasVisible)
        {
            _wasVisible = true;
            if (_hiddenWithWindow)
            {
                _hiddenWithWindow = false;
                SyncLoadingAnimation();
                return;
            }
            _awaitingSeriesDraw = true;
            var entranceRemaining = CurrentRemainingEntranceDelayMs();
            if (entranceRemaining > 0)
            {
                _pendingDelayMs = entranceRemaining;
                _sweepRequestTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            TryStartPendingSweep();
            SyncLoadingAnimation();
        }
    }

    private bool IsHiddenOnlyByWindow()
    {
        var window = Window.GetWindow(this);
        if (window is null || (window.IsVisible && window.WindowState != WindowState.Minimized))
        {
            return false;
        }
        for (DependencyObject? current = this; current is not null && !ReferenceEquals(current, window); current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement element && element.Visibility != Visibility.Visible)
            {
                return false;
            }
        }
        return true;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        var property = args.Property;
        HoverSourceChanged(property);
        if (_pulseLifetime is null) { return; }
        if (property == ScaleKeyProperty || property == ModeProperty || property == WindowDurationProperty || property == WindowHoursProperty
            || property == BytesProperty || property == RateProperty || property == PlotLeftInsetProperty || property == PlotRightInsetProperty)
        {
            InvalidatePlotCaches();
            ResetPulse();
            var rangeChanged = (property == WindowHoursProperty && Mode == "Health" && args.OldValue is int oldHours && oldHours >= 0 && !Equals(args.OldValue, args.NewValue))
                || (property == WindowDurationProperty && Mode != "Health" && args.OldValue is TimeSpan oldDuration && oldDuration > TimeSpan.Zero && !Equals(args.OldValue, args.NewValue))
                || (property == ScaleKeyProperty && args.OldValue is string oldKey && oldKey.Length > 0 && args.NewValue is string newKey && newKey.Length > 0 && !string.Equals(oldKey, newKey, StringComparison.Ordinal));
            if (rangeChanged)
            {
                _awaitingSeriesDraw = true;
                _healthDrewEmpty = false;
                // Preserve any active LineMotion entrance deadline when TickDashboard() updates ScaleKey right after tab switch.
                var entranceRemaining = CurrentRemainingEntranceDelayMs();
                _pendingDelayMs = entranceRemaining;
                _sweepRequestTimestamp = entranceRemaining > 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                if (entranceRemaining > 0)
                {
                    StopRangeSweep();
                    InvalidateVisual();
                }
                TryStartPendingSweep();
            }
            SyncLoadingAnimation();
        }
        else if (property == IsLoadingProperty)
        {
            SyncLoadingAnimation();
        }
        else if (property == PointsProperty || property == SecondaryPointsProperty || property == IntervalsProperty
            || property == EndAtProperty || property == DataAtProperty)
        {
            InvalidatePlotCaches();
            if (_awaitingSeriesDraw)
            {
                TryStartPendingSweep();
            }
            else if (property == IntervalsProperty && Mode == "Health" && _healthDrewEmpty && Intervals is { Count: > 0 })
            {
                _healthDrewEmpty = false;
                PlayRangeSweep(CurrentRemainingEntranceDelayMs());
            }
            SyncLoadingAnimation();
            QueuePulse();
        }
    }

    private bool _awaitingSeriesDraw = true;
    private bool _healthDrewEmpty;
    private bool _wasVisible;
    private bool _hiddenWithWindow;
    private bool _loadingFinishing;
    private double _pendingDelayMs;
    private long _sweepRequestTimestamp;
    private double _entranceDelayMs;
    private long _entranceStartTimestamp;
    private AnimationClock? _sweepClock;
    private AnimationClock? _loadingClock;

    internal bool IsLoadingSweepVisible => IsLoading || _loadingFinishing;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidatePlotCaches();
        if (_hoverInitialized) ClearHover();
        if (_pulseLifetime is not null) ResetPulse();
        if (_awaitingSeriesDraw) { TryStartPendingSweep(); }
    }

    private bool CanAnimateSweep => IsLoaded && IsVisible
        && Window.GetWindow(this)?.WindowState != WindowState.Minimized
        && SystemParameters.ClientAreaAnimation
        && !ColorStateAssist.GetReduceMotion(this)
        && !LineMotion.GetIsSuspended(this);

    private void ResetPulse()
    {
        _pulseClock.Stop(); _pulseLayer.Children.Clear(); _pulseReady = false;
        _beforePrimary = []; _beforeSecondary = []; _beforeHealth = [];
        _pulsePending?.Abort(); _pulsePending = null;
        if (!CanAnimateSweep)
        {
            if (ColorStateAssist.GetReduceMotion(this) || !SystemParameters.ClientAreaAnimation)
            {
                _awaitingSeriesDraw = false;
                _pendingDelayMs = 0;
                _sweepRequestTimestamp = 0;
                _entranceDelayMs = 0;
                _entranceStartTimestamp = 0;
            }
            StopRangeSweep();
        }
        HoverMotionChanged(); SyncLoadingAnimation(); QueuePulse();
    }

    private bool HasRenderableLoadedSeries()
    {
        if (Mode == "Health") { return Intervals is { Count: > 0 }; }
        var windowStart = EndAt - Duration;
        return HasValidPointInWindow(Points, windowStart, EndAt)
            || HasValidPointInWindow(SecondaryPoints, windowStart, EndAt);
    }

    private void SyncLoadingAnimation()
    {
        if (!CanAnimateSweep)
        {
            StopLoadingAnimation();
            return;
        }
        if (IsLoading && !HasRenderableLoadedSeries())
        {
            if (_loadingClock is not null && !_loadingFinishing) { return; }
            StartLoopingLoadingAnimation();
            return;
        }
        if (_loadingClock is null || _loadingFinishing)
        {
            return;
        }
        var phase = Math.Clamp(LoadingPhase, 0d, 1d);
        if (phase >= 0.985d)
        {
            StopLoadingAnimation();
            return;
        }
        FinishLoadingAnimation(phase);
    }

    private void StartLoopingLoadingAnimation()
    {
        ClearLoadingClock();
        _loadingFinishing = false;
        var animation = new DoubleAnimation(0d, 1d, new Duration(TimeSpan.FromMilliseconds(750)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Timeline.SetDesiredFrameRate(animation, 60);
        var clock = animation.CreateClock();
        _loadingClock = clock;
        ApplyAnimationClock(LoadingPhaseProperty, clock, HandoffBehavior.SnapshotAndReplace);
        clock.Controller!.Begin();
        InvalidateVisual();
    }

    private void FinishLoadingAnimation(double fromPhase)
    {
        ClearLoadingClock();
        _loadingFinishing = true;
        var remainingMs = Math.Max(140d, (1d - fromPhase) * 650d);
        var animation = new DoubleAnimation(fromPhase, 1d, new Duration(TimeSpan.FromMilliseconds(remainingMs)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        Timeline.SetDesiredFrameRate(animation, 60);
        var clock = animation.CreateClock();
        _loadingClock = clock;
        clock.Completed += (_, _) =>
        {
            if (ReferenceEquals(_loadingClock, clock))
            {
                StopLoadingAnimation();
            }
        };
        ApplyAnimationClock(LoadingPhaseProperty, clock, HandoffBehavior.SnapshotAndReplace);
        clock.Controller!.Begin();
        InvalidateVisual();
    }

    private void ClearLoadingClock()
    {
        if (_loadingClock is null) { return; }
        ApplyAnimationClock(LoadingPhaseProperty, null);
        _loadingClock.Controller?.Remove();
        _loadingClock = null;
    }

    private void StopLoadingAnimation()
    {
        var changed = _loadingClock is not null || _loadingFinishing;
        ClearLoadingClock();
        _loadingFinishing = false;
        if (changed) { InvalidateVisual(); }
    }

    private bool HasRenderableSeriesData()
    {
        if (ActualWidth < 2 || ActualHeight < 2) { return false; }
        if (Mode == "Health") { return EndAt > DateTimeOffset.UnixEpoch; }
        var windowStart = EndAt - Duration;
        return HasValidPointInWindow(Points, windowStart, EndAt)
            || HasValidPointInWindow(SecondaryPoints, windowStart, EndAt);
    }

    private static bool HasValidPointInWindow(IReadOnlyList<PlotPoint>? source, DateTimeOffset from, DateTimeOffset to)
    {
        if (source is null || source.Count == 0) { return false; }
        for (var index = 0; index < source.Count; index++)
        {
            var point = source[index];
            if (point.At >= from && point.At <= to && Valid(point.Value)) { return true; }
        }
        return false;
    }

    internal double EffectiveSweepProgress => !CanAnimateSweep ? 1d : (_awaitingSeriesDraw ? 0d : Math.Clamp(SweepProgress, 0d, 1d));

    internal double CurrentRemainingEntranceDelayMs()
    {
        if (_entranceDelayMs <= 0 || _entranceStartTimestamp <= 0) { return 0; }
        var remaining = _entranceDelayMs - System.Diagnostics.Stopwatch.GetElapsedTime(_entranceStartTimestamp).TotalMilliseconds;
        if (remaining <= 0)
        {
            _entranceDelayMs = 0;
            _entranceStartTimestamp = 0;
            return 0;
        }
        return remaining;
    }

    internal void RequestEntranceSweep(double delayMs = 0)
    {
        _hiddenWithWindow = false;
        if (ColorStateAssist.GetReduceMotion(this) || !SystemParameters.ClientAreaAnimation)
        {
            _awaitingSeriesDraw = false;
            _pendingDelayMs = 0;
            _sweepRequestTimestamp = 0;
            _entranceDelayMs = 0;
            _entranceStartTimestamp = 0;
            StopRangeSweep();
            return;
        }
        _awaitingSeriesDraw = true;
        _pendingDelayMs = Math.Max(0, delayMs);
        _sweepRequestTimestamp = _pendingDelayMs > 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        _entranceDelayMs = _pendingDelayMs;
        _entranceStartTimestamp = _sweepRequestTimestamp;
        StopRangeSweep();
        InvalidateVisual();
        TryStartPendingSweep();
    }

    private void TryStartPendingSweep()
    {
        if (!_awaitingSeriesDraw) { return; }
        if (!CanAnimateSweep)
        {
            if (ColorStateAssist.GetReduceMotion(this) || !SystemParameters.ClientAreaAnimation)
            {
                _awaitingSeriesDraw = false;
                _pendingDelayMs = 0;
                _sweepRequestTimestamp = 0;
                _entranceDelayMs = 0;
                _entranceStartTimestamp = 0;
            }
            return;
        }
        if (!HasRenderableSeriesData()) { return; }
        var pendingRemaining = _pendingDelayMs > 0 && _sweepRequestTimestamp > 0
            ? Math.Max(0, _pendingDelayMs - System.Diagnostics.Stopwatch.GetElapsedTime(_sweepRequestTimestamp).TotalMilliseconds)
            : _pendingDelayMs;
        var remainingDelay = Math.Max(CurrentRemainingEntranceDelayMs(), pendingRemaining);
        _awaitingSeriesDraw = false;
        _pendingDelayMs = 0;
        _sweepRequestTimestamp = 0;
        _healthDrewEmpty = Mode == "Health" && (Intervals is null || Intervals.Count == 0);
        PlayRangeSweep(remainingDelay);
    }

    internal static DoubleAnimationUsingKeyFrames CreateSweepTimeline(double delayMs, double durationMs = SweepDurationMilliseconds)
    {
        KeyTime At(double ms) => KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs + ms));
        return new DoubleAnimationUsingKeyFrames
        {
            FillBehavior = FillBehavior.Stop,
            Duration = TimeSpan.FromMilliseconds(delayMs + durationMs),
            KeyFrames =
            {
                new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new DiscreteDoubleKeyFrame(0, At(0)),
                new EasingDoubleKeyFrame(1, At(durationMs), new QuadraticEase { EasingMode = EasingMode.EaseOut }),
            },
        };
    }

    internal void PlayRangeSweep(double delayMs = 0)
    {
        if (!CanAnimateSweep)
        {
            StopRangeSweep();
            return;
        }
        LineMotion.ReleaseTarget(this, SweepProgressProperty);
        if (_sweepClock is not null)
        {
            ApplyAnimationClock(SweepProgressProperty, null);
            _sweepClock.Controller?.Remove();
            _sweepClock = null;
        }
        AnimationTimeline timeline = delayMs > 0
            ? CreateSweepTimeline(delayMs, SweepDurationMilliseconds)
            : new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(SweepDurationMilliseconds)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };
        Timeline.SetDesiredFrameRate(timeline, 60);
        var clock = timeline.CreateClock();
        _sweepClock = clock;
        clock.Completed += (_, _) =>
        {
            if (ReferenceEquals(_sweepClock, clock))
            {
                ApplyAnimationClock(SweepProgressProperty, null);
                clock.Controller?.Remove();
                _sweepClock = null;
                InvalidateVisual();
            }
        };
        ApplyAnimationClock(SweepProgressProperty, clock, HandoffBehavior.SnapshotAndReplace);
        clock.Controller!.Begin();
        clock.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
        InvalidateVisual();
    }

    internal void StopRangeSweep()
    {
        if (_sweepClock is not null)
        {
            ApplyAnimationClock(SweepProgressProperty, null);
            _sweepClock.Controller?.Remove();
            _sweepClock = null;
        }
        if (DependencyPropertyHelper.GetValueSource(this, SweepProgressProperty).IsAnimated)
        {
            ApplyAnimationClock(SweepProgressProperty, null);
            BeginAnimation(SweepProgressProperty, null);
        }
        InvalidateVisual();
    }
    private void QueuePulse()
    {
        if (!IsLoaded || _pulsePending is not null) { return; }
        // Lower than DataBind/Render: all property notifications in this observation settle first.
        // No drawing/animation mutation is performed inside FrameworkElement.OnRender.
        _pulsePending = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePulse));
    }
    private void UpdatePulse()
    {
        _pulsePending = null;
        var health = Mode == "Health" ? Intervals?.ToArray() ?? [] : [];
        if (Mode != "Health" && ActualWidth >= 2 && ActualHeight >= 2)
        {
            EnsureTrendDataPrepared();
        }
        var primary = Mode == "Health" ? [] : _visiblePoints;
        var secondary = Mode == "Health" ? [] : _visibleSecondary;
        var previousEnd = _beforeEnd;
        _pulseClock.Stop(); _pulseLayer.Children.Clear();
        if (_pulseReady && _pulseLifetime?.Animated == true && ActualWidth >= 2 && ActualHeight >= 2)
        {
            var red = Brush(Mode == "Health" ? "Semantic.Brush.StatusFailed" : "Semantic.Brush.Accent", "#E11D48").CloneCurrentValue(); red.Freeze();
            var hasChanges = false;
            using (var drawing = _pulseLayer.Open())
            {
                if (Mode == "Health")
                {
                    // A moving wall-clock alone is not a new health observation.
                    if (DataAt is not null && _beforeRecord is not null && (DataAt > _beforeRecord || EndAt == previousEnd))
                    {
                        var spans = DataChangeRegions.Health(_beforeHealth, health, previousEnd, EndAt - Duration, EndAt);
                        hasChanges = spans.Count > 0; DrawHealthPulse(drawing, spans, red);
                    }
                }
                else if (!_plot.IsEmpty)
                {
                    var first = DataChangeRegions.Trend(_beforePrimary, primary, _beforeRecord, DataAt);
                    var second = DataChangeRegions.Trend(_beforeSecondary, secondary, _beforeRecord, DataAt);
                    hasChanges = first.Any(value => value) || second.Any(value => value);
                    if (hasChanges)
                    {
                        drawing.PushClip(new RectangleGeometry(_plot));
                        DrawTrendPulse(drawing, primary, first, red); DrawTrendPulse(drawing, secondary, second, red);
                        drawing.Pop();
                    }
                }
            }
            if (hasChanges && _pulseLayer.Children.Count > 0)
            {
                PulseGeometryBuilds++;
                _pulseClock.Start(_pulseLayer, DrawingGroup.OpacityProperty,
                    new DoubleAnimation(1, 0, DataChangePulse.Length) { FillBehavior = FillBehavior.Stop }, () => _pulseLayer.Children.Clear());
            }
        }
        _beforePrimary = primary; _beforeSecondary = secondary; _beforeHealth = health;
        _beforeEnd = EndAt; _beforeRecord = DataAt; _pulseReady = !IsLoading;
    }
    private void DrawTrendPulse(DrawingContext drawing, IReadOnlyList<PlotPoint> points, bool[] changed, System.Windows.Media.Brush red)
    {
        if (!changed.Any(value => value)) { return; }
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            for (var index = 0; index < points.Count; index++)
            {
                var point = points[index]; if (!Valid(point.Value)) { continue; }
                var connects = index > 0 && CanConnect(points[index - 1], point, ResourceTrendBuffer.MaximumGap);
                if (connects && (changed[index] || changed[index - 1]))
                { path.BeginFigure(Project(points[index - 1]), false, false); path.LineTo(Project(point), true, false); }
                else if (changed[index] && !connects)
                { var at = Project(point); drawing.DrawRectangle(red, null, new Rect(at.X - 1.6, at.Y - 1.6, 3.2, 3.2)); }
            }
        }
        geometry.Freeze();
        var pen = new Pen(red, 2.15)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        drawing.DrawGeometry(null, pen, geometry);
        Point Project(PlotPoint point) => new(_plot.Left + Position(point.At) / ActualWidth * _plot.Width,
            _plot.Bottom - (point.Value!.Value - _renderScale.Minimum) / (_renderScale.Maximum - _renderScale.Minimum) * _plot.Height);
    }
    private void DrawHealthPulse(DrawingContext drawing, IReadOnlyList<HealthInterval> changed, System.Windows.Media.Brush red)
    {
        var height = Math.Max(12, ActualHeight - 30); var count = Math.Max(1, (int)(ActualWidth / 9)); var stride = ActualWidth / count;
        var cellWidth = Math.Max(1, stride - 3);
        var pen = new Pen(red, 1); pen.Freeze();
        var from = EndAt - Duration;
        var seconds = Math.Max(1e-6, Duration.TotalSeconds);
        foreach (var span in changed)
        {
            if (span.State == "failed")
            {
                var u0 = Math.Max(0, (span.From - from).TotalSeconds / seconds * count);
                var u1 = Math.Min(count, (span.To - from).TotalSeconds / seconds * count);
                if (u1 <= u0) { continue; }
                var first = Math.Clamp((int)u0, 0, count - 1); var last = Math.Clamp((int)Math.Ceiling(u1) - 1, 0, count - 1);
                for (var index = first; index <= last; index++)
                {
                    drawing.DrawRectangle(red, null, new Rect(index * stride, 0, cellWidth, height));
                }
                continue;
            }
            var left = Math.Max(0, Position(span.From)); var right = Math.Min(ActualWidth, Position(span.To));
            if (right <= left) { continue; }
            for (var index = 0; index < count; index++)
            {
                var x = Math.Max(index * stride, left); var to = Math.Min(index * stride + cellWidth, right);
                if (to <= x) { continue; }
                var rect = new Rect(x, 0, to - x, height);
                if (span.State != "unknown") { drawing.DrawRectangle(red, null, rect); }
                else
                {
                    // Unknown stays hatched, never a red solid block pretending to be a fault.
                    drawing.PushClip(new RectangleGeometry(rect));
                    for (double y = -8; y < height + 8; y += 8)
                    { drawing.DrawLine(pen, new Point(index * stride, y + 6), new Point(index * stride + cellWidth, y)); }
                    drawing.Pop();
                }
            }
        }
    }
}
