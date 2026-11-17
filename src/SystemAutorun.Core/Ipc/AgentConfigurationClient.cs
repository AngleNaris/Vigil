using SystemAutorun.Core.Configuration;

namespace SystemAutorun.Core.Ipc;

// A frontend's revision cursor, not a configuration store. Serializing reads and writes
// prevents its own overlapping requests from racing the CAS revision. Other frontends
// still conflict normally; failed mutations are never silently rebased or retried.
public sealed class AgentConfigurationClient(AgentClient client)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _revision;

    public string? Revision => Volatile.Read(ref _revision);

    public async Task<ConfigurationWorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await client.SendAsync<ConfigurationWorkspaceSnapshot>(
                "configuration", cancellationToken: cancellationToken).ConfigureAwait(false);
            _revision = snapshot.Revision;
            return snapshot;
        }
        catch
        {
            _revision = null;
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<WorkspaceMutation<T>> MutateAsync<T>(
        string command, string? name = null, object? payload = null,
        string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        if (!AgentProtocol.IsConfigurationMutation(command))
        {
            throw new ArgumentException("This client accepts only revisioned configuration mutations.", nameof(command));
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var revision = expectedRevision ?? _revision
                ?? throw new AgentCommandException("configuration_not_loaded", "Reload configuration before applying another change.");
            var operationId = AgentProtocol.NewOperationId();
            var result = await client.SendAsync<WorkspaceMutation<T>>(command, name, payload,
                expectedRevision: revision, cancellationToken: cancellationToken, operationId: operationId).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result.Revision))
            {
                throw new AgentOutcomeUnknownException(operationId, new IOException("Mutation response has no revision."));
            }
            _revision = result.Revision;
            return result;
        }
        catch
        {
            _revision = null;
            throw;
        }
        finally { _gate.Release(); }
    }
}
