using System.Text.Json.Serialization;

namespace SystemAutorun.Core.Recovery;

// A restart-safe hint, not confirmation, launch authority or permission to replay/clear.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecoveryPendingOperation
{
    public required int SchemaVersion { get; init; }
    public required string RootId { get; init; }
    public required string SubjectKey { get; init; }
    public required string TaskName { get; init; }
    public required string ExpectedRevision { get; init; }
    public required string OperationId { get; init; }
    public required DateTimeOffset ReservedAt { get; init; }

    internal static bool Valid(RecoveryPendingOperation? value) => value is not null && value.SchemaVersion == 1
        && Digest(value.RootId) && Digest(value.ExpectedRevision) && Name(value.TaskName)
        && value.SubjectKey is { Length: >= 1 and <= 256 } && value.SubjectKey.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
        && value.OperationId is { Length: 32 } && Guid.TryParseExact(value.OperationId, "N", out var id)
        && id.Version == 7 && id.ToString("N") == value.OperationId
        && value.ReservedAt != default && value.ReservedAt.Offset == TimeSpan.Zero;
    internal static bool Digest(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool Name(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
}

public enum RecoveryPendingState { Missing, Present, Unavailable }
public sealed record RecoveryPendingSnapshot(RecoveryPendingState State, RecoveryPendingOperation? Operation = null, string? Reason = null)
{
    public bool BlocksSubmission => State != RecoveryPendingState.Missing;
}

// Journal metadata only. Even CompletedHistory cannot unlock a pending operation.
public enum RecoveryReceiptState { NotFound, Running, Unknown, ResultUnavailable, CompletedHistory }
public sealed record RecoveryReceiptObservation(RecoveryReceiptState State, string OperationId);
