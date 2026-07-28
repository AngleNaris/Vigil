using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Execution;

namespace SystemAutorun.IntegrationTests;

public sealed class NativeProcessLauncherTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    public void QuotesWindowsArguments(string value, string expected)
    {
        Assert.Equal(expected, NativeProcessLauncher.QuoteArgument(value));
    }

    [Fact]
    public async Task NoConsoleLaunchCreatesNoTopLevelWindow()
    {
        var task = new AutorunTask(
            "hidden-test",
            AutorunTaskType.Windows,
            true,
            string.Empty,
            Environment.ExpandEnvironmentVariables("%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"),
            new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 2" },
            null,
            null,
            null,
            null,
            Array.Empty<string>(),
            0,
            new HealthCheckDefinition(HealthCheckType.Process, "powershell.exe", null, null, 5, null, null),
            new LaunchOptions(
                RunAsMode.CurrentUser,
                LaunchTrigger.Manual,
                WindowMode.NoConsole,
                ProcessLifecycle.Tracked));

        var processId = new NativeProcessLauncher().Start(task);
        using var process = System.Diagnostics.Process.GetProcessById(processId);
        await Task.Delay(300);

        Assert.Equal(IntPtr.Zero, process.MainWindowHandle);
        await process.WaitForExitAsync();
    }
}
