using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Windows.Health;

// Single-use loopback bridge for ssh.exe so SSH keeps ProxyCommand=none and strict
// HostKeyAlias verification while tunneling over an optional local SOCKS5/HTTP proxy.
internal sealed class SshLocalProxyTunnel : IAsyncDisposable
{
    private const int MaximumHttpHeaderBytes = 8192;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts;
    private readonly Task _relayTask;
    private volatile string? _tunnelError;
    private int _disposed;

    private SshLocalProxyTunnel(
        TcpListener listener,
        int localPort,
        RemoteHostDefinition definition,
        string scheme,
        string proxyHost,
        int proxyPort,
        CancellationToken cancellationToken)
    {
        _listener = listener;
        LocalPort = localPort;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _relayTask = Task.Run(() => RunAsync(definition, scheme, proxyHost, proxyPort, _cts.Token), CancellationToken.None);
    }

    internal int LocalPort { get; }
    internal string? TunnelError => _tunnelError;

    internal static SshLocalProxyTunnel? StartIfConfigured(
        RemoteHostDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!RemoteHostDefinitionValidator.TryParseProxyEndpoint(
                definition.ProxyUri,
                out var scheme,
                out var proxyHost,
                out var proxyPort,
                out var error))
        {
            if (error is not null)
            {
                throw new ArgumentException(error, nameof(definition));
            }
            return null;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        try
        {
            var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            return new SshLocalProxyTunnel(listener, localPort, definition, scheme, proxyHost, proxyPort, cancellationToken);
        }
        catch
        {
            listener.Stop();
            throw;
        }
    }

