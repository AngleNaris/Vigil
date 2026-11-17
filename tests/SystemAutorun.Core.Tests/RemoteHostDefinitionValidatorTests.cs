using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

public sealed class RemoteHostDefinitionValidatorTests
{
    [Fact]
    public void AcceptsAgentAuthenticationWithDedicatedKnownHosts()
    {
        var definition = CreateDefinition();

        RemoteHostDefinitionValidator.Validate(definition);
    }

    [Fact]
    public void RequiresIdentityFileForPrivateKeyAuthentication()
    {
        var definition = CreateDefinition() with
        {
            Authentication = RemoteAuthenticationMode.PrivateKey,
            IdentityFile = null,
        };

        var exception = Assert.Throws<ArgumentException>(
            () => RemoteHostDefinitionValidator.Validate(definition));

        Assert.Contains("identity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void RejectsInvalidPorts(int port)
    {
        var definition = CreateDefinition() with { Port = port };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => RemoteHostDefinitionValidator.Validate(definition));
    }

    [Fact]
    public void RejectsWhitespaceInsideConnectionTokens()
    {
        var definition = CreateDefinition() with { Host = "server name" };

        Assert.Throws<ArgumentException>(
            () => RemoteHostDefinitionValidator.Validate(definition));
    }

    private static RemoteHostDefinition CreateDefinition() => new()
    {
        Id = "home-server",
        Name = "Home Server",
        Platform = RemoteHostPlatform.Linux,
        Host = "home.example.test",
        UserName = "operator",
        KnownHostsFile = @"C:\Users\operator\.ssh\known_hosts.systemautorun",
    };
}
