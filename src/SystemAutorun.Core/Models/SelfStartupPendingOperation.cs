using System.Text.Json.Serialization;

namespace SystemAutorun.Core.Models;

// Recovery hint only. No confirmation, prepared context, native authority or
// permission to retry/clear is recovered from this record.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SelfStartupPendingOperation
{
    public required int SchemaVersion { get; init; }
    public required string RootId { get; init; }
    public required string OperationId { get; init; }
    public required bool Enabled { get; init; }
    public required DateTimeOffset ReservedAt { get; init; }
}

public enum SelfStartupPendingState { Missing, Present, Unavailable }

public sealed record SelfStartupPendingSnapshot(SelfStartupPendingState State,
    SelfStartupPendingOperation? Operation = null, string? Reason = null)
{
    public bool BlocksSubmission => State != SelfStartupPendingState.Missing;
}
