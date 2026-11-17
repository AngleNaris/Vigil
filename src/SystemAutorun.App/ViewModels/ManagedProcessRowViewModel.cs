using SystemAutorun.Core.Models;

namespace SystemAutorun.App.ViewModels;

public sealed class ManagedProcessRowViewModel
{
    public ManagedProcessRowViewModel(ManagedProcessSnapshot snapshot) => Snapshot = snapshot;

    public ManagedProcessSnapshot Snapshot { get; }

    public string Status => Snapshot.State;

    public string TaskName => Snapshot.TaskName;

    public string Description => Snapshot.Description;

    public string Type => Snapshot.Type;

    public string TypeLabel => Snapshot.Type switch
    {
        "windows" => "Windows",
        "windows_service" => "Windows 服务",
        "wsl_boot" => "WSL 启动",
        "wsl_systemd" => "WSL 服务",
        "wsl_command" => "WSL 命令",
        _ => Snapshot.Type,
    };

    public string Reason => Snapshot.Reason;

    public string Detail => Snapshot.Detail;

    public int ProcessCount => Snapshot.ProcessCount;

    public string ProcessIdText => Snapshot.Processes.Count == 0
        ? "--"
        : string.Join(", ", Snapshot.Processes.Select(process => process.ProcessId));

    public string CpuText => Snapshot.CpuUsageAvailable ? $"{Snapshot.CpuUsagePercent:0.0}%" : "--";

    public string WorkingSetText => ProcessCount > 0 ? FormatBytes(Snapshot.WorkingSetBytes) : "--";

    public string PrivateMemoryText => ProcessCount > 0 ? FormatBytes(Snapshot.PrivateMemoryBytes) : "--";

    public int ThreadCount => Snapshot.ThreadCount;

    public string StartedAtText => Snapshot.StartedAt is null
        ? "--"
        : Snapshot.StartedAt.Value.ToString("MM-dd HH:mm:ss");

    public string ProcessNames => Snapshot.Processes.Count == 0
        ? "未发现匹配进程"
        : string.Join(
            ", ",
            Snapshot.Processes
                .Select(process => process.Name)
                .Distinct(StringComparer.CurrentCultureIgnoreCase));

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var display = (double)Math.Max(0, bytes);
        var unit = 0;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return $"{display:0.#} {units[unit]}";
    }
}
