namespace SystemAutorun.Core.Models;

// Filters select native metadata only; no path, executable or mutation is accepted.
public sealed record StartupListQuery(string Scope = "all", string Kind = "all", string State = "all",
    string? Search = null, int Limit = 100)
{
    // Leave 8 KiB for fixed header, bounded source errors and the transport envelope.
    public const int MaximumSerializedEntriesBytes = 120 * 1024;

    public StartupListQuery Validate()
    {
        var scope = Normalize(Scope, "scope", "all", "user", "machine");
        var kind = Normalize(Kind, "type", "all", "registry", "shortcut");
        var state = Normalize(State, "state", "all", "active", "disabled");
        if (Limit is < 1 or > 200) { throw new ArgumentException("Startup --limit must be 1..200."); }
        if (Search is { } text && (text.Length > 256 || text.Any(char.IsControl)))
        { throw new ArgumentException("Startup --search must be at most 256 characters without control characters."); }
        return this with { Scope = scope, Kind = kind, State = state, Search = Search?.Trim() };
    }

    public bool IncludesSource(string source)
    {
        var user = source is "Startup" or "HKCU";
        var shortcut = source is "Startup" or "Common Startup";
        return KnownSource(source) && (Scope == "all" || (Scope == "user") == user)
            && (Kind == "all" || (Kind == "shortcut") == shortcut);
    }

    public bool Matches(StartupEntry entry)
    {
        return IncludesSource(entry.Source)
            && (State == "all" || (State == "disabled") == entry.IsDisabled)
            && (string.IsNullOrEmpty(Search) || new[] { entry.Source, entry.Name, entry.Origin, entry.Target,
                entry.RawCommand, entry.Arguments, entry.ManagedTask }.Any(value => value?.Contains(Search, StringComparison.OrdinalIgnoreCase) == true));
    }

    public static bool KnownSource(string source) => source is "Startup" or "Common Startup" or "HKCU" or "HKLM" or "HKLM-WOW64";

    public static bool ValidIssue(StartupSourceIssue issue) => issue is not null
        && (issue.Source == "backups" || KnownSource(issue.Source)) && issue.Reason is { Length: > 0 and <= 64 }
        && issue.Reason.All(character => character is >= 'a' and <= 'z' or '_');

    public static bool ValidEntry(StartupEntry entry) =>
        KnownSource(entry.Source) && !string.IsNullOrWhiteSpace(entry.Id) && entry.Id.Length <= 256
        && !string.IsNullOrWhiteSpace(entry.Name) && entry.Name.Length <= 1024 && entry.Origin is { Length: <= 32768 }
        && (entry.RawCommand?.Length ?? 0) <= 32768 && (entry.Arguments?.Length ?? 0) <= 32768
        && (entry.Target?.Length ?? 0) <= 4096 && (entry.WorkingDirectory?.Length ?? 0) <= 4096
        && (entry.Error?.Length ?? 0) <= 2048 && (entry.ManagedTask?.Length ?? 0) <= 256
        && (entry.BackupId?.Length ?? 0) <= 256;

    private static string Normalize(string? value, string name, params string[] allowed)
    {
        var normalized = value?.ToLowerInvariant();
        return normalized is not null && allowed.Contains(normalized, StringComparer.Ordinal) ? normalized
            : throw new ArgumentException($"Startup {name} must be {string.Join('|', allowed)}.");
    }
}

public sealed record StartupInventorySnapshot(int SchemaVersion, string ConfigurationRevision,
    DateTimeOffset CompletedAt, DateTimeOffset? ObservedAt, bool Available, string? Reason,
    IReadOnlyList<StartupEntry> Entries, int? ScannedEntries, int? MatchedEntries, bool Truncated,
    bool IsElevated, bool HasEntryErrors, int ReadableSources, IReadOnlyList<StartupSourceIssue> SourceIssues)
{
    // Native sources are volatile and bounded, never an atomic census of Windows startup state.
    public bool CountsAreLowerBounds => Available;
}

public sealed record StartupSourceIssue(string Source, string Reason);
