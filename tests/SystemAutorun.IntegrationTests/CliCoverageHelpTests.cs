using SystemAutorun.Cli;
using Xunit;

namespace SystemAutorun.IntegrationTests;

public sealed class CliCoverageHelpTests
{
    [Fact]
    public void HelpDistinguishesImplementedGuiWiringFromRemainingBusinessGapsAndAcceptance()
    {
        var help = CliApplication.HelpText;
        Assert.DoesNotContain("GUI self migration", help);
        Assert.DoesNotContain("GUI shared log integration", help);
        Assert.Contains("GUI self, shared logs and remote RPC are wired", help);
        Assert.Contains("not complete GUI/CLI acceptance", help);
        Assert.Contains("Pending parity (NOT claimed implemented): startup disable/restore/rebuild, boot install/uninstall", help);
        Assert.Contains("system log provider adapters and offline/orphan recovery hint inventory", help);
        Assert.Contains("Boot writes stay closed", help);
        Assert.Contains("Native actions remain blocked", help);
    }
}
