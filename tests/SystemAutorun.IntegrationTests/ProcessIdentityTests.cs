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
}
