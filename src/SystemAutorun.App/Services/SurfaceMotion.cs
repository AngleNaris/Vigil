using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using Color = System.Windows.Media.Color;
using MenuItem = System.Windows.Controls.MenuItem;

namespace SystemAutorun.App.Services;

/// <summary>
/// R106: template triggers set <c>SurfaceMotion.Fill</c> instead of <c>Border.Background</c>; the
/// border's real background is one owned brush whose color eases to each new fill. Reversing mid-way
/// starts from the displayed color, so hover-out and deselection are as smooth as hover-in. Reduced
/// motion (inherited <see cref="ColorStateAssist.ReduceMotionProperty"/> or the system setting) snaps.
/// </summary>
internal static class SurfaceMotion
{
    private static readonly Duration Length = new(TimeSpan.FromMilliseconds(140));
    private static readonly Duration GlyphLength = new(TimeSpan.FromMilliseconds(120));
    private static readonly Duration ReadingLength = new(TimeSpan.FromMilliseconds(200));
    internal static readonly Duration PresenceDuration = new(TimeSpan.FromMilliseconds(160));
    internal static readonly Duration ToolPresenceDuration = new(TimeSpan.FromMilliseconds(300));
    internal const double PresenceTravel = 2;

    public static readonly DependencyProperty FillProperty = DependencyProperty.RegisterAttached(
        "Fill", typeof(Brush), typeof(SurfaceMotion), new PropertyMetadata(null, FillChanged));

    private static readonly DependencyProperty OwnedProperty = DependencyProperty.RegisterAttached(
        "Owned", typeof(SolidColorBrush), typeof(SurfaceMotion), new PropertyMetadata(null));

    public static void SetFill(DependencyObject target, Brush? value) => target.SetValue(FillProperty, value);
    public static Brush? GetFill(DependencyObject target) => (Brush?)target.GetValue(FillProperty);

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.RegisterAttached(
        "Stroke", typeof(Brush), typeof(SurfaceMotion), new PropertyMetadata(null, StrokeChanged));
    private static readonly DependencyProperty StrokeOwnedProperty = DependencyProperty.RegisterAttached(
        "StrokeOwned", typeof(SolidColorBrush), typeof(SurfaceMotion), new PropertyMetadata(null));
    private static readonly DependencyProperty StrokeRestProperty = DependencyProperty.RegisterAttached(
        "StrokeRest", typeof(SolidColorBrush), typeof(SurfaceMotion), new PropertyMetadata(null));
    public static void SetStroke(DependencyObject target, Brush? value) => target.SetValue(StrokeProperty, value);
    public static Brush? GetStroke(DependencyObject target) => (Brush?)target.GetValue(StrokeProperty);

    internal static SolidColorBrush? ResolveRestingStroke(Border border)
    {
        if (GetStroke(border) is null) { return null; }
        if (border.GetValue(StrokeRestProperty) is SolidColorBrush cached) { return cached; }
        if (!FrameHoverAssist.GetIsInside(border) && GetStroke(border) is SolidColorBrush unhovered) { return unhovered; }
        return border.TryFindResource("Semantic.Brush.BorderSubtle") as SolidColorBrush ?? GetStroke(border) as SolidColorBrush;
    }

    internal static SolidColorBrush? PrepareStrokeForLineMotion(Border border)
    {
        var rest = ResolveRestingStroke(border);
        if (rest is null) { return null; }
        (border.GetValue(StrokeOwnedProperty) as SolidColorBrush)?.BeginAnimation(SolidColorBrush.ColorProperty, null);
        border.SetValue(StrokeOwnedProperty, null);
        border.SetCurrentValue(Border.BorderBrushProperty, FrameHoverAssist.GetIsInside(border) && GetStroke(border) is SolidColorBrush active ? active : rest);
        return rest;
    }

