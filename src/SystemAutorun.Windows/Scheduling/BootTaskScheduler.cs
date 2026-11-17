using System.Runtime.InteropServices;

namespace SystemAutorun.Windows.Scheduling;

public enum BootTaskState
{
    Unknown = 0,
    Disabled = 1,
    Queued = 2,
    Ready = 3,
    Running = 4,
}

public sealed record BootTaskStatus(
    bool Installed,
    string DisplayName,
    BootTaskState State,
    DateTime? LastRunTime,
    int? LastTaskResult,
    DateTime? NextRunTime,
    int? MissedRuns,
    string? Executable,
    string? Arguments);

public sealed record BootTaskInstallResult(
    bool Installed,
    bool Verified,
    string DisplayName,
    string Executable,
    string Arguments,
    BootTaskState State);

public sealed class BootTaskScheduler
{
    public const string FolderPath = @"\Vigil";
    public const string TaskName = "Boot";
    public const string DisplayName = @"\Vigil\Boot";

    private const int TaskCreateOrUpdate = 6;
    private const int TaskActionExecute = 0;
    private const int TaskTriggerBoot = 8;
    private const int TaskLogonServiceAccount = 5;
    private const int TaskRunLevelHighest = 1;
    private const int TaskInstancesIgnoreNew = 2;

    public BootTaskStatus GetStatus()
    {
        dynamic service = Connect();
        dynamic? task = TryGetTask(service);
        return task is null
            ? new BootTaskStatus(false, DisplayName, BootTaskState.Unknown, null, null, null, null, null, null)
            : ReadStatus(task);
    }

    public BootTaskInstallResult Install(string agentPath, string rootDirectory)
    {
        agentPath = ValidateExecutable(agentPath);
        rootDirectory = ValidateRoot(rootDirectory);
        var arguments = BuildAgentArguments(rootDirectory);

        dynamic service = Connect();
        dynamic root = service.GetFolder(@"\");
        dynamic folder = GetOrCreateFolder(root, service);
        dynamic definition = service.NewTask(0);
        definition.RegistrationInfo.Description =
            "Starts Vigil system-scoped tasks at Windows boot.";
        definition.Settings.Enabled = true;
        definition.Settings.StartWhenAvailable = true;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.RestartCount = 3;
        definition.Settings.RestartInterval = "PT1M";
        definition.Settings.ExecutionTimeLimit = "PT0S";
        definition.Settings.MultipleInstances = TaskInstancesIgnoreNew;

        definition.Principal.UserId = "SYSTEM";
        definition.Principal.LogonType = TaskLogonServiceAccount;
        definition.Principal.RunLevel = TaskRunLevelHighest;

        dynamic trigger = definition.Triggers.Create(TaskTriggerBoot);
        trigger.Enabled = true;

        dynamic action = definition.Actions.Create(TaskActionExecute);
        action.Path = agentPath;
        action.Arguments = arguments;
        action.WorkingDirectory = rootDirectory;

        _ = folder.RegisterTaskDefinition(
            TaskName,
            definition,
            TaskCreateOrUpdate,
            "SYSTEM",
            null,
            TaskLogonServiceAccount,
            null);

        Thread.Sleep(300);
        var status = GetStatus();
        if (!status.Installed
            || !string.Equals(status.Executable, agentPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(status.Arguments, arguments, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The boot task was registered but post-install verification failed.");
        }

        return new BootTaskInstallResult(
            true,
            true,
            DisplayName,
            agentPath,
            arguments,
            status.State);
    }

    public bool Uninstall() => Uninstall(Connect);

    internal static bool Uninstall(Func<object> connect)
    {
        dynamic service = connect();
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

        var absent = false;
        try { _ = folder.GetTask(TaskName); }
        catch (Exception exception) when (IsMissing(exception)) { absent = true; }
        if (!absent)
        {
            throw new InvalidOperationException("The boot task was not confirmed absent after deletion.");
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
            // The task itself is already removed; an empty folder is harmless.
        }

        return true;
    }

    private static bool IsMissing(Exception exception) =>
        exception is COMException or FileNotFoundException or DirectoryNotFoundException
        && (uint)exception.HResult is 0x80070002 or 0x80070003;

    public void Run()
    {
        dynamic service = Connect();
        dynamic? task = TryGetTask(service);
        if (task is null)
        {
            throw new InvalidOperationException($"Boot task '{DisplayName}' is not installed.");
        }

        _ = task.Run(null);
    }

    internal static string BuildAgentArguments(string rootDirectory) =>
        $"--boot --root {Quote(rootDirectory)}";

    internal static BootTaskState MapState(int value) => value switch
    {
        1 => BootTaskState.Disabled,
        2 => BootTaskState.Queued,
        3 => BootTaskState.Ready,
        4 => BootTaskState.Running,
        _ => BootTaskState.Unknown,
    };

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

    private static BootTaskStatus ReadStatus(dynamic task)
    {
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

        return new BootTaskStatus(
            true,
            DisplayName,
            MapState((int)task.State),
            NormalizeDate((DateTime)task.LastRunTime),
            (int)task.LastTaskResult,
            NormalizeDate((DateTime)task.NextRunTime),
            (int)task.NumberOfMissedRuns,
            executable,
            arguments);
    }

    private static DateTime? NormalizeDate(DateTime value) =>
        value.Year <= 1900 ? null : value;

    private static string ValidateExecutable(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(fullPath)
            || !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath)
            || !string.Equals(
                fullPath,
                BootConfigurationStore.ResolveTrustedAgentPath(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "The boot task executable must be the protected Vigil Agent installation.");
        }

        return fullPath;
    }

    private static string ValidateRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(fullPath, BootConfigurationStore.RootDirectory, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(fullPath)
            || !File.Exists(Path.Combine(fullPath, "autorun.yaml")))
        {
            throw new UnauthorizedAccessException(
                "The boot task root must be the protected Vigil ProgramData directory.");
        }

        return fullPath;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
