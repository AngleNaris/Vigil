using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemAutorun.App.Services;

internal sealed class GuiSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;

    internal GuiSettingsStore(string path)
    {
        _path = path;
    }

    internal async Task<GuiSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return new GuiSettings();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<GuiSettings>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false) ?? new GuiSettings();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new GuiSettings();
        }
    }

    internal async Task SaveAsync(GuiSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException($"Invalid settings path: {_path}");
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await File.WriteAllTextAsync(
            _path,
            json + Environment.NewLine,
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record GuiSettings
{
    [JsonPropertyName("auto_start_missing_on_launch")]
    public bool AutoStartMissingOnLaunch { get; init; }
}
