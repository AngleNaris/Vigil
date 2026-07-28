namespace SystemAutorun.Core.Models;

public enum AutorunTaskType
{
    Windows,
    WslBoot,
    WslSystemd,
    WslCommand,
}

public enum HealthCheckType
{
    Process,
    Tcp,
    WslRunning,
    SystemdActive,
    WslProcess,
}

public enum RunAsMode
{
    CurrentUser,
    System,
}

public enum LaunchTrigger
{
    Manual,
    Logon,
    Boot,
}

public enum WindowMode
{
    Normal,
    Minimized,
    Hidden,
    NoConsole,
    Tray,
}

public enum ProcessLifecycle
{
    Detached,
    Tracked,
    Service,
}

public static class TaskEnumExtensions
{
    public static string ToKey(this AutorunTaskType value) => value switch
    {
        AutorunTaskType.Windows => "windows",
        AutorunTaskType.WslBoot => "wsl_boot",
        AutorunTaskType.WslSystemd => "wsl_systemd",
        AutorunTaskType.WslCommand => "wsl_command",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToKey(this WindowMode value) => value switch
    {
        WindowMode.Normal => "normal",
        WindowMode.Minimized => "minimized",
        WindowMode.Hidden => "hidden",
        WindowMode.NoConsole => "no_console",
        WindowMode.Tray => "tray",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
