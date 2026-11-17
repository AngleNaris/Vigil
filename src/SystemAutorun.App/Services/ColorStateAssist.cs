using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using MenuItem = System.Windows.Controls.MenuItem;

namespace SystemAutorun.App.Services;

// Row: list text plus its existing divider; Icon: borderless icon buttons; Toggle: inverted segmented choice.
public enum ColorFocusRole { None, Foreground, Border, LightBorder, DangerBorder, Caption, Row, Icon, Toggle }

/// <summary>Owns only per-control color brushes; never adds focus geometry or mutates shared resources.</summary>
public static class ColorStateAssist
{
    public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached(
        "Role", typeof(ColorFocusRole), typeof(ColorStateAssist), new PropertyMetadata(ColorFocusRole.None, RoleChanged));
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.RegisterAttached(
        "ReduceMotion", typeof(bool), typeof(ColorStateAssist),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, MotionChanged));
    public static readonly DependencyProperty IsMutedProperty = DependencyProperty.RegisterAttached(
        "IsMuted", typeof(bool), typeof(ColorStateAssist), new PropertyMetadata(false, MotionChanged));
    public static void SetIsMuted(DependencyObject target, bool value) => target.SetValue(IsMutedProperty, value);
    public static bool GetIsMuted(DependencyObject target) => (bool)target.GetValue(IsMutedProperty);
    public static readonly DependencyProperty HoverActionProperty = DependencyProperty.RegisterAttached(
        "HoverAction", typeof(bool), typeof(ColorStateAssist), new PropertyMetadata(false, HoverActionChanged));
    public static void SetHoverAction(DependencyObject target, bool value) => target.SetValue(HoverActionProperty, value);
    public static bool GetHoverAction(DependencyObject target) => (bool)target.GetValue(HoverActionProperty);
    public static readonly DependencyProperty FrameHoveredProperty = DependencyProperty.RegisterAttached(
        "FrameHovered", typeof(bool), typeof(ColorStateAssist), new PropertyMetadata(false, HoverStateChanged));
    public static void SetFrameHovered(DependencyObject target, bool value) => target.SetValue(FrameHoveredProperty, value);
    public static bool GetFrameHovered(DependencyObject target) => (bool)target.GetValue(FrameHoveredProperty);
    public static readonly DependencyProperty RestingBorderBrushProperty = DependencyProperty.RegisterAttached(
        "RestingBorderBrush", typeof(Brush), typeof(ColorStateAssist), new PropertyMetadata(null, MotionChanged));
    // An existing heading can show focus without recoloring the container's semantic content.
    public static readonly DependencyProperty CaptionBrushProperty = DependencyProperty.RegisterAttached(
        "CaptionBrush", typeof(Brush), typeof(ColorStateAssist), new PropertyMetadata(null));
    public static void SetCaptionBrush(DependencyObject target, Brush? value) => target.SetValue(CaptionBrushProperty, value);
    public static Brush? GetCaptionBrush(DependencyObject target) => (Brush?)target.GetValue(CaptionBrushProperty);
    private static readonly DependencyProperty HookProperty = DependencyProperty.RegisterAttached(
        "Hook", typeof(Hook), typeof(ColorStateAssist));

    public static void SetRole(DependencyObject target, ColorFocusRole value) => target.SetValue(RoleProperty, value);
    public static ColorFocusRole GetRole(DependencyObject target) => (ColorFocusRole)target.GetValue(RoleProperty);
    public static void SetReduceMotion(DependencyObject target, bool value) => target.SetValue(ReduceMotionProperty, value);
    public static bool GetReduceMotion(DependencyObject target) => (bool)target.GetValue(ReduceMotionProperty);
    public static void SetRestingBorderBrush(DependencyObject target, Brush? value) => target.SetValue(RestingBorderBrushProperty, value);
    public static Brush? GetRestingBorderBrush(DependencyObject target) => (Brush?)target.GetValue(RestingBorderBrushProperty);

    internal static SolidColorBrush? ResolveRestingBorderBrush(DependencyObject? target)
    {
        if (target is null) return null;
        if (GetRestingBorderBrush(target) is SolidColorBrush resting) return resting;
        if (target is Border border
            && System.Windows.Data.BindingOperations.GetBindingExpression(border, Border.BorderBrushProperty)?.ResolvedSource is DependencyObject source
            && !ReferenceEquals(source, target))
        {
            return ResolveRestingBorderBrush(source);
        }
        return (target.GetValue(HookProperty) as Hook)?.ResolveRestingBorderBrush();
    }

    private static void RoleChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Control control) return;
        (control.GetValue(HookProperty) as Hook)?.Dispose();
        var role = (ColorFocusRole)args.NewValue;
        var hook = role == ColorFocusRole.None ? null : new Hook(control, role);
        control.SetValue(HookProperty, hook);
    }

    private static void HoverActionChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Control control || control.GetValue(HookProperty) is not Hook previous) { return; }
        previous.Dispose();
        control.SetValue(HookProperty, new Hook(control, GetRole(control)));
    }

    private static void HoverStateChanged(DependencyObject target, DependencyPropertyChangedEventArgs args) =>
        (target.GetValue(HookProperty) as Hook)?.Refresh();

    private static void MotionChanged(DependencyObject target, DependencyPropertyChangedEventArgs args) =>
        (target.GetValue(HookProperty) as Hook)?.Refresh(instant: true);

    private sealed class Channel(DependencyProperty property, Brush? original)
    {
        public DependencyProperty Property { get; } = property;
        public Brush? Original { get; } = original;
        public SolidColorBrush? Owned { get; set; }
        public Color? Target { get; set; }
    }

    private sealed class Hook : IDisposable
    {
        private readonly Control _control;
        private readonly ColorFocusRole _role;
        private readonly Channel[] _channels;
        private readonly DependencyPropertyDescriptor? _tag;
        private readonly DependencyPropertyDescriptor? _mouseOver;
        private readonly DependencyPropertyDescriptor? _pressed;
        private readonly DependencyPropertyDescriptor? _highlighted;
        private readonly DependencyPropertyDescriptor? _submenuOpen;
        private readonly DependencyPropertyDescriptor? _dropDownOpen;
        private bool _active;

        public Hook(Control control, ColorFocusRole role)
        {
            _control = control; _role = role;
            DependencyProperty[] properties = role switch
            {
                ColorFocusRole.Border or ColorFocusRole.LightBorder or ColorFocusRole.DangerBorder when GetHoverAction(control)
                    => [Control.BorderBrushProperty, Control.ForegroundProperty],
                ColorFocusRole.Foreground or ColorFocusRole.Icon or ColorFocusRole.Toggle => [Control.ForegroundProperty],
                ColorFocusRole.Caption => [CaptionBrushProperty],
                ColorFocusRole.Row => [Control.ForegroundProperty, Control.BorderBrushProperty],
                _ => [Control.BorderBrushProperty]
            };
            _channels = properties.Select(property => new Channel(property, control.GetValue(property) as Brush)).ToArray();
            _tag = DependencyPropertyDescriptor.FromProperty(FrameworkElement.TagProperty, control.GetType());
            _mouseOver = DependencyPropertyDescriptor.FromProperty(UIElement.IsMouseOverProperty, control.GetType());
            if (control is ButtonBase)
            {
                _pressed = DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, control.GetType());
            }
            if (control is MenuItem)
            {
                _highlighted = DependencyPropertyDescriptor.FromProperty(MenuItem.IsHighlightedProperty, control.GetType());
                _submenuOpen = DependencyPropertyDescriptor.FromProperty(MenuItem.IsSubmenuOpenProperty, control.GetType());
            }
            else if (control is ComboBoxItem)
            {
                _highlighted = DependencyPropertyDescriptor.FromProperty(ComboBoxItem.IsHighlightedProperty, control.GetType());
            }
            else if (control is ComboBox)
            {
                _dropDownOpen = DependencyPropertyDescriptor.FromProperty(ComboBox.IsDropDownOpenProperty, control.GetType());
            }
            control.Loaded += Loaded; control.Unloaded += Unloaded;
            Refresh(instant: true);
            if (control.IsLoaded) Activate();
        }

        private void Loaded(object sender, RoutedEventArgs args)
        { if (ReferenceEquals(args.OriginalSource, _control)) { Activate(); } }
        private void Unloaded(object sender, RoutedEventArgs args)
        { if (ReferenceEquals(args.OriginalSource, _control)) { Deactivate(); } }
        private void Activate()
        {
            if (_active) return;
            _active = true;
            _control.IsKeyboardFocusWithinChanged += StateChanged;
            // TextBox's IsKeyboardFocused template trigger runs after focus-within changes.
            // Reassert the owned brush once the routed focus notification reaches the control.
            _control.GotKeyboardFocus += RoutedStateChanged; _control.LostKeyboardFocus += RoutedStateChanged;
            _control.IsEnabledChanged += StateChanged;
            _control.MouseEnter += RoutedStateChanged; _control.MouseLeave += RoutedStateChanged;
            _control.AddHandler(Selector.SelectedEvent, new RoutedEventHandler(RoutedStateChanged));
            _control.AddHandler(Selector.UnselectedEvent, new RoutedEventHandler(RoutedStateChanged));
            _control.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(RoutedStateChanged));
            _control.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(RoutedStateChanged));
            _tag?.AddValueChanged(_control, TagChanged);
            _mouseOver?.AddValueChanged(_control, MouseOverChanged);
            _pressed?.AddValueChanged(_control, TagChanged);
            _highlighted?.AddValueChanged(_control, TagChanged);
            _submenuOpen?.AddValueChanged(_control, TagChanged);
            _dropDownOpen?.AddValueChanged(_control, TagChanged);
            SystemParameters.StaticPropertyChanged += SystemChanged;
            ThemeService.Changed += ThemeChanged;
            Refresh(instant: true);
        }

        private void Deactivate()
        {
            if (!_active) return;
            _active = false;
            _control.IsKeyboardFocusWithinChanged -= StateChanged;
            _control.GotKeyboardFocus -= RoutedStateChanged; _control.LostKeyboardFocus -= RoutedStateChanged;
            _control.IsEnabledChanged -= StateChanged;
            _control.MouseEnter -= RoutedStateChanged; _control.MouseLeave -= RoutedStateChanged;
            _control.RemoveHandler(Selector.SelectedEvent, new RoutedEventHandler(RoutedStateChanged));
            _control.RemoveHandler(Selector.UnselectedEvent, new RoutedEventHandler(RoutedStateChanged));
            _control.RemoveHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(RoutedStateChanged));
            _control.RemoveHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(RoutedStateChanged));
            _tag?.RemoveValueChanged(_control, TagChanged);
            _mouseOver?.RemoveValueChanged(_control, MouseOverChanged);
            _pressed?.RemoveValueChanged(_control, TagChanged);
            _highlighted?.RemoveValueChanged(_control, TagChanged);
            _submenuOpen?.RemoveValueChanged(_control, TagChanged);
            _dropDownOpen?.RemoveValueChanged(_control, TagChanged);
            SystemParameters.StaticPropertyChanged -= SystemChanged;
            ThemeService.Changed -= ThemeChanged;
            Stop();
        }

        private void StateChanged(object sender, DependencyPropertyChangedEventArgs args) => Refresh();
        private void RoutedStateChanged(object sender, RoutedEventArgs args) => Refresh();
        private void TagChanged(object? sender, EventArgs args) => Refresh();
        private void MouseOverChanged(object? sender, EventArgs args) => Refresh();
        private void ThemeChanged(object? sender, EventArgs args)
        {
            // The palette event is process-wide; refresh on this control's own UI thread.
            if (_control.Dispatcher.CheckAccess()) { Refresh(instant: true); }
            else if (!_control.Dispatcher.HasShutdownStarted) { _control.Dispatcher.BeginInvoke(new Action(() => Refresh(instant: true))); }
        }
        private void SystemChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Refresh(instant: true);
        }

        public void Refresh(bool instant = false)
        {
            var focused = _control.IsKeyboardFocusWithin;
            var selected = _control.Tag is true || (_control switch
            {
                ListBoxItem item => item.IsSelected,
                DataGridRow row => row.IsSelected,
                TabItem item => item.IsSelected,
                MenuItem menuItem => menuItem.IsSubmenuOpen,
                ToggleButton toggle => toggle.IsChecked == true,
                _ => false
            });
            var muted = GetIsMuted(_control) || !_control.IsEnabled;
            var animate = !instant && _active && SystemParameters.ClientAreaAnimation && !GetReduceMotion(_control);
            foreach (var channel in _channels)
            {
                Apply(channel, ColorFor(channel.Property, focused, selected, muted), animate);
            }
        }

        private Color ColorFor(DependencyProperty property, bool focused, bool selected, bool muted)
        {
            var hovered = _control.IsEnabled && (_control.IsMouseOver || GetFrameHovered(_control));
            var highlighted = _control.IsEnabled && _control switch
            {
                MenuItem menuItem => menuItem.IsHighlighted,
                ComboBoxItem comboItem => comboItem.IsHighlighted,
                ComboBox comboBox => comboBox.IsDropDownOpen,
                ButtonBase button => button.IsPressed,
                _ => false
            };
            var interactiveRed = hovered || highlighted;
            var dropDownOpen = _control is ComboBox { IsDropDownOpen: true };

            if (property == Control.BorderBrushProperty && (hovered || dropDownOpen)
                && _role is (ColorFocusRole.Border or ColorFocusRole.LightBorder or ColorFocusRole.DangerBorder))
            {
                return ResourceColor(dropDownOpen ? "Semantic.Brush.InteractionActiveHover" : "Semantic.Brush.InteractionHover");
            }
            if (property == Control.ForegroundProperty)
            {
                if (!_control.IsEnabled) { return ResourceColor("Semantic.Brush.TextDisabled"); }
                if (_control is System.Windows.Controls.CheckBox)
                {
                    return ResourceColor(hovered || selected || focused ? "Semantic.Brush.TextPrimary" : "Semantic.Brush.InteractionRest");
                }
                var redFillSelected = selected && (_control.Tag is true || _control is not System.Windows.Controls.Primitives.ToggleButton);
                if (_role is not ColorFocusRole.Caption and not ColorFocusRole.Row
                    && (interactiveRed || redFillSelected)
                    && (GetHoverAction(_control) || _role is ColorFocusRole.Foreground or ColorFocusRole.Icon or ColorFocusRole.Toggle))
                {
                    // Active elements at rest keep red ink on neutral grey; hover/press flips to white ink on red fill.
                    return ResourceColor(interactiveRed ? "Semantic.Brush.InteractionOnHover" : "Semantic.Brush.InteractionOnActive");
                }
                if (GetHoverAction(_control) && _role is ColorFocusRole.Border or ColorFocusRole.LightBorder or ColorFocusRole.DangerBorder)
                {
                    var resting = _role == ColorFocusRole.LightBorder ? "PrimaryForeground"
                        : _role == ColorFocusRole.DangerBorder ? "StatusFailed" : "TextPrimary";
                    return ResourceColor("Semantic.Brush." + resting);
                }
            }
            string key;
            switch (_role)
            {
                case ColorFocusRole.Foreground or ColorFocusRole.Caption:
                    key = !_control.IsEnabled ? "TextDisabled"
                        : selected || GetIsMuted(_control) ? focused ? "InteractionSelectedFocus" : "InteractionSelected"
                        : focused ? "InteractionFocus" : "InteractionRest";
                    break;
                case ColorFocusRole.Icon:
                    key = !_control.IsEnabled ? "TextDisabled"
                        : interactiveRed ? "InteractionOnHover"
                        : selected ? "InteractionOnActive"
                        : focused ? "IconFocus" : "IconRest";
                    break;
                case ColorFocusRole.Toggle:
                    key = !_control.IsEnabled ? "TextDisabled"
                        : interactiveRed ? "InteractionOnHover"
                        : selected ? "InteractionOnActive"
                        : focused ? "IconFocus" : "IconRest";
                    break;
                case ColorFocusRole.Row when property == Control.ForegroundProperty:
                    // Hovered/pressed rows use accent-on-hover; active rows use accent-on-grey; focused-only rows shift ink without darkening the row divider.
                    key = interactiveRed ? "InteractionOnHover"
                        : selected ? "InteractionOnActive"
                        : focused ? "InteractionFocus"
                        : muted ? "RowMuted" : "RowRest";
                    break;
                case ColorFocusRole.Row:
                    key = focused ? "RowFocusBorder" : "BorderSubtle";
                    break;
                default:
                    var normal = _role switch
                    {
                        ColorFocusRole.LightBorder => "FocusAccent",
                        ColorFocusRole.DangerBorder => "StatusFailed",
                        _ => "BorderStrong"
                    };
                    if (!_control.IsEnabled) key = "BorderSubtle";
                    else if (focused) key = _role == ColorFocusRole.LightBorder ? "InteractionActiveHover" : "InteractionHover";
                    else if (GetRestingBorderBrush(_control) is SolidColorBrush resting) return resting.Color;
                    else key = normal;
                    break;
            }
            return ResourceColor("Semantic.Brush." + key);
        }

        private void Apply(Channel channel, Color color, bool animate)
        {
            if (_control.IsEnabled && (_control.IsMouseOver || GetFrameHovered(_control)))
            {
                LineMotion.ReleaseTarget(_control, channel.Property);
            }
            if (channel.Target == color && channel.Owned is not null)
            {
                if (!ReferenceEquals(_control.GetValue(channel.Property), channel.Owned)
                    && !Validation.GetHasError(_control))
                {
                    _control.SetCurrentValue(channel.Property, channel.Owned);
                }
                if (!animate) Stop(channel);
                return;
            }
            // A library focus trigger may already have changed the effective brush this tick.
            // Reverse from our displayed brush, not that new template endpoint.
            var current = channel.Owned?.Color ?? (_control.GetValue(channel.Property) as SolidColorBrush)?.Color ?? color;
            channel.Owned?.BeginAnimation(SolidColorBrush.ColorProperty, null);
            channel.Target = color;
            var brush = new SolidColorBrush(animate ? current : color);
            channel.Owned = brush;
            _control.SetCurrentValue(channel.Property, brush);
            if (animate && current != color)
            {
                var delta = Math.Max(Math.Max(Math.Abs(color.R - current.R), Math.Abs(color.G - current.G)),
                    Math.Max(Math.Abs(color.B - current.B), Math.Abs(color.A - current.A)));
                var durationMs = 90 * Math.Clamp(delta / 96d, 0.35, 1d);
                var animation = new ColorAnimation(current, color, new Duration(TimeSpan.FromMilliseconds(durationMs)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.HoldEnd
                };
                animation.Completed += (_, _) =>
                {
                    if (!ReferenceEquals(channel.Owned, brush)) return;
                    brush.Color = color;
                    brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                };
                brush.BeginAnimation(SolidColorBrush.ColorProperty, animation, HandoffBehavior.SnapshotAndReplace);
            }
        }

        private Color ResourceColor(string key) => ((SolidColorBrush)_control.FindResource(key)).Color;

        internal SolidColorBrush? ResolveRestingBorderBrush()
        {
            if (!_control.IsEnabled) return _control.TryFindResource("Semantic.Brush.BorderSubtle") as SolidColorBrush;
            if (GetRestingBorderBrush(_control) is SolidColorBrush resting) return resting;
            var key = _role switch
            {
                ColorFocusRole.Row => "BorderSubtle",
                ColorFocusRole.LightBorder => "FocusAccent",
                ColorFocusRole.DangerBorder => "StatusFailed",
                _ => "BorderStrong"
            };
            return _control.TryFindResource("Semantic.Brush." + key) as SolidColorBrush;
        }
        private void Stop()
        {
            foreach (var channel in _channels) Stop(channel);
        }

        private static void Stop(Channel channel)
        {
            channel.Owned?.BeginAnimation(SolidColorBrush.ColorProperty, null);
            if (channel.Owned is not null && channel.Target is { } color) channel.Owned.Color = color;
        }

        public void Dispose()
        {
            Deactivate();
            _control.Loaded -= Loaded; _control.Unloaded -= Unloaded;
            foreach (var channel in _channels)
            {
                if (ReferenceEquals(_control.GetValue(channel.Property), channel.Owned)) _control.SetCurrentValue(channel.Property, channel.Original);
            }
        }
    }
}
