using System.Runtime.InteropServices;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.Agent;

internal sealed class AgentStartupRegistrationService(string rootDirectory, IStartupRegistrationReader reader)
{
    private int _reading;
    private Task? _pendingRelease;
    internal async Task<StartupRegistrationSnapshot> ReadAsync(string kind, CancellationToken cancellationToken)
    {
        if (!StartupRegistrationContract.ValidKind(kind)) { throw new ArgumentException("Unknown registration kind."); }
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0) { return StartupRegistrationContract.Unavailable(kind, "registration_busy"); }
        Task<StartupRegistrationData>? read = null;
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            budget.CancelAfter(TimeSpan.FromSeconds(5));
            read = reader.ReadAsync(kind, budget.Token);
            var data = await read.WaitAsync(budget.Token).ConfigureAwait(false);
            budget.Token.ThrowIfCancellationRequested();
            if (data is null) { throw new InvalidDataException("Missing registration metadata."); }
            return StartupRegistrationContract.Observe(kind, data, rootDirectory);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return StartupRegistrationContract.Unavailable(kind, "registration_timeout"); }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { return StartupRegistrationContract.Unavailable(kind, "permission_denied"); }
        catch (COMException exception) when ((uint)exception.HResult == 0x80070005)
        { return StartupRegistrationContract.Unavailable(kind, "permission_denied"); }
        catch (StartupRegistrationSourceException exception) when (StartupRegistrationContract.ValidReason(exception.Reason))
        { return StartupRegistrationContract.Unavailable(kind, exception.Reason); }
        catch (InvalidDataException) { return StartupRegistrationContract.Unavailable(kind, "registration_invalid"); }
        catch (Exception) { return StartupRegistrationContract.Unavailable(kind, "registration_source_unavailable"); }
        finally
        {
            if (read is { IsCompleted: false }) { _pendingRelease = ReleaseWhenFinishedAsync(read, budget); }
            else { budget.Dispose(); Volatile.Write(ref _reading, 0); }
        }
    }
    private async Task ReleaseWhenFinishedAsync(Task read, CancellationTokenSource budget)
    {
        try { await read.ConfigureAwait(false); }
        catch (Exception) { /* The caller already has unavailable/cancelled, with no native payload logged. */ }
        finally { budget.Dispose(); Volatile.Write(ref _reading, 0); }
    }
}
