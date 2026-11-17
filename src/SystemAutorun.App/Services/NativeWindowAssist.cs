using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Shell;

namespace SystemAutorun.App.Services;

internal static class NativeWindowAssist
{
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C0_0000;
    private const long WsSysMenu = 0x0008_0000;
    private const long WsThickFrame = 0x0004_0000;
    private const long WsMinimizeBox = 0x0002_0000;
    private const long WsMaximizeBox = 0x0001_0000;

    private const int DwmNonClientRenderingPolicy = 2;
    private const int DwmNonClientRenderingEnabled = 2;
    private const int DwmTransitionsForceDisabled = 3;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DoNotRound = 1;

    private static readonly DependencyProperty ThemeSubscriptionProperty =
        DependencyProperty.RegisterAttached(
            "ThemeSubscription",
            typeof(EventHandler),
            typeof(NativeWindowAssist),
            new PropertyMetadata(null));

    public static readonly DependencyProperty UseSquareDarkChromeProperty =
        DependencyProperty.RegisterAttached(
            "UseSquareDarkChrome",
            typeof(bool),
            typeof(NativeWindowAssist),
            new PropertyMetadata(false, OnUseSquareDarkChromeChanged));

    public static bool GetUseSquareDarkChrome(DependencyObject element) =>
        (bool)element.GetValue(UseSquareDarkChromeProperty);

    public static void SetUseSquareDarkChrome(DependencyObject element, bool value) =>
        element.SetValue(UseSquareDarkChromeProperty, value);

    private static void OnUseSquareDarkChromeChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not Window window || eventArgs.NewValue is not true)
        {
            return;
        }

        if (new WindowInteropHelper(window).Handle != 0)
        {
            ApplyNativeChrome(window);
        }
        else
        {
            window.SourceInitialized += OnWindowSourceInitialized;
        }
    }

    private static void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window) { return; }
        window.SourceInitialized -= OnWindowSourceInitialized;
        ApplyNativeChrome(window);
    }

    private static void ApplyNativeChrome(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) { return; }

        ApplyThemeAttribute(handle);
        AttachThemeListener(window);

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var preference = DoNotRound;
            _ = DwmSetWindowAttribute(
                handle,
                DwmWindowCornerPreference,
                ref preference,
                sizeof(int));
        }

        if (window.AllowsTransparency)
        {
            return;
        }

        if (WindowChrome.GetWindowChrome(window) is { } chrome && chrome.GlassFrameThickness == new Thickness(0))
        {
            chrome.GlassFrameThickness = new Thickness(0, 0, 0, 1);
        }

        var currentStyle = GetWindowLongPtr(handle, GwlStyle).ToInt64();
        var updatedStyle = currentStyle | WsCaption | WsSysMenu;
        if (window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
        {
            updatedStyle |= WsMinimizeBox | WsMaximizeBox | WsThickFrame;
        }
        else if (window.ResizeMode == ResizeMode.CanMinimize)
        {
            updatedStyle |= WsMinimizeBox;
        }

        if (updatedStyle != currentStyle)
        {
            _ = SetWindowLongPtr(handle, GwlStyle, (nint)updatedStyle);
        }

        var ncPolicy = DwmNonClientRenderingEnabled;
        _ = DwmSetWindowAttribute(handle, DwmNonClientRenderingPolicy, ref ncPolicy, sizeof(int));

        var transitionsDisabled = 0;
        _ = DwmSetWindowAttribute(handle, DwmTransitionsForceDisabled, ref transitionsDisabled, sizeof(int));

        var margins = new Margins { Bottom = 1 };
        _ = DwmExtendFrameIntoClientArea(handle, ref margins);
    }

    private static void AttachThemeListener(Window window)
    {
        if (window.GetValue(ThemeSubscriptionProperty) is not null) { return; }

        EventHandler onThemeChanged = (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != 0)
            {
                ApplyThemeAttribute(handle);
            }
        };
        window.SetValue(ThemeSubscriptionProperty, onThemeChanged);
        ThemeService.Changed += onThemeChanged;
        window.Closed += OnWindowClosed;
    }

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window) { return; }
        window.Closed -= OnWindowClosed;
        if (window.GetValue(ThemeSubscriptionProperty) is EventHandler handler)
        {
            ThemeService.Changed -= handler;
            window.ClearValue(ThemeSubscriptionProperty);
        }
    }

    private static void ApplyThemeAttribute(nint handle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) { return; }
        var darkMode = ThemeService.IsLight ? 0 : 1;
        if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref darkMode, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(
                handle,
                DwmUseImmersiveDarkModeBefore20H1,
                ref darkMode,
                sizeof(int));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(
        nint windowHandle,
        ref Margins margins);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
}
