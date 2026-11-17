using System.Windows.Input;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SystemAutorun.App.Dialogs;

public partial class LogViewerWindow : ToolWindow
{
    private readonly LogViewerViewModel _viewModel;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<Window, string?> _chooseExportFile;
    private bool _initialized, _closed;
    private int _closeGeneration;
    private TaskCompletionSource? _shutdown;
    private Task _exportTask = Task.CompletedTask;
    internal Task InitializationTask { get; private set; } = Task.CompletedTask;
    internal Task ShutdownTask => _shutdown?.Task ?? Task.CompletedTask;
    internal override Task ResourceCleanupTask => ShutdownTask;

    internal LogViewerWindow(LogViewerViewModel viewModel, Func<Window, string?>? chooseExportFile = null)
    {
        InitializeComponent(); _viewModel = viewModel; DataContext = viewModel;
        _chooseExportFile = chooseExportFile ?? ChooseExportFile; Closed += Window_Closed;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || _closed) { return; }
        _initialized = true; InitializationTask = _viewModel.InitializeAsync(); await InitializationTask;
    }
    protected override void OnCloseAccepted()
    {
        ++_closeGeneration;
        // Publish before the decorative delay; a veto/reversal never means resource cleanup finished.
        _shutdown ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_closed) { return; }
        _closed = true;
        // Owner destruction can bypass Closing, so Closed also establishes the pending promise.
        _shutdown ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ShutDownAsync();
    }
    private async Task ShutDownAsync()
    {
        try
        {
            try
            {
                Task cancel;
                try { _lifetime.Cancel(); cancel = Task.CompletedTask; }
                catch (Exception error) { cancel = Task.FromException(error); }
                await Task.WhenAll(cancel, _viewModel.DisposeAsync().AsTask(), InitializationTask, _exportTask);
            }
            finally { _lifetime.Dispose(); }
            _shutdown!.TrySetResult();
        }
        catch (Exception error)
        {
            _shutdown!.TrySetException(error);
            System.Diagnostics.Trace.TraceError("Log tool view cleanup failed: {0}", error.GetType().Name);
        }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || IsExitPending || !_viewModel.CanExport || !_exportTask.IsCompleted) { return; }
        var generation = _closeGeneration;
        var destination = _chooseExportFile(this);
        // The native picker runs a nested message loop: the owner may have closed or reversed an exit.
        if (destination is null || _closed || IsExitPending || generation != _closeGeneration) { return; }
        _exportTask = _viewModel.ExportAsync(destination, _lifetime.Token); await _exportTask;
    }
    private static string? ChooseExportFile(Window owner)
    {
        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出当前页（新文件，不覆盖）", Filter = "NDJSON 日志快照|*.ndjson", DefaultExt = ".ndjson",
            AddExtension = true, OverwritePrompt = false, FileName = "logs-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".ndjson",
        };
        return picker.ShowDialog(owner) == true ? picker.FileName : null;
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
