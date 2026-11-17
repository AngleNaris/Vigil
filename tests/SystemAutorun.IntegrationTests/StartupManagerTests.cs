using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed class StartupManagerTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Tool\\tool.exe\" --silent", @"C:\Program Files\Tool\tool.exe", "--silent")]
    [InlineData("C:\\Tools\\tool.exe\t--silent", @"C:\Tools\tool.exe", "--silent")]
    [InlineData("tool.exe", "tool.exe", "")]
    public void SplitsUnambiguousStartupCommandWithoutProbing(string command, string expectedTarget, string expectedArguments)
    {
        var result = StartupManager.SplitInventoryCommand(command);

        Assert.Equal(expectedTarget, result.Target);
        Assert.Equal(expectedArguments, result.Arguments);
    }
}
