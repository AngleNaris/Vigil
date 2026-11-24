using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SystemAutorun.Windows.Scheduling;

public sealed record MaintenanceTaskStatus(
    bool Installed,
    bool MatchesRoot,
    string DisplayName,
    string? Executable,
    string? Arguments,
    string? Error = null);

public sealed class MaintenanceTaskScheduler
{
    public const string FolderPath = @"\Vigil";
    public const string TaskName = "Maintenance";
    public const string DisplayName = @"\Vigil\Maintenance";

    private const int TaskCreateOrUpdate = 6;
    private const int TaskActionExecute = 0;
    private const int TaskLogonServiceAccount = 5;
    private const int TaskRunLevelHighest = 1;
    private const int TaskInstancesIgnoreNew = 2;

    public MaintenanceTaskStatus GetStatus(string? expectedRootDirectory = null)
    {
        try
        {
            dynamic service = Connect();
            dynamic? task = TryGetTask(service);
            if (task is null)
            {
                return new MaintenanceTaskStatus(false, false, DisplayName, null, null);
            }

            string? executable = null;
            string? arguments = null;
            try
            {
                dynamic action = task.Definition.Actions.Item(1);
                executable = action.Path as string;
                arguments = action.Arguments as string;
            }
            catch (Exception exception) when (IsMissing(exception) || exception is COMException)
            {
            }

            var matchesRoot = true;
            if (!string.IsNullOrWhiteSpace(expectedRootDirectory))
            {
                var normalized = Path.GetFullPath(expectedRootDirectory);
                matchesRoot = !string.IsNullOrWhiteSpace(arguments)
                    && arguments.Contains(BuildRootFlag(normalized), StringComparison.OrdinalIgnoreCase);
            }

            return new MaintenanceTaskStatus(true, matchesRoot, DisplayName, executable, arguments);
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return new MaintenanceTaskStatus(false, false, DisplayName, null, null, exception.Message);
        }
    }

    public MaintenanceTaskStatus Install(string elevatedExecutablePath, string rootDirectory, string callerSid)
    {
        elevatedExecutablePath = ValidateElevatedExecutable(elevatedExecutablePath);
        rootDirectory = ValidateRoot(rootDirectory);
        callerSid = ValidateSid(callerSid);
        var arguments = BuildArguments(rootDirectory, callerSid);

        dynamic service = Connect();
        dynamic root = service.GetFolder(@"\");
        dynamic folder = GetOrCreateFolder(root, service);
        dynamic definition = service.NewTask(0);
        definition.RegistrationInfo.Description =
            "Runs Vigil allowlisted system-process maintenance (TerminateOnly) on demand.";
        definition.Settings.Enabled = true;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = "PT1M";
        definition.Settings.MultipleInstances = TaskInstancesIgnoreNew;

        definition.Principal.UserId = "SYSTEM";
        definition.Principal.LogonType = TaskLogonServiceAccount;
        definition.Principal.RunLevel = TaskRunLevelHighest;

        dynamic action = definition.Actions.Create(TaskActionExecute);
        action.Path = elevatedExecutablePath;
        action.Arguments = arguments;
        action.WorkingDirectory = rootDirectory;

        // Grant SYSTEM and Administrators full access, and grant ONLY Read + Execute to the installing user SID
        // so the standard-user Vigil.Agent can trigger this allowlisted task without being able to modify it.
        var sddl = $"D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GXGR;;;{callerSid})";
        _ = folder.RegisterTaskDefinition(
            TaskName,
            definition,
            TaskCreateOrUpdate,
            "SYSTEM",
            null,
            TaskLogonServiceAccount,
            sddl);

        Thread.Sleep(250);
        var status = GetStatus(rootDirectory);
        if (!status.Installed || !status.MatchesRoot)
        {
            throw new InvalidOperationException("The maintenance task was registered but verification failed.");
        }

        return status;
    }

    public bool Uninstall()
    {
        dynamic service = Connect();
        dynamic? folder;
        try
        {
            folder = service.GetFolder(FolderPath);
        }
        catch (Exception exception) when (IsMissing(exception))
        {
            return false;
        }

        try
        {
            folder.DeleteTask(TaskName, 0);
        }
        catch (Exception exception) when (IsMissing(exception))
        {
            return false;
        }

        try
        {
            dynamic tasks = folder.GetTasks(1);
            if ((int)tasks.Count == 0)
            {
                dynamic root = service.GetFolder(@"\");
                root.DeleteFolder(FolderPath, 0);
            }
        }
        catch (Exception exception) when (IsMissing(exception) || exception is COMException)
        {
        }

        return true;
    }

    public void Trigger()
    {
        dynamic service = Connect();
        dynamic? task = TryGetTask(service);
        if (task is null)
        {
            throw new InvalidOperationException($"Maintenance task '{DisplayName}' is not installed.");
        }

        _ = task.Run(null);
    }

    internal static string BuildArguments(string rootDirectory, string callerSid) =>
        $"run-maintenance {BuildRootFlag(rootDirectory)} --sid {Quote(callerSid)}";

    private static string BuildRootFlag(string rootDirectory) =>
        $"--root {Quote(rootDirectory)}";

    private static string ValidateElevatedExecutable(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(fullPath)
            || !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath))
        {
            throw new UnauthorizedAccessException("The maintenance helper executable path is invalid.");
        }

        var attrs = File.GetAttributes(fullPath);
        if ((attrs & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("The maintenance helper executable must not be a reparse point.");
        }

        var fileName = Path.GetFileName(fullPath);
        if (!fileName.Equals("Vigil.Elevated.exe", StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("SystemAutorun.Elevated.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The maintenance helper executable must be Vigil.Elevated.exe.");
        }

        return fullPath;
    }

    private static string ValidateRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Configuration root does not exist: {fullPath}");
        }

        return fullPath;
    }

    private static string ValidateSid(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            throw new ArgumentException("Caller SID is required.", nameof(sid));
        }

        var parsed = new SecurityIdentifier(sid.Trim());
        return parsed.Value;
    }

    private static dynamic Connect()
    {
        var schedulerType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)
            ?? throw new PlatformNotSupportedException("Windows Task Scheduler COM is unavailable.");
        dynamic service = Activator.CreateInstance(schedulerType)
            ?? throw new InvalidOperationException("Failed to create the Task Scheduler COM service.");
        service.Connect();
        return service;
    }

    private static dynamic GetOrCreateFolder(dynamic root, dynamic service)
    {
        try
        {
            return service.GetFolder(FolderPath);
        }
        catch (Exception exception) when (IsMissing(exception) || exception is COMException)
        {
            return root.CreateFolder("Vigil", Type.Missing);
        }
    }

    private static dynamic? TryGetTask(dynamic service)
    {
        try
        {
            dynamic folder = service.GetFolder(FolderPath);
            return folder.GetTask(TaskName);
        }
        catch (Exception exception) when (IsMissing(exception) || exception is COMException)
        {
            return null;
        }
    }

    private static bool IsMissing(Exception exception) =>
        exception is COMException or FileNotFoundException or DirectoryNotFoundException
        && (uint)exception.HResult is 0x80070002 or 0x80070003;

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
