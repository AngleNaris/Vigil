using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Recovery;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SystemAutorun.App.Dialogs;

public partial class RecoveryWindow : ToolWindow
{
    private readonly RecoveryViewModel _model;
    private readonly DispatcherTimer _observer;
    private Window? _boundOwner;
    private bool _loaded, _closed, _needsSafeFocus;
    private RecoveryWorkflowState _lastState;
    internal Task InitializationTask { get; private set; } = Task.CompletedTask;
    private TaskCompletionSource? _shutdown;
    internal Task ShutdownTask => _shutdown?.Task ?? Task.CompletedTask;
    internal override Task ResourceCleanupTask => ShutdownTask;
    protected override void OnCloseAccepted() => _shutdown ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal RecoveryWindow(RecoveryViewModel model)
    {
        InitializeComponent(); _model = model; DataContext = model; _model.PropertyChanged += Model_PropertyChanged;
        _observer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, Observe, Dispatcher); _observer.Stop();
        Closed += Window_Closed;
        StateChanged += (_, _) => { if (WindowState == WindowState.Maximized) { WindowState = WindowState.Normal; } };
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || _closed) { return; } _loaded = true; _boundOwner = Owner;
        if (_boundOwner is { } owner) { owner.SizeChanged += Owner_SizeChanged; UpdateOwnerBounds(); }
        _observer.Start(); InitializationTask = _model.InitializeAsync(); CancelButton.Focus(); await InitializationTask;
        if (IsPresentationActive && IsActive) { FocusSafeAction(); }
    }
    private void Observe(object? sender, EventArgs e)
    {
        if (_closed) { return; } _model.ObserveSnapshot(); CheckStateTransition();
        if (!_needsSafeFocus || !IsActive || IsExitPending) { return; } _needsSafeFocus = false;
        if (_model.ShowConfirmation || !IsKeyboardFocusWithin || ReferenceEquals(Keyboard.FocusedElement, this)
            || Keyboard.FocusedElement is not UIElement { IsVisible: true, IsEnabled: true }) { FocusSafeAction(); }
    }
    private void Model_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (!_closed && string.IsNullOrEmpty(e.PropertyName)) { CheckStateTransition(); } }
    private void CheckStateTransition()
    {
        var state = _model.Snapshot.State; if (state == _lastState) { return; }
        _lastState = state; _needsSafeFocus = true;
        // A new confirmation/unknown page must not inherit a scrolled-away heading
        // and safety notice from expanded diagnostics. This does not activate/focus.
        if (state is RecoveryWorkflowState.AwaitingConfirmation or RecoveryWorkflowState.OutcomeUnknown) { ContentScroll.ScrollToTop(); }
    }
    private void FocusSafeAction()
    {
        if (_model.ShowConfirmation) { CancelButton.Focus(); }
        else if (_model.CanQuery) { QueryButton.Focus(); }
        else if (_model.CanRead && RefreshButton.IsVisible) { RefreshButton.Focus(); }
        else { CancelButton.Focus(); }
    }
    private void Owner_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateOwnerBounds();
    private void UpdateOwnerBounds()
    {
        if (_closed || _boundOwner is null) { return; }
        ToolWindowLayout.Apply(this);
    }
    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_closed) { return; } _closed = true; _observer.Stop(); _observer.Tick -= Observe; _model.PropertyChanged -= Model_PropertyChanged;
        if (_boundOwner is { } owner) { owner.SizeChanged -= Owner_SizeChanged; } _boundOwner = null;
        // Forced owner destruction can skip Closing. Shutdown only joins view reads;
        // confirmed workflow completion remains owned by the name-scoped coordinator.
        _shutdown ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ShutDownAsync();
    }
    private async Task ShutDownAsync()
    {
        try { await Task.WhenAll(DisposeModelAsync(), InitializationTask); _shutdown!.TrySetResult(); }
        catch (Exception error)
        {
            _shutdown!.TrySetException(error);
            _ = _shutdown.Task.Exception;
            System.Diagnostics.Trace.TraceError("Recovery view cleanup failed: {0}", error.GetType().Name);
        }
    }
    private async Task DisposeModelAsync() => await _model.DisposeAsync();
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
