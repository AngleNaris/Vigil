using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;
    private readonly StartupManager _startupManager;
    private readonly StartupCacheStore _startupCache;

    internal MainWindow(
        MainWindowViewModel viewModel,
        StartupManager startupManager,
        StartupCacheStore startupCache)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _startupManager = startupManager;
        _startupCache = startupCache;
        DataContext = viewModel;
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, OnRefreshTimer, Dispatcher);
        _refreshTimer.Stop();

        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.ErrorRequested += (_, message) => MessageDialog.ShowError(this, "操作失败", message);
        viewModel.DoctorRequested += (_, report) => new DoctorWindow(report) { Owner = this }.ShowDialog();
        viewModel.DetailRequested += (_, detail) => new TaskDetailWindow(detail) { Owner = this }.ShowDialog();
        viewModel.ConfirmRequested = message =>
            MessageDialog.ShowConfirm(this, "确认任务操作", message);
        viewModel.StartupManagerRequested += (_, _) => OpenStartupManager();
        viewModel.AddProgramRequested += (_, _) => OpenAddProgram();
    }

    internal MainWindowViewModel ViewModel => _viewModel;

    internal void ActivateFromBackground()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (App.CurrentApp.IsExiting)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        App.CurrentApp.ShowTrayInfo("SystemAutorun", "管理器仍在后台运行。");
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.AutoRefresh))
        {
            if (_viewModel.AutoRefresh)
            {
                _refreshTimer.Start();
            }
            else
            {
                _refreshTimer.Stop();
            }
        }
    }

    private void OnRefreshTimer(object? sender, EventArgs e)
    {
        if (_viewModel.AutoRefresh && _viewModel.RefreshCommand.CanExecute(null))
        {
            _viewModel.RefreshCommand.Execute(null);
        }
    }

    private void TaskGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_viewModel.ShowDetailCommand.CanExecute(null))
        {
            _viewModel.ShowDetailCommand.Execute(null);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        App.CurrentApp.RequestExit();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        MessageDialog.ShowInfo(
            this,
            "关于 SystemAutorun",
            "SystemAutorun · Windows 原生自启动管理器\n.NET 10 / WPF / Named Pipe Agent");
    }

    private void InstallBoot_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        App.CurrentApp.RunElevated("install-boot");
    }

    private void UninstallBoot_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        App.CurrentApp.RunElevated("uninstall-boot");
    }

    private void SelfStartup_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is System.Windows.Controls.CheckBox checkBox)
        {
            App.CurrentApp.SetSelfStartup(checkBox.IsChecked == true);
        }
    }

    private void OpenStartupManager()
    {
        var viewModel = new StartupManagerViewModel(
            _startupManager,
            _startupCache,
            App.CurrentApp.RunElevatedStartupMutationAsync);
        _ = new StartupManagerWindow(viewModel, _viewModel.AddWindowsTaskAsync)
        {
            Owner = this,
        }.ShowDialog();
    }

    private async void OpenAddProgram()
    {
        var dialog = new AddProgramWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Request is not null)
        {
            _ = await _viewModel.AddWindowsTaskAsync(dialog.Request);
        }
    }
}
