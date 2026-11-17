using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SystemAutorun.App.ViewModels;
using Binding = System.Windows.Data.Binding;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace SystemAutorun.App.Controls;

/// <summary>Host resources and aggregated exception logs, hosted as tabs of the main window.</summary>
public partial class MonitoringView : System.Windows.Controls.UserControl
{
    private HostTabViewModel? _dragHost;
    private Point _dragStart;
    private bool _draggingHost;
    private bool _hostOrderChanged;

    public MonitoringView()
    {
        InitializeComponent();
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
        if (target is null || target.IsLocal)
        {
            e.Handled = true;
        }
    }

    private void HostList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _draggingHost || DataContext is not MainWindowViewModel viewModel) { return; }
        var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is HostTabViewModel { IsLocal: false } host && viewModel.EditRemoteHostCommand.CanExecute(host))
        {
            viewModel.EditRemoteHostCommand.Execute(host);
            e.Handled = true;
        }
    }

    private void HostList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ResetDragState();
        if (e.ChangedButton != MouseButton.Left || DataContext is not MainWindowViewModel viewModel || viewModel.IsBusy) { return; }
        var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is HostTabViewModel { IsLocal: false } host && viewModel.Hosts.Count > 2)
        {
            _dragHost = host;
            _dragStart = e.GetPosition(HostList);
        }
    }

    private void HostList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragHost is null || DataContext is not MainWindowViewModel viewModel) { return; }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishDrag(viewModel);
            return;
        }

        var current = e.GetPosition(HostList);
        if (!_draggingHost)
        {
            if (Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) { return; }
            _draggingHost = true;
            _ = HostList.CaptureMouse();
        }

        UpdateDragOrder(viewModel, current.Y);
    }

    private void HostList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel) { FinishDrag(viewModel); }
        else { ResetDragState(); }
    }

    private void HostList_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel) { FinishDrag(viewModel); }
        else { ResetDragState(); }
    }

    internal bool UpdateDragOrder(MainWindowViewModel viewModel, double pointerY)
    {
        if (_dragHost is null) { return false; }
        var currentIndex = viewModel.Hosts.IndexOf(_dragHost);
        if (currentIndex < 1) { return false; }
        var targetIndex = currentIndex;
        for (var i = 1; i < viewModel.Hosts.Count; i++)
        {
            if (i == currentIndex) { continue; }
            if (HostList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container || !container.IsVisible) { continue; }
            var top = container.TranslatePoint(new Point(0, 0), HostList).Y;
            var height = container.ActualHeight;
            if (i < currentIndex && pointerY < top + height * 0.45)
            {
                targetIndex = i;
                break;
            }
            if (i > currentIndex && pointerY > top + height * 0.55)
            {
                targetIndex = i;
            }
        }
        targetIndex = Math.Clamp(targetIndex, 1, viewModel.Hosts.Count - 1);
        if (targetIndex == currentIndex) { return false; }
        viewModel.Hosts.Move(currentIndex, targetIndex);
        viewModel.SelectedHostTab = _dragHost;
        _hostOrderChanged = true;
        return true;
    }

    private void FinishDrag(MainWindowViewModel viewModel)
    {
        var shouldPersist = _hostOrderChanged;
        ResetDragState();
        if (HostList.IsMouseCaptured) { HostList.ReleaseMouseCapture(); }
        if (!shouldPersist) { return; }
        var orderedIds = viewModel.Hosts.Where(item => !item.IsLocal).Select(item => item.Id).ToArray();
        _ = PersistHostOrderAsync(viewModel, orderedIds);
    }

    private void ResetDragState()
    {
        _dragHost = null;
        _draggingHost = false;
        _hostOrderChanged = false;
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
