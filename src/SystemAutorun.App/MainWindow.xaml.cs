using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Windows.Scheduling;
using SystemAutorun.Windows.Startup;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace SystemAutorun.App;

public partial class MainWindow : Window, IDialogScrimHost
{
    private readonly HashSet<Window> _openDialogs = [];
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _dashboardClock;
    private readonly DispatcherTimer _backgroundAlertTimer;
    private readonly AgentStartupClient _startupClient;
    private readonly StartupCacheStore _startupCache;
    private readonly ListReorderDragController<TaskRowViewModel> _serviceDragController;
    private bool _hasBeenPresented;
    private bool _refreshOnReturn;
    private bool _initialEntrancePlayed;
    private bool _dialogBackdropActive;
    private bool _closed;
    private HwndSource? _windowSource;

    internal MainWindow(MainWindowViewModel viewModel, AgentStartupClient startupClient, StartupCacheStore startupCache)
    {
        InitializeComponent();
        DialogScrim.PresentationSettled += (_, _) =>
        {
            if (_dialogBackdropActive && DialogScrim.IsPresented) { MainScene.Visibility = Visibility.Hidden; }
        };
        _viewModel = viewModel;
        _startupClient = startupClient;
        _startupCache = startupCache;
        DataContext = viewModel;
        viewModel.NavigateCommand.Execute(MainWindowViewModel.WorkloadsPage);
        viewModel.InitializeDashboard();
        _refreshTimer = new DispatcherTimer(viewModel.AutoRefreshInterval, DispatcherPriority.Background, OnRefreshTimer, Dispatcher);
        if (!viewModel.AutoRefresh) { _refreshTimer.Stop(); }
        _dashboardClock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnDashboardClock, Dispatcher);
        _backgroundAlertTimer = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, OnBackgroundAlertTimer, Dispatcher);
        _backgroundAlertTimer.Stop();
        IsVisibleChanged += (_, _) => UpdatePresentationActivity();
        StateChanged += (_, _) => { UpdateWindowRootMaximizePadding(); UpdatePresentationActivity(); };
        SizeChanged += (_, _) => { if (WindowState == WindowState.Maximized) { UpdateWindowRootMaximizePadding(); } };
        UpdateWindowRootMaximizePadding();
        UpdatePresentationActivity();
        // R109: the first show grows the window's structural lines in; later tray restores do not replay.
        Loaded += (_, args) =>
        {
            if (_initialEntrancePlayed || !ReferenceEquals(args.OriginalSource, this)) { return; }
            _initialEntrancePlayed = true;
            LineMotion.Play(MainScene);
        };
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.RefreshCommand.PropertyChanged += RefreshCommand_PropertyChanged;
        viewModel.ErrorRequested += (_, message) =>
        {
            if (!App.CurrentApp.IsExiting) { MessageDialog.ShowError(DialogOwner, "操作失败", message); }
        };
        viewModel.DoctorRequested += (_, report) =>
        {
            if (App.CurrentApp.IsExiting) { return; }
            var owner = DialogOwner;
            var previous = Keyboard.FocusedElement;
            _ = new DoctorWindow(report) { Owner = owner }.ShowDialog();
            if (owner == this) { RestoreDashboardFocus(previous, SettingsButton); }
        };
        viewModel.DetailRequested += (_, detail) =>
        {
            if (App.CurrentApp.IsExiting) { return; }
            var owner = DialogOwner;
            var previous = Keyboard.FocusedElement;
            _ = new TaskDetailWindow(detail) { Owner = owner }.ShowDialog();
            if (owner == this) { RestoreDashboardFocus(previous); }
        };
        viewModel.LogViewerRequested += async (_, taskName) => await OpenLogViewerAsync(taskName);
        viewModel.ConfirmRequested = message => MessageDialog.ShowConfirm(DialogOwner, "确认任务操作", message);
        viewModel.StartupManagerRequested += (_, _) => OpenStartupManager();
        viewModel.AddProgramRequested += (_, _) => OpenAddProgram();
        viewModel.EditTaskRequested += (_, task) => OpenEditProgram(task);
        viewModel.RemoteHostDialogRequested += (_, editingId) => OpenRemoteHosts(editingId);
        viewModel.AlertSettingsRequested += (_, focusKey) => OpenAlertSettings(focusKey);
        viewModel.AutoRestartSettingsRequested += (_, taskName) => OpenAutoRestartSettings(taskName);
        viewModel.AlertRequested += (_, message) => App.CurrentApp.ShowTrayInfo("家庭服务器告警", message);

        AutomationViewControl.Bind(
            () => _viewModel.PrepareAlertSettingsEditAsync(),
            (settings, original) => _viewModel.ApplyAutoRestartSettingsAsync(settings, original),
            () => _viewModel.TryReadAutoRestartStatusAsync(),
            ruleId => _viewModel.RunAutoRestartRuleAsync(ruleId),
            install => App.CurrentApp.RunMaintenanceElevationAsync(install));

        _serviceDragController = new ListReorderDragController<TaskRowViewModel>(
            ServiceList,
            () => _viewModel.Tasks,
            () => 0,
            () => _viewModel.CanReorderTasks,
            (from, to) => _viewModel.Tasks.Move(from, to),
            item => _viewModel.SelectedTask = item,
            PersistTaskOrderAsync,
            active =>
            {
                if (active) { _viewModel.SuppressTaskReorder = true; }
            });
        _serviceDragController.Attach();
    }

    internal MainWindowViewModel ViewModel => _viewModel;
    internal TimeSpan BackgroundAlertInterval
    {
        get => _backgroundAlertTimer.Interval;
        set => _backgroundAlertTimer.Interval = value;
    }
    private Window DialogOwner => OwnedWindows.Cast<Window>().LastOrDefault(window =>
        window is ToolWindow tool ? tool.IsPresentationActive : window.IsVisible) ?? this;

    internal void ActivateFromBackground()
    {
        Show();
        if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; }
        if (!Activate() && !IsActive)
        {
            Topmost = true;
            Topmost = false;
            Activate();
        }
        Focus();
    }

    // R105: any tool dialog owned (directly or through another dialog) by this window dims it.
    void IDialogScrimHost.Track(Window dialog)
    {
        if (!_openDialogs.Add(dialog)) { return; }
        dialog.Closed += Dialog_Closed;
        dialog.IsVisibleChanged += Dialog_IsVisibleChanged;
        if (dialog is ToolWindow tool) { tool.PresentationChanged += Dialog_PresentationChanged; }
        UpdateScrim();
    }

    private void Dialog_Closed(object? sender, EventArgs args)
    {
        if (sender is Window dialog) { UntrackDialog(dialog); UpdateScrim(); }
    }

    private void Dialog_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateScrim();
    private void Dialog_PresentationChanged(object? sender, EventArgs args) => UpdateScrim();

    private void UntrackDialog(Window dialog)
    {
        dialog.Closed -= Dialog_Closed;
        dialog.IsVisibleChanged -= Dialog_IsVisibleChanged;
        if (dialog is ToolWindow tool) { tool.PresentationChanged -= Dialog_PresentationChanged; }
        _openDialogs.Remove(dialog);
    }

    private void UpdateScrim()
    {
        // Keep the owner covered through the tool's exit, not merely until its input is retired.
        var blocked = _openDialogs.Any(dialog => dialog.IsVisible);
        if (blocked == _dialogBackdropActive) { return; }
        _dialogBackdropActive = blocked;
        if (blocked)
        {
            LineMotion.SetIsSuspended(this, true);
            LineMotion.StopTree(MainScene);
            MainScene.IsHitTestVisible = false;
            DialogScrim.IsPresented = true;
        }
        else
        {
            MainScene.Visibility = Visibility.Visible;
            MainScene.IsHitTestVisible = true;
            LineMotion.SetIsSuspended(this, false);
            DialogScrim.IsPresented = false;
            if (!App.CurrentApp.IsExiting) { LineMotion.Transition(MainScene); }
        }
        UpdatePresentationActivity();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
        UpdateWindowRootMaximizePadding();
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WindowChrome's non-client caption and resize strip do not route WPF mouse events.
        if (message is 0x00A1 or 0x00A4 or 0x00A7) { DismissSearchFocus(); }
        return IntPtr.Zero;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (App.CurrentApp.IsExiting) { return; }
        e.Cancel = true;
        Hide();
        App.CurrentApp.ShowTrayInfo("Vigil", "管理器仍在后台运行。");
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        if (_windowSource is { IsDisposed: false } source) { source.RemoveHook(WindowMessageHook); }
        _windowSource = null;
        foreach (var dialog in _openDialogs.ToArray()) { UntrackDialog(dialog); }
        UpdateScrim();
        _refreshOnReturn = false;
        _refreshTimer.Stop();
        _dashboardClock.Stop();
        _backgroundAlertTimer.Stop();
        _viewModel.RefreshCommand.PropertyChanged -= RefreshCommand_PropertyChanged;
        _viewModel.DetachDashboard();
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.AutoRefresh))
        {
            if (!_viewModel.AutoRefresh) { DataChangePulse.SetSuppress(this, false); }
            UpdatePresentationActivity();
        }
        if (e.PropertyName == nameof(MainWindowViewModel.AutoRefreshMilliseconds))
        { _refreshTimer.Interval = _viewModel.AutoRefreshInterval; }
        if (e.PropertyName is nameof(MainWindowViewModel.IsBusy) or nameof(MainWindowViewModel.IsRefreshing))
        { RefreshOnReturnWhenReady(); }
    }

    private void RefreshCommand_PropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshOnReturnWhenReady();

    private void RefreshOnReturnWhenReady()
    {
        if (!_refreshOnReturn || !_viewModel.IsPresentationActive || !_viewModel.AutoRefresh || !_viewModel.RefreshCommand.CanExecute(null)) { return; }
        _refreshOnReturn = false;
        _ = RunReturnRefreshAsync();
    }

    private async Task RunReturnRefreshAsync()
    {
        DataChangePulse.SetSuppress(this, true);
        try
        {
            if (_viewModel.FullRefreshDue)
            {
                _viewModel.RequestAutomaticFullRefresh();
                if (_viewModel.RefreshCommand.ExecutionTask is { } fullTask) { await fullTask; }
            }
            else
            {
                await _viewModel.RefreshLiveAsync();
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        finally
        {
            DataChangePulse.SetSuppress(this, false);
        }
    }

    private void UpdatePresentationActivity()
    {
        if (_closed)
        {
            _refreshOnReturn = false;
            DataChangePulse.SetSuppress(this, false);
            _dashboardClock.Stop();
            _refreshTimer.Stop();
            _backgroundAlertTimer.Stop();
            return;
        }
        var wasActive = _viewModel.IsPresentationActive;
        var active = IsVisible && WindowState != WindowState.Minimized && !_dialogBackdropActive;
        _viewModel.SetPresentationActive(active);
        if (active)
        {
            _backgroundAlertTimer.Stop();
            _dashboardClock.Start();
            if (_viewModel.AutoRefresh) { _refreshTimer.Start(); } else { _refreshTimer.Stop(); }
            if ((_hasBeenPresented || _viewModel.HasMonitoringSnapshot) && !wasActive && _viewModel.AutoRefresh)
            {
                DataChangePulse.SetSuppress(this, true);
                _refreshOnReturn = true;
            }
            _hasBeenPresented = true;
            RefreshOnReturnWhenReady();
        }
        else
        {
            _refreshOnReturn = false;
            DataChangePulse.SetSuppress(this, false);
            _dashboardClock.Stop();
            _refreshTimer.Stop();
            if (_viewModel.AutoRefresh) { _backgroundAlertTimer.Start(); } else { _backgroundAlertTimer.Stop(); }
        }
    }

    private void OnBackgroundAlertTimer(object? sender, EventArgs e)
    {
        if (_closed || _viewModel.IsPresentationActive || !_viewModel.AutoRefresh) { return; }
        _viewModel.RequestAutomaticFullRefresh();
    }

    private void OnDashboardClock(object? sender, EventArgs e)
    {
        _viewModel.TickDashboard();
        if (_viewModel.AutoRefresh && _viewModel.FullRefreshDue) { _viewModel.RequestAutomaticFullRefresh(); }
    }

    private async void OnRefreshTimer(object? sender, EventArgs e)
    {
        if (!_viewModel.AutoRefresh) { return; }
        if (_viewModel.IsAutomationPage) { _ = AutomationViewControl.RefreshAsync(); }
        if (!_viewModel.FullRefreshDue)
        {
            _viewModel.RefreshResourcesIfDue();
            await _viewModel.RefreshLiveAsync();
        }
        else { _viewModel.RequestAutomaticFullRefresh(); }
    }

    private void ServiceList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is TaskRowViewModel task) { _viewModel.SelectedTask = task; }
    }

    private void ServiceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _serviceDragController.IsDragging || _viewModel.IsBusy) { return; }
        var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is TaskRowViewModel task)
        {
            _viewModel.SelectedTask = task;
            if (_viewModel.EditTaskCommand.CanExecute(null))
            {
                _viewModel.EditTaskCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private async Task PersistTaskOrderAsync()
    {
        try
        {
            var ordered = _viewModel.Tasks.Select(item => item.Name).ToArray();
            await _viewModel.ReorderTasksAsync(ordered);
        }
        finally
        {
            _viewModel.SuppressTaskReorder = false;
        }
    }

    private void SearchSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SearchSurface)) { SearchBox.Focus(); }
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!SearchBox.IsKeyboardFocusWithin || e.OriginalSource is not DependencyObject source) { return; }
        for (DependencyObject? current = source; current is not null;)
        {
            if (ReferenceEquals(current, SearchSurface)) { return; }
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }

        DismissSearchFocus();
    }

    private void DismissSearchFocus()
    {
        if (!SearchBox.IsKeyboardFocusWithin) { return; }
        // Keep keyboard shortcuts routed through the window until the clicked control takes focus.
        MainScene.Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            switch (e.Key)
            {
                case Key.F:
                    if (_viewModel.IsExceptionLogsPage) { MonitoringTabs.FocusExceptionSearch(); }
                    else if (_viewModel.IsWorkloadsPage) { SearchBox.Focus(); SearchBox.SelectAll(); }
                    e.Handled = true; return;
                case Key.D1: SelectTab(MainWindowViewModel.OverviewPage); e.Handled = true; return;
                case Key.D2:
                    SelectTab(MainWindowViewModel.WorkloadsPage);
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => ServiceList.Focus()));
                    e.Handled = true; return;
                case Key.D3:
                    if (_viewModel.ShowDetailCommand.CanExecute(null)) { _viewModel.ShowDetailCommand.Execute(null); }
                    e.Handled = true; return;
                case Key.D4: SelectTab(MainWindowViewModel.ExceptionLogsPage); e.Handled = true; return;
            }
        }
        if (e.Key == Key.F5 && _viewModel.IsAutomationPage)
        {
            _ = AutomationViewControl.RefreshAsync();
        }
        if (e.Key == Key.Delete && _viewModel.IsWorkloadsPage
            && Keyboard.FocusedElement is not TextBoxBase and not ComboBox
            && _viewModel.RemoveTaskCommand.CanExecute(null))
        {
            _viewModel.RemoveTaskCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null and not T)
        {
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return current as T;
    }

    private void MoreMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) { return; }
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ServicesTab_Click(object sender, RoutedEventArgs e) => SelectTab(MainWindowViewModel.WorkloadsPage);
    private void AutomationTab_Click(object sender, RoutedEventArgs e) => SelectTab(MainWindowViewModel.AutomationPage);
    private void Hosts_Click(object sender, RoutedEventArgs e) => SelectTab(MainWindowViewModel.OverviewPage);
    private void ExceptionLogs_Click(object sender, RoutedEventArgs e) => SelectTab(MainWindowViewModel.ExceptionLogsPage);

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_viewModel.IsAutomationPage) { _ = AutomationViewControl.RefreshAsync(); }
    }

    // R106: appearance follows Windows by default; the choice applies live to every open window.
    private void AppearanceMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        _ = e;
        foreach (var item in ((MenuItem)sender).Items.OfType<MenuItem>())
        { item.IsChecked = string.Equals(item.Tag as string, ThemeService.Preference.ToString(), StringComparison.Ordinal); }
    }

    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<ThemePreference>(tag, out var preference))
        { ThemeService.Set(preference); }
    }

    private void AccentColorMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        _ = e;
        foreach (var item in ((MenuItem)sender).Items.OfType<MenuItem>())
        {
            item.IsChecked = item.Tag is string tag
                && string.Equals(tag, ThemeService.AccentPreference, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void AccentPreset_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is MenuItem { Tag: string tag })
        {
            ThemeService.SetAccent(tag);
        }
    }

    private void CustomAccent_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (App.CurrentApp.IsExiting) { return; }
        var previous = Keyboard.FocusedElement;
        _ = new AccentColorWindow { Owner = DialogOwner }.ShowDialog();
        RestoreDashboardFocus(previous, SettingsButton);
    }

    // R105: services, automation, host and exception logs are peer tabs of this window.
    internal void SelectTab(string page)
    {
        // Service controls, automation and exception logs are local-only. Leaving the host tab returns to the
        // local host (OnSelectedHostTabChanged refreshes) so remote resources never back them.
        if (page != MainWindowViewModel.OverviewPage && _viewModel.Hosts.FirstOrDefault(host => host.IsLocal) is { } local
            && !ReferenceEquals(_viewModel.SelectedHostTab, local))
        { _viewModel.SelectedHostTab = local; }
        if (_viewModel.NavigateCommand.CanExecute(page)) { _viewModel.NavigateCommand.Execute(page); }
        if (page == MainWindowViewModel.AutomationPage)
        {
            AutomationViewControl.PlayEntranceAnimation();
            _ = AutomationViewControl.RefreshAsync();
        }
    }

    private void RestoreDashboardFocus(IInputElement? previous, UIElement? fallback = null)
    {
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsVisible || !IsActive || !IsEnabled) { return; }
            if (previous is UIElement { IsVisible: true, IsEnabled: true } element
                && element is not MenuItem && element.Focus()) { return; }
            if (fallback is { IsVisible: true, IsEnabled: true } && fallback.Focus()) { return; }
            if (_viewModel.SelectedTask is { } selected)
            {
                ServiceList.ScrollIntoView(selected);
                if (ServiceList.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem row && row.Focus()) { return; }
            }
            SearchBox.Focus();
        }));
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Hide_Click(object sender, RoutedEventArgs e) => Close();
    private void Exit_Click(object sender, RoutedEventArgs e) => App.CurrentApp.RequestExit();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        OpenAboutWindow(autoCheckOnLoad: false);
    }

    private void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        OpenAboutWindow(autoCheckOnLoad: true);
    }

    internal void OpenAboutWindow(bool autoCheckOnLoad)
    {
        if (App.CurrentApp.IsExiting) { return; }
        var previous = Keyboard.FocusedElement;
        _ = new AboutWindow(autoCheckOnLoad) { Owner = DialogOwner }.ShowDialog();
        RestoreDashboardFocus(previous, SettingsButton);
    }
    private async void InstallBoot_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = await App.CurrentApp.RunElevatedCommandAsync("install-boot");
        UpdateBootTaskMenuItems();
    }

    private async void UninstallBoot_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = await App.CurrentApp.RunElevatedCommandAsync("uninstall-boot");
        UpdateBootTaskMenuItems();
    }

    private async void Recovery_Click(object sender, RoutedEventArgs e) => await OpenRecoveryAsync(sender);

    internal async Task OpenRecoveryAsync(object sender)
    {
        // Capture only a local name; the tool reads admission/current intent itself.
        if (!_viewModel.IsLocalHostSelected || _viewModel.IsBusy || _viewModel.SelectedTask is not { } selected) { return; }
        var previous = Keyboard.FocusedElement;
        var fromMore = sender is MenuItem entry && ItemsControl.ItemsControlFromItemContainer(entry) is System.Windows.Controls.ContextMenu menu
            && ReferenceEquals(menu.PlacementTarget, ServiceMoreButton);
        RecoveryViewModel? model = null; RecoveryWindow? window = null;
        try
        {
            model = App.CurrentApp.CreateRecoveryModel(selected.Name); if (model is null) { return; }
            SelfStartupTool.WithOwnerSuspended(this, () => { window = new RecoveryWindow(model) { Owner = this }; _ = window.ShowDialog(); });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { if (IsVisible && !App.CurrentApp.IsExiting) { MessageDialog.ShowError(this, "恢复状态工具不可用", AgentErrorMessages.Format(error)); } }
        finally
        {
            RestoreDashboardFocus(previous, fromMore ? ServiceMoreButton : null);
            try
            {
                try
                {
                    if (window is not null)
                    {
                        if (!window.ClosureTask.IsCompleted) { window.Close(); }
                        await window.ShutdownTask;
                    }
                }
                finally { if (model is not null) { await model.DisposeAsync(); } }
            }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Recovery tool view cleanup failed: {0}", error.GetType().Name); }
        }
    }

    internal Func<bool> BootTaskInstalledProbe { get; set; } = IsBootTaskInstalledCore;

    private static bool IsBootTaskInstalledCore()
    {
        try
        {
            return new BootTaskScheduler().GetStatus().Installed;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal void UpdateBootTaskMenuItems()
    {
        bool installed;
        try
        {
            installed = BootTaskInstalledProbe();
        }
        catch (Exception)
        {
            installed = false;
        }

        InstallBootMenuItem.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        UninstallBootMenuItem.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void WindowsStartupMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        SelfStartupMenuItem.IsChecked = IsSelfStartupRegistered();
        UpdateBootTaskMenuItems();
    }

    private bool IsSelfStartupRegistered()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) { return false; }
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_viewModel.RootDirectory));
            var registered = UserStartupRegistration.IsEnabled(executable, root);
            _viewModel.IsSelfStartupEnabled = registered;
            return registered;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException
            or System.Security.SecurityException or InvalidOperationException)
        {
            return _viewModel.IsSelfStartupEnabled;
        }
    }

    private void SelfStartup_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var enabled = SelfStartupMenuItem.IsChecked;
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            SelfStartupMenuItem.IsChecked = !enabled;
            MessageDialog.ShowError(DialogOwner, "登录自动启动设置失败", "无法定位当前 Vigil 可执行文件路径。");
            return;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_viewModel.RootDirectory));
            UserStartupRegistration.SetEnabled(enabled, executable, root);
            _viewModel.IsSelfStartupEnabled = enabled;
            _viewModel.StatusMessage = enabled ? "已开启登录自动启动（默认最小化到托盘）。" : "已关闭登录自动启动。";
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException
            or System.Security.SecurityException or InvalidOperationException)
        {
            SelfStartupMenuItem.IsChecked = !enabled;
            MessageDialog.ShowError(DialogOwner, "登录自动启动设置失败", exception.Message);
        }
    }

    internal async Task OpenLogViewerAsync(string? taskName)
    {
        if (App.CurrentApp.IsExiting) { return; }
        var previous = Keyboard.FocusedElement;
        LogViewerViewModel? model = null; LogViewerWindow? window = null;
        try
        {
            model = _viewModel.CreateLogViewer(taskName);
            window = new LogViewerWindow(model) { Owner = DialogOwner };
            _ = window.ShowDialog();
        }
        finally
        {
            RestoreDashboardFocus(previous, taskName is null ? SettingsButton : null);
            try
            {
                try
                {
                    if (window is not null)
                    {
                        // Hide can end a modal frame without Closed. Do not dispose a still-live view.
                        if (!window.ClosureTask.IsCompleted) { window.Close(); }
                        await window.ShutdownTask;
                    }
                }
                finally { if (model is not null) { await model.DisposeAsync(); } }
            }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Log tool caller cleanup failed: {0}", error.GetType().Name); }
        }
    }

    private void OpenStartupManager()
    {
        if (App.CurrentApp.IsExiting) { return; }
        var previous = Keyboard.FocusedElement;
        var model = new StartupManagerViewModel(_startupClient, _startupCache);
        _ = new StartupManagerWindow(model) { Owner = DialogOwner }.ShowDialog();
        if (model.ConfigurationChanged && _viewModel.RefreshCommand.CanExecute(null)) { _viewModel.RefreshCommand.Execute(null); }
        RestoreDashboardFocus(previous, SettingsButton);
    }

    internal AddProgramWindow CreateAddProgramWindow() => new(save: _viewModel.AddTaskAsync) { Owner = DialogOwner };

    private void OpenAddProgram()
    {
        if (App.CurrentApp.IsExiting) { return; }
        var previous = Keyboard.FocusedElement;
        try { _ = CreateAddProgramWindow().ShowDialog(); }
        finally { RestoreDashboardFocus(previous); }
    }

    private void OpenEditProgram(Core.Models.AutorunTask task)
    {
        if (App.CurrentApp.IsExiting) { return; }
        var previous = Keyboard.FocusedElement;
        try
        {
            _ = new AddProgramWindow(task, updated => _viewModel.UpdateTaskAsync(task.Name, updated))
            {
                Owner = DialogOwner,
            }.ShowDialog();
        }
        finally { RestoreDashboardFocus(previous); }
    }

    private async void OpenRemoteHosts(string? editingId = null) => await OpenRemoteHostsAsync(editingId);

    internal async Task OpenRemoteHostsAsync(string? editingId = null)
    {
        if (App.CurrentApp.IsExiting) { return; }
        var previous = Keyboard.FocusedElement;
        try
        {
            var original = await _viewModel.PrepareRemoteHostsEditAsync();
            if (App.CurrentApp.IsExiting) { return; }
            var editing = string.IsNullOrWhiteSpace(editingId)
                ? null
                : original.Preferences.RemoteHosts.FirstOrDefault(host =>
                    string.Equals(host.Id, editingId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(editingId) && editing is null)
            {
                MessageDialog.ShowError(DialogOwner, "远程服务器不存在", "选中的远程服务器已不在当前配置中。");
                return;
            }
            var dialog = new RemoteHostsWindow(original.Preferences.RemoteHosts,
                definitions => _viewModel.ApplyRemoteHostsAsync(definitions, original),
                editing) { Owner = DialogOwner };
            if (dialog.ShowDialog() == true && editing is null && dialog.SavedHostId is { } savedId)
            {
                var added = _viewModel.Hosts.FirstOrDefault(host =>
                    string.Equals(host.Id, savedId, StringComparison.OrdinalIgnoreCase));
                if (added is not null) { _viewModel.SelectedHostTab = added; }
            }
        }
        catch (Exception exception)
        {
            if (!App.CurrentApp.IsExiting) { MessageDialog.ShowError(DialogOwner, "远程服务器读取失败", AgentErrorMessages.Format(exception)); }
        }
        finally { RestoreDashboardFocus(previous, SettingsButton); }
    }

    private async void OpenAlertSettings(string? focusTargetKey = null) => await OpenAlertSettingsAsync(focusTargetKey);

    internal async Task OpenAlertSettingsAsync(string? focusTargetKey = null)
    {
        if (App.CurrentApp.IsExiting) { return; }
        var previous = Keyboard.FocusedElement;
        try
        {
            var original = await _viewModel.PrepareAlertSettingsEditAsync();
            if (App.CurrentApp.IsExiting) { return; }
            var dialog = new AlertSettingsWindow(
                original.Preferences.AlertSettings,
                original.Preferences.RemoteHosts,
                original.Configuration.Tasks,
                settings => _viewModel.ApplyAlertSettingsAsync(settings, original),
                focusTargetKey)
            {
                Owner = DialogOwner,
            };
            _ = dialog.ShowDialog();
        }
        catch (Exception exception)
        {
            if (!App.CurrentApp.IsExiting) { MessageDialog.ShowError(DialogOwner, "报警阈值读取失败", AgentErrorMessages.Format(exception)); }
        }
        finally { RestoreDashboardFocus(previous, SettingsButton); }
    }

    private async void OpenAutoRestartSettings(string? focusTaskName = null) => await OpenAutoRestartSettingsAsync(focusTaskName);

    internal async Task OpenAutoRestartSettingsAsync(string? focusTaskName = null)
    {
        if (App.CurrentApp.IsExiting) { return; }
        SelectTab(MainWindowViewModel.AutomationPage);
        await AutomationViewControl.RefreshAsync(focusTaskName);
    }

    private void UpdateWindowRootMaximizePadding()
    {
        if (WindowRootBorder is null) { return; }
        if (WindowState != WindowState.Maximized)
        {
            WindowRootBorder.Padding = new Thickness(0);
            WindowRootBorder.BorderThickness = new Thickness(1);
            return;
        }

        var left = 0d;
        var top = 0d;
        var right = 0d;
        var bottom = 0d;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0 && GetWindowRect(handle, out var rect))
        {
            var workArea = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            left = Math.Max(0, (workArea.Left - rect.Left) / dpi.DpiScaleX);
            top = Math.Max(0, (workArea.Top - rect.Top) / dpi.DpiScaleY);
            right = Math.Max(0, (rect.Right - workArea.Right) / dpi.DpiScaleX);
            bottom = Math.Max(0, (rect.Bottom - workArea.Bottom) / dpi.DpiScaleY);
        }

        if (left == 0 && top == 0 && right == 0 && bottom == 0)
        {
            var frame = SystemParameters.WindowResizeBorderThickness;
            left = frame.Left + SystemParameters.FixedFrameVerticalBorderWidth;
            right = frame.Right + SystemParameters.FixedFrameVerticalBorderWidth;
            top = frame.Top + SystemParameters.FixedFrameHorizontalBorderHeight;
            bottom = frame.Bottom + SystemParameters.FixedFrameHorizontalBorderHeight;
        }

        WindowRootBorder.BorderThickness = new Thickness(0);
        WindowRootBorder.Padding = new Thickness(left, top, right, bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect lpRect);
}
