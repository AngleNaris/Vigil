using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemAutorun.Core.Ipc;

public sealed record AgentRequest(
    string Id,
    string Command,
    string? Name = null,
    JsonElement? Payload = null);

public sealed record AgentResponse(
    string Id,
    bool Ok,
    object? Data = null,
    string? Error = null);

public static class AgentProtocol
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    public static string PipeName
    {
        get
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            return $"SystemAutorun.Agent.{sid.Replace('-', '_')}";
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
