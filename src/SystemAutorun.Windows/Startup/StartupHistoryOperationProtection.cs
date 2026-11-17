using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SystemAutorun.Windows.Startup;

internal static partial class StartupHistoryProtection
{
    private static readonly byte[] OperationMagic = "SAHOP001"u8.ToArray();
    private static readonly byte[] OperationEntropy = Encoding.UTF8.GetBytes("SystemAutorun.StartupHistory.OperationLink.CurrentUser.v1");

    // Codec only. It does not establish that a Journal/owner actually issued this link,
    // attest an application, publish sidecars, backfill old records or authorize native IO.
    internal static byte[] ProtectOperationLink(StartupHistoryOperationLink link, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); var actor = Actor(); StartupHistoryOperationFormat.Validate(link);
        if (link.Candidate.Binding.ActorSid != actor) { throw new CryptographicException("History operation actor context is unavailable."); }
        var json = StartupHistoryOperationFormat.Write(link); byte[] frame = []; byte[]? result = null;
        try
        {
            frame = new byte[HeaderBytes + json.Length]; OperationMagic.CopyTo(frame, 0);
            SHA256.HashData(json).CopyTo(frame, 8); json.CopyTo(frame, HeaderBytes);
            result = Transform(frame, protect: true, operationLink: true);
            token.ThrowIfCancellationRequested(); RequireActor(actor); return result;
        }
        catch { if (result is not null) { CryptographicOperations.ZeroMemory(result); } throw; }
        finally { CryptographicOperations.ZeroMemory(json); CryptographicOperations.ZeroMemory(frame); }
    }

    internal static StartupHistoryOperationLink UnprotectOperationLink(ReadOnlySpan<byte> bytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); var actor = Actor();
        if (bytes.Length is 0 or > MaximumProtectedBytes) { throw new CryptographicException("History protected operation byte limit is invalid."); }
        var input = bytes.ToArray(); byte[] frame = [];
        try
        {
            frame = Transform(input, protect: false, operationLink: true);
            if (frame.Length <= HeaderBytes || !frame.AsSpan(0, 8).SequenceEqual(OperationMagic)
                || !CryptographicOperations.FixedTimeEquals(frame.AsSpan(8, 32), SHA256.HashData(frame.AsSpan(HeaderBytes))))
            { throw new CryptographicException("History protected operation frame is invalid."); }
            var link = StartupHistoryOperationFormat.Read(frame.AsSpan(HeaderBytes));
            token.ThrowIfCancellationRequested(); RequireActor(actor);
            if (link.Candidate.Binding.ActorSid != actor) { throw new CryptographicException("History operation actor binding changed."); }
            return link;
        }
        finally { CryptographicOperations.ZeroMemory(input); CryptographicOperations.ZeroMemory(frame); }
    }

    // Caller must independently authenticate latest/current and retain the file leases.
    // Owning these byte snapshots stabilizes transport only, NOT a store namespace.
    // No index, filename, scan, file writer, receipt change or native re-observation here.
    internal static StartupHistoryOperationCheck InspectOperationLink(ReadOnlySpan<byte> operationBytes,
        StartupHistoryOperationReference expected, ReadOnlySpan<byte> candidateBytes, ReadOnlySpan<byte> currentBytes,
        StartupHistoryBinding current, StartupHistoryLatest? latest, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); StartupHistoryOperationFormat.ValidateReference(expected); StartupHistoryFormat.ValidateBinding(current);
        if (operationBytes.IsEmpty) { return new("unlinked", "history_operation_link_missing"); }
        if (operationBytes.Length > MaximumProtectedBytes || candidateBytes.Length > MaximumProtectedBytes || currentBytes.Length > MaximumProtectedBytes)
        { return new("unverified", "history_operation_protection_invalid"); }
        if (candidateBytes.IsEmpty || currentBytes.IsEmpty) { return new("unverified", "history_operation_proof_missing"); }
        byte[] operation = []; byte[] candidate = []; byte[] selected = [];
        try
        {
            operation = operationBytes.ToArray(); candidate = candidateBytes.ToArray(); selected = currentBytes.ToArray();
            var check = Inspect(selected, current, latest, token);
            if (check.State is not ("candidate" or "protected_committed")) { return new("unverified", check.Reason ?? "history_operation_latest_unavailable"); }
            var link = UnprotectOperationLink(operation, token);
            if (Convert.ToHexStringLower(SHA256.HashData(candidate)) != link.CandidateProtectedDigest)
            { return new("unverified", "history_operation_candidate_bytes_changed"); }
            return StartupHistoryOperationFormat.Match(link, expected, Unprotect(candidate, token), Unprotect(selected, token));
        }
        catch (CryptographicException) { return new("unverified", "history_operation_protection_invalid"); }
        catch (JsonException) { return new("unverified", "history_operation_payload_invalid"); }
        finally
        {
            CryptographicOperations.ZeroMemory(operation); CryptographicOperations.ZeroMemory(candidate); CryptographicOperations.ZeroMemory(selected);
        }
    }
}
