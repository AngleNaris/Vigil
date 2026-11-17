using System.Security.Cryptography;
using System.Text;

namespace SystemAutorun.Core.Configuration;

// The wire carries bytes, not an Agent-side pathname. Base64url has predictable JSON size.
public sealed record ConfigurationDocumentRequest(string Utf8Base64Url, bool Confirmed = false)
{
    public const int MaximumBytes = 40 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ConfigurationDocumentRequest FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaximumBytes) { throw new ArgumentException("YAML input exceeds the 40 KiB document limit."); }
        _ = StrictUtf8.GetString(bytes);
        return new(Encode(bytes));
    }

    public static ConfigurationDocumentRequest FromText(string text) => FromBytes(StrictUtf8.GetBytes(text));

    public byte[] Decode()
    {
        if (Utf8Base64Url is null || Utf8Base64Url.Length > (MaximumBytes + 2) / 3 * 4
            || Utf8Base64Url.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        { throw new ArgumentException("Document content must be bounded, canonical UTF-8 base64url."); }
        try
        {
            var value = Utf8Base64Url.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='));
            if (FromBytes(bytes).Utf8Base64Url != Utf8Base64Url) { throw new ArgumentException("Document encoding is not canonical."); }
            return bytes;
        }
        catch (FormatException exception) { throw new ArgumentException("Invalid document encoding.", exception); }
    }

    public static string Text(byte[] bytes)
    {
        var text = StrictUtf8.GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

public sealed record ConfigurationDocumentSnapshot(int SchemaVersion, string Revision, DateTimeOffset CapturedAt,
    string Utf8Base64Url, string Sha256, int ByteCount, bool Utf8Valid) : IRevisionedResult;

public sealed record ConfigurationDocumentChanges(bool Available, IReadOnlyList<string> AddedTasks,
    IReadOnlyList<string> RemovedTasks, IReadOnlyList<string> ChangedTasks, bool SettingsChanged, string? Reason = null);

public sealed record ConfigurationValidationResult(int SchemaVersion, bool Valid, string Revision, DateTimeOffset CapturedAt,
    string Sha256, int ByteCount, int TaskCount, ConfigurationDocumentChanges? Changes, string? Error = null)
    : IRevisionedResult
{
    public string Scope => "Known YAML fields only; unknown properties remain verbatim and are not semantically validated. Existing overrides are applied and retained; separate preferences are retained, not validated.";
}

public sealed record ConfigurationApplyResult(bool Succeeded, bool Changed, string Sha256, int ByteCount,
    int TaskCount, string? BackupPath, ConfigurationDocumentChanges Changes);

public sealed class ConfigurationSourceBusyException(Exception inner) : IOException("Configuration is in use; no replacement was confirmed. Reload before a new operation.", inner);

public sealed class ConfigurationOutcomeUnknownException(string message, Exception inner) : IOException(message, inner);
