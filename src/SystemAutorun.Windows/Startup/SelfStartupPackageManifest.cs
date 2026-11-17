using System.Text.Json;

namespace SystemAutorun.Windows.Startup;

// Schema-1 publisher asset integrity only. Adjacent hashes are NOT a signature,
// publisher identity or permission to mutate Windows. Extra user data is not attested.
internal static class SelfStartupPackageManifest
{
    internal const int MaximumBytes = 2 * 1024 * 1024;
    internal const int MaximumFiles = 4096;
    internal const long MaximumFileBytes = 512L * 1024 * 1024;
    internal const long MaximumPackageBytes = 1024L * 1024 * 1024;
    internal static IReadOnlyList<string> RequiredFiles { get; } = Array.AsReadOnly(new[]
    {
        "Vigil.exe", "SystemAutorun.Core.dll", "SystemAutorun.Windows.dll", "Vigil.runtimeconfig.json", "Vigil.dll", "Vigil.deps.json",
        "cli/vigil.exe", "cli/vigil.dll", "cli/vigil.deps.json", "cli/vigil.runtimeconfig.json", "cli/SystemAutorun.Core.dll",
        "cli/coreclr.dll", "cli/hostfxr.dll", "cli/hostpolicy.dll", "cli/System.Private.CoreLib.dll",
        "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll",
        "runtime/Vigil.Agent.exe", "runtime/Vigil.Agent.dll", "runtime/Vigil.Agent.deps.json",
        "runtime/Vigil.Elevated.exe", "runtime/Vigil.Elevated.dll", "runtime/Vigil.Elevated.deps.json",
        "runtime/coreclr.dll", "runtime/hostfxr.dll", "runtime/hostpolicy.dll", "runtime/System.Private.CoreLib.dll",
        "runtime/SystemAutorun.Core.dll", "runtime/SystemAutorun.Windows.dll", "runtime/Vigil.Agent.runtimeconfig.json", "runtime/Vigil.Elevated.runtimeconfig.json",
    });

    internal static IReadOnlyList<SelfStartupPackageEntry> Read(byte[] bytes)
    {
        try { return ReadCore(bytes); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("Invalid package manifest format.", error); }
    }

    private static IReadOnlyList<SelfStartupPackageEntry> ReadCore(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) { throw new InvalidDataException("Invalid package manifest bounds."); }
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 16 });
        var root = document.RootElement;
        RequireFields(root, "schema_version", "runtime", "self_contained", "created_utc", "files");
        if (root.GetProperty("schema_version").GetInt32() != 1 || root.GetProperty("runtime").GetString() != "win-x64"
            || !root.GetProperty("self_contained").GetBoolean() || !root.GetProperty("created_utc").TryGetDateTimeOffset(out var created)
            || created.Offset != TimeSpan.Zero) { throw new InvalidDataException("Unsupported package manifest."); }
        var files = root.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is 0 or > MaximumFiles)
        { throw new InvalidDataException("Invalid package file count."); }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SelfStartupPackageEntry>(); long total = 0;
        foreach (var file in files.EnumerateArray())
        {
            RequireFields(file, "path", "sha256", "size");
            var path = file.GetProperty("path").GetString(); var hash = file.GetProperty("sha256").GetString(); var size = file.GetProperty("size").GetInt64();
            if (!ValidRelativePath(path) || !paths.Add(path!) || path!.Equals("publish-manifest.json", StringComparison.OrdinalIgnoreCase)
                || hash is not { Length: 64 } || !hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
                || size is < 0 or > MaximumFileBytes || size > MaximumPackageBytes - total)
            { throw new InvalidDataException("Unsafe or unsupported package entry."); }
            total += size; result.Add(new(path, hash, size));
        }
        if (RequiredFiles.Any(required => !result.Any(entry => entry.Path == required)))
        { throw new InvalidDataException("Published runtime assets are incomplete."); }
        return result.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
    }

    private static bool ValidRelativePath(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 1024 || value.Contains('\\') || value.StartsWith('/') || value.EndsWith('/')) { return false; }
        if (value.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)) { return false; }
        // Pure lexical validation only: reuse the existing DOS/ADS/device-name rules,
        // including CONIN$/CONOUT$ and superscript COM/LPT digits. No synthetic-path IO.
        try { _ = StartupShortcutPathLease.LocalPath(@"C:\SystemAutorunPackage\" + value.Replace('/', '\\')); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private static void RequireFields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length
            || value.EnumerateObject().Any(property => !names.Contains(property.Name, StringComparer.Ordinal)))
        { throw new InvalidDataException("Unexpected package manifest fields."); }
    }
}

internal sealed record SelfStartupPackageEntry(string Path, string Sha256, long Size);
