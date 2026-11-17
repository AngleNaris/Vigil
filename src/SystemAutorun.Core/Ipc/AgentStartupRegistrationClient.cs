using System.Text.Json;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Ipc;

public sealed class AgentStartupRegistrationClient(AgentClient client, string rootDirectory)
{
    public async Task<StartupRegistrationSnapshot> ReadAsync(string kind, CancellationToken cancellationToken = default)
    {
        if (!StartupRegistrationContract.ValidKind(kind)) { throw new ArgumentException("Registration kind must be self or boot."); }
        var root = AgentProtocol.ForRoot(rootDirectory).RootDirectory;
        var data = await client.SendAsync<JsonElement>($"startup-{kind}-status", cancellationToken: cancellationToken).ConfigureAwait(false);
        return ReadObservation(data, kind, root);
    }

    public static StartupRegistrationSnapshot ReadObservation(JsonElement data, string kind, string rootDirectory)
    {
        try { return ValidateObservation(data, kind, rootDirectory); }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or StartupRegistrationSourceException)
        { throw new AgentCommandException("protocol_mismatch", "Agent returned invalid or mismatched registration metadata."); }
    }

    private static StartupRegistrationSnapshot ValidateObservation(JsonElement data, string kind, string rootDirectory)
    {
        _ = data.GetProperty("available").GetBoolean();
        var value = data.Deserialize<StartupRegistrationSnapshot>(AgentProtocol.JsonOptions)
            ?? throw new JsonException("Missing registration observation.");
        var now = DateTimeOffset.UtcNow;
        if (!StartupRegistrationContract.ValidKind(kind) || value.SchemaVersion != 1 || value.Kind != kind
            || value.RegistrationScope != StartupRegistrationContract.Scope(kind) || value.RegistrationId != StartupRegistrationContract.Id(kind)
            || value.CompletedAt < now.AddMinutes(-1) || value.CompletedAt > now.AddSeconds(5)
            || JsonSerializer.SerializeToUtf8Bytes(value, AgentProtocol.JsonOptions).Length > StartupRegistrationContract.MaximumSnapshotBytes)
        { throw new JsonException("Invalid registration observation identity, time or bounds."); }
        if (!value.Available)
        {
            if (!StartupRegistrationContract.ValidReason(value.Reason) || value.ObservedAt is not null || value.Registration is not null
                || value.AssociatedRoot is not null || value.RootAssociation != "unknown")
            { throw new JsonException("Unavailable registration contains apparent observations."); }
            return value;
        }
        if (value.Reason is not null || value.ObservedAt != value.CompletedAt || value.Registration is null
            || !StartupRegistrationContract.ValidData(kind, value.Registration)) { throw new JsonException("Invalid registration metadata."); }
        var expected = StartupRegistrationContract.Observe(kind, value.Registration, rootDirectory);
        if (value.AssociatedRoot != expected.AssociatedRoot || value.RootAssociation != expected.RootAssociation)
        { throw new JsonException("Registration root association does not match its metadata."); }
        return value;
    }
}
