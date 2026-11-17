using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using MaterialDesignThemes.Wpf;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Application = System.Windows.Application;
using Window = System.Windows.Window;
using UIElement = System.Windows.UIElement;
using DependencyObject = System.Windows.DependencyObject;

namespace SystemAutorun.App.Services;

internal enum ThemePreference { System, Dark, Light }

internal sealed record AccentPreset(
    string Id,
    string Label,
    string SwatchHex,
    AccentThemeColors Dark,
    AccentThemeColors Light);

internal sealed record AccentThemeColors(
    string Accent,
    string InteractionHover,
    string InteractionActiveHover,
    string InteractionOnActive,
    string InteractionOnHover,
    string ChartSecondary);

/// <summary>
/// R106/R131: dark and gray-white palettes plus customizable accent colors. Every semantic brush is one
/// shared resource; switching updates overrides in place so open windows recolor live without rebuilding.
/// Fault/critical semantic colors stay independent of the user's accent choice.
/// </summary>
internal static class ThemeService
{
    internal const string DefaultAccentId = "crimson";

    internal static readonly IReadOnlyList<AccentPreset> AccentPresets =
    [
        new("crimson", "绯红（默认）", "#E11D48",
            new("#E11D48", "#C41740", "#A61338", "#F84B68", "#E6E6EC", "#9E334D"),
            new("#BE123C", "#BE123C", "#9F1239", "#BE123C", "#FFFFFF", "#E16479")),
        new("cobalt", "电光蓝", "#3B82F6",
            new("#3B82F6", "#1D4ED8", "#1E40AF", "#60A5FA", "#E6E6EC", "#3B6998"),
            new("#1D4ED8", "#1D4ED8", "#1E3A8A", "#1D4ED8", "#FFFFFF", "#60A5FA")),
        new("cyan", "极光青", "#06B6D4",
            new("#06B6D4", "#0C667E", "#095266", "#22D3EE", "#E6E6EC", "#2A7B88"),
            new("#0C667E", "#0C667E", "#095266", "#0C667E", "#FFFFFF", "#38BDF8")),
        new("emerald", "翡翠绿", "#10B981",
            new("#10B981", "#046C4E", "#055A42", "#34D399", "#E6E6EC", "#2D7A5E"),
            new("#047857", "#047857", "#064E3B", "#047857", "#FFFFFF", "#34D399")),
        new("violet", "暗夜紫", "#8B5CF6",
            new("#8B5CF6", "#6D28D9", "#5B21B6", "#A78BFA", "#E6E6EC", "#6E4894"),
            new("#6D28D9", "#6D28D9", "#4C1D95", "#6D28D9", "#FFFFFF", "#A78BFA")),
        new("amber", "琥珀金", "#F59E0B",
            new("#F59E0B", "#A14807", "#7D3605", "#FBBF24", "#E6E6EC", "#9A6B28"),
            new("#A14807", "#A14807", "#7D3605", "#A14807", "#FFFFFF", "#F59E0B")),
    ];

