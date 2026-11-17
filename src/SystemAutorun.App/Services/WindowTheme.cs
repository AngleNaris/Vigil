using System.Windows.Input;

namespace SystemAutorun.App.Services;

/// <summary>Implemented by a window that dims itself while any of its dialogs is open.</summary>
internal interface IDialogScrimHost
{
    void Track(Window dialog);
}

/// <summary>Implicit styles do not fall back from a derived Window to the Window key.</summary>
internal static class WindowTheme
{
    private static bool _installed;

    internal static void Install()
    {
        if (_installed) { return; }
        _installed = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, args) =>
            {
                if (sender is not Window window || !ReferenceEquals(args.OriginalSource, window)) { return; }
                Apply(window);
                ToolWindowLayout.Apply(window);
                // R106: owned tool windows ease in; the main window opens directly.
                if (window is not ToolWindow && window.Owner is not null && window.Content is FrameworkElement body && !SurfaceMotion.GetFadeOnShow(body))
                { SurfaceMotion.SetFadeOnShow(body, true); }
                // R105: a tool dialog dims the main window it ultimately belongs to.
                for (var owner = window.Owner; owner is not null; owner = owner.Owner)
                { if (owner is IDialogScrimHost host) { host.Track(window); } }
            }));
        // Shared self-drawn dialog captions close through one command instead of per-window handlers.
        CommandManager.RegisterClassCommandBinding(typeof(Window), new CommandBinding(SystemCommands.CloseWindowCommand,
            (sender, _) => { if (sender is Window window) { window.Close(); } }));
    }

    internal static void Apply(Window window)
    {
        // Respect a dialog's deliberately supplied style; otherwise use the one
        // shared recipe rather than maintaining per-window copies and overrides.
        if (window.Style is null && window.ReadLocalValue(FrameworkElement.StyleProperty) == DependencyProperty.UnsetValue
            && window.TryFindResource(typeof(Window)) is Style)
        {
            window.SetResourceReference(FrameworkElement.StyleProperty, typeof(Window));
        }
    }
}
