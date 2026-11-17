using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Diagnostics;

namespace SystemAutorun.App.Services;

internal sealed class SingleInstanceService : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly EventWaitHandle _instanceHandle;
    private readonly bool _isPrimary;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _listenGate = new();
    private Task? _listenTask;
    private bool _disposed;

    internal SingleInstanceService(string? instanceName = null)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        instanceName ??= $"Vigil.App.{sid}";
        _pipeName = instanceName.Replace('-', '_');
        _instanceHandle = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            $"Local\\{instanceName}",
            out _isPrimary);
    }

    internal bool IsPrimary => _isPrimary;

    internal void StartListening(Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        lock (_listenGate)
        {
            if (!IsPrimary || _disposed || _listenTask is not null)
            {
                return;
            }

            _listenTask = ListenAsync(activate, _shutdown.Token);
        }
    }

    internal async Task NotifyPrimaryAsync(CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync("activate".AsMemory(), timeout.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Task? listenTask;
        lock (_listenGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown.Cancel();
            listenTask = _listenTask;
        }

        if (listenTask is not null)
        {
            try
            {
                await listenTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _instanceHandle.Dispose();
        _shutdown.Dispose();
    }

    private async Task ListenAsync(Action activate, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.Equals(command, "activate", StringComparison.Ordinal))
                {
                    try
                    {
                        activate();
                    }
                    catch (Exception exception)
                    {
                        Trace.TraceError("Single-instance activation failed: {0}", exception);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Single-instance listener retrying after pipe failure: {0}", exception);
                try
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}
