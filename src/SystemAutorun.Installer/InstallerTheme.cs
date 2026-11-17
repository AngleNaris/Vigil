using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace SystemAutorun.Installer;

internal static class InstallerTheme
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmDoNotRound = 1;

    internal static bool IsLight { get; private set; }

    internal static void Apply(ResourceDictionary resources, bool? forceLight = null)
    {
        var light = forceLight ?? ShouldUseLightTheme();
        IsLight = light;

        var palette = light
            ? new Dictionary<string, string>
            {
                ["WindowBackground"] = "#F4F4F5",
                ["Surface"] = "#FAFAFA",
                ["SurfaceInset"] = "#FFFFFF",
                ["SurfaceHover"] = "#E4E4E7",
                ["Border"] = "#E1E1E5",
                ["BorderStrong"] = "#C4C4CA",
                ["TextPrimary"] = "#18181B",
                ["TextSecondary"] = "#52525B",
                ["TextMuted"] = "#6B6B75",
                ["TextDisabled"] = "#A1A1AA",
                ["ActionPrimary"] = "#27272A",
                ["PrimaryForeground"] = "#FAFAFA",
                ["FocusAccent"] = "#18181B",
                ["Accent"] = "#BE123C",
                ["AccentHover"] = "#BE123C",
                ["AccentPressed"] = "#9F1239",
                ["OnAccent"] = "#FFFFFF",
            }
            : new Dictionary<string, string>
            {
                ["WindowBackground"] = "#141417",
                ["Surface"] = "#18181C",
                ["SurfaceInset"] = "#1C1C21",
                ["SurfaceHover"] = "#26262D",
                ["Border"] = "#2B2B33",
                ["BorderStrong"] = "#3B3B45",
                ["TextPrimary"] = "#D4D4DC",
                ["TextSecondary"] = "#A1A1AA",
                ["TextMuted"] = "#8B8B95",
                ["TextDisabled"] = "#5C5C66",
                ["ActionPrimary"] = "#D4D4DC",
                ["PrimaryForeground"] = "#141417",
                ["FocusAccent"] = "#D4D4DC",
                ["Accent"] = "#E11D48",
                ["AccentHover"] = "#C41740",
                ["AccentPressed"] = "#A61338",
                ["OnAccent"] = "#E6E6EC",
            };

        foreach (var (key, hex) in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }
    }

    internal static void ApplyWindowFrame(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var style = GetWindowLongPtr(handle, -16).ToInt64();
        const long wsCaption = 0x00C00000L;
        const long wsSysMenu = 0x00080000L;
        const long wsMinimizeBox = 0x00020000L;
        var updated = new IntPtr(style | wsCaption | wsSysMenu | wsMinimizeBox);
        SetWindowLongPtr(handle, -16, updated);

        var dark = IsLight ? 0 : 1;
        DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        var corner = DwmDoNotRound;
        DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref corner, sizeof(int));
    }

    private static bool ShouldUseLightTheme()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appearancePath = Path.Combine(localAppData, "Vigil", "appearance.json");
            if (!File.Exists(appearancePath))
            {
                appearancePath = Path.Combine(localAppData, "SystemAutorun", "appearance.json");
            }
            if (File.Exists(appearancePath))
            {
                var node = JsonNode.Parse(File.ReadAllText(appearancePath));
                var pref = node?["theme"]?.GetValue<string>();
                if (string.Equals(pref, "Light", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(pref, "Dark", StringComparison.OrdinalIgnoreCase)) return false;
            }
        }
        catch
        {
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value != 0;
            }
        }
        catch
        {
        }

        return false;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
