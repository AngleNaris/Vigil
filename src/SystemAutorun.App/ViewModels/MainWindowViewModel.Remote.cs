using SystemAutorun.Core.Models;

namespace SystemAutorun.App.ViewModels;

public partial class MainWindowViewModel
{
    private CancellationTokenSource? _remoteCancellation;
    private long _remoteGeneration;

    private void CancelRemoteRefresh()
    {
        _remoteGeneration++;
        _remoteCancellation?.Cancel(); // The request owns disposal after its wait ends.
    }

    private bool RemoteRequestIsCurrent(HostTabViewModel tab, long generation) => !_dashboardDisposed
        && generation == _remoteGeneration && ReferenceEquals(SelectedHostTab, tab) && Hosts.Contains(tab);

    private static bool RemoteObservationFresh(DateTimeOffset capturedAt) => DateTimeOffset.UtcNow - capturedAt is var age
        && age >= TimeSpan.FromSeconds(-5) && age <= TimeSpan.FromSeconds(30);

    private bool IsRemoteObservationFresh(DateTimeOffset capturedAt, bool allowResumeGrace = false) =>
        DateTimeOffset.UtcNow - capturedAt is var age
        && age >= TimeSpan.FromSeconds(-5)
        && (age <= TimeSpan.FromSeconds(30) || (allowResumeGrace && InRemoteResumeGrace));

    internal static bool IsPendingOrStaleObservationReason(string? reason) =>
        string.Equals(reason, "远程观测已过期，请刷新重新查询。", StringComparison.Ordinal)
        || string.Equals(reason, "等待当前主机的后台观测。", StringComparison.Ordinal)
        || string.Equals(reason, "正在查询当前主机的后台观测…", StringComparison.Ordinal)
        || string.Equals(reason, "远程主机已禁用。", StringComparison.Ordinal)
        || string.Equals(reason, "后台配置不可用；远程观测未知，请刷新重试。", StringComparison.Ordinal);

    private bool HasMultipleEnabledRemoteHosts =>
        Hosts.Count(tab => !tab.IsLocal && tab.Definition?.Enabled == true) > 1;

    private bool HasOtherRemoteHostApproachingExpiry(HostTabViewModel currentTab) =>
        Hosts.Any(other => !ReferenceEquals(other, currentTab)
            && !other.IsLocal
            && other.Definition?.Enabled == true
            && (other.RemoteSnapshot is not { Online: true } snapshot
                || DateTimeOffset.UtcNow - snapshot.CapturedAt >= TimeSpan.FromSeconds(15)));

    private void SetRemoteUnavailable(HostTabViewModel tab, string reason)
    {
        if (!IsPendingOrStaleObservationReason(reason) && ReferenceEquals(SelectedHostTab, tab))
        {
            _remoteResumeGraceUntilUtc = null;
        }
        if (tab.Definition is not { } definition) { return; }
        var unavailable = RemoteHostStatusSnapshot.Offline(definition, reason);
        tab.ApplyRemoteStatus(unavailable);
        if (ReferenceEquals(SelectedHostTab, tab)) { ApplyRemoteHostStatus(unavailable); }
    }

    private void InvalidateRemoteObservations(string reason)
    {
        _remoteResumeGraceUntilUtc = null;
        CancelRemoteRefresh();
        foreach (var tab in Hosts.Where(tab => !tab.IsLocal).ToArray()) { SetRemoteUnavailable(tab, reason); }
        UpdateResourceAlerts();
    }

    private void ExpireRemoteObservations()
    {
        var expired = Hosts.Where(tab => tab.RemoteSnapshot is { Online: true } snapshot
            && !IsRemoteObservationFresh(snapshot.CapturedAt, allowResumeGrace: true)).ToArray();
        if (expired.Length == 0) { return; }
        foreach (var tab in expired)
        { SetRemoteUnavailable(tab, "远程观测已过期，请刷新重新查询。"); }
        UpdateResourceAlerts();
    }
}
