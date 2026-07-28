using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Models;

namespace SystemAutorun.App.Dialogs;

public partial class StartupManagerWindow : Window
{
    private readonly StartupManagerViewModel _viewModel;
    private readonly Func<AddWindowsTaskRequest, Task<bool>> _addTask;
    private bool _initialized;

    internal StartupManagerWindow(
        StartupManagerViewModel viewModel,
        Func<AddWindowsTaskRequest, Task<bool>> addTask)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _addTask = addTask;
        DataContext = viewModel;
        viewModel.ConfirmRequested = message =>
            MessageDialog.ShowConfirm(this, "确认启动项操作", message);
        viewModel.ErrorRequested += (_, message) =>
            MessageDialog.ShowError(this, "启动项操作失败", message);
        viewModel.AddProgramRequested += ViewModel_AddProgramRequested;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await _viewModel.InitializeAsync();
    }

    private async void ViewModel_AddProgramRequested(object? sender, StartupEntry entry)
    {
        _ = sender;
        var dialog = new AddProgramWindow(entry) { Owner = this };
        if (dialog.ShowDialog() == true
            && dialog.Request is not null
            && await _addTask(dialog.Request)
            && _viewModel.RefreshCommand.CanExecute(null))
        {
            _viewModel.RefreshCommand.Execute(null);
        }
    }
}
