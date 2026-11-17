using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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
    private IInputElement? _detailsReturn;
    internal Task InitializationTask { get; private set; } = Task.CompletedTask;

    internal StartupManagerWindow(StartupManagerViewModel viewModel)
    {
        InitializeComponent(); _viewModel = viewModel; DataContext = viewModel;
        viewModel.AddProgramRequested += ViewModel_AddProgramRequested;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) { return; }
        _initialized = true; InitializationTask = _viewModel.InitializeAsync(); await InitializationTask;
    }
    private void Window_Closed(object? sender, EventArgs e)
    {
        _viewModel.AddProgramRequested -= ViewModel_AddProgramRequested;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged; _viewModel.Dispose();
    }
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StartupManagerViewModel.ShowDetails) && _viewModel.ShowDetails)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            { if (IsVisible && IsEnabled && _viewModel.ShowDetails) { _ = DetailsText.Focus(); } });
        }
    }
    private void ViewModel_AddProgramRequested(object? sender, StartupEntry entry)
    {
        if (App.CurrentApp.IsExiting) { return; }
        AddProgramWindow? dialog = null;
        dialog = new AddProgramWindow(entry, async request =>
        {
            if (_viewModel.HasUnknownAdoption)
            {
                MessageDialog.ShowError(dialog!, "结果尚未确定", _viewModel.StatusMessage); return false;
            }
            var confirmed = MessageDialog.ShowConfirm(dialog!, "确认纳管配置",
                "只新增本配置的工作负载，不禁用原启动登记，也不主动请求执行。\n既有自动策略仍可能运行新任务，原启动登记也可能重复启动。\n确认采用当前表单吗？");
            if (!confirmed) { return false; }
            var succeeded = await _viewModel.AdoptAsync(request, confirmed: true);
            if (!succeeded) { MessageDialog.ShowError(dialog!, "纳管状态提示 · 输入已保留", _viewModel.StatusMessage); }
            return succeeded;
        }) { Owner = this };
        _detailsReturn = AdoptButton;
        _ = dialog.ShowDialog(); RestoreDetailsFocus();
    }
    private void DetailsTrigger_Click(object sender, RoutedEventArgs e) => _detailsReturn = sender is MenuItem ? MoreButton : sender as IInputElement;
    private void NativeDetails_Click(object sender, RoutedEventArgs e) => _detailsReturn = MoreButton;
    private void CloseDetails_Click(object sender, RoutedEventArgs e) => RestoreDetailsFocus();
    private void RestoreDetailsFocus()
    {
        var target = _detailsReturn as UIElement;
        if (target is { IsVisible: true, IsEnabled: true }) { _ = target.Focus(); }
        else { _ = StartupList.Focus(); }
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_viewModel.ShowDetails) { _viewModel.CloseDetailsCommand.Execute(null); RestoreDetailsFocus(); }
            else { Close(); }
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        { _ = SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
    }
    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender; var menu = button.ContextMenu;
        foreach (var item in menu.Items.OfType<MenuItem>())
        { item.IsChecked = (string)item.Tag == _viewModel.SelectedFilter.Value; }
        menu.PlacementTarget = button; menu.IsOpen = true;
    }
    private void FilterOption_Click(object sender, RoutedEventArgs e) =>
        _viewModel.SelectedFilter = _viewModel.Filters.Single(filter => filter.Value == (string)((MenuItem)sender).Tag);
    private void More_Click(object sender, RoutedEventArgs e)
    {
        MoreButton.ContextMenu.PlacementTarget = MoreButton; MoreButton.ContextMenu.IsOpen = true;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
