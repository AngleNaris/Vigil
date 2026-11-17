using System.Security.Cryptography;
using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Monitoring;

public static class MonitoringTaskIdentity
{
    public static string For(AutorunTask task) => For(TaskSnapshot.From(task, HealthResult.Unknown("identity", "identity")));

    public static string For(TaskSnapshot task)
    {
        // Scheduling/display policy changes do not turn the target into another workload.
        // A replacement target or health definition under the same name must not inherit its history.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Name = task.Name.ToUpperInvariant(), task.Type, task.Executable, task.Arguments,
            task.WorkingDirectory, task.Distro, task.Service, task.Command, task.Health, task.Launch.RunAs,
        }, AgentProtocol.JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
