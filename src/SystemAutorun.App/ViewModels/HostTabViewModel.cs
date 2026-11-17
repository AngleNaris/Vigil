using CommunityToolkit.Mvvm.ComponentModel;
using SystemAutorun.Core.Models;

namespace SystemAutorun.App.ViewModels;

public sealed partial class HostTabViewModel : ObservableObject
{
    internal HostTabViewModel()
    {
        Id = "local";
        Name = Environment.MachineName;
        Address = "本机";
        Platform = RemoteHostPlatform.Windows;
        IsLocal = true;
        Status = "OFF";
        StatusDetail = "正在连接本机 Agent。";
        UpdatedText = "尚未刷新";
    }

    internal HostTabViewModel(RemoteHostDefinition definition)
    {
        Definition = definition;
        Id = definition.Id;
        Name = definition.Name;
        Address = $"{definition.Host}:{definition.Port}";
        Platform = definition.Platform;
        IsLocal = false;
        Status = definition.Enabled ? "UNKNOWN" : "OFF";
        StatusDetail = definition.Enabled ? "尚未连接。" : "远程主机已禁用。";
        UpdatedText = "尚未刷新";
    }

    public string Id { get; }

    public string Name { get; }

    public string Address { get; }

    public RemoteHostPlatform Platform { get; }

    public string PlatformLabel => Platform == RemoteHostPlatform.Windows ? "WIN" : "LINUX";

    public bool IsLocal { get; }

    public RemoteHostDefinition? Definition { get; }

    public RemoteHostStatusSnapshot? RemoteSnapshot { get; private set; }

    [ObservableProperty]
    private string status;

    [ObservableProperty]
    private string statusDetail;

    [ObservableProperty]
    private string updatedText;

    [ObservableProperty]
    private bool hasHighResourceUsage;

    [ObservableProperty]
    private string highResourceSummary = string.Empty;

    internal void UpdateResourceAlert(bool hasHighResource, string summary)
    {
        HasHighResourceUsage = hasHighResource;
        HighResourceSummary = summary;
    }

    internal void ApplyLocalStatus(string value, string detail, DateTimeOffset? capturedAt)
    {
        Status = value;
        StatusDetail = detail;
        UpdatedText = capturedAt is null ? "尚未刷新" : capturedAt.Value.ToString("HH:mm:ss");
    }

    internal void ApplyRemoteStatus(RemoteHostStatusSnapshot snapshot)
    {
        RemoteSnapshot = snapshot;
        Status = snapshot.Online ? "OK" : Definition?.Enabled == false ? "OFF" : "UNKNOWN";
        StatusDetail = snapshot.Online
            ? $"{snapshot.MachineName} · {snapshot.OsDescription}"
            : snapshot.Error ?? "远程主机不可用。";
        UpdatedText = snapshot.Online ? snapshot.CapturedAt.ToLocalTime().ToString("HH:mm:ss") : "尚无可用观测";
    }
}
