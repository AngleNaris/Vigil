namespace SystemAutorun.Core.Models;

public sealed record HealthResult(bool Healthy, string Reason, string Detail, bool Observed = true)
{
    public static HealthResult Ok(string reason, string detail) => new(true, reason, detail);

    public static HealthResult Fail(string reason, string detail) => new(false, reason, detail);

    public static HealthResult Unknown(string reason, string detail) => new(false, reason, detail, Observed: false);
}

public sealed record TaskSnapshot(
    string Status,
    bool Healthy,
    bool Enabled,
    string Name,
    string Type,
    string Description,
    string Reason,
    string Detail,
    int StartupDelay,
    string? Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    string? Distro,
    string? Service,
    IReadOnlyList<string> DependsOn,
    LaunchOptions Launch,
    string? Command = null,
    HealthCheckDefinition? Health = null,
    bool ExpectedRunning = false,
    bool HealthObserved = true,
    bool? StartOnAppLaunch = null,
    bool? AutoRecover = null)
{
    public static TaskSnapshot From(AutorunTask task, HealthResult health) => new(
        !task.Enabled ? "OFF" : !health.Observed ? "UNKNOWN" : health.Healthy ? "OK" : "FAIL",
        health.Healthy,
        task.Enabled,
        task.Name,
        task.Type.ToKey(),
        task.Description,
        health.Reason,
        health.Detail,
        task.StartupDelay,
        task.Executable,
        task.Arguments,
        task.WorkingDirectory,
        task.Distro,
        task.Service,
        task.DependsOn,
        task.Launch,
        task.Command,
        task.Health,
        task.ExpectedRunning,
        health.Observed,
        task.StartOnAppLaunch,
        task.AutoRecover);
}

public sealed record TaskStatusDetail(
    AutorunTask Task,
    HealthResult Health,
    IReadOnlyList<int> Processes,
    IReadOnlyList<string> RecentLogs);

public sealed record CommandResult(bool Succeeded, string Message, int? ProcessId = null);

public sealed record DoctorCheck(string Check, bool Ok, string Detail);

public sealed record DoctorReport(IReadOnlyList<DoctorCheck> Checks)
{
    public int Failed => Checks.Count(check => !check.Ok);

    public bool AllOk => Failed == 0;
}

public sealed record DriveStatusSnapshot(
    string Name,
    string Label,
    long TotalBytes,
    long FreeBytes)
{
    public double UsedPercent => TotalBytes <= 0
        ? 0
        : Math.Clamp((TotalBytes - FreeBytes) * 100d / TotalBytes, 0, 100);
}

public sealed record NetworkStatusSnapshot(
    string Name,
    string Address,
    long BytesReceived,
    long BytesSent,
    string? InterfaceId = null);

public sealed record HostStatusSnapshot(
    string MachineName,
    string OsDescription,
    DateTimeOffset CapturedAt,
    TimeSpan Uptime,
    double CpuUsagePercent,
    long TotalMemoryBytes,
    long AvailableMemoryBytes,
    IReadOnlyList<DriveStatusSnapshot> Drives,
    IReadOnlyList<NetworkStatusSnapshot> Networks,
    int LogicalProcessorCount = 0,
    int ProcessCount = 0,
    int ThreadCount = 0,
    double? DiskReadBytesPerSecond = null,
    double? DiskWriteBytesPerSecond = null,
    double? NetworkReceiveBytesPerSecond = null,
    double? NetworkSendBytesPerSecond = null,
    string? DiskIoError = null,
    string? NetworkRateError = null)
{
    public double MemoryUsedPercent => TotalMemoryBytes <= 0
        ? 0
        : Math.Clamp((TotalMemoryBytes - AvailableMemoryBytes) * 100d / TotalMemoryBytes, 0, 100);
}
