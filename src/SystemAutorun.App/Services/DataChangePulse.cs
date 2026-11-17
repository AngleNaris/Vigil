using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace SystemAutorun.App.Services;

/// <summary>Opt-in feedback for observation changes, not rolling-text frames or elapsed clocks.</summary>
public static class DataChangePulse
{
    internal static readonly Duration Length = new(TimeSpan.FromMilliseconds(600));
    private static int _activeClocks, _activeWatchers;
    internal static int ActiveClocks => Volatile.Read(ref _activeClocks);
    internal static int ActiveWatchers => Volatile.Read(ref _activeWatchers);

    public static readonly DependencyProperty SuppressProperty = DependencyProperty.RegisterAttached(
        "Suppress", typeof(bool), typeof(DataChangePulse),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static void SetSuppress(DependencyObject owner, bool value) => owner.SetValue(SuppressProperty, value);
    public static bool GetSuppress(DependencyObject owner) => (bool)owner.GetValue(SuppressProperty);
    public static readonly DependencyProperty ContextProperty = DependencyProperty.RegisterAttached(
        "Context", typeof(string), typeof(DataChangePulse), new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.Inherits, (owner, _) => (owner.GetValue(LifetimeProperty) as Lifetime)?.Reset()));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(string), typeof(DataChangePulse), new PropertyMetadata(null, ValueChanged));
    private static readonly DependencyProperty ReadoutProperty = DependencyProperty.RegisterAttached("Readout", typeof(Readout), typeof(DataChangePulse));
    private static readonly DependencyProperty LifetimeProperty = DependencyProperty.RegisterAttached("Lifetime", typeof(Lifetime), typeof(DataChangePulse));
    public static void SetContext(DependencyObject owner, string? value) => owner.SetValue(ContextProperty, value);
    public static string? GetContext(DependencyObject owner) => (string?)owner.GetValue(ContextProperty);
    public static void SetValue(DependencyObject owner, string? value) => owner.SetValue(ValueProperty, value);
    public static string? GetValue(DependencyObject owner) => (string?)owner.GetValue(ValueProperty);
    private static void ValueChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not TextBlock text) { return; }
        if (text.GetValue(ReadoutProperty) is not Readout readout)
        { readout = new Readout(text); text.SetValue(ReadoutProperty, readout); }
        readout.Queue();
    }

    internal static Lifetime Watch(FrameworkElement owner, Action reset)
    {
        var lifetime = new Lifetime(owner, reset); owner.SetValue(LifetimeProperty, lifetime);
        if (owner.IsLoaded) { owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(lifetime.Activate)); }
        return lifetime;
    }

    internal sealed class Lifetime
    {
        private readonly FrameworkElement _owner;
        private readonly Action _reset;
        private readonly DependencyPropertyDescriptor? _reduce, _suspend;
        private Window? _window;
        private bool _active;
        internal bool Animated => _owner.IsLoaded && _owner.IsVisible && _window?.WindowState != WindowState.Minimized
            && SystemParameters.ClientAreaAnimation && !ColorStateAssist.GetReduceMotion(_owner) && !LineMotion.GetIsSuspended(_owner)
            && !GetSuppress(_owner);
        internal Lifetime(FrameworkElement owner, Action reset)
        {
            _owner = owner; _reset = reset;
            _reduce = DependencyPropertyDescriptor.FromProperty(ColorStateAssist.ReduceMotionProperty, owner.GetType());
            _suspend = DependencyPropertyDescriptor.FromProperty(LineMotion.IsSuspendedProperty, owner.GetType());
            owner.Loaded += Loaded; owner.Unloaded += Unloaded; owner.IsVisibleChanged += VisibilityChanged;
        }
        private void Loaded(object sender, RoutedEventArgs args) { if (ReferenceEquals(args.OriginalSource, _owner)) Activate(); }
        private void Unloaded(object sender, RoutedEventArgs args)
        {
            if (!ReferenceEquals(args.OriginalSource, _owner)) { return; }
            if (_active)
            {
                _active = false; Interlocked.Decrement(ref _activeWatchers); _reduce?.RemoveValueChanged(_owner, Changed); _suspend?.RemoveValueChanged(_owner, Changed);
                ThemeService.Changed -= Changed; SystemParameters.StaticPropertyChanged -= SystemChanged;
                if (_window is not null) { _window.StateChanged -= Changed; _window = null; }
            }
            Reset();
        }
        internal void Activate()
        {
            if (_active || !_owner.IsLoaded) { return; }
            _active = true; Interlocked.Increment(ref _activeWatchers); _reduce?.AddValueChanged(_owner, Changed); _suspend?.AddValueChanged(_owner, Changed);
            ThemeService.Changed += Changed; SystemParameters.StaticPropertyChanged += SystemChanged;
            _window = Window.GetWindow(_owner); if (_window is not null) { _window.StateChanged += Changed; }
            Reset();
        }
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => Reset();
        private void SystemChanged(object? sender, PropertyChangedEventArgs args)
        { if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Reset(); }
        private void Changed(object? sender, EventArgs args) => Reset();
        internal void Reset()
        {
            if (_owner.Dispatcher.CheckAccess()) { _reset(); }
            else if (!_owner.Dispatcher.HasShutdownStarted) { _owner.Dispatcher.BeginInvoke(new Action(_reset)); }
        }
    }

    // One controllable root per owner; no static render callback, timer or per-frame reprojection.
    internal sealed class ClockLease
    {
        private AnimationClock? _clock;
        private Animatable? _target;
        private DependencyProperty? _property;
        private Action? _finished;
        internal bool Active => _clock is not null;
        internal void Start(Animatable target, DependencyProperty property, AnimationTimeline animation, Action finished)
        {
            Stop(); _target = target; _property = property; _finished = finished;
            _clock = animation.CreateClock(); _clock.Completed += Completed;
            Interlocked.Increment(ref _activeClocks);
            target.ApplyAnimationClock(property, _clock, HandoffBehavior.SnapshotAndReplace);
            _clock.Controller!.Begin(); _clock.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
        }
        private void Completed(object? sender, EventArgs args) => Stop();
        internal void Stop()
        {
            if (_clock is null) { return; }
            var clock = _clock; _clock = null; clock.Completed -= Completed;
            _target!.ApplyAnimationClock(_property!, null); clock.Controller?.Remove();
            _target = null; _property = null; Interlocked.Decrement(ref _activeClocks);
            var finished = _finished; _finished = null; finished?.Invoke();
        }
    }

    private sealed class Readout
    {
        private readonly TextBlock _text;
        private readonly Lifetime _lifetime;
        private readonly ClockLease _clock = new();
        private DispatcherOperation? _pending;
        private bool _ready;
        private string? _previous, _context;
        internal Readout(TextBlock text) { _text = text; _lifetime = Watch(text, Reset); }
        private void Reset() { _clock.Stop(); _ready = false; _pending?.Abort(); _pending = null; Queue(); }
        internal void Queue()
        {
            if (!_text.IsLoaded || _pending is not null) { return; }
            _pending = _text.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Update));
        }
        private void Update()
        {
            _pending = null;
            var current = GetValue(_text); var context = GetContext(_text);
            var changed = _ready && context == _context && !string.IsNullOrEmpty(context)
                && _previous?.Any(char.IsDigit) == true && current != _previous;
            _previous = current; _context = context; _ready = true;
            if (!changed || !_lifetime.Animated || _text.Foreground is not SolidColorBrush) { return; }
            _clock.Stop();
            var original = _text.Foreground; var normal = ((SolidColorBrush)original).Color;
            var owned = new SolidColorBrush(normal); _text.SetCurrentValue(TextBlock.ForegroundProperty, owned);
            var red = (_text.TryFindResource("Semantic.Brush.Accent") as SolidColorBrush)?.Color ?? Color.FromRgb(225, 29, 72);
            _clock.Start(owned, SolidColorBrush.ColorProperty, new ColorAnimation(red, normal, Length) { FillBehavior = FillBehavior.Stop }, () =>
            {
                if (ReferenceEquals(_text.Foreground, owned)) { _text.SetCurrentValue(TextBlock.ForegroundProperty, original); }
            });
        }
    }
}
