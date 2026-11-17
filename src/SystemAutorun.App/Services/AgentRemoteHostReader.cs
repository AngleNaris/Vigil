using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.App.Services;

// Bridge the existing presentation DTO to the shared, root-scoped observation.
// A definition is context only: only its configured ID crosses the pipe.
internal sealed class AgentRemoteHostReader(AgentClient client, AgentConfigurationClient configuration) : IRemoteHostReader
{
    private readonly AgentHostClient _hosts = new(client);

    public async Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default)
    {
        var revision = configuration.Revision
            ?? throw new AgentCommandException("configuration_unavailable", "请先读取当前主机配置。");
        var result = await _hosts.ReadRemoteAsync(host.Id, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Revision != revision || configuration.Revision != revision)
        { throw new AgentCommandException("configuration_changed", "主机配置已变化，请刷新后重新查询。"); }
        if (!result.Available)
        { return RemoteHostStatusSnapshot.Offline(host, $"{result.Reason}：{result.Snapshot.Error ?? "远程观测不可用。"}"); }

        var snapshot = result.Snapshot;
        var age = DateTimeOffset.UtcNow - snapshot.CapturedAt;
        if (!string.Equals(snapshot.Id, host.Id, StringComparison.OrdinalIgnoreCase)
            || snapshot.Platform != host.Platform || snapshot.Name != host.Name || snapshot.Host != host.Host
            || age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromSeconds(30))
        { throw new AgentCommandException("remote_source_mismatch", "远程观测与当前主机或采样时间不符。"); }
        return snapshot;
    }
}
