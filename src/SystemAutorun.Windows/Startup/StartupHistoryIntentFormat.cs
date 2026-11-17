using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Windows.Startup;

// Editable root metadata, not provenance, a receipt, native evidence or permission.
// Outer names preserve R42 default-serializer wire bytes; nested candidate names
// deliberately remain PascalCase, while the ledger record uses its own attributes.
internal sealed record StartupHistoryDisableIntent(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("transactionId")] string TransactionId,
    [property: JsonPropertyName("record")] StartupBackupRecord Record,
    [property: JsonPropertyName("candidate")] StartupHistoryPayload Candidate,
    [property: JsonPropertyName("previousLedgerMissing")] bool PreviousLedgerMissing,
    [property: JsonPropertyName("previousLedgerSha256")] string? PreviousLedgerSha256,
    [property: JsonPropertyName("ledgerSha256")] string LedgerSha256,
    [property: JsonPropertyName("configurationSha256")] string ConfigurationSha256);

internal static class StartupHistoryIntentFormat
{
    internal const int MaximumBytes = StartupBackupLedger.MaximumBytes;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Strict) { MaxDepth = 12 };

    internal static byte[] Write(StartupHistoryDisableIntent intent)
    {
        Validate(intent); var bytes = JsonSerializer.SerializeToUtf8Bytes(intent, Options);
        if (bytes.Length <= MaximumBytes) { return bytes; }
        CryptographicOperations.ZeroMemory(bytes); throw new JsonException("History intent exceeds its byte limit.");
    }
    internal static StartupHistoryDisableIntent Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) { throw new JsonException("History intent byte limit is invalid."); }
        var intent = JsonSerializer.Deserialize<StartupHistoryDisableIntent>(bytes, Options) ?? throw new JsonException("History intent is missing.");
        Validate(intent); return intent;
    }
    private static void Validate(StartupHistoryDisableIntent intent)
    {
        if (intent is null || intent.SchemaVersion != 1 || intent.Operation != "history-disable" || !StartupHistoryFormat.Identifier(intent.TransactionId)
            || intent.Record is null || intent.Candidate is null || !HashShape(intent.LedgerSha256) || !HashShape(intent.ConfigurationSha256)
            || (intent.PreviousLedgerMissing ? intent.PreviousLedgerSha256 is not null : !HashShape(intent.PreviousLedgerSha256))
            || intent.LedgerSha256 == intent.PreviousLedgerSha256)
        { throw new JsonException("History intent fields are invalid."); }
        StartupHistoryFormat.Validate(intent.Candidate); var candidate = intent.Candidate; var record = intent.Record; var binding = candidate.Binding;
        if (candidate.Phase != "candidate" || candidate.Sequence != 1 || record.Disposition != "disabled" || !StartupBackupLedger.Valid(record)
            || record.Id != binding.RecordId || record.Source != binding.Source.Source || record.Name != binding.Name
            || record.Origin != Path.Combine(binding.Source.Directory.Path, record.Name + ".lnk")
            || record.BackupPath != Path.Combine(binding.Archive.Path, record.Id + ".lnk") || record.DisabledAt.UtcTicks != candidate.CandidateAt.UtcTicks)
        { throw new JsonException("History intent requires an exact new disable tuple."); }
        try
        {
            if (StartupHistoryFormat.RecordDigest(record) != binding.RecordDigest) { throw new JsonException("History intent record digest changed."); }
        }
        catch (ArgumentException error) { throw new JsonException("History intent record is invalid.", error); }
    }
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool HashShape(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
