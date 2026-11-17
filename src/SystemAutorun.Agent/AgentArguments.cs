using SystemAutorun.Core.Infrastructure;

namespace SystemAutorun.Agent;

internal sealed record AgentArguments(string? RootDirectory, bool BootMode)
{
    internal static AgentArguments Parse(IReadOnlyList<string> arguments)
    {
        var parsed = ProcessLaunchArguments.Parse(arguments, allowBoot: true);
        return new AgentArguments(parsed.RootDirectory, parsed.BootMode);
    }
}
