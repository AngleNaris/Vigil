using System.Security.Principal;
using System.Text;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Windows.Scheduling;

try
{
    var arguments = ElevatedArguments.Parse(args);
    EnsureAdministrator();
    var paths = ProjectPaths.Resolve(arguments.RootDirectory);
    var scheduler = new BootTaskScheduler();
    switch (arguments.Command)
    {
        case "install-boot":
            var trustedAgentPath = BootConfigurationStore.ResolveTrustedAgentPath();
            var bootPaths = await new BootConfigurationStore(new ConfigurationLoader())
                .PrepareAsync(paths);
            _ = scheduler.Install(trustedAgentPath, bootPaths.RootDirectory);
            break;
        case "uninstall-boot":
            _ = scheduler.Uninstall();
            break;
        case "start-boot":
            scheduler.Run();
            break;
        case "startup-disable":
            using (var manager = new SystemAutorun.Windows.Startup.StartupManager(paths))
            {
                _ = await manager.DisableAsync(
                    Required(arguments.Source, "--source"),
                    Required(arguments.Name, "--name"),
                    Required(arguments.Origin, "--origin"));
            }

            break;
        case "startup-restore":
            using (var manager = new SystemAutorun.Windows.Startup.StartupManager(paths))
            {
                _ = await manager.RestoreAsync(Required(arguments.BackupId, "--backup-id"));
            }

            break;
        default:
            throw new ArgumentException("Unsupported elevated command.");
    }

    return 0;
}
catch (Exception exception)
{
    await WriteFailureAsync(exception);
    return 1;
}

static void EnsureAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
    {
        throw new UnauthorizedAccessException("Administrator privileges are required.");
    }
}

static string Required(string? value, string option) =>
    string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException($"Required option is missing: {option}")
        : value;

static async Task WriteFailureAsync(Exception exception)
{
    try
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SystemAutorun",
            "logs");
        Directory.CreateDirectory(directory);
        await File.AppendAllTextAsync(
            Path.Combine(directory, "elevated.log"),
            $"[{DateTimeOffset.Now:O}] {exception}{Environment.NewLine}",
            new UTF8Encoding(false));
    }
    catch (Exception logException) when (
        logException is IOException or UnauthorizedAccessException or System.Security.SecurityException)
    {
    }
}

internal sealed record ElevatedArguments(
    string Command,
    string? RootDirectory,
    string? Source,
    string? Name,
    string? Origin,
    string? BackupId)
{
    internal static ElevatedArguments Parse(IReadOnlyList<string> arguments)
    {
        var command = arguments.Count > 0 ? arguments[0] : string.Empty;
        string? rootDirectory = null;
        string? source = null;
        string? name = null;
        string? origin = null;
        string? backupId = null;
        for (var index = 1; index < arguments.Count; index++)
        {
            if (index + 1 >= arguments.Count)
            {
                throw new ArgumentException($"Missing value for option: {arguments[index]}");
            }

            var value = arguments[++index];
            switch (arguments[index - 1])
            {
                case "--root":
                    rootDirectory = value;
                    break;
                case "--source":
                    source = value;
                    break;
                case "--name":
                    name = value;
                    break;
                case "--origin":
                    origin = value;
                    break;
                case "--backup-id":
                    backupId = value;
                    break;
                default:
                    throw new ArgumentException($"Unsupported argument: {arguments[index - 1]}");
            }
        }

        return new ElevatedArguments(command, rootDirectory, source, name, origin, backupId);
    }
}
