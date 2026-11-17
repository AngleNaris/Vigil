using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;

namespace SystemAutorun.Core.Configuration;

internal static class OverrideFileCodec
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    public static async Task<Dictionary<string, bool>> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return CreateDictionary();
        }

        await using var stream = SnapshotFile.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(
            stream,
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
            },
            cancellationToken).ConfigureAwait(false);

        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Object => ReadObject(document.RootElement),
            JsonValueKind.Array => ReadLegacyArray(document.RootElement),
            JsonValueKind.Null => CreateDictionary(),
            _ => throw new JsonException("Overrides must be a JSON object or legacy array."),
        };
    }

    public static async Task WriteAsync(
        string path,
        IReadOnlyDictionary<string, bool> values,
        CancellationToken cancellationToken)
    {
        var serializable = values.ToDictionary(
            pair => pair.Key,
            pair => new OverrideValue(pair.Value),
            StringComparer.OrdinalIgnoreCase);
        var json = JsonSerializer.Serialize(serializable, WriteOptions);
        await File.WriteAllTextAsync(
            path,
            json + Environment.NewLine,
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, bool> ReadObject(JsonElement root)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, OverrideValue?>>(
            root.GetRawText(),
            ReadOptions);
        var result = CreateDictionary();
        if (values is null)
        {
            return result;
        }

        foreach (var (name, value) in values)
        {
            Add(result, name, value?.Enabled
                ?? throw new JsonException($"Override '{name}' must be an object."));
        }

        return result;
    }

    private static Dictionary<string, bool> ReadLegacyArray(JsonElement root)
    {
        var values = JsonSerializer.Deserialize<LegacyOverrideValue?[]>(
            root.GetRawText(),
            ReadOptions);
        var result = CreateDictionary();
        if (values is null)
        {
            return result;
        }

        foreach (var value in values)
        {
            if (value is null)
            {
                throw new JsonException("Legacy override entries must be objects.");
            }

            Add(result, value.Name, value.Enabled);
        }

        return result;
    }

    private static void Add(Dictionary<string, bool> values, string? name, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new JsonException("Override task name is required.");
        }

        if (!values.TryAdd(name.Trim(), enabled))
        {
            throw new JsonException($"Duplicate override task name: {name.Trim()}");
        }
    }

    private static Dictionary<string, bool> CreateDictionary() =>
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record OverrideValue(bool Enabled);

    private sealed record LegacyOverrideValue(string? Name, bool Enabled);
}
