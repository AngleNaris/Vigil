using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using SystemAutorun.App.Services;
using Size = System.Windows.Size;

namespace SystemAutorun.App.Controls;

/// <summary>Clips a naturally measured body without scaling text; a reversed transition starts at its current height.</summary>
public sealed class DisclosurePresenter : Decorator
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(DisclosurePresenter), new PropertyMetadata(false, ExpansionChanged));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(DisclosurePresenter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure, ProgressChanged));
    private static readonly DependencyPropertyKey ChevronAnglePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ChevronAngle), typeof(double), typeof(DisclosurePresenter), new PropertyMetadata(0d));
    public static readonly DependencyProperty ChevronAngleProperty = ChevronAnglePropertyKey.DependencyProperty;

    private int _generation;
    private bool _subscribed;
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public double Progress => (double)GetValue(ProgressProperty);
    public double ChevronAngle => (double)GetValue(ChevronAngleProperty);

    static DisclosurePresenter()
    {
        ColorStateAssist.ReduceMotionProperty.OverrideMetadata(typeof(DisclosurePresenter),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits,
                (target, args) => { if (args.NewValue is true) { ((DisclosurePresenter)target).Settle(); } }));
    }

    public DisclosurePresenter()
    {
        ClipToBounds = true;
        Loaded += (_, _) =>
        {
            if (!_subscribed) { SystemParameters.StaticPropertyChanged += SystemChanged; _subscribed = true; }
            Settle();
        };
        Unloaded += (_, _) =>
        {
            if (_subscribed) { SystemParameters.StaticPropertyChanged -= SystemChanged; _subscribed = false; }
            Settle();
        };
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null) { return default; }
        Child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        return new Size(Child.DesiredSize.Width, Child.DesiredSize.Height * Progress);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, arrangeSize.Width, Child.DesiredSize.Height));
        return arrangeSize;
    }

    private static void ProgressChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var body = (DisclosurePresenter)target;
        body.SetCurrentValue(OpacityProperty, (double)args.NewValue);
        body.SetValue(ChevronAnglePropertyKey, 180 * (double)args.NewValue);
    }

    private static void ExpansionChanged(DependencyObject target, DependencyPropertyChangedEventArgs args) =>
        ((DisclosurePresenter)target).Transition();

    private void Transition()
    {
        if (!IsLoaded || !IsVisible || !SystemParameters.ClientAreaAnimation || ColorStateAssist.GetReduceMotion(this))
        { Settle(); return; }
        var from = Progress;
        var to = IsExpanded ? 1d : 0d;
        var generation = ++_generation;
        PrepareInput();
        if (Child is not null) { Child.Visibility = Visibility.Visible; }
        BeginAnimation(ProgressProperty, null);
        // A new clock may not tick until the next render pass. Keep its snapshot at the
        // displayed value, not the destination, so reversal never flashes an endpoint.
        SetValue(ProgressProperty, from);
        if (Math.Abs(from - to) < .001) { Settle(); return; }
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.Completed += (_, _) => { if (generation == _generation) { Settle(); } };
        BeginAnimation(ProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void PrepareInput()
    {
        if (!IsExpanded && IsVisible && IsKeyboardFocusWithin && TemplatedParent is Expander expander
            && expander.Template.FindName("HeaderSite", expander) is UIElement header)
        { header.Focus(); }
        IsHitTestVisible = IsExpanded;
        KeyboardNavigation.SetTabNavigation(this, IsExpanded ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
    }

    private void Settle()
    {
        ++_generation;
        BeginAnimation(ProgressProperty, null);
        var target = IsExpanded ? 1d : 0d;
        SetValue(ProgressProperty, target); SetCurrentValue(OpacityProperty, target);
        PrepareInput();
        // Hidden still measures naturally, but its descendants cannot receive input or keyboard focus.
        if (Child is not null) { Child.Visibility = IsExpanded ? Visibility.Visible : Visibility.Hidden; }
    }

    private void SystemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) { return; }
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) { _ = Dispatcher.BeginInvoke(new Action(() => { if (_subscribed) { Settle(); } })); }
            return;
        }
        if (!SystemParameters.ClientAreaAnimation) { Settle(); }
    }
}
