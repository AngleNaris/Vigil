using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SystemAutorun.App.Services;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace SystemAutorun.App.Controls;

/// <summary>An opaque replacement for suspended owner content; expands from center and diagonally wipes its retained drift texture.</summary>
public sealed class DialogBackdrop : Border
{
    private static readonly Duration RevealDuration = TimeSpan.FromMilliseconds(280);
    private static readonly Duration OpacityDuration = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan WipeDelay = TimeSpan.FromMilliseconds(180);
    private static readonly Duration WipeDuration = TimeSpan.FromMilliseconds(260);
    private static readonly Duration ExitWipeDuration = TimeSpan.FromMilliseconds(110);
    private static readonly Duration ExitRevealDuration = TimeSpan.FromMilliseconds(220);

    public static readonly DependencyProperty IsPresentedProperty = DependencyProperty.Register(
        nameof(IsPresented), typeof(bool), typeof(DialogBackdrop), new PropertyMetadata(false, (target, _) => ((DialogBackdrop)target).Update()));
    private static readonly DependencyProperty RevealProgressProperty = DependencyProperty.Register(
        nameof(RevealProgress), typeof(double), typeof(DialogBackdrop), new PropertyMetadata(0d, (target, _) => ((DialogBackdrop)target).ApplyRevealClip()));
    private static readonly DependencyProperty WipeProgressProperty = DependencyProperty.Register(
        nameof(WipeProgress), typeof(double), typeof(DialogBackdrop), new PropertyMetadata(0d, (target, _) => ((DialogBackdrop)target).ApplyWipeMask()));

    internal static readonly TimeSpan DriftDuration = TimeSpan.FromSeconds(7);
    public bool IsPresented { get => (bool)GetValue(IsPresentedProperty); set => SetValue(IsPresentedProperty, value); }
    private double RevealProgress { get => (double)GetValue(RevealProgressProperty); set => SetValue(RevealProgressProperty, value); }
    private double WipeProgress { get => (double)GetValue(WipeProgressProperty); set => SetValue(WipeProgressProperty, value); }

    private readonly Rectangle _texture = new() { Opacity = .15, IsHitTestVisible = false };
    private readonly RectangleGeometry _revealClip = new();
    internal Rectangle TextureVisual => _texture;
    private TranslateTransform? _drift;
    private LinearGradientBrush? _wipeMask;
    private GradientStop? _wipeStopSolid;
    private GradientStop? _wipeStopFade;
    private Window? _window;
    private bool _subscribed;
    private bool _isDrifting;
    private int _generation;
    internal event EventHandler? PresentationSettled;

    static DialogBackdrop()
    {
        ColorStateAssist.ReduceMotionProperty.OverrideMetadata(typeof(DialogBackdrop), new FrameworkPropertyMetadata(false,
            FrameworkPropertyMetadataOptions.Inherits, (target, args) =>
            {
                var backdrop = (DialogBackdrop)target;
                if (args.NewValue is true) { backdrop.Settle(); }
                else { backdrop.UpdateMotion(); }
            }));
    }

