using System.ComponentModel;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using SystemAutorun.App.Controls;

namespace SystemAutorun.App.Services;

/// <summary>
/// Explicit opt-in for tools with a fixed body. Closing handlers are synchronous guards, not cleanup.
/// Only Closed may release irreversible resources. A cancelled exit keeps the same window and model.
/// </summary>
public class ToolWindow : Window, IDialogScrimHost
{
    private PresencePanel? _surface;
    private bool _exitPending, _committing, _closed, _revealOnShow;
    private bool? _pendingResult;
    private int _generation, _commitQueuedFor = -1;
    private readonly TaskCompletionSource _closure = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal event EventHandler? PresentationChanged;
    internal bool IsPresentationActive => IsVisible && !_exitPending && !_closed;
    internal bool IsExitPending => _exitPending;
    internal Task ClosureTask => _closure.Task; // Native Closed, not a promise that view resources have finished disposing.
    internal virtual Task ResourceCleanupTask => Task.CompletedTask;
    private bool _applicationExit;
    private readonly HashSet<Window> _nestedDialogs = [];
    private readonly Dictionary<UIElement, Visibility> _coveredChildren = [];
    private DialogBackdrop? _backdrop;
    private double _restTop = double.NaN;
    private bool _shellNeedsEntry;
    private int _shellGeneration;
    private bool _watchingMotion;

    static ToolWindow()
    {
        ColorStateAssist.ReduceMotionProperty.OverrideMetadata(typeof(ToolWindow), new FrameworkPropertyMetadata(false,
            FrameworkPropertyMetadataOptions.Inherits, (target, args) =>
            { if (args.NewValue is true) { ((ToolWindow)target).SettleShell(); } }));
    }

    private void SettleShell()
    {
        ++_shellGeneration;
        BeginAnimation(OpacityProperty, null); BeginAnimation(TopProperty, null);
        Surface_Settled(_surface, EventArgs.Empty);
    }

    private void WatchMotion(bool active)
    {
        if (active == _watchingMotion) { return; }
        _watchingMotion = active;
        if (active) { SystemParameters.StaticPropertyChanged += MotionChanged; }
        else { SystemParameters.StaticPropertyChanged -= MotionChanged; }
    }

