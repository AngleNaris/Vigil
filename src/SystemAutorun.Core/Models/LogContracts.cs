using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemAutorun.Core.Models;

// Associations are shared preferences, written with the same workspace CAS as other settings.
// Requests select a stable source ID, never a client-supplied file path or command.
public sealed record LogSourceDefinition
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "file";
    public string? TaskName { get; init; }
    public string? Path { get; init; }
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalSettings { get; init; }
}

public sealed record LogSourceInfo(string Id, string Kind, string Origin, string? TaskName,
    string? Path, string? UnavailableReason = null);

public sealed record LogSourcesSnapshot(int SchemaVersion, string Revision, DateTimeOffset ObservedAt,
    IReadOnlyList<LogSourceInfo> Sources, int TotalSources = 0, bool Truncated = false);

public sealed record LogQuery(string? Source = null, string? Task = null, string? Level = null,
    DateTimeOffset? Since = null, int Tail = 100, string? Cursor = null, bool FromStart = false);

public sealed record LogEntry(string FileIdentity, long ByteOffset, DateTimeOffset? Timestamp,
    string? Level, string Message, bool Truncated = false, bool LossyUtf8 = false);

public sealed record LogPage(int SchemaVersion, LogSourceInfo Source, bool Available, string? Reason,
    DateTimeOffset ObservedAt, IReadOnlyList<LogEntry> Entries, string? NextCursor,
    bool HasMore, bool Truncated, int ScannedBytes, int UnknownTimestampExcluded = 0);
