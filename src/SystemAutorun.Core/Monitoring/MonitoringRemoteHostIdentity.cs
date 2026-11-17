using System.Security.Cryptography;
using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Monitoring;

public static class MonitoringRemoteHostIdentity
{
    public static string For(RemoteHostDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Id = definition.Id.Trim().ToLowerInvariant(),
            definition.Platform,
            Host = definition.Host.Trim().ToLowerInvariant(),
            definition.Port,
            UserName = definition.UserName.Trim(),
            definition.Authentication,
            IdentityFile = definition.Authentication == RemoteAuthenticationMode.PrivateKey
                ? NormalizePath(definition.IdentityFile)
                : string.Empty,
            KnownHostsFile = NormalizePath(definition.KnownHostsFile),
        }, AgentProtocol.JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static string NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return string.Empty; }
        try { return RemoteHostDefinitionValidator.ResolveLocalFilePath(value).ToUpperInvariant(); }
        catch (ArgumentException) { return value.Trim().Replace('/', '\\').ToUpperInvariant(); }
    }
}
