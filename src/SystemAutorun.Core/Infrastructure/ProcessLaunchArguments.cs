namespace SystemAutorun.Core.Infrastructure;

public sealed record ProcessLaunchArguments(string? RootDirectory, bool Background, bool BootMode)
{
    public static ProcessLaunchArguments Parse(
        IReadOnlyList<string> arguments, bool allowBackground = false, bool allowBoot = false)
    {
        string? root = null;
        var background = false;
        var boot = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--root":
                    if (root is not null)
                    {
                        throw new ArgumentException("--root may only be specified once.");
                    }

                    if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index])
                        || arguments[index].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("--root requires a configuration directory.");
                    }

                    root = arguments[index];
                    break;
                case "--background" when allowBackground:
                    background = true;
                    break;
                case "--boot" when allowBoot:
                    boot = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown launch argument: {arguments[index]}");
            }
        }

        return new ProcessLaunchArguments(root, background, boot);
    }
}
