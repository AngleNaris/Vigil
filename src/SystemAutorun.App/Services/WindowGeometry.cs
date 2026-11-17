using System.Windows.Threading;
using Window = System.Windows.Window;

namespace SystemAutorun.App.Services;

/// <summary>Per-user main-window geometry; never writes Agent-owned configuration or persists minimized bounds.</summary>
internal static class WindowGeometry
{
    internal static void Attach(Window window)
    {
        var stored = ThemeService.LoadWindowSize();
        var area = SystemParameters.WorkArea;
        window.MinWidth = Math.Min(window.MinWidth, area.Width);
        window.MinHeight = Math.Min(window.MinHeight, area.Height);
        if (stored is { } size)
        {
            window.Width = Math.Clamp(size.Width, window.MinWidth, area.Width);
            window.Height = Math.Clamp(size.Height, window.MinHeight, area.Height);
        }
        else
        {
            var scale = Math.Min(1, Math.Min(area.Width / window.Width, area.Height / window.Height));
            window.Width = Math.Max(window.MinWidth, window.Width * scale);
            window.Height = Math.Max(window.MinHeight, window.Height * scale);
        }
        var ready = false;
        var rememberedWidth = 0d;
        var rememberedHeight = 0d;
        var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(650) };
        timer.Tick += (_, _) => { timer.Stop(); Save(); };
        window.Loaded += (_, _) =>
        {
            if (ready) { return; }
            ready = true;
            rememberedWidth = window.ActualWidth;
            rememberedHeight = window.ActualHeight;
        };
        window.SizeChanged += (_, _) =>
        {
            if (!ready || window.WindowState != WindowState.Normal) { return; }
            timer.Stop();
            timer.Start();
        };
        window.IsVisibleChanged += (_, args) => { if (args.NewValue is false) { timer.Stop(); Save(); } };
        window.Closing += (_, _) => { timer.Stop(); Save(); };
        window.Closed += (_, _) => timer.Stop();

        void Save()
        {
            if (!ready) { return; }
            var bounds = window.RestoreBounds;
            var width = window.WindowState == WindowState.Normal ? window.ActualWidth : bounds.Width;
            var height = window.WindowState == WindowState.Normal ? window.ActualHeight : bounds.Height;
            if (!double.IsFinite(width) || !double.IsFinite(height) || width < 400 || height < 300
                || width > 16384 || height > 16384
                || Math.Abs(width - rememberedWidth) < .5 && Math.Abs(height - rememberedHeight) < .5) { return; }
            if (ThemeService.SaveWindowSize(width, height)) { rememberedWidth = width; rememberedHeight = height; }
        }
    }
}
