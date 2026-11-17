using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SystemAutorun.App.Dialogs;

public partial class SelfStartupWindow : ToolWindow
{
    private readonly SelfStartupViewModel _model;
    private readonly DispatcherTimer _observer;
    private bool _loaded, _closed;
    private Window? _boundOwner;
    private UIElement? _returnFocus;
    private SelfStartupWorkflowState _lastState;
    private bool _needsSafeFocus;
    internal Task InitializationTask { get; private set; } = Task.CompletedTask;
    private TaskCompletionSource? _shutdown;
    internal Task ShutdownTask => _shutdown?.Task ?? Task.CompletedTask;
    internal override Task ResourceCleanupTask => ShutdownTask;
    protected override void OnCloseAccepted() => _shutdown ??= new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal SelfStartupWindow(SelfStartupViewModel model)
    {
        InitializeComponent(); _model = model; DataContext = model;
        _observer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, Observe, Dispatcher);
        _observer.Stop(); Closed += Window_Closed;
        StateChanged += (_, _) => { if (WindowState == WindowState.Maximized) { WindowState = WindowState.Normal; } };
    }
    internal void SetEntryContext(Window owner, UIElement? returnFocus)
    {
        Dispatcher.VerifyAccess();
        if (_closed) { throw new InvalidOperationException("The self startup tool is closed."); }
        CancelPendingClose();
        if (!ReferenceEquals(Owner, owner))
        {
            Owner = owner; if (_loaded) { BindOwner(); } _needsSafeFocus = true;
        }
        _returnFocus = returnFocus;
    }
    private void BindOwner()
    {
        if (ReferenceEquals(_boundOwner, Owner)) { return; }
        if (_boundOwner is { } previous) { previous.SizeChanged -= Owner_SizeChanged; }
        _boundOwner = Owner;
        if (_boundOwner is { } owner) { owner.SizeChanged += Owner_SizeChanged; UpdateOwnerBounds(); }
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || _closed) { return; }
        _loaded = true; BindOwner();
        _observer.Start(); InitializationTask = _model.InitializeAsync();
        CancelButton.Focus(); await InitializationTask;
        if (IsPresentationActive && IsActive) { FocusSafeAction(); }
    }
    private void Observe(object? sender, EventArgs e)
    {
        if (_closed) { return; }
        _model.ObserveSnapshot();
        var state = _model.Snapshot.State;
        if (state != _lastState) { _lastState = state; _needsSafeFocus = true; }
        // Background completion must not steal focus, but retain the safe-focus
        // obligation until this window is active again. Do not fight later user focus.
        if (!_needsSafeFocus || !IsActive || IsExitPending) { return; }
        _needsSafeFocus = false;
        if (!IsKeyboardFocusWithin || ReferenceEquals(Keyboard.FocusedElement, this)
            || Keyboard.FocusedElement is not UIElement { IsVisible: true, IsEnabled: true })
        { FocusSafeAction(); }
    }
    private void FocusSafeAction()
    {
        // Never move focus to an implicit default/confirm action.
        if (_model.CanQuery) { QueryButton.Focus(); } else { CancelButton.Focus(); }
    }
    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_closed) { return; }
        _closed = true; _observer.Stop(); _observer.Tick -= Observe;
        var owner = _boundOwner; var returnFocus = _returnFocus; _returnFocus = null;
        if (owner is not null) { owner.SizeChanged -= Owner_SizeChanged; _boundOwner = null; }
        // Owned teardown can skip Closing. Never throw out of this event: the
        // following handler must evict the closed window from the root registry.
        _shutdown ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ShutDownAsync();
        if (owner is not null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            try
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (owner.IsVisible && owner.IsActive && owner.IsEnabled && returnFocus is { IsVisible: true, IsEnabled: true }) { returnFocus.Focus(); }
                }));
            }
            catch (InvalidOperationException error) { System.Diagnostics.Trace.TraceError("Self startup return focus unavailable: {0}", error.GetType().Name); }
        }
    }
    private async Task ShutDownAsync()
    {
        try { await Task.WhenAll(DisposeModelAsync(), InitializationTask); _shutdown!.TrySetResult(); }
        catch (Exception error)
        {
            _shutdown!.TrySetException(error);
            _ = _shutdown.Task.Exception; // Modeless callers need not await; later awaiters still receive the failure.
            System.Diagnostics.Trace.TraceError("Self startup view cleanup failed: {0}", error.GetType().Name);
        }
    }
    // Capture synchronous Dispose failures as a task so initialization is still joined.
    private async Task DisposeModelAsync() => await _model.DisposeAsync();
    private void Owner_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateOwnerBounds();
    private void UpdateOwnerBounds()
    {
        if (_closed || _boundOwner is null) { return; }
        ToolWindowLayout.Apply(this);
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!_model.CanCopy) { return; }
        try { System.Windows.Clipboard.SetText(_model.OperationId); _model.ReportCopy(true); }
        catch (ExternalException) { _model.ReportCopy(false); }
    }
}

/// <summary>One modeless tool per physical root, shared by the menu and monitoring entry.</summary>
internal static class SelfStartupTool
{
    private static readonly Dictionary<string, SelfStartupWindow> Windows = new(StringComparer.Ordinal);
    internal static void WithOwnerSuspended(Window owner, Action showDialog)
    {
        owner.Dispatcher.VerifyAccess();
        // ShowDialog disables every visible window on this thread. Hide this
        // modeless view first, retaining its preparation and in-flight workflow.
        var suspended = Windows.Values.Where(window => ReferenceEquals(window.Owner, owner) && window.IsVisible).ToArray();
        foreach (var window in suspended) { window.CancelPendingClose(); window.Hide(); }
        try { showDialog(); }
        finally
        {
            foreach (var window in suspended)
            {
                if (!Windows.Values.Contains(window) || window.IsVisible || !ReferenceEquals(window.Owner, owner)
                    || !owner.IsVisible || owner.Dispatcher.HasShutdownStarted
                    || System.Windows.Application.Current is App { IsExiting: true }) { continue; }
                var activated = window.ShowActivated;
                try { window.CancelPendingClose(); window.ShowActivated = false; window.Show(); }
                finally { window.ShowActivated = activated; }
            }
        }
    }
    internal static SelfStartupWindow Open(Window owner, ProjectPaths paths, UIElement? returnFocus = null)
    {
        owner.Dispatcher.VerifyAccess();
        var root = AgentProtocol.ForRoot(paths.RootDirectory).RootId;
        if (Windows.TryGetValue(root, out var existing))
        {
            existing.SetEntryContext(owner, returnFocus);
            if (!existing.IsVisible) { existing.Show(); }
            existing.Activate(); return existing;
        }
        var window = new SelfStartupWindow(new(paths)); window.SetEntryContext(owner, returnFocus);
        Windows.Add(root, window);
        window.Closed += (_, _) => { if (Windows.GetValueOrDefault(root) == window) { Windows.Remove(root); } };
        try { window.Show(); return window; }
        catch { Windows.Remove(root); window.Close(); throw; }
    }
}
