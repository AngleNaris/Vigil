using System.Windows.Interop;
using System.Windows.Media;

namespace SystemAutorun.App.Services;

/// <summary>Shared owner-relative limits; content scrolls instead of making a tool another main window.</summary>
internal static class ToolWindowLayout
{
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(BoundsState), typeof(ToolWindowLayout), new PropertyMetadata(null));

    internal static void Apply(Window window)
    {
        if (window.Owner is null) { return; }
        if (window.GetValue(StateProperty) is not BoundsState state)
        {
            state = new BoundsState(window);
            window.SetValue(StateProperty, state);
        }
        state.Update();
    }

    private sealed class BoundsState
    {
        private readonly Window _window;
        private readonly double _minimumWidth, _minimumHeight, _maximumWidth, _maximumHeight;
        private Window? _root;
        private bool _updating;

        internal BoundsState(Window window)
        {
            _window = window;
            (_minimumWidth, _minimumHeight, _maximumWidth, _maximumHeight) = (window.MinWidth, window.MinHeight, window.MaxWidth, window.MaxHeight);
            window.Closed += Closed;
            window.StateChanged += StateChanged;
            window.IsVisibleChanged += VisibilityChanged;
        }

        internal void Update()
        {
            if (_updating || _window.Owner is not { } owner) { return; }
            while (owner.Owner is { } parent) { owner = parent; }
            if (!ReferenceEquals(owner, _root))
            {
                Unbind(); _root = owner;
                owner.SizeChanged += OwnerSizeChanged;
                owner.DpiChanged += OwnerDpiChanged;
            }
            if (owner.WindowState == WindowState.Minimized || owner.ActualWidth <= 0 || owner.ActualHeight <= 0) { return; }
            _updating = true;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(owner);
                var workArea = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(owner).Handle).WorkingArea;
                var width = Math.Min(_maximumWidth, Math.Max(1, Math.Min(owner.ActualWidth * .88, workArea.Width / dpi.DpiScaleX - 48)));
                var height = Math.Min(_maximumHeight, Math.Max(1, Math.Min(owner.ActualHeight * .84, workArea.Height / dpi.DpiScaleY - 48)));
                // Small work areas must not let the former minimum override the owner limit.
                _window.MinWidth = Math.Min(_minimumWidth, width);
                _window.MinHeight = Math.Min(_minimumHeight, height);
                _window.MaxWidth = width; _window.MaxHeight = height;
                if (!double.IsNaN(_window.Width) && _window.Width > width) { _window.Width = width; }
                if (_window.SizeToContent == SizeToContent.Manual && _window.Height > height) { _window.Height = height; }
            }
            finally { _updating = false; }
        }

        private void OwnerSizeChanged(object sender, SizeChangedEventArgs args) => Update();
        private void OwnerDpiChanged(object sender, System.Windows.DpiChangedEventArgs args) => Update();
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) { if (args.NewValue is true) { Update(); } }
        private void StateChanged(object? sender, EventArgs args)
        {
            if (_window.WindowState == WindowState.Maximized) { _window.WindowState = WindowState.Normal; }
            Update();
        }
        private void Unbind()
        {
            if (_root is null) { return; }
            _root.SizeChanged -= OwnerSizeChanged; _root.DpiChanged -= OwnerDpiChanged; _root = null;
        }
        private void Closed(object? sender, EventArgs args)
        {
            Unbind();
            _window.Closed -= Closed; _window.StateChanged -= StateChanged; _window.IsVisibleChanged -= VisibilityChanged;
            _window.ClearValue(StateProperty);
        }
    }
}
