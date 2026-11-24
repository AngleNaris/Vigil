using System.Security.Principal;
using System.Text;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Windows.Execution;
using SystemAutorun.Windows.Scheduling;

// Reject before parsing roots, checking elevation, constructing native adapters or
// writing a profile log. The App uses the same support policy before runas.
if (!ElevatedCommandPolicy.IsSupported(args.FirstOrDefault())) { return ElevatedCommandPolicy.RejectedExitCode; }

try
{
    var arguments = ElevatedArguments.Parse(args);
    EnsureAdministrator();
    var paths = ProjectPaths.Resolve(arguments.RootDirectory);
    var scheduler = new BootTaskScheduler();
    var maintenanceScheduler = new MaintenanceTaskScheduler();
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
        case "install-maintenance":
        {
            var selfPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to determine elevated helper executable path.");
            var targetSid = arguments.CallerSid
                ?? WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("Unable to determine target user SID.");
            _ = maintenanceScheduler.Install(selfPath, paths.RootDirectory, targetSid);
            break;
        }
        case "uninstall-maintenance":
            _ = maintenanceScheduler.Uninstall();
            break;
        case "run-maintenance":
        {
            var targetSid = arguments.CallerSid
                ?? throw new ArgumentException("run-maintenance requires --sid.");
            var executor = new ProcessMaintenanceExecutor();
            return await executor.ExecuteElevatedRequestAsync(paths, targetSid);
        }
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

static async Task WriteFailureAsync(Exception exception)
{
    try
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vigil",
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
    string? CallerSid)
{
    internal static ElevatedArguments Parse(IReadOnlyList<string> arguments)
    {
        var command = arguments.Count > 0 ? arguments[0] : string.Empty;
        string? rootDirectory = null;
        string? callerSid = null;
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
                case "--sid":
                    callerSid = value;
                    break;
                default:
                    throw new ArgumentException($"Unsupported argument: {arguments[index - 1]}");
            }
        }

        return new ElevatedArguments(command, rootDirectory, callerSid);
    }
}
