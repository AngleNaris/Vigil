using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Monitoring;

public sealed record NetworkRateSample(double? ReceiveBytesPerSecond, double? SendBytesPerSecond, string? Error = null);

// A rate requires two real, timestamped observations of the SAME active interface set.
// Names and IP addresses are labels, not interface identities. A reset is a gap, never zero traffic.
public sealed class HostNetworkRateTracker
{
    public static readonly TimeSpan MaximumGap = TimeSpan.FromSeconds(3);
    private readonly object _gate = new();
    private DateTimeOffset? _at;
    private Dictionary<string, NetworkStatusSnapshot>? _previous;

    public NetworkRateSample Observe(DateTimeOffset at, IReadOnlyList<NetworkStatusSnapshot> networks)
    {
        lock (_gate)
        {
            if (networks.Count == 0 || networks.Any(row => string.IsNullOrWhiteSpace(row.InterfaceId)
                || row.BytesReceived < 0 || row.BytesSent < 0)
                || networks.Select(row => row.InterfaceId).Distinct(StringComparer.Ordinal).Count() != networks.Count)
            {
                _at = null; _previous = null;
                return new(null, null, "network-identity-unavailable");
            }
            var current = networks.ToDictionary(row => row.InterfaceId!, StringComparer.Ordinal);
            var previous = _previous; var previousAt = _at;
            // A duplicate is not another sample. A backwards clock starts a new, gapped baseline.
            if (previousAt is { } last && at == last) { return new(null, null, "network-duplicate-sample"); }
            _previous = current; _at = at;
            if (previous is null || previousAt is null) { return new(null, null, "network-warming-up"); }
            if (at < previousAt || at - previousAt > MaximumGap) { return new(null, null, "network-sample-gap"); }
            if (previous.Count != current.Count || current.Keys.Any(id => !previous.ContainsKey(id)))
            { return new(null, null, "network-interface-changed"); }
            double received = 0, sent = 0;
            foreach (var (id, row) in current)
            {
                var before = previous[id];
                if (row.BytesReceived < before.BytesReceived || row.BytesSent < before.BytesSent)
                { return new(null, null, "network-counter-reset"); }
                received += row.BytesReceived - before.BytesReceived;
                sent += row.BytesSent - before.BytesSent;
            }
            var seconds = (at - previousAt.Value).TotalSeconds;
            return new(received / seconds, sent / seconds);
        }
    }

    public void Reset()
    {
        lock (_gate) { _at = null; _previous = null; }
    }
}
