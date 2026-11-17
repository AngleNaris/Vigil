using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace SystemAutorun.App.Services;

/// <summary>Tracks a composite frame without adding a hit surface or moving keyboard focus.</summary>
internal static class FrameHoverAssist
{
    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached(
        "Track", typeof(bool), typeof(FrameHoverAssist), new PropertyMetadata(false, TrackChanged));
    public static readonly DependencyProperty InnermostProperty = DependencyProperty.RegisterAttached(
        "Innermost", typeof(bool), typeof(FrameHoverAssist), new PropertyMetadata(false));
    private static readonly DependencyPropertyKey IsInsidePropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsInside", typeof(bool), typeof(FrameHoverAssist), new PropertyMetadata(false));
    public static readonly DependencyProperty IsInsideProperty = IsInsidePropertyKey.DependencyProperty;
    private static readonly DependencyProperty HookProperty = DependencyProperty.RegisterAttached(
        "Hook", typeof(Hook), typeof(FrameHoverAssist));
    private static readonly ConditionalWeakTable<Window, WindowRouter> Routers = new();

    public static void SetTrack(DependencyObject target, bool value) => target.SetValue(TrackProperty, value);
    public static bool GetTrack(DependencyObject target) => (bool)target.GetValue(TrackProperty);
    public static void SetInnermost(DependencyObject target, bool value) => target.SetValue(InnermostProperty, value);
    public static bool GetInnermost(DependencyObject target) => (bool)target.GetValue(InnermostProperty);
    public static bool GetIsInside(DependencyObject target) => (bool)target.GetValue(IsInsideProperty);

    private static void TrackChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) { return; }
        (target.GetValue(HookProperty) as Hook)?.Dispose();
        target.SetValue(HookProperty, args.NewValue is true ? new Hook(element) : null);
    }

    internal static DependencyObject? NearestFrame(DependencyObject? element)
    {
        while (element is not null)
        {
            if (GetTrack(element) && GetInnermost(element)) { return element; }
            element = element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private sealed class WindowRouter
    {
        private readonly Window _window;
        private readonly List<Hook> _hooks = [];
        private bool _attached;

        public WindowRouter(Window window) => _window = window;

        public void Register(Hook hook)
        {
            if (!_hooks.Contains(hook)) { _hooks.Add(hook); }
            if (!_attached)
            {
                _attached = true;
                _window.PreviewMouseMove += OnPreviewMouseMove;
                _window.MouseLeave += OnMouseLeave;
                _window.StateChanged += OnStateChanged;
            }
        }

        public void Unregister(Hook hook)
        {
            _hooks.Remove(hook);
            if (_hooks.Count == 0 && _attached)
            {
                _attached = false;
                _window.PreviewMouseMove -= OnPreviewMouseMove;
                _window.MouseLeave -= OnMouseLeave;
                _window.StateChanged -= OnStateChanged;
            }
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs args)
        {
            if (_window.WindowState == WindowState.Minimized)
            {
                ClearAll();
                return;
            }
            var innermost = NearestFrame(args.OriginalSource as DependencyObject);
            for (var index = 0; index < _hooks.Count; index++)
            {
                _hooks[index].Evaluate(args, innermost);
            }
        }

        private void OnMouseLeave(object sender, MouseEventArgs args) => ClearAll();

        private void OnStateChanged(object? sender, EventArgs args)
        {
            if (_window.WindowState == WindowState.Minimized) { ClearAll(); }
        }

        private void ClearAll()
        {
            for (var index = 0; index < _hooks.Count; index++)
            {
                _hooks[index].SetInside(false);
            }
        }
    }

    private sealed class Hook : IDisposable
    {
        private readonly FrameworkElement _frame;
        private Window? _window;
        private WindowRouter? _router;

        internal Hook(FrameworkElement frame)
        {
            _frame = frame;
            frame.Loaded += Loaded;
            frame.Unloaded += Unloaded;
            frame.IsVisibleChanged += VisibilityChanged;
            frame.IsEnabledChanged += EnabledChanged;
            if (frame.IsLoaded) { Attach(); }
        }

        private void Loaded(object sender, RoutedEventArgs args) { if (ReferenceEquals(args.OriginalSource, _frame)) { Attach(); } }
        private void Unloaded(object sender, RoutedEventArgs args) { if (ReferenceEquals(args.OriginalSource, _frame)) { Detach(); } }
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) { if (!_frame.IsVisible) { SetInside(false); } }
        private void EnabledChanged(object sender, DependencyPropertyChangedEventArgs args) { if (!_frame.IsEnabled) { SetInside(false); } }

        private void Attach()
        {
            var window = Window.GetWindow(_frame);
            if (window == _window) { return; }
            Detach();
            _window = window;
            if (_window is null) { return; }
            _router = Routers.GetValue(_window, static w => new WindowRouter(w));
            _router.Register(this);
        }

        internal void Evaluate(MouseEventArgs args, DependencyObject? innermost)
        {
            if (!_frame.IsVisible || !_frame.IsEnabled)
            {
                SetInside(false);
                return;
            }
            if (GetInnermost(_frame) && !ReferenceEquals(innermost, _frame))
            {
                SetInside(false);
                return;
            }
            var width = _frame.ActualWidth;
            var height = _frame.ActualHeight;
            if (width <= 0 || height <= 0)
            {
                SetInside(false);
                return;
            }
            var point = args.GetPosition(_frame);
            var inside = point.X >= 0 && point.Y >= 0 && point.X < width && point.Y < height;
            SetInside(inside);
        }

        internal void SetInside(bool value)
        {
            if (GetIsInside(_frame) != value)
            {
                _frame.SetValue(IsInsidePropertyKey, value);
            }
        }

        private void Detach()
        {
            if (_router is not null)
            {
                _router.Unregister(this);
                _router = null;
            }
            _window = null;
            SetInside(false);
        }

        public void Dispose()
        {
            Detach();
            _frame.Loaded -= Loaded;
            _frame.Unloaded -= Unloaded;
            _frame.IsVisibleChanged -= VisibilityChanged;
            _frame.IsEnabledChanged -= EnabledChanged;
        }
    }
}