    public DialogBackdrop()
    {
        Visibility = Visibility.Collapsed;
        Opacity = 0;
        ClipToBounds = true;
        SetResourceReference(BackgroundProperty, "Semantic.Brush.WindowBackground");
        Child = _texture;
        SizeChanged += (_, _) => ApplyRevealClip();
        Loaded += (_, args) =>
        {
            if (!ReferenceEquals(args.OriginalSource, this) || _subscribed) { return; }
            _subscribed = true;
            ThemeService.Changed += ThemeChanged;
            SystemParameters.StaticPropertyChanged += SystemChanged;
            _window = Window.GetWindow(this);
            if (_window is not null) { _window.StateChanged += WindowChanged; _window.Closed += WindowClosed; }
            Dispatcher.ShutdownStarted += DispatcherShutdown;
            Rebuild(); Update();
        };
        Unloaded += (_, args) =>
        {
            if (!ReferenceEquals(args.OriginalSource, this)) { return; }
            Detach();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible && HasAnimatedProperties) { Settle(); }
            UpdateMotion();
        };
    }

    private void Update()
    {
        var from = Opacity;
        var fromReveal = RevealProgress;
        var fromWipe = WipeProgress;
        var generation = ++_generation;
        BeginAnimation(OpacityProperty, null);
        BeginAnimation(RevealProgressProperty, null);
        BeginAnimation(WipeProgressProperty, null);
        SetValue(OpacityProperty, from);
        SetValue(RevealProgressProperty, fromReveal);
        SetValue(WipeProgressProperty, fromWipe);

        if (IsPresented) { Visibility = Visibility.Visible; }
        if (IsPresented && _drift is null) { Rebuild(); }
        UpdateMotion();

        var animated = SystemParameters.ClientAreaAnimation && !ColorStateAssist.GetReduceMotion(this);
        var target = IsPresented ? 1d : 0d;
        // A dynamically inserted nested backdrop must not cover its owner before its first Loaded.
        if (IsPresented && !IsLoaded && animated)
        {
            SetValue(OpacityProperty, 0d);
            SetValue(RevealProgressProperty, 0d);
            SetValue(WipeProgressProperty, 0d);
            ApplyRevealClip();
            ApplyWipeMask();
            return;
        }
        if (!animated || !IsLoaded || !IsVisible || _window?.WindowState == WindowState.Minimized
            || (Math.Abs(from - target) < .001 && Math.Abs(fromReveal - target) < .001 && Math.Abs(fromWipe - target) < .001))
        { Settle(); return; }

        if (IsPresented)
        {
            var opacityAnimation = new DoubleAnimation(from, 1d, OpacityDuration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd };
            var revealAnimation = new DoubleAnimation(fromReveal, 1d, RevealDuration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd };
            var wipeAnimation = new DoubleAnimation(fromWipe, 1d, WipeDuration)
            {
                BeginTime = fromWipe <= 0.01 ? WipeDelay : TimeSpan.Zero,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.HoldEnd,
            };
            wipeAnimation.Completed += (_, _) => { if (generation == _generation) { Settle(); } };
            BeginAnimation(OpacityProperty, opacityAnimation, HandoffBehavior.SnapshotAndReplace);
            BeginAnimation(RevealProgressProperty, revealAnimation, HandoffBehavior.SnapshotAndReplace);
            BeginAnimation(WipeProgressProperty, wipeAnimation, HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            var wipeAnimation = new DoubleAnimation(fromWipe, 0d, ExitWipeDuration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }, FillBehavior = FillBehavior.HoldEnd };
            var revealAnimation = new DoubleAnimation(fromReveal, 0d, ExitRevealDuration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.HoldEnd };
            var opacityAnimation = new DoubleAnimation(from, 0d, ExitRevealDuration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.HoldEnd };
            opacityAnimation.Completed += (_, _) => { if (generation == _generation) { Settle(); } };
            BeginAnimation(WipeProgressProperty, wipeAnimation, HandoffBehavior.SnapshotAndReplace);
            BeginAnimation(RevealProgressProperty, revealAnimation, HandoffBehavior.SnapshotAndReplace);
            BeginAnimation(OpacityProperty, opacityAnimation, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void Settle()
    {
        ++_generation;
        BeginAnimation(OpacityProperty, null);
        BeginAnimation(RevealProgressProperty, null);
        BeginAnimation(WipeProgressProperty, null);
        var target = IsPresented ? 1d : 0d;
        SetValue(OpacityProperty, target);
        SetValue(RevealProgressProperty, target);
        SetValue(WipeProgressProperty, target);
        Visibility = IsPresented ? Visibility.Visible : Visibility.Collapsed;
        ApplyRevealClip();
        ApplyWipeMask();
        UpdateMotion();
        PresentationSettled?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyRevealClip()
    {
        var progress = RevealProgress;
        if (progress >= 0.999 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            Clip = null;
            return;
        }
        var visibleWidth = ActualWidth * Math.Clamp(progress, 0d, 1d);
        var left = (ActualWidth - visibleWidth) * 0.5;
        _revealClip.Rect = new Rect(left, 0, visibleWidth, ActualHeight);
        if (!ReferenceEquals(Clip, _revealClip))
        {
            Clip = _revealClip;
        }
    }

    private void ApplyWipeMask()
    {
        var progress = WipeProgress;
        if (progress >= 0.999)
        {
            _texture.OpacityMask = null;
            return;
        }
        if (_wipeMask is null)
        {
            _wipeStopSolid = new GradientStop(Color.FromArgb(255, 255, 255, 255), -0.14);
            _wipeStopFade = new GradientStop(Color.FromArgb(0, 255, 255, 255), 0d);
            _wipeMask = new LinearGradientBrush(
                new GradientStopCollection { _wipeStopSolid, _wipeStopFade },
                new Point(0, 1),
                new Point(1, 0));
        }
        var edge = Math.Clamp(progress, 0d, 1d) * 1.14;
        _wipeStopSolid!.Offset = edge - 0.14;
        _wipeStopFade!.Offset = edge;
        if (!ReferenceEquals(_texture.OpacityMask, _wipeMask))
        {
            _texture.OpacityMask = _wipeMask;
        }
    }

    private void Rebuild()
    {
        if (_isDrifting)
        {
            _drift?.BeginAnimation(TranslateTransform.XProperty, null);
            _isDrifting = false;
        }
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 48, 48));
            var ink = TryFindResource("Semantic.Brush.InteractionHover") as SolidColorBrush
                ?? TryFindResource("Semantic.Brush.StatusFailed") as SolidColorBrush
                ?? Brushes.Crimson;
            var stroke = ink.CloneCurrentValue();
            stroke.Freeze();
            var pen = new Pen(stroke, 1);
            pen.Freeze();
            for (var offset = -48; offset <= 48; offset += 48)
            { context.DrawLine(pen, new Point(offset, 48), new Point(offset + 48, 0)); }
        }
        drawing.Freeze();
        _drift = new TranslateTransform();
        _texture.Fill = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 48, 48),
            ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 48, 48), Transform = _drift,
        };
    }

    private void UpdateMotion()
    {
        if (_drift is null)
        {
            _isDrifting = false;
            return;
        }
        var shouldDrift = IsPresented && IsLoaded && IsVisible && _window?.WindowState != WindowState.Minimized
            && SystemParameters.ClientAreaAnimation && !ColorStateAssist.GetReduceMotion(this);
        if (!shouldDrift)
        {
            if (_isDrifting)
            {
                _drift.BeginAnimation(TranslateTransform.XProperty, null);
                _isDrifting = false;
            }
            return;
        }
        if (_isDrifting) { return; }
        _isDrifting = true;
        var animation = new DoubleAnimation(0, 48, DriftDuration) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(animation, 60);
        _drift.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void Detach()
    {
        ++_generation;
        BeginAnimation(OpacityProperty, null);
        BeginAnimation(RevealProgressProperty, null);
        BeginAnimation(WipeProgressProperty, null);
        var target = IsPresented ? 1d : 0d;
        SetValue(OpacityProperty, target);
        SetValue(RevealProgressProperty, target);
        SetValue(WipeProgressProperty, target);
        ApplyRevealClip();
        ApplyWipeMask();
        if (_isDrifting)
        {
            _drift?.BeginAnimation(TranslateTransform.XProperty, null);
            _isDrifting = false;
        }
        ThemeService.Changed -= ThemeChanged;
        SystemParameters.StaticPropertyChanged -= SystemChanged;
        Dispatcher.ShutdownStarted -= DispatcherShutdown;
        if (_window is not null) { _window.StateChanged -= WindowChanged; _window.Closed -= WindowClosed; }
        _window = null; _subscribed = false;
    }
    private void WindowClosed(object? sender, EventArgs args) => Detach();
    private void DispatcherShutdown(object? sender, EventArgs args) => Detach();
    private void ThemeChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) { return; }
        if (Dispatcher.CheckAccess()) { Rebuild(); UpdateMotion(); }
        else { _ = Dispatcher.BeginInvoke(new Action(() => { if (_subscribed) { Rebuild(); UpdateMotion(); } })); }
    }
    private void WindowChanged(object? sender, EventArgs args)
    {
        if (_window?.WindowState == WindowState.Minimized) { Settle(); }
        else { UpdateMotion(); }
    }
    private void SystemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SystemParameters.ClientAreaAnimation) || Dispatcher.HasShutdownStarted) { return; }
        void Apply() { if (!SystemParameters.ClientAreaAnimation) { Settle(); } else { UpdateMotion(); } }
        if (Dispatcher.CheckAccess()) { Apply(); }
        else { _ = Dispatcher.BeginInvoke(new Action(() => { if (_subscribed) { Apply(); } })); }
    }
}
