using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Agent;

internal sealed class AgentServer
{
    private const int MaximumConnections = 4;

    private readonly AgentCommandDispatcher _dispatcher;
    private readonly CancellationTokenSource _shutdown;

    internal AgentServer(AgentCommandDispatcher dispatcher, CancellationTokenSource shutdown)
    {
        _dispatcher = dispatcher;
        _shutdown = shutdown;
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
                        AgentProtocol.PipeName,
                        PipeDirection.InOut,
                        MaximumConnections,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    handlers.RemoveAll(handler => handler.IsCompleted);
                    handlers.Add(HandleConnectionAndDisposeAsync(pipe, capacity, cancellationToken));
                    pipe = null;
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

        var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return;
        }

        AgentRequest? request = null;
        AgentResponse response;
        try
        {
            request = JsonSerializer.Deserialize<AgentRequest>(requestLine, AgentProtocol.JsonOptions)
                ?? throw new JsonException("Request is empty.");
            response = await _dispatcher.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            response = new AgentResponse(string.Empty, false, Error: exception.Message);
        }

        var responseLine = JsonSerializer.Serialize(response, AgentProtocol.JsonOptions);
        await writer.WriteLineAsync(responseLine.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (response.Ok && string.Equals(request?.Command, "shutdown", StringComparison.OrdinalIgnoreCase))
        {
            _shutdown.Cancel();
        }
    }
}
