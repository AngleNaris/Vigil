using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace SystemAutorun.Core.Models;

public sealed record StartupBackupLedgerRead(IReadOnlyList<StartupBackupRecord> Records, bool Truncated, bool InvalidRecords);

// Pure bounded shape/disposition codec; it never opens an origin, target or archive.
public static class StartupBackupLedger
{
    public const int MaximumBytes = 512 * 1024;
    public const int MaximumRecords = 512;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
        MaxDepth = 16,
    };

    public static StartupBackupLedgerRead Read(ReadOnlyMemory<byte> bytes, bool requireComplete = false)
    {
        if (bytes.Length > MaximumBytes) { throw new JsonException("Startup backup ledger exceeds its byte limit."); }
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Array) { throw new JsonException("Startup backup ledger must be an array."); }
        var records = new List<StartupBackupRecord>(); var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalid = false; var count = document.RootElement.GetArrayLength();
        if (requireComplete && count > MaximumRecords) { throw new InvalidOperationException("Startup backup ledger is truncated."); }
        foreach (var element in document.RootElement.EnumerateArray().Take(MaximumRecords))
        {
            try
            {
                CheckUniqueProperties(element);
                var record = element.Deserialize<StartupBackupRecord>(Options);
                if (record is null || !Valid(record) || !ids.Add(record.Id))
                {
                    if (requireComplete) { throw new InvalidOperationException("Startup backup ledger contains invalid or ambiguous records."); }
                    invalid = true; continue;
                }
                records.Add(record);
            }
            catch (JsonException) when (!requireComplete) { invalid = true; }
        }
        return new(records.AsReadOnly(), count > MaximumRecords, invalid);
    }

    // Called BEFORE intent/native effects. Do not publish a ledger our bounded reader
    // cannot read, or drop unknown/contradictory history during a different mutation.
    public static byte[] Write(IReadOnlyList<StartupBackupRecord> records)
    {
        if (records.Count > MaximumRecords) { throw new JsonException("Startup backup ledger record limit reached."); }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (records.Any(record => record is null || !Valid(record) || !ids.Add(record.Id)))
        { throw new JsonException("Startup backup ledger contains invalid or ambiguous records."); }
        using var stream = new LimitedBuffer();
        JsonSerializer.Serialize(stream, records, Options);
        return stream.ToArray();
    }

    public static bool Valid(StartupBackupRecord record)
    {
        if (record.Action != "disable" || !StartupListQuery.KnownSource(record.Source)
            || string.IsNullOrWhiteSpace(record.Id) || record.Id.Length > 249
            || string.IsNullOrWhiteSpace(record.Name) || record.Name.Length > 1024
            || record.Origin is null || !Text(record.Origin) || !Text(record.RawCommand) || !Text(record.Target, 4096)
            || !Text(record.Arguments) || !Text(record.WorkingDirectory, 4096) || !Text(record.BackupPath)) { return false; }
        var shortcut = record.Source is "Startup" or "Common Startup";
        if (shortcut && record.RegistryKind.HasValue || record.RegistryKind is not (null or RegistryValueKind.String or RegistryValueKind.ExpandString)
            || record.RegistryKind.HasValue && record.RawCommand is null) { return false; }
        return record.EffectiveDisposition switch
        {
            "disabled" => record.RestoredAt is null && record.RebuiltAt is null && record.Rebuild is null,
            "restored" => record.RestoredAt.HasValue && record.RebuiltAt is null && record.Rebuild is null,
            "rebuilt" => record.Disposition == "rebuilt" && shortcut && record.RestoredAt is null && record.RebuiltAt.HasValue
                && record.Rebuild is { } audit && !string.IsNullOrWhiteSpace(audit.Target) && Text(audit.Target, 4096)
                && Text(audit.Arguments) && Text(audit.WorkingDirectory, 4096),
            _ => false,
        };
    }

    private static bool Text(string? value, int maximum = 32768) => value is null || value.Length <= maximum;

    private sealed class LimitedBuffer : MemoryStream
    {
        private void Check(int count)
        { if (Length + count > MaximumBytes) { throw new JsonException("Startup backup ledger byte limit reached."); } }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }

    private static void CheckUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || property.Name.Equals(nameof(StartupBackupRecord.EffectiveDisposition), StringComparison.OrdinalIgnoreCase))
                { throw new JsonException("Duplicate or non-persisted startup backup field."); }
                CheckUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        { foreach (var item in element.EnumerateArray()) { CheckUniqueProperties(item); } }
    }
}
