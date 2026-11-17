using System.Text.Json;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Core.Models;

// Registration metadata, not proof that Windows will launch it or that its binary is trusted.
public sealed record StartupRegistrationData(
    bool? Registered, string? ValueKind = null, string? Command = null,
    bool? Enabled = null, string? State = null, int? ActionCount = null,
    int? ActionType = null, string? Executable = null, string? Arguments = null,
    DateTime? LastRunTime = null, int? LastTaskResult = null, DateTime? NextRunTime = null, int? MissedRuns = null);

public sealed record StartupRegistrationSnapshot(
    int SchemaVersion, string Kind, string RegistrationScope, string RegistrationId,
    DateTimeOffset CompletedAt, DateTimeOffset? ObservedAt, bool Available, string? Reason,
    StartupRegistrationData? Registration, string? AssociatedRoot, string RootAssociation);

public sealed class StartupRegistrationSourceException(string reason) : IOException(reason)
{
    public string Reason { get; } = reason;
}

public static class StartupRegistrationContract
{
    public const int MaximumTextCharacters = 8192;
    public const int MaximumSnapshotBytes = 64 * 1024;
    public static bool ValidKind(string kind) => kind is "self" or "boot";
    public static bool ValidReason(string? reason) => reason is "permission_denied" or "registration_busy"
        or "registration_timeout" or "registration_source_unavailable" or "registration_invalid"
        or "registration_too_large" or "registration_changed";
    public static string Scope(string kind) => kind == "self" ? "agent-user-global" : "machine-global";
    public static string Id(string kind) => kind == "self" ? "HKCU/Run/Vigil" : @"\Vigil\Boot";

    public static StartupRegistrationSnapshot Observe(string kind, StartupRegistrationData data, string currentRoot)
    {
        if (!ValidKind(kind) || !ValidData(kind, data)) { throw new InvalidDataException("Invalid startup registration metadata."); }
        var root = kind == "self" && data.Command is not null ? ParseSelfRoot(data.Command) : null;
        var association = data.Registered == false ? "absent" : kind == "boot" ? "not-applicable"
            : root is null ? "unknown" : SameLexicalRoot(root, currentRoot) ? "same-root-lexical" : "other-root-lexical";
        var now = DateTimeOffset.UtcNow;
        var snapshot = new StartupRegistrationSnapshot(1, kind, Scope(kind), Id(kind), now, now, true, null, data, root, association);
        if (JsonSerializer.SerializeToUtf8Bytes(snapshot, AgentProtocol.JsonOptions).Length > MaximumSnapshotBytes)
        { throw new StartupRegistrationSourceException("registration_too_large"); }
        return snapshot;
    }

    public static StartupRegistrationSnapshot Unavailable(string kind, string reason)
    {
        if (!ValidKind(kind) || !ValidReason(reason)) { throw new ArgumentException("Invalid registration status request or reason."); }
        return new(1, kind, Scope(kind), Id(kind), DateTimeOffset.UtcNow, null, false, reason, null, null, "unknown");
    }

    public static bool ValidData(string kind, StartupRegistrationData data)
    {
        if (!ValidKind(kind) || data is null || data.Registered is null
            || new[] { data.Command, data.Executable, data.Arguments }.Any(text => text?.Length > MaximumTextCharacters)) { return false; }
        if (data.Registered == false) { return data == new StartupRegistrationData(false); }
        if (kind == "self")
        {
            if (data.ValueKind is not ("None" or "Unknown" or "String" or "ExpandString" or "Binary" or "DWord" or "MultiString" or "QWord")) { return false; }
            if ((data.ValueKind is "String" or "ExpandString") != (data.Command is not null)) { return false; }
            return data == new StartupRegistrationData(true, data.ValueKind, data.Command);
        }
        if (kind != "boot" || data.ValueKind is not null || data.Command is not null || data.Enabled is null
            || data.State is not ("unknown" or "disabled" or "queued" or "ready" or "running")
            || data.ActionCount is null or < 0 or > 1024 || data.MissedRuns is null or < 0 || data.LastTaskResult is null || data.ActionType < 0
            || data.ActionCount == 1 && data.ActionType is null
            || data.LastRunTime?.Year <= 1900 || data.NextRunTime?.Year <= 1900) { return false; }
        return data.ActionCount == 1 && data.ActionType == 0
            ? data.Executable is not null && data.Arguments is not null
            : data.Executable is null && data.Arguments is null && (data.ActionCount == 1 || data.ActionType is null);
    }

    // Only recognize the canonical legacy GUI command, without shell parsing, expansion,
    // file existence checks or following aliases. This result is NEVER an ownership token.
    public static string? ParseSelfRoot(string command)
    {
        const string separator = "\" --background --root \"";
        if (command.Length > MaximumTextCharacters || !command.StartsWith('"') || !command.EndsWith('"')) { return null; }
        var split = command.IndexOf(separator, StringComparison.Ordinal);
        if (split < 2) { return null; }
        var executable = command[1..split];
        var root = command[(split + separator.Length)..^1];
        if (!LocalAbsolute(executable) || !LocalAbsolute(root) || root.EndsWith('\\') || root.EndsWith('/')
            || !Path.GetFileName(executable).Equals("Vigil.exe", StringComparison.OrdinalIgnoreCase)) { return null; }
        try { return Path.GetFullPath(root); }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private static bool LocalAbsolute(string path) => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':'
        && path[2] is '\\' or '/' && !path.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
            || character is '"' or '<' or '>' or '|' or '?' or '*')
        && !path[2..].Contains(':');
    private static bool SameLexicalRoot(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.Ordinal);
}