    // Light values keep each dark role's position in the hierarchy: background < surface < hover,
    // text 100 > 300 > 500 > disabled, and the inverted primary action.
    private static readonly Dictionary<string, string> Light = new(StringComparer.Ordinal)
    {
        ["WindowBackground"] = "#F4F4F5", ["SurfacePrimary"] = "#FAFAFA", ["SurfaceInset"] = "#FFFFFF",
        ["SurfaceHover"] = "#E4E4E7", ["BorderStrong"] = "#C4C4CA", ["BorderSubtle"] = "#E1E1E5",
        ["TextPrimary"] = "#18181B", ["TextSecondary"] = "#52525B", ["TextHint"] = "#6B6B75", ["TextDisabled"] = "#A1A1AA",
        ["ActionPrimary"] = "#27272A", ["ActionPrimaryHover"] = "#09090B", ["ActionPrimaryPressed"] = "#52525B",
        ["FocusAccent"] = "#18181B", ["PrimaryForeground"] = "#FAFAFA", ["Selection"] = "#E9E9EC",
        ["Accent"] = "#BE123C",
        ["InteractionHover"] = "#BE123C", ["InteractionOnHover"] = "#FFFFFF",
        ["InteractionActive"] = "#E4E4E8", ["InteractionActiveHover"] = "#9F1239", ["InteractionOnActive"] = "#BE123C",
        ["InteractionRest"] = "#6B6B75", ["InteractionFocus"] = "#09090B", ["InteractionSelected"] = "#09090B",
        ["InteractionSelectedFocus"] = "#6B6B75", ["RowRest"] = "#52525B", ["RowSelected"] = "#09090B",
        ["RowMuted"] = "#A1A1AA", ["RowMutedSelected"] = "#6B6B75", ["RowFocusBorder"] = "#E1E1E5",
        ["IconRest"] = "#6B6B75", ["IconFocus"] = "#09090B", ["ToggleSelected"] = "#FAFAFA",
        ["ToggleSelectedFocus"] = "#A1A1AA", ["ToggleSelectedSurface"] = "#18181B", ["FocusBorderOnLight"] = "#A1A1AA",
        ["StatusHealthy"] = "#52525B", ["StatusFailed"] = "#BE123C", ["StatusDisabled"] = "#6B6B75",
        ["StatusWarning"] = "#8A5A00", ["StatusCritical"] = "#BE123C", ["StatusInfo"] = "#52525B",
        ["OnStatus"] = "#FFFFFF", ["TerminalBackground"] = "#FFFFFF", ["ChartSecondary"] = "#E16479", ["HistoryHealthy"] = "#52525B",
        ["DangerHover"] = "#FDE7EC", ["Scrim"] = "#18181B",
    };

    private static readonly string[] AccentOverrideKeys =
    [
        "Accent",
        "InteractionHover",
        "InteractionActiveHover",
        "InteractionOnActive",
        "InteractionOnHover",
        "ChartSecondary",
    ];

    private static bool _installed;

    internal static ThemePreference Preference { get; private set; } = ThemePreference.System;
    internal static string AccentPreference { get; private set; } = DefaultAccentId;
    internal static bool IsLight { get; private set; }

    internal static Color CurrentAccentColor => ResolveSwatchColor(AccentPreference);
    internal static string CurrentAccentLabel => AccentPresets.FirstOrDefault(item =>
        string.Equals(item.Id, AccentPreference, StringComparison.OrdinalIgnoreCase))?.Label
        ?? $"自定义 ({AccentPreference})";

    /// <summary>Raised after the palette or accent color changed; helpers holding copied colors refresh themselves.</summary>
    internal static event EventHandler? Changed;

    internal static string PreferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vigil", "appearance.json");

