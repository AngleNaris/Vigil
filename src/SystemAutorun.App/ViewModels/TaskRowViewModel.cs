using SystemAutorun.Core.Models;

namespace SystemAutorun.App.ViewModels;

public sealed class TaskRowViewModel
{
    public TaskRowViewModel(TaskSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public TaskSnapshot Snapshot { get; }

    public string Status => Snapshot.Status;

    public string Name => Snapshot.Name;

    public string Type => Snapshot.Type;

    public string Distro => Snapshot.Distro ?? string.Empty;

    public string Reason => Snapshot.Reason;

    public string Detail => Snapshot.Detail;

    public string Description => string.IsNullOrWhiteSpace(Snapshot.Description)
        ? "未提供任务描述。"
        : Snapshot.Description;

    public bool Enabled => Snapshot.Enabled;

    public bool Healthy => Snapshot.Healthy;

    public string TypeLabel => Snapshot.Type switch
    {
        "windows" => "Windows",
        "wsl_boot" => "WSL 启动",
        "wsl_systemd" => "WSL 服务",
        "wsl_command" => "WSL 命令",
        _ => Snapshot.Type,
    };

    public string TargetSummary => string.IsNullOrWhiteSpace(Distro)
        ? TypeLabel
        : $"{TypeLabel} · {Distro}";

    public string ReasonLabel => !Enabled
        ? "已禁用"
        : Snapshot.Reason switch
        {
            "process-running" => "进程运行中",
            "process-not-found" => "未找到进程",
            "tcp-open" => "端口可达",
            "tcp-closed" => "端口不可达",
            "wsl-running" => "WSL 运行中",
            "wsl-stopped" => "WSL 未运行",
            "systemd-active" => "服务运行中",
            "systemd-not-active" => "服务未运行",
            "wsl-process-running" => "WSL 进程运行中",
            "wsl-process-not-found" => "未找到 WSL 进程",
            "missing-pattern" => "缺少进程匹配规则",
            "missing-host-or-port" => "缺少地址或端口",
            "missing-distro" => "缺少 WSL 发行版",
            "missing-distro-or-service" => "缺少发行版或服务",
            "missing-distro-or-pattern" => "缺少发行版或进程规则",
            _ => Snapshot.Reason,
        };

    public string LaunchSummary =>
        $"{RunAsLabel(Snapshot.Launch.RunAs)} · {TriggerLabel(Snapshot.Launch.Trigger)} · "
        + WindowModeLabel(Snapshot.Launch.WindowMode);

    public string StartupDelayText => $"{Snapshot.StartupDelay} 秒";

    public string ExecutableDisplay => Snapshot.Executable
        ?? Snapshot.Service
        ?? "不适用";

    private static string RunAsLabel(RunAsMode mode) => mode switch
    {
        RunAsMode.CurrentUser => "当前用户",
        RunAsMode.System => "SYSTEM",
        _ => mode.ToString(),
    };

    private static string TriggerLabel(LaunchTrigger trigger) => trigger switch
    {
        LaunchTrigger.Manual => "手动",
        LaunchTrigger.Logon => "用户登录",
        LaunchTrigger.Boot => "系统启动",
        _ => trigger.ToString(),
    };

    private static string WindowModeLabel(WindowMode mode) => mode switch
    {
        WindowMode.Normal => "普通窗口",
        WindowMode.Minimized => "最小化",
        WindowMode.Hidden => "隐藏窗口",
        WindowMode.NoConsole => "无控制台",
        WindowMode.Tray => "托盘模式",
        _ => mode.ToKey(),
    };
}
