using System.Windows.Controls;
using System.Windows.Input;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Models;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;

namespace SystemAutorun.App.Dialogs;

public partial class StartupManagerWindow : SystemAutorun.App.Services.ToolWindow
{
    private readonly StartupManagerViewModel _viewModel;
    private bool _initialized;
    internal Task InitializationTask { get; private set; } = Task.CompletedTask;

    internal StartupManagerWindow(StartupManagerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.AddProgramRequested += ViewModel_AddProgramRequested;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) { return; }
        _initialized = true;
        InitializationTask = _viewModel.InitializeAsync();
        await InitializationTask;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _viewModel.AddProgramRequested -= ViewModel_AddProgramRequested;
        _viewModel.Dispose();
    }

    private void ViewModel_AddProgramRequested(object? sender, StartupEntry entry)
    {
        if (App.CurrentApp.IsExiting) { return; }
        AddProgramWindow? dialog = null;
        dialog = new AddProgramWindow(entry, async request =>
        {
            if (_viewModel.HasUnknownAdoption)
            {
                MessageDialog.ShowError(dialog!, "结果尚未确定", _viewModel.StatusMessage);
                return false;
            }

            var succeeded = await _viewModel.AdoptAsync(request, confirmed: true);
            if (!succeeded)
            {
                MessageDialog.ShowError(dialog!, "添加到监控列表失败 · 输入已保留", _viewModel.StatusMessage);
            }
            return succeeded;
        }) { Owner = this };
        _ = dialog.ShowDialog();
        _ = StartupList.Focus();
    }

    private void StartupList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _viewModel.AddToTasksCommand.CanExecute(null))
        {
            _viewModel.AddToTasksCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _ = SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var menu = button.ContextMenu;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.IsChecked = (string)item.Tag == _viewModel.SelectedFilter.Value;
        }
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void FilterOption_Click(object sender, RoutedEventArgs e) =>
        _viewModel.SelectedFilter = _viewModel.Filters.Single(filter => filter.Value == (string)((MenuItem)sender).Tag);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
