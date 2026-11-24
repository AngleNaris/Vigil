using System.Globalization;
using System.Text.Json.Serialization;

namespace SystemAutorun.Core.Models;

public sealed record ManagedProcessInstanceSnapshot(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    DateTimeOffset? StartedAt,
    double CpuUsagePercent,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    int ThreadCount,
    bool CpuUsageAvailable = false,
    double DiskReadBytesPerSecond = 0,
    double DiskWriteBytesPerSecond = 0,
    double NetworkReceiveBytesPerSecond = 0,
    double NetworkSendBytesPerSecond = 0);

public sealed record ManagedProcessSnapshot(
    string TaskName,
    string Description,
    string Type,
    bool ExpectedRunning,
    bool Enabled,
    bool Healthy,
    string State,
    string Reason,
    string Detail,
    DateTimeOffset CapturedAt,
    IReadOnlyList<ManagedProcessInstanceSnapshot> Processes,
    bool HealthObserved = true,
    string? TaskIdentity = null)
{
    public int ProcessCount => Processes.Count;

    public bool CpuUsageAvailable => Processes.Count > 0 && Processes.Any(process => process.CpuUsageAvailable);

    public double CpuUsagePercent => Processes.Sum(process => process.CpuUsagePercent);

    public long WorkingSetBytes => Processes.Sum(process => process.WorkingSetBytes);

    public long PrivateMemoryBytes => Processes.Sum(process => process.PrivateMemoryBytes);

    public int ThreadCount => Processes.Sum(process => process.ThreadCount);

    [JsonPropertyName("total_disk_read_bytes_per_second")]
    public double TotalDiskReadBytesPerSecond => Math.Round(Processes.Sum(item => item.DiskReadBytesPerSecond), 2);

    [JsonPropertyName("total_disk_write_bytes_per_second")]
    public double TotalDiskWriteBytesPerSecond => Math.Round(Processes.Sum(item => item.DiskWriteBytesPerSecond), 2);

    [JsonPropertyName("total_network_receive_bytes_per_second")]
    public double TotalNetworkReceiveBytesPerSecond => Math.Round(Processes.Sum(item => item.NetworkReceiveBytesPerSecond), 2);

    [JsonPropertyName("total_network_send_bytes_per_second")]
    public double TotalNetworkSendBytesPerSecond => Math.Round(Processes.Sum(item => item.NetworkSendBytesPerSecond), 2);

    public DateTimeOffset? StartedAt => Processes
        .Where(process => process.StartedAt is not null)
        .Select(process => process.StartedAt)
        .Min();
}

public enum ExceptionLogLevel
{
    Warning,
    Error,
    Critical,
}

public sealed record ExceptionLogEntry(
    DateTimeOffset Timestamp,
    ExceptionLogLevel Level,
    string Source,
    string Message,
    string FileName);

public sealed record ExceptionLogSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<ExceptionLogEntry> Entries,
    int SourceCount,
    bool Truncated);

public enum RemoteHostPlatform
{
    Windows,
    Linux,
}

public enum RemoteAuthenticationMode
{
    SshAgent,
    PrivateKey,
}

public sealed record RemoteHostDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("platform")]
    public RemoteHostPlatform Platform { get; init; }

    [JsonPropertyName("host")]
    public string Host { get; init; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; init; } = 22;

    [JsonPropertyName("user_name")]
    public string UserName { get; init; } = string.Empty;

    [JsonPropertyName("authentication")]
    public RemoteAuthenticationMode Authentication { get; init; } = RemoteAuthenticationMode.SshAgent;

    [JsonPropertyName("identity_file")]
    public string? IdentityFile { get; init; }

    [JsonPropertyName("known_hosts_file")]
    public string KnownHostsFile { get; init; } = string.Empty;

    [JsonPropertyName("connect_timeout_seconds")]
    public int ConnectTimeoutSeconds { get; init; } = 10;

    [JsonPropertyName("command_timeout_seconds")]
    public int CommandTimeoutSeconds { get; init; } = 20;

    [JsonPropertyName("proxy_uri")]
    public string? ProxyUri { get; init; }
}

