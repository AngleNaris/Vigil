namespace SystemAutorun.Core.Models;

public sealed record HealthResult(bool Healthy, string Reason, string Detail)
{
    public static HealthResult Ok(string reason, string detail) => new(true, reason, detail);

    public static HealthResult Fail(string reason, string detail) => new(false, reason, detail);
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
    LaunchOptions Launch)
{
    public static TaskSnapshot From(AutorunTask task, HealthResult health) => new(
        task.Enabled ? (health.Healthy ? "OK" : "FAIL") : "OFF",
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
        task.Launch);
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
