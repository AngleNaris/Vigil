using SystemAutorun.Core.Infrastructure;
using Xunit;

namespace SystemAutorun.Core.Tests;

public sealed class ElevatedCommandPolicyTests
{
    [Theory]
    [InlineData("install-boot")]
    [InlineData("uninstall-boot")]
    [InlineData("start-boot")]
    public void ExistingBootCommandsRemainSupported(string command) => Assert.True(ElevatedCommandPolicy.IsSupported(command));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")]
    [InlineData("startup-disable")] [InlineData("startup-restore")] [InlineData("startup-rebuild")]
    [InlineData("INSTALL-BOOT")] [InlineData("install-boot ")] [InlineData(" install-boot")]
    [InlineData("install-boot\0")] [InlineData("install-boot\r\n")] [InlineData("unknown")]
    public void UnsupportedCommandsCannotEnterTheElevatedWorkflow(string? command)
    {
        Assert.False(ElevatedCommandPolicy.IsSupported(command)); Assert.Equal(64, ElevatedCommandPolicy.RejectedExitCode);
    }
}
