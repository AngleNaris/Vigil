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

    private void SetRemoteUnavailable(HostTabViewModel tab, string reason)
    {
        if (ReferenceEquals(SelectedHostTab, tab)) { _remoteResumeGraceUntilUtc = null; }
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
