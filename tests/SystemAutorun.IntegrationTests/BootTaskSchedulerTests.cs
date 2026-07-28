using System.IO;
using SystemAutorun.Windows.Scheduling;

namespace SystemAutorun.IntegrationTests;

public sealed class BootTaskSchedulerTests
{
    [Theory]
    [InlineData(0, BootTaskState.Unknown)]
    [InlineData(1, BootTaskState.Disabled)]
    [InlineData(2, BootTaskState.Queued)]
    [InlineData(3, BootTaskState.Ready)]
    [InlineData(4, BootTaskState.Running)]
    [InlineData(99, BootTaskState.Unknown)]
    public void MapsTaskSchedulerStates(int value, BootTaskState expected)
    {
        Assert.Equal(expected, BootTaskScheduler.MapState(value));
    }

    [Fact]
    public void QuotesBootAgentRootAsOneArgument()
    {
        Assert.Equal(
            "--boot --root \"C:\\Work Folder\\SystemAutorun\"",
            BootTaskScheduler.BuildAgentArguments(@"C:\Work Folder\SystemAutorun"));
    }

    [Fact]
    public void AcceptsDescendantPathWithinDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "SystemAutorun-boundary");

        Assert.True(BootConfigurationStore.IsWithinDirectory(Path.Combine(root, "logs"), root));
    }

    [Fact]
    public void RejectsEqualSiblingAndTraversedPaths()
    {
        var parent = Path.Combine(Path.GetTempPath(), "SystemAutorun-boundary-parent");
        var root = Path.Combine(parent, "Boot");

        Assert.False(BootConfigurationStore.IsWithinDirectory(root, root));
        Assert.False(BootConfigurationStore.IsWithinDirectory(Path.Combine(parent, "Boot-old"), root));
        Assert.False(BootConfigurationStore.IsWithinDirectory(Path.Combine(root, "..", "outside"), root));
    }

    [Fact]
    public void DeletesOnlyKnownStagingFiles()
    {
        var staging = CreateTemporaryDirectory();
        File.WriteAllText(Path.Combine(staging, "autorun.yaml"), "settings: {}");
        File.WriteAllText(Path.Combine(staging, ".previous-autorun.yaml"), "settings: {}");

        BootConfigurationStore.DeleteKnownStagingDirectory(staging);

        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void RefusesUnexpectedNestedStagingContent()
    {
        var staging = CreateTemporaryDirectory();
        var nested = Directory.CreateDirectory(Path.Combine(staging, "unexpected"));
        File.WriteAllText(Path.Combine(nested.FullName, "sentinel.txt"), "preserve");
        try
        {
            Assert.Throws<UnauthorizedAccessException>(
                () => BootConfigurationStore.DeleteKnownStagingDirectory(staging));

            Assert.True(File.Exists(Path.Combine(nested.FullName, "sentinel.txt")));
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "SystemAutorun.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
