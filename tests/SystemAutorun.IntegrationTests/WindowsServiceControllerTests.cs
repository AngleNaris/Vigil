using SystemAutorun.Windows.Execution;

namespace SystemAutorun.IntegrationTests;

public sealed class WindowsServiceControllerTests
{
    [Fact]
    public void MissingServiceIsReportedWithoutMutation()
    {
        var status = new WindowsServiceController().GetStatus(
            $"SystemAutorun-Missing-{Guid.NewGuid():N}");

        Assert.False(status.Exists);
        Assert.False(status.Running);
        Assert.Null(status.ProcessId);
    }

    [Theory]
    [InlineData(1u, "stopped")]
    [InlineData(2u, "start pending")]
    [InlineData(4u, "running")]
    [InlineData(7u, "paused")]
    public void MapsNativeServiceStates(uint state, string expected)
    {
        Assert.Equal(expected, WindowsServiceController.StateLabel(state));
    }
}
