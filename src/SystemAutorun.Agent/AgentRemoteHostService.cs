using System.Collections.Concurrent;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.Agent;

// Explicit read-only requests only: neither monitoring ticks nor configuration writes
// invoke this service. At most two providers run in on-demand mode, and an ID is single-flight.
internal sealed class AgentRemoteHostService(
    ConfigurationWorkspace workspace,
    IRemoteHostReader reader,
    AgentRemoteHostMonitor? monitor = null,
    TimeProvider? clock = null)
{
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _configurationEpoch;
    private int _configurationWrites;

    // Both entry and completion matter: content revisions alone cannot detect remove /
    // re-add with identical bytes (ABA). Active writes also cover the commit/reply gap.
    internal IDisposable BeginConfigurationWrite()
    {
        Interlocked.Increment(ref _configurationWrites); Interlocked.Increment(ref _configurationEpoch);
        return new ConfigurationWrite(this);
    }
    private sealed class ConfigurationWrite(AgentRemoteHostService owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
            Interlocked.Increment(ref owner._configurationEpoch); Interlocked.Decrement(ref owner._configurationWrites);
        }
    }

    internal async Task<RemoteHostProbeResult> ReadAsync(string id, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var epoch = Volatile.Read(ref _configurationEpoch);
        var original = await workspace.ReadAsync(token).ConfigureAwait(false);
        var definition = original.Preferences.RemoteHosts.FirstOrDefault(host => host.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Remote host ID '{id}' is not configured.");
        if (Volatile.Read(ref _configurationWrites) != 0 || epoch != Volatile.Read(ref _configurationEpoch))
        { return Unavailable("configuration_changed", "A configuration write overlapped this request; reload before querying again."); }
        if (!definition.Enabled) { return Unavailable("host_disabled", "The configured host is disabled."); }

        if (monitor is not null)
        {
            var observed = await monitor.ReadCurrentAsync(definition, original.Revision, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var latestConfig = await workspace.ReadAsync(token).ConfigureAwait(false);
            if (latestConfig.Revision != original.Revision || Volatile.Read(ref _configurationWrites) != 0 || epoch != Volatile.Read(ref _configurationEpoch))
            { return Unavailable("configuration_changed", "Configuration changed during the probe; reload before querying again."); }
            var now = _clock.GetUtcNow();
            if (!observed.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase)
                || !observed.Host.Equals(definition.Host, StringComparison.OrdinalIgnoreCase)
                || observed.Platform != definition.Platform || observed.Name != definition.Name
                || observed.Online && (observed.CapturedAt < now.AddSeconds(-15) || observed.CapturedAt > now.AddSeconds(5)))
            { return Unavailable("remote_source_mismatch", "Remote provider returned a mismatched or stale observation."); }
            return new(original.Revision, observed.Online, observed.Online ? null : "remote_unavailable", now, observed);
        }

        if (!_active.TryAdd(definition.Id, 0)) { return Unavailable("remote_probe_busy", "This host already has a probe in flight."); }
        var entered = false;
        try
        {
            entered = await _slots.WaitAsync(0, token).ConfigureAwait(false);
            if (!entered) { return Unavailable("remote_probe_busy", "The bounded remote probe capacity is in use."); }
            var started = DateTimeOffset.UtcNow;
            var snapshot = await reader.ReadAsync(definition, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var latest = await workspace.ReadAsync(token).ConfigureAwait(false);
            if (latest.Revision != original.Revision || Volatile.Read(ref _configurationWrites) != 0 || epoch != Volatile.Read(ref _configurationEpoch))
            { return Unavailable("configuration_changed", "Configuration changed during the probe; reload before querying again."); }
            if (!snapshot.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase)
                || !snapshot.Host.Equals(definition.Host, StringComparison.OrdinalIgnoreCase)
                || snapshot.Platform != definition.Platform || snapshot.Name != definition.Name
                || snapshot.CapturedAt < started.AddSeconds(-5) || snapshot.CapturedAt > DateTimeOffset.UtcNow.AddSeconds(5))
            { return Unavailable("remote_source_mismatch", "Remote provider returned a mismatched or stale observation."); }
            return new(original.Revision, snapshot.Online, snapshot.Online ? null : "remote_unavailable", DateTimeOffset.UtcNow, snapshot);
        }
        finally
        {
            if (entered) { _slots.Release(); }
            _active.TryRemove(definition.Id, out _);
        }
        RemoteHostProbeResult Unavailable(string reason, string detail) => new(original.Revision, false, reason,
            _clock.GetUtcNow(), RemoteHostStatusSnapshot.Offline(definition, detail));
    }
}
