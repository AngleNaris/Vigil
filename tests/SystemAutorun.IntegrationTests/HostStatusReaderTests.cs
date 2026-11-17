using SystemAutorun.Windows.Health;

namespace SystemAutorun.IntegrationTests;

public sealed class HostStatusReaderTests
{
    [Fact]
    public async Task ReadsWindowsHostSnapshot()
    {
        using var reader = new HostStatusReader();
        var snapshot = await reader.ReadAsync();

        Assert.Equal(Environment.MachineName, snapshot.MachineName);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.OsDescription));
        Assert.InRange(snapshot.CpuUsagePercent, 0, 100);
        Assert.True(snapshot.TotalMemoryBytes > 0);
        Assert.InRange(snapshot.AvailableMemoryBytes, 0, snapshot.TotalMemoryBytes);
        Assert.True(snapshot.Uptime > TimeSpan.Zero);
        Assert.All(snapshot.Drives, drive =>
        {
            Assert.True(drive.TotalBytes > 0);
            Assert.InRange(drive.FreeBytes, 0, drive.TotalBytes);
        });
        Assert.All(snapshot.Networks, network =>
            Assert.False(string.IsNullOrWhiteSpace(network.Address)));
    }
}
