using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SystemAutorun.App.Services;

namespace SystemAutorun.App.Controls;

public enum PresenceMotionKind { Fade, Page, Tool }

/// <summary>
/// Owns visibility, opacity and input eligibility for a presentation layer. Retires input immediately,
/// but keeps the outgoing pixels until the reversible fade finishes. Business enablement belongs to children.
/// </summary>
public sealed class PresencePanel : Grid
{
    public static readonly DependencyProperty IsPresentedProperty = DependencyProperty.Register(
        nameof(IsPresented), typeof(bool), typeof(PresencePanel),
        new PropertyMetadata(false, (target, _) => ((PresencePanel)target).Transition()));
    public static readonly DependencyProperty MotionKindProperty = DependencyProperty.Register(
        nameof(MotionKind), typeof(PresenceMotionKind), typeof(PresencePanel),
        new PropertyMetadata(PresenceMotionKind.Fade, (target, _) =>
        {
            var panel = (PresencePanel)target;
            panel.UpdateAppearance();
            if (!panel.HasAnimatedProperties) { panel.ApplyRestingVisibility(); }
        }));
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        "Progress", typeof(double), typeof(PresencePanel), new PropertyMetadata(0d,
            (target, _) => ((PresencePanel)target).UpdateAppearance()));
    internal event EventHandler? PresentationSettled;

    // A tool starts with its body measured and visible, then enters without changing its desired size.
    internal void Reveal()
    {
        if (!IsPresented || !IsLoaded || !IsVisible || HasAnimatedProperties) { return; }
        if (!SystemParameters.ClientAreaAnimation || ColorStateAssist.GetReduceMotion(this)) { Settle(); return; }
        SetValue(ProgressProperty, 0d);
        Transition();
    }

    internal void FinishPresentation() => Settle();

    private int _generation;
    private bool _subscribed;
    private bool _enterWhenReady;
    private TranslateTransform? _shift;

    public bool IsPresented { get => (bool)GetValue(IsPresentedProperty); set => SetValue(IsPresentedProperty, value); }
    public PresenceMotionKind MotionKind { get => (PresenceMotionKind)GetValue(MotionKindProperty); set => SetValue(MotionKindProperty, value); }

    private void UpdateAppearance()
    {
        var progress = (double)GetValue(ProgressProperty);
        SetCurrentValue(OpacityProperty, progress);
        if (MotionKind is PresenceMotionKind.Page or PresenceMotionKind.Tool)
        {
            if (_shift is null) { _shift = new TranslateTransform(); SetCurrentValue(RenderTransformProperty, _shift); }
            _shift.Y = 0; // The FakeUI skeleton is fixed to its final layout throughout either direction.
        }
        else if (_shift is not null) { _shift.Y = 0; }
    }

    static PresencePanel()
    {
        ColorStateAssist.ReduceMotionProperty.OverrideMetadata(typeof(PresencePanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits,
                (target, args) => { if (args.NewValue is true) { ((PresencePanel)target).Settle(); LineMotion.Stop((PresencePanel)target); } }));
    }

    public PresencePanel()
    {
        Settle();
        Loaded += (_, args) =>
        {
            if (!ReferenceEquals(args.OriginalSource, this)) { return; }
            if (!_subscribed) { SystemParameters.StaticPropertyChanged += SystemChanged; _subscribed = true; }
            Settle();
            RevealPendingPage();
        };
        SizeChanged += (_, _) => RevealPendingPage();
        Unloaded += (_, args) =>
        {
            if (!ReferenceEquals(args.OriginalSource, this)) { return; }
            if (_subscribed) { SystemParameters.StaticPropertyChanged -= SystemChanged; _subscribed = false; }
            Settle(); LineMotion.Stop(this);
        };
        // Ancestor/window hiding must not leave a clock running in a background surface.
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true) { RevealPendingPage(); return; }
            LineMotion.Stop(this);
            if (HasAnimatedProperties) { Settle(); }
        };
    }

    private void RevealPendingPage()
    {
        if (!_enterWhenReady || !IsPresented || !IsLoaded || !IsVisible) { return; }
        _enterWhenReady = false;
        // A first main-window show already owns the whole tree. A first visit to a collapsed page does not.
        if (LineMotion.HasPlayingAncestor(this)) { return; }
        StartEntrance();
    }

    private void StartEntrance()
    {
        _enterWhenReady = false;
        _enterWhenReady = !LineMotion.Play(this) && LineMotion.CanPlay(this);
    }

    private void Transition()
    {
        var from = (double)GetValue(ProgressProperty);
        if (!IsPresented) { _enterWhenReady = false; }
        else if (MotionKind == PresenceMotionKind.Page && from <= .001) { _enterWhenReady = true; }
        var to = IsPresented ? 1d : 0d;
        var generation = ++_generation;
        PrepareInput();
        if (IsPresented) { SetCurrentValue(VisibilityProperty, Visibility.Visible); }
        if (!IsLoaded || !IsVisible || !SystemParameters.ClientAreaAnimation || ColorStateAssist.GetReduceMotion(this)
            || Math.Abs(from - to) < .001)
        { Settle(); return; }
        if (IsPresented && from <= .001 && MotionKind is PresenceMotionKind.Page or PresenceMotionKind.Tool)
        {
            // One entrance owner: no parent fade/rise to dilute or move the child line/pulse sequence.
            Settle();
            StartEntrance();
            return;
        }
        BeginAnimation(ProgressProperty, null);
        // Keep the sampled value even before the replacement clock's first render tick.
        SetValue(ProgressProperty, from);
        var animation = new DoubleAnimation(from, to, MotionKind == PresenceMotionKind.Tool ? SurfaceMotion.ToolPresenceDuration : SurfaceMotion.PresenceDuration)
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.HoldEnd };
        animation.Completed += (_, _) => { if (generation == _generation) { Settle(); } };
        BeginAnimation(ProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
        // Reversing an exit continues the sampled fade; it must not restart the entrance pulse.
        LineMotion.Stop(this);
        if (IsPresented && MotionKind == PresenceMotionKind.Page)
        {
            RequestPlotSweeps(this, 120);
        }
    }

    private static void RequestPlotSweeps(DependencyObject root, double delayMs)
    {
        if (root is DashboardPlot plot)
        {
            plot.RequestEntranceSweep(delayMs);
            return;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            RequestPlotSweeps(VisualTreeHelper.GetChild(root, index), delayMs);
        }
    }

    private void PrepareInput()
    {
        if (MotionKind == PresenceMotionKind.Page) { SetCurrentValue(ZIndexProperty, IsPresented ? 1 : 0); }
        SetCurrentValue(IsHitTestVisibleProperty, IsPresented);
        SetCurrentValue(IsEnabledProperty, IsPresented);
    }

    private void Settle()
    {
        ++_generation;
        BeginAnimation(ProgressProperty, null);
        var target = IsPresented ? 1d : 0d;
        SetValue(ProgressProperty, target); UpdateAppearance();
        PrepareInput();
        ApplyRestingVisibility();
        PresentationSettled?.Invoke(this, EventArgs.Empty);
    }

    // Keep a SizeToContent tool's geometry until its final Closing guard and actual destruction.
    private void ApplyRestingVisibility() => SetCurrentValue(VisibilityProperty,
        IsPresented ? Visibility.Visible : MotionKind == PresenceMotionKind.Tool ? Visibility.Hidden : Visibility.Collapsed);

    private void SystemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) { return; }
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted)
            { _ = Dispatcher.BeginInvoke(new Action(() => { if (_subscribed && !SystemParameters.ClientAreaAnimation) { Settle(); LineMotion.Stop(this); } })); }
            return;
        }
        if (!SystemParameters.ClientAreaAnimation) { Settle(); LineMotion.Stop(this); }
    }
}
