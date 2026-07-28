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

    public string LaunchSummary =>
        $"{Snapshot.Launch.RunAs} / {Snapshot.Launch.Trigger} / {Snapshot.Launch.WindowMode.ToKey()}";
}