    private async Task RunAsync(
        RemoteHostDefinition definition,
        string scheme,
        string proxyHost,
        int proxyPort,
        CancellationToken token)
    {
        try
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                _listener.Stop();
                client.NoDelay = true;

                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                handshakeCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(definition.ConnectTimeoutSeconds, 2, 60)));

                using var upstream = new TcpClient();
                await upstream.ConnectAsync(proxyHost, proxyPort, handshakeCts.Token).ConfigureAwait(false);
                upstream.NoDelay = true;

                await using var clientStream = client.GetStream();
                await using var upstreamStream = upstream.GetStream();

                if (scheme is "socks5" or "socks5h")
                {
                    await ConnectSocks5Async(upstreamStream, definition.Host, definition.Port, handshakeCts.Token).ConfigureAwait(false);
                }
                else
                {
                    await ConnectHttpAsync(upstreamStream, definition.Host, definition.Port, handshakeCts.Token).ConfigureAwait(false);
                }

                var up = PumpAsync(clientStream, upstreamStream, upstream.Client, token);
                var down = PumpAsync(upstreamStream, clientStream, client.Client, token);
                await Task.WhenAny(up, down).ConfigureAwait(false);
                try { await Task.WhenAll(up, down).WaitAsync(TimeSpan.FromMilliseconds(300), token).ConfigureAwait(false); }
                catch (Exception) { }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _tunnelError = exception.Message;
            }
            finally
            {
                client?.Dispose();
            }
        }
        finally
        {
            try { _listener.Stop(); }
            catch (SocketException) { }
        }
    }

    private static async Task PumpAsync(NetworkStream source, NetworkStream destination, Socket destinationSocket, CancellationToken token)
    {
        await source.CopyToAsync(destination, 16384, token).ConfigureAwait(false);
        try { destinationSocket.Shutdown(SocketShutdown.Send); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task ConnectSocks5Async(NetworkStream stream, string targetHost, int targetPort, CancellationToken token)
    {
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);

        var greeting = new byte[2];
        await stream.ReadExactlyAsync(greeting, token).ConfigureAwait(false);
        if (greeting[0] != 0x05 || greeting[1] != 0x00)
        {
            throw new IOException($"SOCKS5 proxy rejected no-authentication handshake (0x{greeting[0]:X2}, 0x{greeting[1]:X2}).");
        }

        var host = UnwrapBracketedHost(targetHost);
        byte[] addressSegment;
        if (IPAddress.TryParse(host, out var ip))
        {
            var ipBytes = ip.GetAddressBytes();
            addressSegment = new byte[1 + ipBytes.Length];
            addressSegment[0] = ip.AddressFamily == AddressFamily.InterNetwork ? (byte)0x01 : (byte)0x04;
            Buffer.BlockCopy(ipBytes, 0, addressSegment, 1, ipBytes.Length);
        }
        else
        {
            var hostBytes = Encoding.ASCII.GetBytes(host);
            if (hostBytes.Length is 0 or > 255)
            {
                throw new ArgumentException("Remote host name exceeds SOCKS5 domain length limit.");
            }
            addressSegment = new byte[2 + hostBytes.Length];
            addressSegment[0] = 0x03;
            addressSegment[1] = (byte)hostBytes.Length;
            Buffer.BlockCopy(hostBytes, 0, addressSegment, 2, hostBytes.Length);
        }

        var request = new byte[3 + addressSegment.Length + 2];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        Buffer.BlockCopy(addressSegment, 0, request, 3, addressSegment.Length);
        request[^2] = (byte)(targetPort >> 8);
        request[^1] = (byte)(targetPort & 0xFF);

        await stream.WriteAsync(request, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);

        var reply = new byte[4];
        await stream.ReadExactlyAsync(reply, token).ConfigureAwait(false);
        if (reply[0] != 0x05)
        {
            throw new IOException("SOCKS5 proxy returned an invalid protocol version.");
        }
        if (reply[1] != 0x00)
        {
            throw new IOException($"SOCKS5 proxy CONNECT failed (code 0x{reply[1]:X2}).");
        }

        var trailingLength = reply[3] switch
        {
            0x01 => 4 + 2,
            0x04 => 16 + 2,
            0x03 => await ReadDomainLengthPlusPortAsync(stream, token).ConfigureAwait(false),
            _ => throw new IOException($"SOCKS5 proxy returned unsupported address type 0x{reply[3]:X2}."),
        };
        if (trailingLength > 0)
        {
            var skip = new byte[trailingLength];
            await stream.ReadExactlyAsync(skip, token).ConfigureAwait(false);
        }
    }

    private static async Task<int> ReadDomainLengthPlusPortAsync(NetworkStream stream, CancellationToken token)
    {
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
        return length[0] + 2;
    }

    private static async Task ConnectHttpAsync(NetworkStream stream, string targetHost, int targetPort, CancellationToken token)
    {
        var host = UnwrapBracketedHost(targetHost);
        var authority = IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{ip}]:{targetPort.ToString(CultureInfo.InvariantCulture)}"
            : $"{host}:{targetPort.ToString(CultureInfo.InvariantCulture)}";
        var payload = Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\nProxy-Connection: Keep-Alive\r\n\r\n");
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);

        // Read single bytes up to CRLFCRLF so pipelined SSH server banner bytes are never consumed here.
        var headerBytes = new List<byte>(256);
        var single = new byte[1];
        while (true)
        {
            if (headerBytes.Count >= MaximumHttpHeaderBytes)
            {
                throw new IOException("HTTP proxy CONNECT response header exceeded bounded limit.");
            }
            var read = await stream.ReadAsync(single, token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("HTTP proxy closed connection before completing CONNECT.");
            }
            headerBytes.Add(single[0]);
            var count = headerBytes.Count;
            if (count >= 4
                && headerBytes[count - 4] == (byte)'\r'
                && headerBytes[count - 3] == (byte)'\n'
                && headerBytes[count - 2] == (byte)'\r'
                && headerBytes[count - 1] == (byte)'\n')
            {
                break;
            }
        }

        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        var lineEnd = headerText.IndexOf("\r\n", StringComparison.Ordinal);
        var statusLine = lineEnd >= 0 ? headerText[..lineEnd] : headerText;
        var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2
            || !parts[0].StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var statusCode)
            || statusCode is < 200 or >= 300)
        {
            throw new IOException($"HTTP proxy CONNECT failed: {statusLine}");
        }
    }

    private static string UnwrapBracketedHost(string targetHost)
    {
        var trimmed = targetHost.Trim();
        return trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length > 2
            ? trimmed[1..^1]
            : trimmed;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _cts.Cancel();
        try { _listener.Stop(); }
        catch (SocketException) { }
        try { await _relayTask.ConfigureAwait(false); }
        catch (Exception) { }
        _cts.Dispose();
    }
}
