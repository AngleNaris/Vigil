using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SystemAutorun.Core.Ipc;

public sealed class AgentClient
{
    private readonly string? _rootDirectory;

    public AgentClient(string? rootDirectory = null) => _rootDirectory = rootDirectory;

    internal string RootId => AgentProtocol.ForRoot(Infrastructure.ProjectPaths.Resolve(_rootDirectory).RootDirectory).RootId;

    public async Task<bool> PingAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendAsync<JsonElement>("ping", timeout: timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.ValueKind is JsonValueKind.Object or JsonValueKind.String;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    public Task<T> SendAsync<T>(
        string command, string? name = null, object? payload = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default,
        string? expectedRevision = null, string? operationId = null) =>
        SendCoreAsync(command, name, payload, timeout, cancellationToken, expectedRevision, operationId,
            root => root.TryGetProperty("data", out var data)
                ? data.Deserialize<T>(AgentProtocol.JsonOptions) ?? throw new JsonException("Agent returned invalid response data.")
                : default!);

    // CLI and other clients that need receipt metadata use the SAME validated transport.
    // Command failures are returned intact (including replay metadata); transport and
    // outcome-unknown exceptions still propagate. Typed SendAsync retains its throw contract.
    public Task<AgentResponse> SendResponseAsync(
        string command, string? name = null, object? payload = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default,
        string? expectedRevision = null, string? operationId = null, bool requireData = false) =>
        SendCoreAsync(command, name, payload, timeout, cancellationToken, expectedRevision, operationId,
            root =>
            {
                var response = root.Deserialize<AgentResponse>(AgentProtocol.JsonOptions) ?? throw new JsonException("Agent returned an invalid response.");
                if (requireData && response.Ok)
                {
                    if (!root.TryGetProperty("data", out var data)
                        || root.EnumerateObject().Count(property => property.Name.Equals("data", StringComparison.OrdinalIgnoreCase)) != 1)
                    { throw new JsonException("Agent returned missing or ambiguous response data."); }
                    response = response with { Data = data.Clone() }; // Preserve explicit null, distinct from a missing field.
                }
                return response;
            }, returnCommandErrors: true);

    private async Task<T> SendCoreAsync<T>(string command, string? name, object? payload,
        TimeSpan? timeout, CancellationToken cancellationToken, string? expectedRevision, string? operationId,
        Func<JsonElement, T> deserialize, bool returnCommandErrors = false)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        if (effectiveTimeout <= TimeSpan.Zero
            || effectiveTimeout > TimeSpan.FromMilliseconds(AgentProtocol.MaximumRequestTimeoutMilliseconds))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout,
                $"Timeout must be between 1 tick and {AgentProtocol.MaximumRequestTimeoutMilliseconds} milliseconds.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.Length > AgentProtocol.MaximumCommandCharacters)
        {
            throw new ArgumentException($"Command must not exceed {AgentProtocol.MaximumCommandCharacters} characters.", nameof(command));
        }

        if (expectedRevision is not null && (expectedRevision.Length != 64 || !expectedRevision.All(char.IsAsciiHexDigit)))
        {
            throw new ArgumentException("expectedRevision must be a 64-character SHA256 revision.", nameof(expectedRevision));
        }

        if (AgentProtocol.IsMutation(command))
        {
            operationId = OperationJournal.NormalizeId(operationId ?? AgentProtocol.NewOperationId());
        }
        else if (operationId is not null)
        {
            throw new ArgumentException("Read-only commands do not take an operationId; query operation by name instead.", nameof(operationId));
        }

        var requestId = Guid.NewGuid().ToString("N");
        JsonElement? payloadElement = payload is null ? null : JsonSerializer.SerializeToElement(payload, AgentProtocol.JsonOptions);
        ValidateRequestSize(new AgentRequest(requestId, command, name, payloadElement,
            AgentProtocol.MaximumRequestTimeoutMilliseconds, RootId: new string('0', 64),
            ExpectedRevision: expectedRevision, OperationId: operationId));

        var paths = Infrastructure.ProjectPaths.Resolve(_rootDirectory);
        var endpoint = AgentProtocol.ForRoot(paths.RootDirectory);
        var elapsed = Stopwatch.StartNew();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(effectiveTimeout);
        await using var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
        timeoutSource.Token.ThrowIfCancellationRequested();
        var remaining = effectiveTimeout - elapsed.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException($"Agent request timed out after {effectiveTimeout.TotalSeconds:0.###}s.");
        }

        var request = new AgentRequest(requestId, command, name, payloadElement,
            Math.Max(1, (int)Math.Ceiling(remaining.TotalMilliseconds)), RootId: endpoint.RootId,
            ExpectedRevision: expectedRevision, OperationId: operationId);
        var json = JsonSerializer.Serialize(request, AgentProtocol.JsonOptions);
        ValidateRequestSize(json);
        var attemptedWrite = false;
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            attemptedWrite = true;
            await writer.WriteLineAsync(json.AsMemory(), timeoutSource.Token).ConfigureAwait(false);
            var line = await ReadResponseLineAsync(reader, timeoutSource.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new IOException("Agent returned an empty response.");
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) || id.GetString() != request.Id)
            {
                throw new IOException("Agent returned a response with an unexpected request id.");
            }

            if (!root.TryGetProperty("protocolVersion", out var version) || version.GetInt32() != AgentProtocol.Version
                || !root.TryGetProperty("rootId", out var rootId) || rootId.GetString() != endpoint.RootId)
            {
                throw new IOException("Agent protocol or configuration root does not match this client.");
            }

            if (operationId is not null && (!root.TryGetProperty("operationId", out var operation)
                || !string.Equals(operation.GetString(), operationId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException("Agent operation identity does not match this client.");
            }

            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new IOException("Agent returned invalid response status.");
            }
            if (!ok.GetBoolean() && !returnCommandErrors)
            {
                throw new AgentCommandException(StringProperty(root, "errorCode") ?? "command_failed",
                    StringProperty(root, "error") ?? "Agent command failed.", StringProperty(root, "revision"),
                    StringProperty(root, "operationId") ?? operationId, request.Id);
            }

            return deserialize(root);
        }
        catch (Exception exception) when (attemptedWrite && operationId is not null
            && exception is not AgentCommandException
            && exception is IOException or OperationCanceledException or TimeoutException or JsonException or InvalidOperationException)
        {
            throw new AgentOutcomeUnknownException(operationId, exception, request.Id);
        }
        finally
        {
            try { await writer.DisposeAsync().ConfigureAwait(false); }
            catch (IOException)
            {
                // WriteLineAsync already awaited its AutoFlush; there is no pending
                // request to commit here. A peer closing after its reply must not
                // replace a validated ACK, business failure, or original exchange error.
            }
        }
    }

    private static string? StringProperty(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) ? property.GetString() : null;

    private static async Task<string?> ReadResponseLineAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) { return line.Length == 0 ? null : line.ToString(); }
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] == '\n') { return line.ToString().TrimEnd('\r'); }
                line.Append(buffer[index]);
                if (line.Length > AgentProtocol.MaximumResponseCharacters)
                {
                    throw new IOException("Agent response exceeds the bounded response size.");
                }
            }
        }
    }

    private static void ValidateRequestSize(AgentRequest request) => ValidateRequestSize(JsonSerializer.Serialize(request, AgentProtocol.JsonOptions));

    private static void ValidateRequestSize(string json)
    {
        if (json.Length > AgentProtocol.MaximumRequestCharacters)
        {
            throw new ArgumentException($"Agent request exceeds the {AgentProtocol.MaximumRequestCharacters} character limit.", "payload");
        }
    }
}