    /// <summary>R106: a page or dialog body eases in (opacity 0.4 → 1, 2 DIP rise, 160 ms) each time it
    /// becomes visible. Content is interactive throughout; reduced motion shows it immediately.</summary>
    public static readonly DependencyProperty FadeOnShowProperty = DependencyProperty.RegisterAttached(
        "FadeOnShow", typeof(bool), typeof(SurfaceMotion), new PropertyMetadata(false, FadeOnShowChanged));

    public static void SetFadeOnShow(DependencyObject target, bool value) => target.SetValue(FadeOnShowProperty, value);
    public static bool GetFadeOnShow(DependencyObject target) => (bool)target.GetValue(FadeOnShowProperty);

    private static void FadeOnShowChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) { return; }
        element.IsVisibleChanged -= Reveal; element.Loaded -= RevealLoaded;
        if (args.NewValue is true)
        {
            element.IsVisibleChanged += Reveal; element.Loaded += RevealLoaded;
            // Set from an already-raised Loaded (WindowTheme does this for dialogs): play now.
            if (element.IsLoaded) { Play(element); }
        }
    }

    private static void RevealLoaded(object sender, RoutedEventArgs args)
    { if (ReferenceEquals(args.OriginalSource, sender)) { Play((FrameworkElement)sender); } }

    private static void Reveal(object sender, DependencyPropertyChangedEventArgs args)
    { if (args.NewValue is true) { Play((FrameworkElement)sender); } }

    private static readonly DependencyProperty FadeVersionProperty = DependencyProperty.RegisterAttached(
        "FadeVersion", typeof(int), typeof(SurfaceMotion), new PropertyMetadata(0));

    private static void Play(FrameworkElement element)
    {
        if (!element.IsVisible) { return; }
        var shift = element.RenderTransform as TranslateTransform;
        if (shift is null || shift.IsFrozen) { element.RenderTransform = shift = new TranslateTransform(); }
        if (!SystemParameters.ClientAreaAnimation || ColorStateAssist.GetReduceMotion(element))
        {
            element.BeginAnimation(UIElement.OpacityProperty, null); shift.BeginAnimation(TranslateTransform.YProperty, null);
            return;
        }
        var reveal = PresenceDuration;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var version = (int)element.GetValue(FadeVersionProperty) + 1;
        element.SetValue(FadeVersionProperty, version);
        var opacity = new DoubleAnimation(0.4, 1, reveal) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        opacity.Completed += (_, _) =>
        {
            if ((int)element.GetValue(FadeVersionProperty) != version) { return; }
            element.BeginAnimation(UIElement.OpacityProperty, null);
            shift.BeginAnimation(TranslateTransform.YProperty, null);
        };
        element.BeginAnimation(UIElement.OpacityProperty, opacity);
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(PresenceTravel, 0, reveal) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    private static void FillChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        // Border in a template; a Control whose third-party template binds its own Background (MenuItem).
        if (target is not (Border or Control)) { return; }
        var property = target is Border ? Border.BackgroundProperty : Control.BackgroundProperty;
        var element = (FrameworkElement)target;
        var from = (target.GetValue(property) as SolidColorBrush)?.Color;
        (target.GetValue(OwnedProperty) as SolidColorBrush)?.BeginAnimation(SolidColorBrush.ColorProperty, null);
        target.SetValue(OwnedProperty, null);
        // At rest the element shows the shared theme brush itself, so a theme switch recolors it live.
        target.SetValue(property, args.NewValue);
        if (args.NewValue is not SolidColorBrush next || from is not { } start || start == next.Color
            || !element.IsLoaded || !SystemParameters.ClientAreaAnimation || ColorStateAssist.GetReduceMotion(element))
        { return; }
        // Only while easing does the element hold a private brush; it hands back to the shared one at the end.
        var owned = new SolidColorBrush(start);
        target.SetValue(OwnedProperty, owned);
        target.SetValue(property, owned);
        var animation = FillAnimation(start, next.Color);
        animation.Completed += (_, _) =>
        {
            if (!ReferenceEquals(target.GetValue(OwnedProperty), owned)) { return; }
            target.SetValue(OwnedProperty, null);
            target.SetValue(property, GetFill(target));
        };
        owned.BeginAnimation(SolidColorBrush.ColorProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void StrokeChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Border border) { return; }
        var inside = FrameHoverAssist.GetIsInside(border);
        if (args.NewValue is SolidColorBrush restCandidate && !inside)
        {
            border.SetValue(StrokeRestProperty, restCandidate);
        }
        if (inside)
        {
            LineMotion.ReleaseTarget(border, Border.BorderBrushProperty);
        }
        var from = (border.BorderBrush as SolidColorBrush)?.Color
            ?? (target.GetValue(StrokeOwnedProperty) as SolidColorBrush)?.Color
            ?? ResolveRestingStroke(border)?.Color;
        (target.GetValue(StrokeOwnedProperty) as SolidColorBrush)?.BeginAnimation(SolidColorBrush.ColorProperty, null);
        target.SetValue(StrokeOwnedProperty, null);
        border.SetCurrentValue(Border.BorderBrushProperty, args.NewValue);
        if (args.NewValue is not SolidColorBrush next || from is not { } start || start == next.Color
            || !border.IsLoaded || !SystemParameters.ClientAreaAnimation || ColorStateAssist.GetReduceMotion(border)) { return; }
        var owned = new SolidColorBrush(start);
        target.SetValue(StrokeOwnedProperty, owned); border.SetCurrentValue(Border.BorderBrushProperty, owned);
        var animation = FillAnimation(start, next.Color);
        animation.Completed += (_, _) =>
        {
            if (!ReferenceEquals(target.GetValue(StrokeOwnedProperty), owned)) { return; }
            target.SetValue(StrokeOwnedProperty, null); border.SetCurrentValue(Border.BorderBrushProperty, GetStroke(target));
        };
        owned.BeginAnimation(SolidColorBrush.ColorProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    internal static ColorAnimation FillAnimation(Color start, Color end)
    {
        // Transparent is transparent WHITE in WPF. Interpolating its hidden RGB makes dark hovers flash gray.
        if (start.A == 0) { start = Color.FromArgb(0, end.R, end.G, end.B); }
        if (end.A == 0) { end = Color.FromArgb(0, start.R, start.G, start.B); }
        return new ColorAnimation(start, end, Length)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
    }

    private static readonly DependencyProperty PresentedKeyProperty = DependencyProperty.RegisterAttached(
        "PresentedKey", typeof(object), typeof(SurfaceMotion), new PropertyMetadata(null));
    private static readonly DependencyProperty TransitionQueuedProperty = DependencyProperty.RegisterAttached(
        "TransitionQueued", typeof(bool), typeof(SurfaceMotion), new PropertyMetadata(false));

    // CollectionView resets can report A → null → A in one binding turn. Only a settled identity change enters.
    public static readonly DependencyProperty TransitionKeyProperty = DependencyProperty.RegisterAttached(
        "TransitionKey", typeof(object), typeof(SurfaceMotion), new PropertyMetadata(null, (target, args) =>
        {
            if (target is not FrameworkElement element) { return; }
            if (!Animated(element)) { element.SetValue(PresentedKeyProperty, args.NewValue); return; }
            if ((bool)element.GetValue(TransitionQueuedProperty)) { return; }
            element.SetValue(TransitionQueuedProperty, true);
            element.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() =>
            {
                element.SetValue(TransitionQueuedProperty, false);
                var key = GetTransitionKey(element);
                var previousKey = element.GetValue(PresentedKeyProperty);
                if (Equals(previousKey, key)) { return; }
                element.SetValue(PresentedKeyProperty, key);
                if (Animated(element) && (previousKey is not null || !LineMotion.HasPlayingAncestor(element)))
                {
                    LineMotion.Transition(element);
                }
            }));
        }));
    public static void SetTransitionKey(DependencyObject target, object? value) => target.SetValue(TransitionKeyProperty, value);
    public static object? GetTransitionKey(DependencyObject target) => target.GetValue(TransitionKeyProperty);

    private static readonly DependencyProperty ActiveProperty = DependencyProperty.RegisterAttached(
        "Active", typeof(DoubleAnimation), typeof(SurfaceMotion), new PropertyMetadata(null));

    private static bool Animated(FrameworkElement element) => element.IsLoaded && element.IsVisible && !LineMotion.GetIsSuspended(element)
        && SystemParameters.ClientAreaAnimation && !ColorStateAssist.GetReduceMotion(element);

    /// <summary>Fades a template part (a checkbox glyph) to the value set by a trigger, reversible mid-way.</summary>
    public static readonly DependencyProperty TargetOpacityProperty = DependencyProperty.RegisterAttached(
        "TargetOpacity", typeof(double), typeof(SurfaceMotion), new PropertyMetadata(0d, TargetOpacityChanged));

    public static void SetTargetOpacity(DependencyObject target, double value) => target.SetValue(TargetOpacityProperty, value);
    public static double GetTargetOpacity(DependencyObject target) => (double)target.GetValue(TargetOpacityProperty);

    private static void TargetOpacityChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) { return; }
        var from = element.Opacity;
        var to = (double)args.NewValue;
        element.Opacity = to; // The base value stays final; the Stop animation only covers the transition.
        Run(element, UIElement.OpacityProperty, from, to, GlyphLength);
    }

    /// <summary>Replaces <c>RangeBase.Value</c> on a usage meter: each new reading eases from the shown value.</summary>
    public static readonly DependencyProperty MeterValueProperty = DependencyProperty.RegisterAttached(
        "MeterValue", typeof(double), typeof(SurfaceMotion), new PropertyMetadata(0d, MeterValueChanged));

    public static void SetMeterValue(DependencyObject target, double value) => target.SetValue(MeterValueProperty, value);
    public static double GetMeterValue(DependencyObject target) => (double)target.GetValue(MeterValueProperty);

    private static void MeterValueChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not RangeBase meter) { return; }
        var from = meter.Value;
        var to = (double)args.NewValue;
        meter.Value = to;
        Run(meter, RangeBase.ValueProperty, from, to, ReadingLength);
    }

    /// <summary>Replaces <c>TextBlock.Text</c> on a live reading: numbers count to the new value when the
    /// text shape is unchanged ("37%" → "42%"); a new shape or unit ("--", MB → GB) switches at once.</summary>
    public static readonly DependencyProperty RollingTextProperty = DependencyProperty.RegisterAttached(
        "RollingText", typeof(string), typeof(SurfaceMotion), new PropertyMetadata(null, RollingTextChanged));

    private static readonly DependencyProperty RollProgressProperty = DependencyProperty.RegisterAttached(
        "RollProgress", typeof(double), typeof(SurfaceMotion), new PropertyMetadata(1d, RollProgressChanged));

    private static readonly DependencyProperty RollProperty = DependencyProperty.RegisterAttached(
        "Roll", typeof(TextRoll), typeof(SurfaceMotion), new PropertyMetadata(null));

    public static void SetRollingText(DependencyObject target, string? value) => target.SetValue(RollingTextProperty, value);
    public static string? GetRollingText(DependencyObject target) => (string?)target.GetValue(RollingTextProperty);

    private static void RollingTextChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock text) { return; }
        var to = (string?)args.NewValue ?? string.Empty;
        var roll = Animated(text) ? TextRoll.TryCreate(text.Text, to) : null;
        text.SetValue(RollProperty, roll);
        text.SetValue(RollProgressProperty, 1d);
        if (roll is null) { Stop(text, RollProgressProperty); text.Text = to; return; }
        Run(text, RollProgressProperty, 0, 1, ReadingLength);
    }

    private static void RollProgressChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        using var timing = MotionTiming.Measure("readout.frame", target);
        if (target is TextBlock text && text.GetValue(RollProperty) is TextRoll roll)
        {
            var next = roll.At((double)args.NewValue);
            if (!string.Equals(text.Text, next, StringComparison.Ordinal)) { text.Text = next; }
        }
    }

    // The base value is already final, so a finished or superseded clock simply gives way to it.
    private static void Run(FrameworkElement element, DependencyProperty property, double from, double to, Duration length)
    {
        if (!Animated(element)) { Stop(element, property); return; }
        var animation = new DoubleAnimation(from, to, length)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        if (property == RollProgressProperty) { Timeline.SetDesiredFrameRate(animation, 30); }
        element.SetValue(ActiveProperty, animation);
        animation.Completed += (_, _) =>
        {
            if (ReferenceEquals(element.GetValue(ActiveProperty), animation)) { Stop(element, property); }
        };
        element.BeginAnimation(property, animation);
    }

    private static void Stop(FrameworkElement element, DependencyProperty property)
    {
        element.ClearValue(ActiveProperty);
        element.BeginAnimation(property, null);
    }

    private sealed class TextRoll
    {
        private readonly string _target;
        private readonly string[] _gaps;
        private readonly double[] _from, _to;
        private readonly int[] _decimals;

        private TextRoll(string target, string[] gaps, double[] from, double[] to, int[] decimals)
        { _target = target; _gaps = gaps; _from = from; _to = to; _decimals = decimals; }

        private static readonly Regex NumberPattern = new(@"\d+(?:\.\d+)?", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static Regex Number() => NumberPattern;

        internal static TextRoll? TryCreate(string from, string to)
        {
            var before = Number().Matches(from);
            var after = Number().Matches(to);
            if (from == to || after.Count == 0 || before.Count != after.Count
                || Number().Replace(from, "#") != Number().Replace(to, "#")) { return null; }
            return new(to, Number().Split(to),
                before.Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture)).ToArray(),
                after.Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture)).ToArray(),
                after.Select(match => match.Value.Contains('.') ? match.Length - match.Value.IndexOf('.') - 1 : 0).ToArray());
        }

        internal string At(double progress)
        {
            if (progress >= 1) { return _target; }
            var text = new StringBuilder(_gaps[0]);
            for (var index = 0; index < _to.Length; index++)
            {
                var value = _from[index] + (_to[index] - _from[index]) * progress;
                text.Append(value.ToString("F" + _decimals[index], CultureInfo.InvariantCulture)).Append(_gaps[index + 1]);
            }
            return text.ToString();
        }
    }

    /// <summary>Fades the body of a template-owned popup (ComboBox drop-down, submenu) each time it opens.</summary>
    public static readonly DependencyProperty FadePopupProperty = DependencyProperty.RegisterAttached(
        "FadePopup", typeof(bool), typeof(SurfaceMotion), new PropertyMetadata(false, FadePopupChanged));

    public static void SetFadePopup(DependencyObject target, bool value) => target.SetValue(FadePopupProperty, value);
    public static bool GetFadePopup(DependencyObject target) => (bool)target.GetValue(FadePopupProperty);

    private static void FadePopupChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is ComboBox combo)
        {
            combo.DropDownOpened -= PopupOpened;
            if (args.NewValue is true) { combo.DropDownOpened += PopupOpened; }
        }
        else if (target is MenuItem item)
        {
            item.SubmenuOpened -= SubmenuOpened;
            if (args.NewValue is true) { item.SubmenuOpened += SubmenuOpened; }
        }
    }

    private static void SubmenuOpened(object sender, RoutedEventArgs args)
    { if (ReferenceEquals(args.OriginalSource, sender)) { FadeTemplatePopup((Control)sender); } }

    private static void PopupOpened(object? sender, EventArgs args) => FadeTemplatePopup((Control)sender!);

    private static void FadeTemplatePopup(Control owner)
    {
        // A popup without its own alpha channel would show black through the partly transparent body.
        if (owner.Template?.FindName("PART_Popup", owner) is Popup { AllowsTransparency: true, Child: FrameworkElement body })
        { SetFadeOnShow(body, true); }
    }
}