using System.Text.Json;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Core.Models;

// A diagnostic observation, not a mutation receipt or permission to continue one.
public sealed record StartupOperationInspectionSnapshot(int SchemaVersion, string QueriedOperationId,
    DateTimeOffset CompletedAt, DateTimeOffset ObservedAt, bool DiagnosticOnly,
    string JournalState, string JournalReason, string PersistedReceiptState, string ObservedReceiptState,
    StartupOperationEvidence? Evidence, bool NativeWriteReady, bool ExecutionRequested,
    bool TransactionFinalized, bool MayFinalize, bool ReceiptUpdated, bool ReplayRequested);

public sealed record StartupOperationEvidence(string State, string Reason, string AssociationState, string AssociationReason,
    string? RecordId, string? TransactionId, string NativeState, string NativeReason);

public static class StartupOperationInspectionPolicy
{
    public const int MaximumBytes = 16 * 1024;
    public static void Validate(StartupOperationInspectionSnapshot value, string operationId)
    {
        var id = OperationJournal.NormalizeId(operationId);
        if (value.SchemaVersion != 1 || value.QueriedOperationId != id || !value.DiagnosticOnly
            || value.NativeWriteReady || value.ExecutionRequested || value.TransactionFinalized || value.MayFinalize || value.ReceiptUpdated || value.ReplayRequested
            || value.CompletedAt == default || value.CompletedAt != value.ObservedAt
            || DateTimeOffset.UtcNow - value.CompletedAt > TimeSpan.FromSeconds(30) || value.CompletedAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(5)
            || value.JournalState is not ("observed" or "missing" or "busy" or "unverified" or "unavailable") || !Reason(value.JournalReason))
        { throw new JsonException("Invalid operation diagnostic header or authority."); }
        if (value.JournalState != "observed")
        {
            if (value.PersistedReceiptState != "unobserved" || value.ObservedReceiptState != "unobserved" || value.Evidence is not null)
            { throw new JsonException("Unobserved journal must not carry evidence."); }
            return;
        }
        if (value.PersistedReceiptState is not ("running" or "completed" or "unknown")
            || value.ObservedReceiptState != (value.PersistedReceiptState == "running" ? "unknown" : value.PersistedReceiptState)
            || value.Evidence is not { } evidence || !Reason(evidence.Reason) || !Reason(evidence.AssociationReason) || !Reason(evidence.NativeReason)
            || evidence.State is not ("observed" or "none" or "unlinked" or "busy" or "unverified" or "unavailable")
            || evidence.AssociationState is not ("associated_candidate" or "associated_committed" or "unobserved" or "unlinked" or "unverified" or "busy" or "unavailable")
            || evidence.NativeState is not ("original_matches" or "archive_matches" or "unobserved" or "unverified"))
        { throw new JsonException("Invalid diagnostic axes."); }
        var associated = evidence.AssociationState is "associated_candidate" or "associated_committed";
        if (evidence.State == "observed")
        {
            if (!associated || !Identifier(evidence.RecordId) || !Identifier(evidence.TransactionId)
                || evidence.NativeState is not ("original_matches" or "archive_matches"))
            { throw new JsonException("Observed association must have verified bounded identities and native evidence."); }
        }
        else if (associated || evidence.RecordId is not null || evidence.TransactionId is not null
            || evidence.State is "none" or "unlinked" && evidence.NativeState != "unobserved")
        { throw new JsonException("Unverified association must not publish identities."); }
    }

    public static string? FailureCode(StartupOperationInspectionSnapshot value) =>
        value.ObservedReceiptState == "unknown" ? "startup_operation_unknown"
        : value.JournalState == "busy" || value.Evidence?.State == "busy" ? "startup_operation_busy"
        : value.JournalState == "observed" && value.ObservedReceiptState == "completed" && value.Evidence?.State == "observed" ? null
        : "startup_operation_incomplete";

    private static bool Identifier(string? value) => value is { Length: 32 } && Guid.TryParseExact(value, "N", out var id) && id.ToString("N") == value;
    private static bool Reason(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}