    private void MotionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SystemParameters.ClientAreaAnimation) || SystemParameters.ClientAreaAnimation) { return; }
        if (Dispatcher.CheckAccess()) { SettleShell(); }
        else if (!Dispatcher.HasShutdownStarted)
        { _ = Dispatcher.BeginInvoke(new Action(() => { if (!_closed && _watchingMotion) { SettleShell(); } })); }
    }

    internal void CloseForApplicationExit()
    {
        Dispatcher.VerifyAccess();
        _applicationExit = true;
        CancelPendingClose();
        Close();
    }

    public ToolWindow()
    {
        // Window.Opacity only composites the entire shell on WPF's supported per-pixel target.
        // On an opaque HWND it fades the drawing into the native black clear surface instead.
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true) { WatchMotion(true); _revealOnShow = true; HoldShell(); }
            else
            {
                WatchMotion(false);
                _revealOnShow = false;
                SettleShell();
                if (!_committing) { CancelPendingClose(); }
            }
        };
    }

    protected override void OnInitialized(EventArgs e)
    {
        WindowTheme.Apply(this);
        base.OnInitialized(e);
        EnsureSurface();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        WindowTheme.Apply(this);
        EnsureSurface(); // Also supports a code-created window initialized before its Content was assigned.
        HoldShell();
        // Construction alone must not retain a never-shown tool. Source creation precedes Loaded/visibility.
        if (System.Windows.Application.Current is App app) { app.TrackToolWindow(this); }
        base.OnSourceInitialized(e);
    }

    private void EnsureSurface()
    {
        // Consumers declare the shared wrapper in XAML. Reparenting Content during Initialized
        // can reenter WPF's unfinished AddLogicalChild and attach the same body to two parents.
        if (_surface is not null || Content is not PresencePanel surface) { return; }
        _surface = surface;
        _surface.Loaded += Surface_Loaded;
        _surface.IsVisibleChanged += Surface_IsVisibleChanged;
        _surface.PresentationSettled += Surface_Settled;
    }

    private void Surface_Loaded(object sender, RoutedEventArgs e) => RevealIfNeeded();
    private void Surface_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (e.NewValue is true) { RevealIfNeeded(); } }
    private void RevealIfNeeded()
    {
        if (!_revealOnShow || _shellNeedsEntry || _exitPending || _closed || _surface is not { IsLoaded: true, IsVisible: true }) { return; }
        _revealOnShow = false;
        _surface.Reveal();
    }

    private void HoldShell()
    {
        _shellNeedsEntry = true;
        if (SystemParameters.ClientAreaAnimation && !ColorStateAssist.GetReduceMotion(this))
        { BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0, Duration.Forever)); }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_shellNeedsEntry && !_exitPending && !_closed) { AnimateShell(true); RevealIfNeeded(); }
    }

    private void AnimateShell(bool entering)
    {
        var opacity = Opacity;
        var top = Top;
        var first = entering && _shellNeedsEntry;
        _shellNeedsEntry = false;
        if (first)
        {
            _restTop = (double)GetAnimationBaseValue(TopProperty);
            if (!double.IsFinite(_restTop)) { _restTop = top; }
        }
        var generation = ++_shellGeneration;
        if (!IsVisible || !SystemParameters.ClientAreaAnimation || ColorStateAssist.GetReduceMotion(this) || _applicationExit)
        { BeginAnimation(OpacityProperty, null); BeginAnimation(TopProperty, null); return; }
        var animation = new DoubleAnimation(first ? 0 : opacity, entering ? 1 : 0, SurfaceMotion.ToolPresenceDuration)
        { EasingFunction = new CubicEase { EasingMode = entering ? EasingMode.EaseOut : EasingMode.EaseIn }, FillBehavior = FillBehavior.HoldEnd };
        animation.Completed += (_, _) =>
        {
            if (entering && generation == _shellGeneration)
            { BeginAnimation(OpacityProperty, null); BeginAnimation(TopProperty, null); }
            else if (!entering && generation == _shellGeneration) { Surface_Settled(_surface, EventArgs.Empty); }
        };
        BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        if (double.IsFinite(_restTop) && double.IsFinite(top))
        {
            BeginAnimation(TopProperty, new DoubleAnimation(first ? _restTop + 8 : top, entering ? _restTop : _restTop + 8, SurfaceMotion.ToolPresenceDuration)
            { EasingFunction = new CubicEase { EasingMode = entering ? EasingMode.EaseOut : EasingMode.EaseIn }, FillBehavior = FillBehavior.HoldEnd });
        }
    }

    void IDialogScrimHost.Track(Window dialog)
    {
        if (!_nestedDialogs.Add(dialog)) { return; }
        dialog.Closed += NestedClosed;
        dialog.IsVisibleChanged += NestedVisibilityChanged;
        UpdateNestedBackdrop();
    }

    private void NestedVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateNestedBackdrop();
    private void NestedClosed(object? sender, EventArgs args)
    {
        if (sender is Window window)
        {
            window.Closed -= NestedClosed; window.IsVisibleChanged -= NestedVisibilityChanged;
            _nestedDialogs.Remove(window);
        }
        UpdateNestedBackdrop();
    }

    private void UpdateNestedBackdrop()
    {
        if (_surface is null || _closed) { return; }
        var covered = _nestedDialogs.Any(window => window.IsVisible);
        if (covered == (_backdrop?.IsPresented == true)) { return; }
        if (covered)
        {
            LineMotion.SetIsSuspended(this, true);
            LineMotion.StopTree(_surface);
            foreach (UIElement child in _surface.Children)
            {
                if (ReferenceEquals(child, _backdrop)) { continue; }
                _coveredChildren[child] = child.Visibility;
            }
            if (_backdrop is null)
            {
                _backdrop = new DialogBackdrop();
                _backdrop.PresentationSettled += (_, _) =>
                {
                    if (_closed || !_backdrop.IsPresented || !_nestedDialogs.Any(window => window.IsVisible)) { return; }
                    foreach (var child in _coveredChildren.Keys) { child.SetCurrentValue(VisibilityProperty, Visibility.Hidden); }
                };
                System.Windows.Controls.Grid.SetRowSpan(_backdrop, Math.Max(1, _surface.RowDefinitions.Count));
                System.Windows.Controls.Grid.SetColumnSpan(_backdrop, Math.Max(1, _surface.ColumnDefinitions.Count));
                _surface.Children.Add(_backdrop);
            }
            _backdrop.IsPresented = true;
        }
        else
        {
            foreach (var (child, visibility) in _coveredChildren) { child.SetCurrentValue(VisibilityProperty, visibility); }
            _coveredChildren.Clear();
            LineMotion.SetIsSuspended(this, false);
            if (_backdrop is not null) { _backdrop.IsPresented = false; }
            if (IsPresentationActive && System.Windows.Application.Current is not App { IsExiting: true })
            { LineMotion.Transition(_surface); }
        }
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // Window-level InputBindings sit outside the disabled body (for example the log tool's F5).
        if (_exitPending) { e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }

    protected sealed override void OnClosing(CancelEventArgs e)
    {
        if (_committing)
        {
            // WPF clears DialogResult after a cancelled close. Restore it while its _isClosing is true:
            // the setter cannot recursively Close, and final guards see the original result.
            if (_pendingResult is { } result) { DialogResult = result; }
            base.OnClosing(e);
            if (e.Cancel) { CancelPendingClose(); }
            return;
        }
        if (_exitPending) { e.Cancel = true; return; }
        base.OnClosing(e);
        if (e.Cancel || _closed) { return; }
        OnCloseAccepted();
        if (_surface is not { IsLoaded: true, IsVisible: true } || !SystemParameters.ClientAreaAnimation
            || _applicationExit || ColorStateAssist.GetReduceMotion(_surface) || Dispatcher.HasShutdownStarted
            || System.Windows.Application.Current is App { IsExiting: true })
        { return; } // No decorative delay: let this original Closing finish normally.

        _exitPending = true; _pendingResult = DialogResult; ++_generation;
        e.Cancel = true;
        PresentationChanged?.Invoke(this, EventArgs.Empty);
        AnimateShell(false);
        _surface.IsPresented = false; // Retire input now; completion can only queue another Close.
    }

    /// <summary>May publish a lifetime-completion task; must not dispose resources or cancel a committed workflow.</summary>
    protected virtual void OnCloseAccepted() { }

    internal void CancelPendingClose()
    {
        Dispatcher.VerifyAccess();
        if (!_exitPending || _closed) { return; }
        _exitPending = false; _pendingResult = null; ++_generation;
        if (_surface is not null) { _surface.IsPresented = true; }
        AnimateShell(true);
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Surface_Settled(object? sender, EventArgs e)
    {
        if (!_exitPending || _closed || !IsVisible || _surface is not { IsPresented: false, Opacity: 0 }
            || Dispatcher.HasShutdownStarted || _commitQueuedFor == _generation) { return; }
        if (SystemParameters.ClientAreaAnimation && !ColorStateAssist.GetReduceMotion(this) && Opacity > .001) { return; }
        var generation = _commitQueuedFor = _generation;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (_closed || !_exitPending || !IsVisible || generation != _generation) { return; }
            _committing = true;
            try { Close(); }
            finally { _committing = false; }
        }));
    }

    protected sealed override void OnClosed(EventArgs e)
    {
        _closed = true; _exitPending = false; _pendingResult = null; ++_generation;
        WatchMotion(false); SettleShell();
        foreach (var dialog in _nestedDialogs)
        { dialog.Closed -= NestedClosed; dialog.IsVisibleChanged -= NestedVisibilityChanged; }
        _nestedDialogs.Clear(); _coveredChildren.Clear();
        if (_backdrop is not null) { _backdrop.IsPresented = false; }
        if (_surface is not null)
        {
            _surface.PresentationSettled -= Surface_Settled;
            _surface.Loaded -= Surface_Loaded;
            _surface.IsVisibleChanged -= Surface_IsVisibleChanged;
            _surface.IsPresented = false;
            _surface.FinishPresentation();
        }
        try { base.OnClosed(e); }
        finally { _closure.TrySetResult(); }
    }
}
