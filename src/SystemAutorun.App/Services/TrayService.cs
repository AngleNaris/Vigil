using System.Runtime.InteropServices;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DrawingIcon = System.Drawing.Icon;
using FormsMouseButtons = System.Windows.Forms.MouseButtons;
using FormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using WpfApplication = System.Windows.Application;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfSeparator = System.Windows.Controls.Separator;

namespace SystemAutorun.App.Services;

internal sealed class TrayService : IDisposable
{
    private const int WmActivate = 0x0006;
    private const int WaInactive = 0;

    private readonly FormsNotifyIcon _notifyIcon;
    private readonly WpfContextMenu _contextMenu;
    private readonly Dispatcher _dispatcher;
    private HwndSource? _popupSource;
    private bool _disposed;

    internal TrayService(
        Action show,
        Action refresh,
        Action doctor,
        Action openLogs,
        Action exit)
    {
        _dispatcher = WpfApplication.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _contextMenu = new WpfContextMenu
        {
            Placement = PlacementMode.MousePoint,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            FontSize = 13,
        };
        if (WpfApplication.Current?.TryFindResource("Semantic.Font.Interface") is WpfFontFamily fontFamily)
        {
            _contextMenu.FontFamily = fontFamily;
        }
        TextOptions.SetTextFormattingMode(_contextMenu, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(_contextMenu, TextRenderingMode.ClearType);

        _contextMenu.Items.Add(CreateItem("打开 Vigil", show));
        _contextMenu.Items.Add(CreateItem("刷新任务状态", refresh));
        _contextMenu.Items.Add(CreateItem("系统诊断", doctor));
        _contextMenu.Items.Add(CreateItem("打开日志", openLogs));
        _contextMenu.Items.Add(new WpfSeparator());
        _contextMenu.Items.Add(CreateItem("退出", exit));
        _contextMenu.Opened += ContextMenu_Opened;
        _contextMenu.Closed += ContextMenu_Closed;
        ThemeService.Changed += OnThemeChanged;

        _notifyIcon = new FormsNotifyIcon
        {
            Text = "Vigil 家庭服务器控制台",
            Icon = DrawingIcon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => show();
        _notifyIcon.MouseUp += (_, args) =>
        {
            if (args.Button != FormsMouseButtons.Right || _disposed) { return; }
            _ = _dispatcher.BeginInvoke(() =>
            {
                if (_disposed) { return; }
                if (_contextMenu.IsOpen) { _contextMenu.IsOpen = false; }
                _contextMenu.Placement = PlacementMode.MousePoint;
                _contextMenu.IsOpen = true;
            });
        };
    }

    internal WpfContextMenu ContextMenu => _contextMenu;

    internal void ShowInfo(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(2500);
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        ThemeService.Changed -= OnThemeChanged;
        _contextMenu.Opened -= ContextMenu_Opened;
        _contextMenu.Closed -= ContextMenu_Closed;
        DetachPopupHook();
        if (_dispatcher.CheckAccess())
        {
            _contextMenu.IsOpen = false;
        }
        else if (!_dispatcher.HasShutdownStarted)
        {
            _ = _dispatcher.BeginInvoke(() => _contextMenu.IsOpen = false);
        }
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private WpfMenuItem CreateItem(string header, Action action)
    {
        var item = new WpfMenuItem { Header = header };
        item.Click += (_, _) =>
        {
            _contextMenu.IsOpen = false;
            _ = _dispatcher.BeginInvoke(action);
        };
        return item;
    }

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        DetachPopupHook();
        if (PresentationSource.FromVisual(_contextMenu) is HwndSource source)
        {
            _popupSource = source;
            source.AddHook(PopupWindowHook);
            _ = SetForegroundWindow(source.Handle);
            _contextMenu.Focus();
        }
    }

    private void ContextMenu_Closed(object sender, RoutedEventArgs e) => DetachPopupHook();

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) { return; }
        if (_dispatcher.CheckAccess())
        {
            RefreshMenuResources();
        }
        else
        {
            _ = _dispatcher.BeginInvoke(RefreshMenuResources);
        }
    }

    private void RefreshMenuResources()
    {
        if (_disposed) { return; }
        _contextMenu.Style = null;
        _contextMenu.SetResourceReference(FrameworkElement.StyleProperty, typeof(WpfContextMenu));
        foreach (var entry in _contextMenu.Items)
        {
            if (entry is WpfMenuItem item)
            {
                item.Style = null;
                item.SetResourceReference(FrameworkElement.StyleProperty, typeof(WpfMenuItem));
            }
            else if (entry is WpfSeparator separator)
            {
                separator.Style = null;
                separator.SetResourceReference(FrameworkElement.StyleProperty, WpfMenuItem.SeparatorStyleKey);
            }
        }
        var transient = new ResourceDictionary();
        _contextMenu.Resources.MergedDictionaries.Add(transient);
        _contextMenu.Resources.MergedDictionaries.Remove(transient);
        _contextMenu.InvalidateVisual();
    }

    private void DetachPopupHook()
    {
        if (_popupSource is { IsDisposed: false } source)
        {
            source.RemoveHook(PopupWindowHook);
        }
        _popupSource = null;
    }

    private nint PopupWindowHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmActivate && (wParam.ToInt64() & 0xFFFF) == WaInactive && _contextMenu.IsOpen)
        {
            _contextMenu.IsOpen = false;
        }
        return 0;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);
}