    internal static void Install(string? preferencePath = null)
    {
        if (_installed) { return; }
        _installed = true;
        var (theme, accent) = Load(preferencePath ?? PreferencePath);
        Preference = theme;
        AccentPreference = accent;
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == UserPreferenceCategory.General && Preference == ThemePreference.System)
            { Application.Current?.Dispatcher.BeginInvoke(new Action(() => Apply(Preference, AccentPreference, persist: null))); }
        };
        Apply(Preference, AccentPreference, persist: null);
    }

    internal static void Set(ThemePreference preference) => Apply(preference, AccentPreference, PreferencePath);

    internal static void SetAccent(string accent) => Apply(Preference, NormalizeAccentInput(accent), PreferencePath);

    internal static void Apply(ThemePreference preference, string? persist) =>
        Apply(preference, AccentPreference, persist);

    internal static void Apply(ThemePreference preference, string? accent, string? persist)
    {
        Preference = preference;
        AccentPreference = NormalizeAccentInput(accent);
        var light = preference == ThemePreference.Light || (preference == ThemePreference.System && SystemUsesLight());
        // Every XAML use is a DynamicResource, so overriding a key on Application.Resources (which wins
        // over the merged theme dictionaries) repaints open windows. Dark removes the overrides and the
        // original definitions in Semantics.xaml show again.
        var resources = Application.Current.Resources;
        foreach (var (key, value) in Light)
        {
            var name = "Semantic.Brush." + key;
            if (!light) { resources.Remove(name); continue; }
            var opacity = (resources.MergedDictionaries.Select(dictionary => FindIn(dictionary, name)).FirstOrDefault(found => found is not null)?.Opacity) ?? 1;
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)) { Opacity = opacity };
            brush.Freeze();
            resources[name] = brush;
        }

        if (!string.Equals(AccentPreference, DefaultAccentId, StringComparison.OrdinalIgnoreCase) || light)
        {
            var palette = ResolveAccentPalette(AccentPreference, light);
            var pairs = new (string Key, string Hex)[]
            {
                ("Accent", palette.Accent),
                ("InteractionHover", palette.InteractionHover),
                ("InteractionActiveHover", palette.InteractionActiveHover),
                ("InteractionOnActive", palette.InteractionOnActive),
                ("InteractionOnHover", palette.InteractionOnHover),
                ("ChartSecondary", palette.ChartSecondary),
            };
            foreach (var (key, hex) in pairs)
            {
                var name = "Semantic.Brush." + key;
                var opacity = (resources.MergedDictionaries.Select(dictionary => FindIn(dictionary, name)).FirstOrDefault(found => found is not null)?.Opacity) ?? 1;
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) { Opacity = opacity };
                brush.Freeze();
                resources[name] = brush;
            }
        }
        else
        {
            foreach (var key in AccentOverrideKeys) { resources.Remove("Semantic.Brush." + key); }
        }

        if (light != IsLight || persist is null)
        {
            IsLight = light;
            try
            {
                var helper = new PaletteHelper();
                var theme = helper.GetTheme();
                theme.SetBaseTheme(light ? BaseTheme.Light : BaseTheme.Dark);
                helper.SetTheme(theme);
            }
            catch (InvalidOperationException) { /* No MaterialDesign theme dictionary (isolated resource tests). */ }
        }
        if (persist is not null) { Save(persist, preference, AccentPreference); }
        Changed?.Invoke(null, EventArgs.Empty);
        // Self-drawn plots read brushes while rendering; repaint them. Application.Windows belongs to the
        // application thread (the only UI thread in the product; tests may call from another STA).
        if (Application.Current.Dispatcher.CheckAccess())
        { foreach (Window window in Application.Current.Windows) { RefreshVisuals(window); } }
    }

    internal static bool IsPresetAccent(string? accent) =>
        AccentPresets.Any(preset => string.Equals(preset.Id, accent, StringComparison.OrdinalIgnoreCase));

    internal static string NormalizeAccentInput(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) { return DefaultAccentId; }
        var trimmed = raw.Trim();
        var preset = AccentPresets.FirstOrDefault(item => string.Equals(item.Id, trimmed, StringComparison.OrdinalIgnoreCase));
        if (preset is not null) { return preset.Id; }
        return TryNormalizeHex(trimmed, out var normalized) ? normalized : DefaultAccentId;
    }

    internal static bool TryNormalizeHex(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) { return false; }
        var text = raw.Trim();
        if (text.StartsWith('#')) { text = text[1..]; }
        if (text.Length != 6 || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }
        normalized = "#" + text.ToUpperInvariant();
        return true;
    }

    internal static bool TryParseColor(string? raw, out Color color)
    {
        color = default;
        if (!TryNormalizeHex(raw, out var normalized)) { return false; }
        color = (Color)ColorConverter.ConvertFromString(normalized);
        return true;
    }

    internal static Color ResolveSwatchColor(string? accent)
    {
        var normalized = NormalizeAccentInput(accent);
        var preset = AccentPresets.FirstOrDefault(item => string.Equals(item.Id, normalized, StringComparison.OrdinalIgnoreCase));
        var hex = preset?.SwatchHex ?? normalized;
        return (Color)ColorConverter.ConvertFromString(hex);
    }

    internal static AccentThemeColors ResolveAccentPalette(string? accent, bool light)
    {
        var normalized = NormalizeAccentInput(accent);
        var preset = AccentPresets.FirstOrDefault(item => string.Equals(item.Id, normalized, StringComparison.OrdinalIgnoreCase));
        if (preset is not null)
        {
            return light ? preset.Light : preset.Dark;
        }

        var baseColor = (Color)ColorConverter.ConvertFromString(normalized);
        return DeriveCustomPalette(baseColor, light);
    }

    private static AccentThemeColors DeriveCustomPalette(Color baseColor, bool light)
    {
        var (h, s, l) = RgbToHsl(baseColor);
        if (light)
        {
            var hoverColor = EnsureContrastAgainstLight(h, s, Math.Min(l, 0.48), Color.FromRgb(0xFF, 0xFF, 0xFF), 4.8);
            hoverColor = EnsureContrastAgainstLight(h, s, RgbToHsl(hoverColor).L, Color.FromRgb(0xE4, 0xE4, 0xE8), 4.5);
            var (hh, hs, hl) = RgbToHsl(hoverColor);
            var activeHover = HslToRgb(hh, hs, Math.Max(0.10, hl * 0.78));
            var secondary = Blend(baseColor, Color.FromRgb(0xFF, 0xFF, 0xFF), 0.38);
            var accentHex = FormatHex(hoverColor);
            return new(
                accentHex,
                accentHex,
                FormatHex(activeHover),
                accentHex,
                "#FFFFFF",
                FormatHex(secondary));
        }
        else
        {
            var onHover = Color.FromRgb(0xE6, 0xE6, 0xEC);
            var activeSurface = Color.FromRgb(0x24, 0x24, 0x2B);
            var accent = EnsureContrastAgainstDark(h, s, Math.Max(l, 0.45), Color.FromRgb(0x14, 0x14, 0x17), 3.6);
            var hoverColor = EnsureContrastAgainstLight(h, Math.Max(s, 0.45), Math.Min(l, 0.45), onHover, 4.7);
            var (hh, hs, hl) = RgbToHsl(hoverColor);
            var activeHover = HslToRgb(hh, hs, Math.Max(0.10, hl * 0.78));
            var onActive = EnsureContrastAgainstDark(h, s, Math.Max(l, 0.56), activeSurface, 4.7);
            var secondary = Blend(accent, Color.FromRgb(0x14, 0x14, 0x17), 0.40);
            return new(
                FormatHex(accent),
                FormatHex(hoverColor),
                FormatHex(activeHover),
                FormatHex(onActive),
                "#E6E6EC",
                FormatHex(secondary));
        }
    }

    private static Color EnsureContrastAgainstLight(double h, double s, double initialL, Color ink, double minimumContrast)
    {
        var lo = 0.05;
        var hi = Math.Clamp(initialL, 0.05, 0.90);
        var candidate = HslToRgb(h, s, hi);
        if (Contrast(candidate, ink) >= minimumContrast) { return candidate; }
        for (var step = 0; step < 14; step++)
        {
            var mid = (lo + hi) * 0.5;
            candidate = HslToRgb(h, s, mid);
            if (Contrast(candidate, ink) >= minimumContrast) { lo = mid; }
            else { hi = mid; }
        }
        return HslToRgb(h, s, lo);
    }

    private static Color EnsureContrastAgainstDark(double h, double s, double initialL, Color surface, double minimumContrast)
    {
        var lo = Math.Clamp(initialL, 0.15, 0.95);
        var hi = 0.92;
        var candidate = HslToRgb(h, s, lo);
        if (Contrast(candidate, surface) >= minimumContrast) { return candidate; }
        for (var step = 0; step < 14; step++)
        {
            var mid = (lo + hi) * 0.5;
            candidate = HslToRgb(h, s, mid);
            if (Contrast(candidate, surface) >= minimumContrast) { hi = mid; }
            else { lo = mid; }
        }
        return HslToRgb(h, s, hi);
    }

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value) { var c = value / 255d; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static Color Blend(Color a, Color b, double weightB) => Color.FromRgb(
        (byte)Math.Clamp(Math.Round(a.R * (1 - weightB) + b.R * weightB), 0, 255),
        (byte)Math.Clamp(Math.Round(a.G * (1 - weightB) + b.G * weightB), 0, 255),
        (byte)Math.Clamp(Math.Round(a.B * (1 - weightB) + b.B * weightB), 0, 255));

    private static string FormatHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static (double H, double S, double L) RgbToHsl(Color color)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) * 0.5;
        if (Math.Abs(max - min) < 1e-6) { return (0d, 0d, l); }
        var d = max - min;
        var s = l > 0.5 ? d / (2d - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6d : 0d)
            : max == g ? (b - r) / d + 2d
            : (r - g) / d + 4d;
        return (h / 6d, s, l);
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        if (s <= 1e-6)
        {
            var gray = (byte)Math.Clamp(Math.Round(l * 255d), 0, 255);
            return Color.FromRgb(gray, gray, gray);
        }
        static double HueToRgb(double p, double q, double t)
        {
            if (t < 0d) { t += 1d; }
            if (t > 1d) { t -= 1d; }
            if (t < 1d / 6d) { return p + (q - p) * 6d * t; }
            if (t < 0.5d) { return q; }
            if (t < 2d / 3d) { return p + (q - p) * (2d / 3d - t) * 6d; }
            return p;
        }
        var q = l < 0.5 ? l * (1d + s) : l + s - l * s;
        var p = 2d * l - q;
        return Color.FromRgb(
            (byte)Math.Clamp(Math.Round(HueToRgb(p, q, h + 1d / 3d) * 255d), 0, 255),
            (byte)Math.Clamp(Math.Round(HueToRgb(p, q, h) * 255d), 0, 255),
            (byte)Math.Clamp(Math.Round(HueToRgb(p, q, h - 1d / 3d) * 255d), 0, 255));
    }

    private static SolidColorBrush? FindIn(System.Windows.ResourceDictionary dictionary, string key) =>
        dictionary.Contains(key) && dictionary[key] is SolidColorBrush brush ? brush
        : dictionary.MergedDictionaries.Select(child => FindIn(child, key)).FirstOrDefault(found => found is not null);

    private static void RefreshVisuals(DependencyObject root)
    {
        if (root is UIElement element) { element.InvalidateVisual(); }
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        { RefreshVisuals(System.Windows.Media.VisualTreeHelper.GetChild(root, index)); }
    }

    internal static bool SystemUsesLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { return false; }
    }

    private static (ThemePreference Theme, string Accent) Load(string path)
    {
        try
        {
            if (!File.Exists(path)) { return (ThemePreference.System, DefaultAccentId); }
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var theme = document.RootElement.TryGetProperty("theme", out var value)
                && Enum.TryParse<ThemePreference>(value.GetString(), ignoreCase: true, out var parsed) ? parsed : ThemePreference.System;
            var accent = document.RootElement.TryGetProperty("accent", out var accentValue)
                ? NormalizeAccentInput(accentValue.GetString())
                : DefaultAccentId;
            return (theme, accent);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { return (ThemePreference.System, DefaultAccentId); }
    }

    private static readonly object PreferenceGate = new();

    internal static System.Windows.Size? LoadWindowSize()
    {
        try
        {
            if (!File.Exists(PreferencePath)) { return null; }
            using var document = JsonDocument.Parse(File.ReadAllText(PreferencePath));
            if (document.RootElement.TryGetProperty("window_width", out var width)
                && document.RootElement.TryGetProperty("window_height", out var height)
                && width.TryGetDouble(out var w) && height.TryGetDouble(out var h)
                && double.IsFinite(w) && double.IsFinite(h) && w >= 400 && h >= 300 && w <= 16384 && h <= 16384)
            { return new System.Windows.Size(w, h); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { System.Diagnostics.Trace.TraceWarning("Window size could not be read: {0}", exception.Message); }
        return null;
    }

    internal static bool SaveWindowSize(double width, double height) => Patch(PreferencePath, document =>
    {
        document["window_width"] = width;
        document["window_height"] = height;
    });

    private static void Save(string path, ThemePreference preference, string accent) =>
        Patch(path, document =>
        {
            document["theme"] = preference.ToString().ToLowerInvariant();
            document["accent"] = NormalizeAccentInput(accent);
        });

    // Theme, accent and window geometry share the existing per-user appearance file, not Agent-owned root settings.
    private static bool Patch(string path, Action<JsonObject> patch)
    {
        lock (PreferenceGate)
        {
            string? temporary = null;
            try
            {
                var document = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : new JsonObject();
                if (document is null) { return false; }
                patch(document);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, document.ToJsonString());
                File.Move(temporary, path, overwrite: true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            { System.Diagnostics.Trace.TraceWarning("GUI appearance could not be saved: {0}", exception.Message); return false; }
            finally
            {
                if (temporary is not null)
                {
                    try { File.Delete(temporary); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    { System.Diagnostics.Trace.TraceWarning("GUI preference temporary file could not be removed: {0}", exception.Message); }
                }
            }
        }
    }
}
