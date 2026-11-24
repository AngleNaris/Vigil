using System.IO;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Execution;

namespace SystemAutorun.IntegrationTests;

public sealed class ProcessIdentityTests
{
    [Fact]
    public void MatchesExactPathCaseInsensitively()
    {
        Assert.True(ProcessIdentity.IsExpectedImage(
            @"C:\Program Files\Tool\Tool.exe",
            @"c:\program files\tool\tool.exe"));
    }

    [Fact]
    public void MatchesChildBinaryWithinInstallTree()
    {
        Assert.True(ProcessIdentity.IsExpectedImage(
            @"C:\Program Files\Tool\bin\worker.exe",
            @"C:\Program Files\Tool\Tool.exe"));
    }

    [Fact]
    public void RejectsSameNameOutsideInstallTree()
    {
        Assert.False(ProcessIdentity.IsExpectedImage(
            @"D:\Other\Tool.exe",
            @"C:\Program Files\Tool\Tool.exe"));
    }

    [Fact]
    public void NormalizesProcessNameWithoutStrippingDotsBeforeExe()
    {
        Assert.Equal("clash.meta", ProcessIdentity.NormalizeProcessName("clash.meta"));
        Assert.Equal("clash.meta", ProcessIdentity.NormalizeProcessName("clash.meta.exe"));
        Assert.Equal("agy-server", ProcessIdentity.NormalizeProcessName("agy-server.EXE"));
    }

    [Fact]
    public void FallsBackToExecutableFileNameWhenTcpHealthCheckOmitsPattern()
    {
        var currentPath = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(currentPath));
        var task = new AutorunTask(
            "tcp-service",
            AutorunTaskType.Windows,
            true,
            "tcp-service",
            currentPath,
            Array.Empty<string>(),
            Path.GetDirectoryName(currentPath),
            null,
            null,
            null,
            Array.Empty<string>(),
            0,
            new HealthCheckDefinition(
                HealthCheckType.Tcp,
                Pattern: null,
                Host: "127.0.0.1",
                Port: 18765,
                TimeoutSeconds: 30,
                Distro: null,
                Service: null),
            LaunchOptions.LegacyDefault(AutorunTaskType.Windows),
            ExpectedRunning: true);

        var pids = new ProcessIdentity().FindMatchingProcessIds(task);
        Assert.Contains(Environment.ProcessId, pids);
    }
}
