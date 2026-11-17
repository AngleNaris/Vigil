using SystemAutorun.Core.Configuration;

namespace SystemAutorun.Core.Models;

// Completion is not an observation when the source is unavailable. Never promote the
// Offline snapshot's placeholder zeroes or CapturedAt into current resource/uptime data.
public sealed record RemoteHostProbeResult(
    string Revision,
    bool Available,
    string? Reason,
    DateTimeOffset CompletedAt,
    RemoteHostStatusSnapshot Snapshot) : IRevisionedResult
{
    public int SchemaVersion { get; init; } = 1;
    public string Source { get; init; } = "configured-ssh";
    public DateTimeOffset? ObservedAt => Available ? Snapshot.CapturedAt : null;
}
