using SystemAutorun.Windows.Execution;

namespace SystemAutorun.IntegrationTests;

public sealed class WindowsCommandLineTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInputProducesNoArguments(string? arguments)
    {
        Assert.Empty(WindowsCommandLine.ParseArguments(arguments));
    }

    [Fact]
    public void ParsesQuotedPathsAndUnicodeValues()
    {
        var arguments = WindowsCommandLine.ParseArguments(
            "--path \"C:\\Program Files\\SystemAutorun\" --name \"后台任务\"");

        Assert.Equal(
            new[] { "--path", @"C:\Program Files\SystemAutorun", "--name", "后台任务" },
            arguments);
    }

    [Fact]
    public void PreservesExplicitEmptyArgument()
    {
        var arguments = WindowsCommandLine.ParseArguments("--value \"\" next");

        Assert.Equal(new[] { "--value", string.Empty, "next" }, arguments);
    }
}
