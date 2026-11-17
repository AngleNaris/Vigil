using System.Security.Cryptography;
using System.Text.Json;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Windows.Startup;

// A separate correlation format, not an extension/upgrade of history payload v1.
// No issuer, owner factory, file name, migration, request confirmation or native rights.
internal sealed record StartupHistoryOperationReference(string OperationId, string Fingerprint, string TransactionId);
internal sealed record StartupHistoryOperationLink(int SchemaVersion, string OperationId, string Fingerprint,
    string TransactionId, string CandidateProtectedDigest, StartupHistoryPayload Candidate)
{
    internal StartupHistoryOperationReference Reference => new(OperationId, Fingerprint, TransactionId);
}
internal sealed record StartupHistoryOperationCheck(string State, string Reason, string? RecordId = null, string? TransactionId = null);

internal static class StartupHistoryOperationFormat
{
    internal const int MaximumBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Strict)
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 10 };

    internal static byte[] Write(StartupHistoryOperationLink link)
    {
        Validate(link); var bytes = JsonSerializer.SerializeToUtf8Bytes(link, Options);
        if (bytes.Length <= MaximumBytes) { return bytes; }
        CryptographicOperations.ZeroMemory(bytes); throw new JsonException("History operation link exceeds its byte limit.");
    }

    internal static StartupHistoryOperationLink Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) { throw new JsonException("History operation link byte limit is invalid."); }
        var link = JsonSerializer.Deserialize<StartupHistoryOperationLink>(bytes, Options)
            ?? throw new JsonException("History operation link is missing.");
        Validate(link); return link;
    }

    internal static void Validate(StartupHistoryOperationLink link)
    {
        if (link is null || link.SchemaVersion != 1 || !Digest(link.CandidateProtectedDigest))
        { throw new JsonException("History operation link fields are invalid."); }
        ValidateReference(link.Reference); StartupHistoryFormat.Validate(link.Candidate);
        if (link.Candidate.Phase != "candidate" || link.Candidate.Sequence != 1)
        { throw new JsonException("A new exact v1 candidate is required for operation correlation."); }
    }

    internal static void ValidateReference(StartupHistoryOperationReference reference)
    {
        if (reference is null || !CanonicalOperationId(reference.OperationId) || !Digest(reference.Fingerprint)
            || !StartupHistoryFormat.Identifier(reference.TransactionId))
        { throw new JsonException("History operation reference is invalid."); }
    }

    // DATA comparison only. These payloads do not authenticate their own latest state.
    // The protected wrapper checks transport digests; a future IO reader must pin/seal all
    // independent index/proof/link handles before publishing even an associated result.
    internal static StartupHistoryOperationCheck Match(StartupHistoryOperationLink link, StartupHistoryOperationReference expected,
        StartupHistoryPayload candidate, StartupHistoryPayload current)
    {
        Validate(link); ValidateReference(expected); StartupHistoryFormat.Validate(candidate); StartupHistoryFormat.Validate(current);
        if (link.Reference != expected) { return new("unverified", "history_operation_reference_changed"); }
        if (link.Candidate != candidate) { return new("unverified", "history_operation_candidate_changed"); }
        if (current.Binding != candidate.Binding) { return new("unverified", "history_operation_context_changed"); }
        if (current.Phase == "candidate")
        {
            if (current != candidate) { return new("unverified", "history_operation_candidate_changed"); }
        }
        else if (current.Sequence != candidate.Sequence + 1 || current.CandidateAt != candidate.CandidateAt)
        { return new("unverified", "history_operation_transition_changed"); }
        return new(current.Phase == "candidate" ? "associated_candidate" : "associated_committed", "history_correlation_only",
            candidate.Binding.RecordId, link.TransactionId);
    }

    private static bool Digest(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool CanonicalOperationId(string? value)
    {
        if (value is null) { return false; }
        try { return OperationJournal.NormalizeId(value) == value; }
        catch (AgentCommandException) { return false; }
    }
}