public static class RemoteHostDefinitionValidator
{
    public static void Validate(RemoteHostDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateToken(definition.Id, nameof(definition.Id), allowSpaces: false);
        ValidateToken(definition.Name, nameof(definition.Name), allowSpaces: true);
        ValidateToken(definition.Host, nameof(definition.Host), allowSpaces: false);
        ValidateToken(definition.UserName, nameof(definition.UserName), allowSpaces: false);
        if (!Enum.IsDefined(definition.Platform) || !Enum.IsDefined(definition.Authentication))
        { throw new ArgumentException("Unsupported remote platform or authentication mode."); }
        if (definition.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(definition.Port), "SSH port must be between 1 and 65535.");
        }

        if (definition.ConnectTimeoutSeconds is < 1 or > 60)
        {
            throw new ArgumentOutOfRangeException(
                nameof(definition.ConnectTimeoutSeconds),
                "SSH connection timeout must be between 1 and 60 seconds.");
        }

        if (definition.CommandTimeoutSeconds is < 2 or > 120)
        {
            throw new ArgumentOutOfRangeException(
                nameof(definition.CommandTimeoutSeconds),
                "SSH command timeout must be between 2 and 120 seconds.");
        }

        if (string.IsNullOrWhiteSpace(definition.KnownHostsFile))
        {
            throw new ArgumentException("A dedicated known_hosts file is required.", nameof(definition));
        }

        if (definition.Authentication == RemoteAuthenticationMode.PrivateKey
            && string.IsNullOrWhiteSpace(definition.IdentityFile))
        {
            throw new ArgumentException("Private-key authentication requires an identity file.", nameof(definition));
        }
        _ = ResolveLocalFilePath(definition.KnownHostsFile);
        if (definition.Authentication == RemoteAuthenticationMode.PrivateKey) { _ = ResolveLocalFilePath(definition.IdentityFile!); }
        ValidateProxyUri(definition.ProxyUri);
    }

    public static void ValidateProxyUri(string? proxyUri)
    {
        if (string.IsNullOrWhiteSpace(proxyUri))
        {
            return;
        }

        if (!TryParseProxyEndpoint(proxyUri, out _, out _, out _, out var error))
        {
            throw new ArgumentException(error ?? "Invalid proxy URI.", nameof(proxyUri));
        }
    }

    public static bool TryParseProxyEndpoint(
        string? proxyUri,
        out string scheme,
        out string host,
        out int port,
        out string? error)
    {
        scheme = string.Empty;
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(proxyUri))
        {
            error = null;
            return false;
        }

        if (proxyUri.Length > 256)
        {
            error = "ProxyUri requires 1..256 characters.";
            return false;
        }

        if (proxyUri.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            error = "ProxyUri contains unsupported whitespace or control characters.";
            return false;
        }

        var separator = proxyUri.IndexOf("://", StringComparison.Ordinal);
        string authority;
        if (separator >= 0)
        {
            scheme = proxyUri[..separator].ToLowerInvariant();
            if (scheme is not ("socks5" or "socks5h" or "http"))
            {
                scheme = string.Empty;
                error = "ProxyUri scheme must be socks5, socks5h, or http.";
                return false;
            }
            authority = proxyUri[(separator + 3)..];
        }
        else
        {
            scheme = "socks5";
            authority = proxyUri;
        }

        if (string.IsNullOrEmpty(authority) || authority.IndexOfAny(['/', '?', '#', '@', '\\']) >= 0)
        {
            scheme = string.Empty;
            error = "ProxyUri must use [scheme://]host:port without credentials, paths, or query parameters.";
            return false;
        }

        string portText;
        if (authority.StartsWith('['))
        {
            var closeBracket = authority.IndexOf(']');
            if (closeBracket <= 1 || closeBracket + 2 >= authority.Length || authority[closeBracket + 1] != ':')
            {
                scheme = string.Empty;
                error = "ProxyUri IPv6 endpoint must use [host]:port format.";
                return false;
            }
            host = authority[1..closeBracket];
            portText = authority[(closeBracket + 2)..];
            if (Uri.CheckHostName(host) != UriHostNameType.IPv6)
            {
                scheme = string.Empty;
                host = string.Empty;
                error = "ProxyUri contains an invalid IPv6 host.";
                return false;
            }
        }
        else
        {
            var colon = authority.IndexOf(':');
            if (colon <= 0 || colon != authority.LastIndexOf(':') || colon == authority.Length - 1)
            {
                scheme = string.Empty;
                error = "ProxyUri must include both host and port (host:port).";
                return false;
            }
            host = authority[..colon];
            portText = authority[(colon + 1)..];
            if (Uri.CheckHostName(host) is UriHostNameType.Unknown or UriHostNameType.Basic or UriHostNameType.IPv6)
            {
                scheme = string.Empty;
                host = string.Empty;
                error = "ProxyUri contains an invalid host.";
                return false;
            }
        }

        if (!portText.All(character => character is >= '0' and <= '9')
            || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || port is < 1 or > 65535)
        {
            scheme = string.Empty;
            host = string.Empty;
            port = 0;
            error = "ProxyUri port must be between 1 and 65535.";
            return false;
        }

        error = null;
        return true;
    }

    public static string NormalizeProxyUri(string? proxyUri) =>
        TryParseProxyEndpoint(proxyUri, out var scheme, out var host, out var port, out _)
            ? host.Contains(':', StringComparison.Ordinal)
                ? $"{scheme}://[{host.ToLowerInvariant()}]:{port.ToString(CultureInfo.InvariantCulture)}"
                : $"{scheme}://{host.ToLowerInvariant()}:{port.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;

    // Lexical only: configuration CRUD must not probe a network path or open credentials.
    public static string ResolveLocalFilePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var path = Environment.ExpandEnvironmentVariables(value);
        if (path.Length > 1024 || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')
            || path.IndexOf(':', 2) >= 0 || path.Any(char.IsControl) || path.Contains('%') || path.Contains("${", StringComparison.Ordinal))
        { throw new ArgumentException("SSH files require absolute local DOS paths; UNC, device paths, streams, unresolved variables and SSH tokens are rejected."); }
        foreach (var part in path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            if (part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(['*', '?', '<', '>', '"', '|']) >= 0
                || System.Text.RegularExpressions.Regex.IsMatch(part.Split('.')[0].TrimEnd(' '), "^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            { throw new ArgumentException("Ambiguous, traversing or reserved SSH file paths are rejected."); }
        }
        return Path.GetFullPath(path);
    }

    private static void ValidateToken(string value, string field, bool allowSpaces)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
        {
            throw new ArgumentException($"{field} requires 1..256 characters.", field);
        }

        if (value.Any(character => char.IsControl(character)
                || (!allowSpaces && char.IsWhiteSpace(character))))
        {
            throw new ArgumentException($"{field} contains unsupported whitespace or control characters.", field);
        }
    }
}

