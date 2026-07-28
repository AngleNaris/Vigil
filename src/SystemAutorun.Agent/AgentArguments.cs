namespace SystemAutorun.Agent;

internal sealed record AgentArguments(string? RootDirectory, bool BootMode)
{
    internal static AgentArguments Parse(IReadOnlyList<string> arguments)
    {
        string? root = null;
        var boot = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--root" when index + 1 < arguments.Count:
                    root = arguments[++index];
                    break;
                case "--boot":
                    boot = true;
                    break;
            }
        }

        return new AgentArguments(root, boot);
    }
}
