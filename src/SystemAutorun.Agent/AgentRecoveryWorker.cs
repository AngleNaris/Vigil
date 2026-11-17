using System.Threading.Channels;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Agent;

internal sealed class AgentRecoveryWorker(AgentTaskController controller, Func<AgentMonitoringSnapshot, bool>? evidenceCurrent = null)
{
    private readonly Channel<AgentMonitoringSnapshot> _latest = Channel.CreateBounded<AgentMonitoringSnapshot>(
        new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private string? _lastError;
    private long _completedCycles;
    internal string? LastError => Volatile.Read(ref _lastError);
    internal long CompletedCycles => Interlocked.Read(ref _completedCycles);
    internal void Enqueue(AgentMonitoringSnapshot cycle) => _latest.Writer.TryWrite(cycle);

    internal async Task RunAsync(CancellationToken token)
    {
        try
        {
            await foreach (var cycle in _latest.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var results = await controller.RecoverObservedAsync(cycle, token, evidenceCurrent: evidenceCurrent).ConfigureAwait(false);
                    var failure = results.FirstOrDefault(result => !result.Succeeded)?.Message;
                    Volatile.Write(ref _lastError, failure is { Length: > 512 } ? failure[..512] : failure);
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    var error = $"{exception.GetType().Name}: {exception.Message}";
                    Volatile.Write(ref _lastError, error.Length > 512 ? error[..512] : error);
                }
                finally { Interlocked.Increment(ref _completedCycles); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { _latest.Writer.TryComplete(); }
    }
}
