using System.Security.Cryptography;
using System.Text.Json;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Core.Models;

// Explicit proposed input, never populated from editable backup/history metadata.
public sealed record StartupRebuildInput(string Target, string? Arguments = null, string? WorkingDirectory = null);

// Pure lexical review, NOT original-record provenance, native identity or a commit credential.
public sealed record StartupRebuildPreview(StartupRebuildInput Input, string InputDigest,
    string ValidationScope, string TargetAvailability);

public static class StartupRebuildInputPolicy
{
    public const int MaximumFieldLength = 8192;

    public static StartupRebuildPreview Preview(StartupRebuildInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var target = LocalPath(input.Target, directory: false);
        var arguments = input.Arguments ?? "";
        ValidateText(arguments, allowTab: true);
        var directory = string.IsNullOrEmpty(input.WorkingDirectory) ? null : LocalPath(input.WorkingDirectory, directory: true);
        var normalized = new StartupRebuildInput(target, arguments, directory);
        // Preserve component case/Unicode and argument bytes; no filesystem equivalence claim.
        var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new object?[]
        { "startup-rebuild-input-v1", normalized.Target, normalized.Arguments, normalized.WorkingDirectory }, AgentProtocol.JsonOptions)));
        return new(normalized, digest, "lexical_only", "not_checked");
    }

    public static bool Matches(StartupRebuildPreview? actual, StartupRebuildInput? requested)
    {
        if (requested is null) { return actual is null; }
        if (actual?.Input is null) { return false; }
        try { return actual == Preview(requested); }
        catch (ArgumentException) { return false; }
    }

    private static string LocalPath(string value, bool directory)
    {
        if (string.IsNullOrEmpty(value)) { throw new ArgumentException("A rebuild target must be explicitly supplied."); }
        ValidateText(value, allowTab: false);
        if (value.Length < 3 || !char.IsAsciiLetter(value[0]) || value[1] != ':' || value[2] is not ('\\' or '/')
            || value.Contains('%'))
        { throw new ArgumentException("Rebuild paths require ordinary local absolute drive-path syntax without expansion tokens."); }
        var path = char.ToUpperInvariant(value[0]) + value[1..].Replace('/', '\\');
        if (path.Contains(@"\\")) { throw new ArgumentException("Rebuild paths cannot contain repeated separators."); }
        if (directory && path.Length > 3 && path.EndsWith('\\')) { path = path[..^1]; }
        var parts = path[3..].Split('\\');
        if (!(directory && path.Length == 3))
        {
            foreach (var part in parts)
            {
                if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                    || part.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 || ReservedName(part))
                { throw new ArgumentException("Rebuild paths contain an ambiguous or unsupported component."); }
            }
        }
        if (!directory && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".com", StringComparison.OrdinalIgnoreCase))
        { throw new ArgumentException("Rebuild accepts only explicit local .exe/.com program paths, not scripts or shell targets."); }
        return path;
    }

    private static bool ReservedName(string part)
    {
        var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && "123456789¹²³".Contains(stem[3]);
    }

    private static void ValidateText(string value, bool allowTab)
    {
        if (value.Length > MaximumFieldLength || value.Any(character => char.IsControl(character) && !(allowTab && character == '\t')))
        { throw new ArgumentException("Rebuild fields exceed their bound or contain unsupported controls."); }
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index]))
                { throw new ArgumentException("Rebuild fields require well-formed Unicode."); }
            }
            else if (char.IsLowSurrogate(value[index])) { throw new ArgumentException("Rebuild fields require well-formed Unicode."); }
        }
    }
}
