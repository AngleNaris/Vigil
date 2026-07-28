using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.App.Services;

internal sealed class NativeCacheStore
{
    private readonly string _path;

    internal NativeCacheStore(string path)
    {
        _path = path;
    }

    internal async Task<IReadOnlyList<TaskSnapshot>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<TaskSnapshot>();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            if (root.TryGetProperty("nativeVersion", out _)
                && root.TryGetProperty("tasks", out var tasks))
            {
                return tasks.Deserialize<TaskSnapshot[]>(AgentProtocol.JsonOptions)
                    ?? Array.Empty<TaskSnapshot>();
            }

            return ParseLegacy(root);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return Array.Empty<TaskSnapshot>();
        }
    }

    internal async Task SaveAsync(
        IReadOnlyList<TaskSnapshot> tasks,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException($"Invalid cache path: {_path}");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var envelope = new NativeCacheEnvelope(2, DateTimeOffset.UtcNow, tasks);
            var json = JsonSerializer.Serialize(envelope, AgentProtocol.JsonOptions);
            await File.WriteAllTextAsync(
                tempPath,
                json,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, _path, true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static IReadOnlyList<TaskSnapshot> ParseLegacy(JsonElement root)
    {
        if (!root.TryGetProperty("entries", out var entries))
        {
            return Array.Empty<TaskSnapshot>();
        }

        foreach (var entry in entries.EnumerateObject())
        {
            if (!entry.Name.Contains("\"list\"", StringComparison.Ordinal)
                || !entry.Value.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var snapshots = new List<TaskSnapshot>();
            foreach (var item in data.EnumerateArray())
            {
                var typeKey = GetString(item, "type") ?? "windows";
                var type = typeKey switch
                {
                    "wsl_boot" => AutorunTaskType.WslBoot,
                    "wsl_systemd" => AutorunTaskType.WslSystemd,
                    "wsl_command" => AutorunTaskType.WslCommand,
                    _ => AutorunTaskType.Windows,
                };
                snapshots.Add(new TaskSnapshot(
                    GetString(item, "status") ?? "OFF",
                    GetBoolean(item, "healthy"),
                    GetBoolean(item, "enabled"),
                    GetString(item, "name") ?? string.Empty,
                    typeKey,
                    GetString(item, "description") ?? string.Empty,
                    GetString(item, "reason") ?? string.Empty,
                    GetString(item, "detail") ?? string.Empty,
                    GetInt32(item, "startup_delay"),
                    GetString(item, "exec"),
                    GetStrings(item, "args"),
                    GetString(item, "working_dir"),
                    GetString(item, "distro"),
                    GetString(item, "service"),
                    GetStrings(item, "depends_on"),
                    LaunchOptions.LegacyDefault(type)));
            }

            return snapshots;
        }

        return Array.Empty<TaskSnapshot>();
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();

    private static int GetInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static IReadOnlyList<string> GetStrings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray()
            : Array.Empty<string>();

    private sealed record NativeCacheEnvelope(
        int NativeVersion,
        DateTimeOffset SavedAt,
        IReadOnlyList<TaskSnapshot> Tasks);
}
