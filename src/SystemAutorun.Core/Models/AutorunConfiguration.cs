namespace SystemAutorun.Core.Models;

public sealed record AutorunConfiguration(
    int Version,
    AutorunSettings Settings,
    IReadOnlyList<AutorunTask> Tasks,
    string RootDirectory)
{
    public AutorunTask GetTask(string name) =>
        Tasks.FirstOrDefault(task => string.Equals(task.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Task not found: {name}");
}

public sealed record AutorunSettings(
    string LogDirectory,
    string BackupDirectory,
    int DefaultStartupDelay);

public sealed record LaunchOptions(
    RunAsMode RunAs,
    LaunchTrigger Trigger,
    WindowMode WindowMode,
    ProcessLifecycle Lifecycle)
{
    public static LaunchOptions LegacyDefault(AutorunTaskType type) => type switch
    {
        AutorunTaskType.Windows => new(
            RunAsMode.CurrentUser,
            LaunchTrigger.Logon,
            WindowMode.Hidden,
            ProcessLifecycle.Tracked),
        AutorunTaskType.WindowsService => new(
            RunAsMode.CurrentUser,
            LaunchTrigger.Logon,
            WindowMode.NoConsole,
            ProcessLifecycle.Service),
        _ => new(
            RunAsMode.CurrentUser,
            LaunchTrigger.Logon,
            WindowMode.NoConsole,
            ProcessLifecycle.Tracked),
    };
}

public sealed record AutorunTask(
    string Name,
    AutorunTaskType Type,
    bool Enabled,
    string Description,
    string? Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    string? Distro,
    string? Service,
    string? Command,
    IReadOnlyList<string> DependsOn,
    int StartupDelay,
    HealthCheckDefinition Health,
    LaunchOptions Launch,
    bool ExpectedRunning = false,
    bool? StartOnAppLaunch = null,
    bool? AutoRecover = null);

public sealed record HealthCheckDefinition(
    HealthCheckType Type,
    string? Pattern,
    string? Host,
    int? Port,
    int TimeoutSeconds,
    string? Distro,
    string? Service);
