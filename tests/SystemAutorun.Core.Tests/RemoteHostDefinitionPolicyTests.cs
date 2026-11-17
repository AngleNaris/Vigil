using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

public sealed class RemoteHostDefinitionPolicyTests
{
    [Theory]
    [InlineData("relative_hosts")]
    [InlineData(@"C:relative")]
    [InlineData(@"\\never-connect.invalid\share\known_hosts")]
    [InlineData(@"\\?\C:\known_hosts")]
    [InlineData(@"\\.\NUL")]
    [InlineData(@"C:\tmp\NUL.txt")]
    [InlineData(@"C:\tmp\known_hosts:stream")]
    [InlineData(@"C:\tmp\..\known_hosts")]
    [InlineData(@"C:\tmp\trailing.\known_hosts")]
    [InlineData(@"C:\tmp\wild*\known_hosts")]
    [InlineData(@"C:\tmp\%h\known_hosts")]
    [InlineData(@"C:\tmp\${HOME}\known_hosts")]
    public void RejectsAmbiguousNonLocalAndSshExpandedFilePathsBeforeAnyFilesystemAccess(string path)
    {
        Assert.ThrowsAny<ArgumentException>(() => RemoteHostDefinitionValidator.Validate(Definition() with { KnownHostsFile = path }));
        Assert.ThrowsAny<ArgumentException>(() => RemoteHostDefinitionValidator.Validate(Definition() with
        { Authentication = RemoteAuthenticationMode.PrivateKey, IdentityFile = path }));
    }

    [Fact]
    public void ExpandsEnvironmentVariablesAndAllowsSpacesInAbsoluteLocalPath()
    {
        var value = @"%USERPROFILE%\synthetic keys\known_hosts";
        Assert.Equal(Path.GetFullPath(Environment.ExpandEnvironmentVariables(value)), RemoteHostDefinitionValidator.ResolveLocalFilePath(value));
        RemoteHostDefinitionValidator.Validate(Definition() with { KnownHostsFile = value });
    }

    [Fact]
    public void RejectsUnsupportedEnumsAndOversizedNamesButDoesNotProbeFilesOrUnusedIdentityPath()
    {
        var host = Definition();
        RemoteHostDefinitionValidator.Validate(host with { IdentityFile = @"\\never-connect.invalid\unused" });
        Assert.Throws<ArgumentException>(() => RemoteHostDefinitionValidator.Validate(host with { Platform = (RemoteHostPlatform)999 }));
        Assert.Throws<ArgumentException>(() => RemoteHostDefinitionValidator.Validate(host with { Authentication = (RemoteAuthenticationMode)999 }));
        Assert.Throws<ArgumentException>(() => RemoteHostDefinitionValidator.Validate(host with { Name = new string('x', 257) }));
    }

    private static RemoteHostDefinition Definition() => new()
    { Id = "fixture", Name = "synthetic", Host = "127.0.0.1", UserName = "synthetic", KnownHostsFile = @"C:\synthetic never-opened\known_hosts" };
}
