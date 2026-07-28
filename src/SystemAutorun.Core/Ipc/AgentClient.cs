using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SystemAutorun.Core.Ipc;

public sealed class AgentClient
{
    public async Task<bool> PingAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendAsync<JsonElement>("ping", timeout: timeout, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return response.ValueKind is JsonValueKind.Object or JsonValueKind.String;
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<T> SendAsync<T>(
        string command,
        string? name = null,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));

        await using var pipe = new NamedPipeClientStream(
            ".",
            AgentProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);

        var request = new AgentRequest(
            Guid.NewGuid().ToString("N"),
            command,
            name,
            payload is null
                ? null
                : JsonSerializer.SerializeToElement(payload, AgentProtocol.JsonOptions));
        var json = JsonSerializer.Serialize(request, AgentProtocol.JsonOptions);

        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
        };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(json.AsMemory(), timeoutSource.Token).ConfigureAwait(false);

        var responseLine = await reader.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(responseLine))
        {
            throw new IOException("Agent returned an empty response.");
        }

        using var document = JsonDocument.Parse(responseLine);
        var root = document.RootElement;
        if (!root.TryGetProperty("ok", out var okElement) || !okElement.GetBoolean())
        {
            var error = root.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : "Agent command failed.";
            throw new InvalidOperationException(error);
        }

        if (!root.TryGetProperty("data", out var dataElement))
        {
            return default!;
        }

        return dataElement.Deserialize<T>(AgentProtocol.JsonOptions)
            ?? throw new JsonException("Agent returned invalid response data.");
    }
}
