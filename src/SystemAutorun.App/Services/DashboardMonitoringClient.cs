using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.App.Services;

internal interface IDashboardMonitoringClient
{
    Task<AgentMonitoringSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default);
    Task<MonitoringHistoryResult> ReadHistoryAsync(MonitoringHistoryQuery query, CancellationToken cancellationToken = default);
    Task<ResourceHistoryResult> ReadResourceHistoryAsync(ResourceHistoryQuery query, CancellationToken cancellationToken = default) =>
        throw new AgentCommandException("resource_history_unavailable", "This monitoring source does not support resource history.");
    Task<RecentResourceHistoryResult> ReadRecentResourcesAsync(ResourceHistoryQuery query, CancellationToken cancellationToken = default) =>
        throw new AgentCommandException("unknown_command", "This monitoring source does not support recent resource schema 2.");
}

// Presentation adapter only. Reads consume the independent Agent's passive sampler;
// they never fall back to list/status/process probes that could execute workloads.
internal sealed class DashboardMonitoringClient(AgentClient client) : IDashboardMonitoringClient
{
    public Task<AgentMonitoringSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default) =>
        client.SendAsync<AgentMonitoringSnapshot>("monitoring-snapshot", timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);

    public Task<MonitoringHistoryResult> ReadHistoryAsync(MonitoringHistoryQuery query, CancellationToken cancellationToken = default) =>
        client.SendAsync<MonitoringHistoryResult>("monitoring-history", payload: query, timeout: TimeSpan.FromSeconds(15), cancellationToken: cancellationToken);

    public async Task<RecentResourceHistoryResult> ReadRecentResourcesAsync(ResourceHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var result = await client.SendAsync<RecentResourceHistoryResult>("recent-resources-v2", payload: query,
            timeout: TimeSpan.FromSeconds(15), cancellationToken: cancellationToken).ConfigureAwait(false);
        ResourceHistoryResponseValidator.Validate(result, query);
        return result;
    }

    public async Task<ResourceHistoryResult> ReadResourceHistoryAsync(ResourceHistoryQuery query, CancellationToken cancellationToken = default)
    {
        var result = await client.SendAsync<ResourceHistoryResult>("resource-history", payload: query,
            timeout: TimeSpan.FromSeconds(15), cancellationToken: cancellationToken).ConfigureAwait(false);
        ResourceHistoryResponseValidator.Validate(result, query);
        return result;
    }
}