public sealed record RemoteHostStatusSnapshot(
    string Id,
    string Name,
    RemoteHostPlatform Platform,
    string Host,
    bool Online,
    string? Error,
    DateTimeOffset CapturedAt,
    string MachineName,
    string OsDescription,
    TimeSpan Uptime,
    double CpuUsagePercent,
    long TotalMemoryBytes,
    long AvailableMemoryBytes,
    IReadOnlyList<DriveStatusSnapshot> Drives,
    string NetworkAddress,
    long BytesReceived,
    long BytesSent,
    int LogicalProcessorCount,
    int ProcessCount,
    int ThreadCount,
    double? DiskReadBytesPerSecond = null,
    double? DiskWriteBytesPerSecond = null,
    double? NetworkReceiveBytesPerSecond = null,
    double? NetworkSendBytesPerSecond = null,
    IReadOnlyList<NetworkStatusSnapshot>? Networks = null)
{
    public long MemoryUsedBytes => TotalMemoryBytes <= 0
        ? 0
        : Math.Clamp(TotalMemoryBytes - AvailableMemoryBytes, 0, TotalMemoryBytes);

    public double MemoryUsedPercent => TotalMemoryBytes <= 0
        ? 0
        : Math.Clamp((TotalMemoryBytes - AvailableMemoryBytes) * 100d / TotalMemoryBytes, 0, 100);

    public static RemoteHostStatusSnapshot Offline(RemoteHostDefinition definition, string error) => new(
        definition.Id,
        definition.Name,
        definition.Platform,
        definition.Host,
        false,
        error,
        DateTimeOffset.Now,
        definition.Host,
        "远程主机不可用",
        TimeSpan.Zero,
        0,
        0,
        0,
        Array.Empty<DriveStatusSnapshot>(),
        "--",
        0,
        0,
        0,
        0,
        0);
}
