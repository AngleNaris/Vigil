using System.Security.Cryptography;
using System.Text.Json;

namespace SystemAutorun.Windows.Startup;

// The fixed external latest index is separate from individual record envelopes.
// Its rows describe stored claims, NOT current native leaf observations or write rights.
internal sealed record StartupHistoryIndexEntry(StartupHistoryBinding Binding, StartupHistoryLatest Latest);
internal sealed record StartupHistoryIndex(int SchemaVersion, string ActorSid, long Generation, string Nonce,
    IReadOnlyList<StartupHistoryIndexEntry> Entries);

internal static class StartupHistoryIndexFormat
{
    internal const int MaximumBytes = 256 * 1024;
    internal const int MaximumEntries = 128;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Strict)
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 10 };

    internal static byte[] Write(StartupHistoryIndex index)
    {
        // Freeze caller-owned collections before validation and serialization.
        if (index?.Entries is null || index.Entries.Count > MaximumEntries) { throw new JsonException("History index entry limit is invalid."); }
        index = index with { Entries = Array.AsReadOnly(index.Entries.ToArray()) }; Validate(index);
        using var stream = new LimitedBuffer();
        try { JsonSerializer.Serialize(stream, index, Options); return stream.ToArray(); }
        finally { CryptographicOperations.ZeroMemory(stream.GetBuffer().AsSpan(0, (int)stream.Length)); }
    }

    internal static StartupHistoryIndex Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) { throw new JsonException("History index byte limit is invalid."); }
        var index = JsonSerializer.Deserialize<StartupHistoryIndex>(bytes, Options)
            ?? throw new JsonException("History index is missing.");
        Validate(index); return index with { Entries = Array.AsReadOnly(index.Entries.ToArray()) };
    }

    private static void Validate(StartupHistoryIndex index)
    {
        if (index is null || index.SchemaVersion != 1 || !StartupHistoryFormat.Sid(index.ActorSid)
            || index.Generation < 1 || !StartupHistoryFormat.Identifier(index.Nonce)
            || index.Entries is null || index.Entries.Count > MaximumEntries)
        { throw new JsonException("History index is invalid."); }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var position = 0; position < index.Entries.Count; position++)
        {
            var entry = index.Entries[position];
            if (entry is null || entry.Binding is null || entry.Latest is null || !StartupHistoryFormat.ValidLatest(entry.Latest))
            { throw new JsonException("History index row is invalid."); }
            StartupHistoryFormat.ValidateBinding(entry.Binding);
            if (entry.Binding.ActorSid != index.ActorSid || !ids.Add(entry.Binding.RecordId))
            { throw new JsonException("History index actor or record is ambiguous."); }
            foreach (var earlier in index.Entries.Take(position))
            {
                var directory = entry.Binding.Source.Directory; var prior = earlier.Binding.Source.Directory;
                var comparison = directory.CaseSensitivity == "sensitive" && prior.CaseSensitivity == "sensitive"
                    ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                // Global across roots and source labels: a physical slot has only one latest.
                if (directory.Identity == prior.Identity && entry.Binding.Name.Equals(earlier.Binding.Name, comparison))
                { throw new JsonException("History index contains an ambiguous native slot."); }
            }
        }
    }

    private sealed class LimitedBuffer : MemoryStream
    {
        private void Check(int count)
        { if (Length + count > MaximumBytes) { throw new JsonException("History index exceeds its byte limit."); } }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }
}
