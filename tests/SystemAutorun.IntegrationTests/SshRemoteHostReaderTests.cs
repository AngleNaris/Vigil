using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.IntegrationTests;

public sealed class SshRemoteHostReaderTests
{
    [Fact]
    public void BuildsStrictNonInteractiveSshArguments()
    {
        var definition = CreateDefinition();

        var arguments = SshRemoteHostReader.BuildArguments(definition);

        Assert.Contains("BatchMode=yes", arguments);
        Assert.Equal(["-T", "-F", "none"], arguments.Take(3));
        foreach (var option in new[] { "PermitLocalCommand=no", "ClearAllForwardings=yes", "ForwardAgent=no", "ForwardX11=no", "Tunnel=no",
            "ProxyCommand=none", "ProxyJump=none", "ControlMaster=no", "ControlPath=none", "UpdateHostKeys=no", "GlobalKnownHostsFile=none",
            "PasswordAuthentication=no", "KbdInteractiveAuthentication=no", "PreferredAuthentications=publickey", "IdentityFile=none", "CertificateFile=none" })
        { Assert.Contains(option, arguments); }
        Assert.Contains("StrictHostKeyChecking=yes", arguments);
        Assert.Contains("ConnectionAttempts=1", arguments);
        Assert.Contains("ConnectTimeout=9", arguments);
        Assert.Contains(
            $"UserKnownHostsFile=\"{Path.GetFullPath(definition.KnownHostsFile).Replace('\\', '/')}\"",
            arguments);
        Assert.Equal("--", arguments[^3]);
        Assert.Equal(definition.Host, arguments[^2]);
        Assert.Contains("python3 -c", arguments[^1], StringComparison.Ordinal);
        Assert.DoesNotContain(definition.UserName, arguments[^1], StringComparison.Ordinal);
        Assert.DoesNotContain(definition.Host, arguments[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateKeyAuthenticationUsesIdentityAsOneArgument()
    {
        var identity = Path.Combine(Path.GetTempPath(), "keys", "home server");
        var definition = CreateDefinition() with
        {
            Authentication = RemoteAuthenticationMode.PrivateKey,
            IdentityFile = identity,
        };

        var arguments = SshRemoteHostReader.BuildArguments(definition);
        var identityIndex = Array.IndexOf(arguments.ToArray(), "-i");

        Assert.True(identityIndex >= 0);
        Assert.Equal(Path.GetFullPath(identity), arguments[identityIndex + 1]);
        Assert.Contains("IdentitiesOnly=yes", arguments); Assert.Contains("IdentityAgent=none", arguments);
        Assert.DoesNotContain("IdentityFile=none", arguments);
    }

    [Fact]
    public void ParsesStrictVersionedRemoteSnapshot()
    {
        var definition = CreateDefinition();
        var output =
            """
            {"schema":1,"platform":"linux","hostname":"nas01","os":"Ubuntu 24.04","uptimeSeconds":86461,"cpuPercent":12.5,"totalMemoryBytes":17179869184,"availableMemoryBytes":8589934592,"logicalProcessorCount":8,"processCount":184,"threadCount":412,"networkAddress":"192.168.1.20","bytesReceived":1200,"bytesSent":600,"disks":[{"name":"/","label":"root","totalBytes":1000,"freeBytes":250}]}
            """;

        var snapshot = SshRemoteHostReader.ParseSnapshot(definition, output);

        Assert.True(snapshot.Online);
        Assert.Equal("nas01", snapshot.MachineName);
        Assert.Equal("Ubuntu 24.04", snapshot.OsDescription);
        Assert.Equal(184, snapshot.ProcessCount);
        Assert.Equal(8, snapshot.LogicalProcessorCount);
        Assert.Equal(412, snapshot.ThreadCount);
        Assert.Equal(50, snapshot.MemoryUsedPercent);
        Assert.Equal(TimeSpan.FromSeconds(86461), snapshot.Uptime);
        Assert.Equal(75, Assert.Single(snapshot.Drives).UsedPercent);
    }

    [Theory]
    [InlineData("banner\n{\"schema\":1}")]
    [InlineData("{\"schema\":2,\"platform\":\"linux\"}")]
    [InlineData("{\"schema\":1,\"platform\":\"windows\"}")]
    public void RejectsUnexpectedOrContaminatedPayloads(string output)
    {
        Assert.ThrowsAny<JsonException>(
            () => SshRemoteHostReader.ParseSnapshot(CreateDefinition(), output));
    }

    [Fact]
    public void WindowsScriptPinsUtf8AndRemoteTextsCannotInjectControlOrUnlimitedOutput()
    {
        var command = SshRemoteHostReader.BuildArguments(CreateDefinition() with { Platform = RemoteHostPlatform.Windows })[^1];
        var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(command.Split(' ')[^1]));
        Assert.Contains("[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)", script, StringComparison.Ordinal);
        var payload = System.Text.Json.Nodes.JsonNode.Parse("{\"schema\":1,\"platform\":\"linux\",\"hostname\":\"safe\",\"uptimeSeconds\":1,\"cpuPercent\":0,\"totalMemoryBytes\":100,\"availableMemoryBytes\":50,\"disks\":[]}")!;
        payload["hostname"] = "safe\u001b[31m\r\n\u202e" + new string('x', 600);
        payload["os"] = new string('y', 600);
        var snapshot = SshRemoteHostReader.ParseSnapshot(CreateDefinition(), payload.ToJsonString());
        Assert.True(snapshot.Online); Assert.Equal(256, snapshot.MachineName.Length); Assert.Equal(256, snapshot.OsDescription.Length);
        Assert.DoesNotContain('\u001b', snapshot.MachineName); Assert.DoesNotContain('\r', snapshot.MachineName); Assert.DoesNotContain('\u202e', snapshot.MachineName);
        payload["disks"] = new System.Text.Json.Nodes.JsonArray(Enumerable.Range(0, 65)
            .Select(_ => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["name"] = "/", ["totalBytes"] = 10, ["freeBytes"] = 5 }).ToArray());
        Assert.Throws<JsonException>(() => SshRemoteHostReader.ParseSnapshot(CreateDefinition(), payload.ToJsonString()));
        Assert.Throws<JsonException>(() => SshRemoteHostReader.ParseSnapshot(CreateDefinition(), new string(' ', 64 * 1024 + 1)));
    }

    [Theory]
    [InlineData("uptimeSeconds", -1)] [InlineData("cpuPercent", 101)] [InlineData("availableMemoryBytes", 101)]
    public void RejectsInvalidMetricsInsteadOfClampingThemIntoHealthyData(string key, int value)
    {
        var payload = System.Text.Json.Nodes.JsonNode.Parse("{\"schema\":1,\"platform\":\"linux\",\"hostname\":\"safe\",\"uptimeSeconds\":1,\"cpuPercent\":0,\"totalMemoryBytes\":100,\"availableMemoryBytes\":50}")!;
        payload[key] = value;
        Assert.Throws<JsonException>(() => SshRemoteHostReader.ParseSnapshot(CreateDefinition(), payload.ToJsonString()));
    }

    [Fact]
    public void ParsesRatesMultiMountsAndNetworkInterfacesCleanly()
    {
        var definition = CreateDefinition();
        var output =
            """
            {"schema":1,"platform":"linux","hostname":"arm-server-1","os":"Ubuntu 24.04.4 LTS","uptimeSeconds":5275920,"cpuPercent":18.4,"totalMemoryBytes":25125552128,"availableMemoryBytes":19005161472,"logicalProcessorCount":4,"processCount":703,"threadCount":4842,"networkAddress":"10.18.245.127","bytesReceived":123456,"bytesSent":654321,"diskReadBytesPerSecond":1024.5,"diskWriteBytesPerSecond":2048.25,"networkReceiveBytesPerSecond":4096.0,"networkSendBytesPerSecond":512.5,"networks":[{"name":"enp0s6","address":"10.0.0.4","bytesReceived":1000,"bytesSent":500},{"name":"zt0","address":"10.18.245.127","bytesReceived":2000,"bytesSent":1500}],"disks":[{"name":"/","label":"/","totalBytes":50000,"freeBytes":15000},{"name":"/data","label":"ext4","totalBytes":90000,"freeBytes":45000}]}
            """;

        var snapshot = SshRemoteHostReader.ParseSnapshot(definition, output);
        Assert.True(snapshot.Online);
        Assert.Equal(1024.5, snapshot.DiskReadBytesPerSecond);
        Assert.Equal(2048.25, snapshot.DiskWriteBytesPerSecond);
        Assert.Equal(4096.0, snapshot.NetworkReceiveBytesPerSecond);
        Assert.Equal(512.5, snapshot.NetworkSendBytesPerSecond);
        Assert.Equal(25125552128L - 19005161472L, snapshot.MemoryUsedBytes);
        Assert.Equal(2, snapshot.Drives.Count);
        Assert.Equal("/data", snapshot.Drives[1].Name);
        Assert.NotNull(snapshot.Networks);
        Assert.Equal(2, snapshot.Networks!.Count);
        Assert.Equal("zt0", snapshot.Networks[1].Name);
        Assert.Equal("10.18.245.127", snapshot.Networks[1].Address);
    }

    [Theory]
    [InlineData("{\"schema\":1,\"platform\":\"linux\",\"hostname\":\"safe\",\"uptimeSeconds\":1,\"cpuPercent\":0,\"totalMemoryBytes\":100,\"availableMemoryBytes\":50,\"diskReadBytesPerSecond\":-1}")]
    [InlineData("{\"schema\":1,\"platform\":\"linux\",\"hostname\":\"safe\",\"uptimeSeconds\":1,\"cpuPercent\":0,\"totalMemoryBytes\":100,\"availableMemoryBytes\":50,\"networkSendBytesPerSecond\":-0.5}")]
    [InlineData("{\"schema\":1,\"platform\":\"linux\",\"hostname\":\"safe\",\"uptimeSeconds\":1,\"cpuPercent\":0,\"totalMemoryBytes\":100,\"availableMemoryBytes\":50,\"networks\":[{\"name\":\"eth0\",\"bytesReceived\":-1,\"bytesSent\":0}]}")]
    public void RejectsNegativeRemoteRatesAndNetworkCounters(string payload)
    {
        Assert.Throws<JsonException>(() => SshRemoteHostReader.ParseSnapshot(CreateDefinition(), payload));
    }

    [Fact]
    public void BuildsStreamArgumentsWithKeepAliveAndValidScripts()
    {
        var linuxArgs = SshRemoteHostReader.BuildStreamArguments(CreateDefinition(), 500);
        Assert.Contains("ServerAliveInterval=10", linuxArgs);
        Assert.Contains("ServerAliveCountMax=2", linuxArgs);
        Assert.Contains("BatchMode=yes", linuxArgs);
        var linuxCommand = linuxArgs[^1];
        Assert.Contains("python3 -c", linuxCommand, StringComparison.Ordinal);
        var b64Start = linuxCommand.IndexOf("b64decode('", StringComparison.Ordinal) + "b64decode('".Length;
        var b64End = linuxCommand.IndexOf("'", b64Start, StringComparison.Ordinal);
        var linuxScript = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(linuxCommand[b64Start..b64End]));
        Assert.Contains("/proc/diskstats", linuxScript, StringComparison.Ordinal);
        Assert.Contains("/proc/mounts", linuxScript, StringComparison.Ordinal);
        Assert.Contains("elapsedMs", linuxScript, StringComparison.Ordinal);
        Assert.Contains("interval = 0.5", linuxScript, StringComparison.Ordinal);

        var winArgs = SshRemoteHostReader.BuildStreamArguments(CreateDefinition() with { Platform = RemoteHostPlatform.Windows }, 1000);
        Assert.Contains("ServerAliveInterval=10", winArgs);
        var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(winArgs[^1].Split(' ')[^1]));
        Assert.Contains("[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)", script, StringComparison.Ordinal);
        Assert.Contains("$interval=1000", script, StringComparison.Ordinal);
        Assert.Contains("elapsedMs", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProxyTunnelArgumentsUseHostKeyAliasAndLoopbackWithoutProxyCommand()
    {
        var customPortDef = CreateDefinition() with { ProxyUri = "socks5://127.0.0.1:10808" };
        var args2222 = SshRemoteHostReader.BuildArguments(customPortDef, localProxyPort: 54321);
        Assert.Contains("ProxyCommand=none", args2222);
        Assert.Contains("ProxyJump=none", args2222);
        Assert.Contains("HostKeyAlias=[192.168.1.20]:2222", args2222);
        Assert.Contains("54321", args2222);
        Assert.DoesNotContain("2222", args2222.Where((arg, idx) => idx > 0 && args2222[idx - 1] == "-p"));
        Assert.Equal("127.0.0.1", args2222[^2]);

        var port22Def = customPortDef with { Host = "VPS.3EFS.COM", Port = 22 };
        var args22 = SshRemoteHostReader.BuildStreamArguments(port22Def, 10_000, localProxyPort: 54322);
        Assert.Contains("HostKeyAlias=vps.3efs.com", args22);
        Assert.Contains("54322", args22);
        Assert.Equal("127.0.0.1", args22[^2]);
    }

    [Fact]
    public void StreamScriptsFilterVirtualNicsCacheProcCountsAndSupportDynamicStdinRate()
    {
        var linuxArgs = SshRemoteHostReader.BuildStreamArguments(CreateDefinition(), 10_000);
        var linuxCommand = linuxArgs[^1];
        var b64Start = linuxCommand.IndexOf("b64decode('", StringComparison.Ordinal) + "b64decode('".Length;
        var b64End = linuxCommand.IndexOf("'", b64Start, StringComparison.Ordinal);
        var linuxScript = Encoding.UTF8.GetString(Convert.FromBase64String(linuxCommand[b64Start..b64End]));
        Assert.Contains("SKIP_NET_PREFIX = ('veth', 'docker', 'br-', 'cni', 'flannel', 'cali', 'virbr')", linuxScript, StringComparison.Ordinal);
        Assert.Contains("read_proc_counts", linuxScript, StringComparison.Ordinal);
        Assert.Contains("poll_interval", linuxScript, StringComparison.Ordinal);
        Assert.Contains("random.uniform(-0.2, 0.2)", linuxScript, StringComparison.Ordinal);
        Assert.Contains("select.select", linuxScript, StringComparison.Ordinal);
        Assert.Contains("interval = 10", linuxScript, StringComparison.Ordinal);

        var winArgs = SshRemoteHostReader.BuildStreamArguments(CreateDefinition() with { Platform = RemoteHostPlatform.Windows }, 10_000);
        var winScript = Encoding.Unicode.GetString(Convert.FromBase64String(winArgs[^1].Split(' ')[^1]));
        Assert.Contains("$interval=10000", winScript, StringComparison.Ordinal);
        Assert.Contains("ReadLineAsync()", winScript, StringComparison.Ordinal);
        Assert.Contains("NextDouble()", winScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalProxyTunnelForwardsSocks5AndHttpConnectCleanly()
    {
        // 1. SOCKS5 loopback proxy test (domain target -> ATYP 0x03).
        var socksProxy = new TcpListener(IPAddress.Loopback, 0);
        socksProxy.Start(1);
        try
        {
            var socksPort = ((IPEndPoint)socksProxy.LocalEndpoint).Port;
            var socksServerTask = Task.Run(async () =>
            {
                using var serverClient = await socksProxy.AcceptTcpClientAsync();
                await using var stream = serverClient.GetStream();
                var greeting = new byte[3];
                await stream.ReadExactlyAsync(greeting);
                Assert.Equal([0x05, 0x01, 0x00], greeting);
                await stream.WriteAsync(new byte[] { 0x05, 0x00 });

                var reqHeader = new byte[5];
                await stream.ReadExactlyAsync(reqHeader);
                Assert.Equal(0x05, reqHeader[0]);
                Assert.Equal(0x01, reqHeader[1]);
                Assert.Equal(0x03, reqHeader[3]);
                var domainLen = reqHeader[4];
                var rest = new byte[domainLen + 2];
                await stream.ReadExactlyAsync(rest);
                var targetDomain = Encoding.ASCII.GetString(rest, 0, domainLen);
                var targetPort = (rest[^2] << 8) | rest[^1];
                Assert.Equal("vps.3efs.com", targetDomain);
                Assert.Equal(2222, targetPort);

                await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 22 });
                await stream.WriteAsync(Encoding.ASCII.GetBytes("SSH-2.0-SOCKS5-OK\r\n"));
                var clientLine = new byte[4];
                await stream.ReadExactlyAsync(clientLine);
                await stream.WriteAsync(clientLine);
            });

            var def = CreateDefinition() with { Host = "vps.3efs.com", Port = 2222, ProxyUri = $"socks5://127.0.0.1:{socksPort}" };
            await using var tunnel = SshLocalProxyTunnel.StartIfConfigured(def, CancellationToken.None)!;
            Assert.NotNull(tunnel);

            using (var sshClient = new TcpClient())
            {
                await sshClient.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort);
                await using var clientStream = sshClient.GetStream();
                var bannerBuf = new byte[19];
                await clientStream.ReadExactlyAsync(bannerBuf);
                Assert.Equal("SSH-2.0-SOCKS5-OK\r\n", Encoding.ASCII.GetString(bannerBuf));
                await clientStream.WriteAsync(Encoding.ASCII.GetBytes("PING"));
                var echoBuf = new byte[4];
                await clientStream.ReadExactlyAsync(echoBuf);
                Assert.Equal("PING", Encoding.ASCII.GetString(echoBuf));
            }
            await socksServerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            socksProxy.Stop();
        }

        // 2. HTTP CONNECT loopback proxy test including pipelined banner right after CRLFCRLF.
        var httpProxy = new TcpListener(IPAddress.Loopback, 0);
        httpProxy.Start(1);
        try
        {
            var httpPort = ((IPEndPoint)httpProxy.LocalEndpoint).Port;
            var httpServerTask = Task.Run(async () =>
            {
                using var serverClient = await httpProxy.AcceptTcpClientAsync();
                await using var stream = serverClient.GetStream();
                var buffer = new byte[512];
                var total = 0;
                while (!Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total));
                    if (read == 0) { break; }
                    total += read;
                }
                var reqText = Encoding.ASCII.GetString(buffer, 0, total);
                Assert.StartsWith("CONNECT 192.0.2.10:2222 HTTP/1.1\r\n", reqText, StringComparison.Ordinal);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\nSSH-2.0-HTTP-OK\r\n"));
            });

            var def = CreateDefinition() with { Host = "192.0.2.10", Port = 2222, ProxyUri = $"http://127.0.0.1:{httpPort}" };
            await using var tunnel = SshLocalProxyTunnel.StartIfConfigured(def, CancellationToken.None)!;
            Assert.NotNull(tunnel);

            using (var sshClient = new TcpClient())
            {
                await sshClient.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort);
                await using var clientStream = sshClient.GetStream();
                var bannerBuf = new byte[17];
                await clientStream.ReadExactlyAsync(bannerBuf);
                Assert.Equal("SSH-2.0-HTTP-OK\r\n", Encoding.ASCII.GetString(bannerBuf));
            }
            await httpServerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            httpProxy.Stop();
        }
    }

    private static RemoteHostDefinition CreateDefinition() => new()
    {
        Id = "linux-home",
        Name = "Linux Home",
        Platform = RemoteHostPlatform.Linux,
        Host = "192.168.1.20",
        Port = 2222,
        UserName = "operator",
        KnownHostsFile = Path.Combine(Path.GetTempPath(), "known hosts", "systemautorun"),
        ConnectTimeoutSeconds = 9,
        CommandTimeoutSeconds = 15,
    };
}
