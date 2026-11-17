using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Agent;

internal sealed class AgentServer
{
    private const int MaximumConnections = 16;

    private readonly AgentCommandDispatcher _dispatcher;
    private readonly CancellationTokenSource _shutdown;
    private readonly AgentEndpoint _endpoint;

    internal AgentServer(AgentCommandDispatcher dispatcher, CancellationTokenSource shutdown, string rootDirectory)
    {
        _dispatcher = dispatcher;
        _shutdown = shutdown;
        _endpoint = AgentProtocol.ForRoot(rootDirectory);
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var capacity = new SemaphoreSlim(MaximumConnections, MaximumConnections);
        var handlers = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(
                        _endpoint.PipeName,
                        PipeDirection.InOut,
                        MaximumConnections,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    handlers.RemoveAll(handler => handler.IsCompleted);
                    handlers.Add(HandleConnectionAndDisposeAsync(pipe, capacity, cancellationToken));
                    pipe = null;
                }
                catch (IOException exception) when (IsClientDisconnect(exception))
                {
                    // A readiness client may connect then time out before Windows completes
                    // ConnectNamedPipe. This abandons ONE unaccepted connection, not the Agent.
                    if (pipe is not null) { await pipe.DisposeAsync().ConfigureAwait(false); }
                    capacity.Release();
                }
                catch
                {
                    if (pipe is not null)
                    {
                        await pipe.DisposeAsync().ConfigureAwait(false);
                    }

                    capacity.Release();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (handlers.Count > 0)
            {
                await Task.WhenAll(handlers).ConfigureAwait(false);
            }
        }
    }

    internal static bool IsClientDisconnect(IOException exception) => (exception.HResult & 0xFFFF) is 109 or 232 or 233;

    private async Task HandleConnectionAndDisposeAsync(
        NamedPipeServerStream pipe,
        SemaphoreSlim capacity,
        CancellationToken cancellationToken)
    {
        try
        {
            await using (pipe.ConfigureAwait(false))
            {
                await HandleConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // Clients may disconnect before the server finishes its final pipe flush.
        }
        finally
        {
            capacity.Release();
        }
    }

    private async Task HandleConnectionAsync(Stream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
        };

        AgentRequest? request = null;
        AgentResponse response;
        try
        {
            using var readTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeoutSource.CancelAfter(AgentProtocol.InitialRequestReadTimeoutMilliseconds);
            var requestLine = await ReadRequestLineAsync(reader, readTimeoutSource.Token)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                return;
            }

            request = JsonSerializer.Deserialize<AgentRequest>(requestLine, AgentProtocol.JsonOptions)
                ?? throw new JsonException("Request is empty.");
            if (string.IsNullOrWhiteSpace(request.Id)
                || request.Id.Length > AgentProtocol.MaximumRequestIdCharacters)
            {
                throw new JsonException(
                    $"Request id is required and must not exceed "
                    + $"{AgentProtocol.MaximumRequestIdCharacters} characters.");
            }

            if (string.IsNullOrWhiteSpace(request.Command)
                || request.Command.Length > AgentProtocol.MaximumCommandCharacters)
            {
                throw new JsonException(
                    $"Request command is required and must not exceed "
                    + $"{AgentProtocol.MaximumCommandCharacters} characters.");
            }

            if (request.ProtocolVersion != AgentProtocol.Version
                || !string.Equals(request.RootId, _endpoint.RootId, StringComparison.Ordinal))
            {
                throw new JsonException("Agent protocol or configuration root does not match this endpoint.");
            }

            using var requestTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (request.TimeoutMilliseconds is int timeoutMilliseconds)
            {
                if (timeoutMilliseconds is <= 0
                    or > AgentProtocol.MaximumRequestTimeoutMilliseconds)
                {
                    throw new JsonException(
                        $"Request timeout must be between 1 and "
                        + $"{AgentProtocol.MaximumRequestTimeoutMilliseconds} milliseconds.");
                }

                requestTimeoutSource.CancelAfter(timeoutMilliseconds);
            }

            response = await _dispatcher.DispatchAsync(request, requestTimeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            response = new AgentResponse(request?.Id ?? string.Empty, false, Error: exception.Message,
                ErrorCode: exception is OperationCanceledException ? "request_timeout" : "invalid_request",
                OperationId: request?.OperationId);
        }

        response = response with { RootId = _endpoint.RootId };
        var responseLine = JsonSerializer.Serialize(response, AgentProtocol.JsonOptions);
        try
        {
            await writer.WriteLineAsync(responseLine.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // A lost acknowledgement must not turn an accepted shutdown into a no-op.
            if (response.Ok && !response.Replayed
                && string.Equals(request?.Command, "shutdown", StringComparison.OrdinalIgnoreCase))
            {
                _shutdown.Cancel();
            }
        }
    }

    private static async Task<string?> ReadRequestLineAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var request = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return request.Length == 0 ? null : request.ToString();
            }

            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    return request.ToString().TrimEnd('\r');
                }

                request.Append(character);
                if (request.Length > AgentProtocol.MaximumRequestCharacters)
                {
                    throw new JsonException(
                        $"Request exceeds the {AgentProtocol.MaximumRequestCharacters} character limit.");
                }
            }
        }
    }
}
