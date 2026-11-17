using System.IO;
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
