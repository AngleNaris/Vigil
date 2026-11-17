using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Monitoring;

public static class ResourceProcessIdentity
{
    public static string? For(ManagedProcessSnapshot? process, DateTimeOffset at, int configurationGeneration = 0)
    {
        if (process is null || process.Type is not ("windows" or "windows_service") || process.ProcessCount == 0
            || process.Processes.Any(item => item.ProcessId <= 0 || item.StartedAt is null || item.StartedAt > at)
            || process.Processes.Select(item => item.ProcessId).Distinct().Count() != process.ProcessCount) { return null; }
        var value = string.Join(";", process.Processes.OrderBy(item => item.ProcessId)
            .Select(item => string.Create(CultureInfo.InvariantCulture, $"{item.ProcessId}:{item.StartedAt!.Value.UtcTicks}")));
        // A configuration boundary may contain an unobserved disable/re-enable; retain the
        // samples, but do not assert continuity through that boundary for any task.
        value = configurationGeneration.ToString(CultureInfo.InvariantCulture) + "|" + value;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
