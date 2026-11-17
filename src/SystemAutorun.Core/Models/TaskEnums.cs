namespace SystemAutorun.Core.Models;

public enum AutorunTaskType
{
    Windows,
    WindowsService,
    WslBoot,
    WslSystemd,
    WslCommand,
}

public enum HealthCheckType
{
    Process,
    Tcp,
    WindowsService,
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
        AutorunTaskType.WindowsService => "windows_service",
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

    public static string ToKey(this HealthCheckType value) => value switch
    {
        HealthCheckType.Process => "process",
        HealthCheckType.Tcp => "tcp",
        HealthCheckType.WindowsService => "windows_service",
        HealthCheckType.WslRunning => "wsl_running",
        HealthCheckType.SystemdActive => "systemd_active",
        HealthCheckType.WslProcess => "wsl_process",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToKey(this RunAsMode value) => value switch
    {
        RunAsMode.CurrentUser => "current_user",
        RunAsMode.System => "system",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToKey(this LaunchTrigger value) => value switch
    {
        LaunchTrigger.Manual => "manual",
        LaunchTrigger.Logon => "logon",
        LaunchTrigger.Boot => "boot",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToKey(this ProcessLifecycle value) => value switch
    {
        ProcessLifecycle.Detached => "detached",
        ProcessLifecycle.Tracked => "tracked",
        ProcessLifecycle.Service => "service",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
