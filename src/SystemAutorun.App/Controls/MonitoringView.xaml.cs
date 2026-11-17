using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using Binding = System.Windows.Data.Binding;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace SystemAutorun.App.Controls;

/// <summary>Host resources and aggregated exception logs, hosted as tabs of the main window.</summary>
public partial class MonitoringView : System.Windows.Controls.UserControl
{
    private HostTabViewModel? _dragHost;
    private readonly ListReorderDragController<HostTabViewModel> _hostDragController;

    public MonitoringView()
    {
        InitializeComponent();
        _hostDragController = new ListReorderDragController<HostTabViewModel>(
            HostList,
            () => ViewModel?.Hosts ?? (IList<HostTabViewModel>)[],
            () => 1,
            () => ViewModel is { IsBusy: false },
            (from, to) => ViewModel?.Hosts.Move(from, to),
            item => { if (ViewModel is { } vm) { vm.SelectedHostTab = item; } },
            () => ViewModel is { } vm
                ? PersistHostOrderAsync(vm, vm.Hosts.Where(item => !item.IsLocal).Select(item => item.Id).ToArray())
                : Task.CompletedTask);

        // No host animation: its leaves own the clocks. Explicit sources cross the UserControl
        // namescope boundary and keep the host visible until the last leaf has finished leaving.
        var presence = new Style(typeof(MonitoringView), Style);
        presence.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        foreach (var leaf in new[] { HostPage, ExceptionsPage })
        {
            presence.Triggers.Add(new DataTrigger
            {
                Binding = new Binding(nameof(Visibility)) { Source = leaf }, Value = Visibility.Visible,
                Setters = { new Setter(VisibilityProperty, Visibility.Visible) },
            });
        }
        Style = presence;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    internal void FocusExceptionSearch()
    {
        ExceptionLogSearchBox.Focus();
        ExceptionLogSearchBox.SelectAll();
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

    private void HostList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) { return; }
        var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is HostTabViewModel host) { viewModel.SelectedHostTab = host; }
    }

    private void HostList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            e.Handled = true;
            return;
        }
        HostTabViewModel? target;
        if (e.CursorLeft < 0 && e.CursorTop < 0)
        {
            target = viewModel.SelectedHostTab;
        }
        else
        {
            var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            target = row?.DataContext as HostTabViewModel;
        }
        if (target is null)
        {
            e.Handled = true;
            return;
        }

        if (HostList.ContextMenu is { } menu)
        {
            var remoteVisibility = target.IsLocal ? Visibility.Collapsed : Visibility.Visible;
            foreach (var item in menu.Items)
            {
                if (item is System.Windows.Controls.MenuItem menuItem && menuItem.Name is "EditHostMenuItem" or "RemoveHostMenuItem")
                {
                    menuItem.Visibility = remoteVisibility;
                }
                else if (item is Separator separator && separator.Name is "HostAlertMenuSeparator" or "HostMenuSeparator")
                {
                    separator.Visibility = remoteVisibility;
                }
            }
        }
    }

    private void HostList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _hostDragController.IsDragging || DataContext is not MainWindowViewModel viewModel) { return; }
        var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is HostTabViewModel { IsLocal: false } host && viewModel.EditRemoteHostCommand.CanExecute(host))
        {
            viewModel.EditRemoteHostCommand.Execute(host);
            e.Handled = true;
        }
    }

    private void HostList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _hostDragController.HandlePreviewMouseLeftButtonDown(e);
        _dragHost = _hostDragController.DragItem;
    }

    private void HostList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        _hostDragController.HandlePreviewMouseMove(e);
        _dragHost = _hostDragController.DragItem;
    }

    private void HostList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _hostDragController.FinishDrag();
        _dragHost = null;
    }

    private void HostList_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _hostDragController.FinishDrag();
        _dragHost = null;
    }

    internal bool UpdateDragOrder(MainWindowViewModel viewModel, double pointerY)
    {
        _ = viewModel;
        if (_dragHost is not null && !ReferenceEquals(_hostDragController.DragItem, _dragHost))
        {
            _hostDragController.SeedTestDragItem(_dragHost);
        }
        return _hostDragController.TryUpdateOrder(pointerY);
    }

    private static async Task PersistHostOrderAsync(MainWindowViewModel viewModel, IReadOnlyList<string> orderedIds)
    {
        try { await viewModel.ReorderRemoteHostsAsync(orderedIds); }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning("Remote host reorder failed: {0}", exception.Message);
        }
    }
}
