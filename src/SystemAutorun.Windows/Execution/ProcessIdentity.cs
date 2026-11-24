using System.Diagnostics;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Native;

namespace SystemAutorun.Windows.Execution;

public sealed record ProcessPresence(IReadOnlyList<int> ProcessIds, bool IdentityUnavailable);

public sealed class ProcessIdentity
{
    public IReadOnlyList<int> FindMatchingProcessIds(AutorunTask task)
    {
        var (presence, unverifiedMatches) = ReadPresenceInternal(task);
        if (presence.ProcessIds.Count > 0)
        {
            return presence.ProcessIds;
        }

        // For resource monitoring only: if a matching process name exists but OpenProcess
        // failed (e.g. elevated/SYSTEM process while Agent runs unelevated), still allow
        // NtQuerySystemInformation-based metric collection without weakening health checks.
        return unverifiedMatches.Count > 0 ? unverifiedMatches : presence.ProcessIds;
    }

    public ProcessPresence ReadPresence(AutorunTask task) => ReadPresenceInternal(task).Presence;

    private (ProcessPresence Presence, IReadOnlyList<int> UnverifiedMatches) ReadPresenceInternal(AutorunTask task)
    {
        var pattern = !string.IsNullOrWhiteSpace(task.Health.Pattern)
            ? task.Health.Pattern
            : !string.IsNullOrWhiteSpace(task.Executable)
                ? Path.GetFileName(task.Executable.Trim())
                : null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return (new([], IdentityUnavailable: true), []);
        }

        var processName = NormalizeProcessName(pattern);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return (new([], IdentityUnavailable: true), []);
        }

        string? expectedPath = null;
        if (!string.IsNullOrWhiteSpace(task.Executable))
        {
            try
            {
                expectedPath = ExecutableResolver.Resolve(task.Executable, task.WorkingDirectory);
            }
            catch (FileNotFoundException)
            {
                expectedPath = Path.IsPathRooted(task.Executable)
                    ? Path.GetFullPath(task.Executable)
                    : null;
            }
        }

        if (!string.IsNullOrWhiteSpace(task.Executable) && expectedPath is null)
        { return (new([], IdentityUnavailable: true), []); }
        var matches = new List<int>();
        var unverifiedMatches = new List<int>();
        var unavailable = false;
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (expectedPath is null)
                {
                    matches.Add(process.Id);
                    continue;
                }

                var actualPath = NativeMethods.TryGetProcessImagePath(process.Id);
                if (actualPath is null)
                {
                    unavailable = true;
                    unverifiedMatches.Add(process.Id);
                }
                else if (IsExpectedImage(actualPath, expectedPath))
                {
                    matches.Add(process.Id);
                }
            }
        }

        return (new(matches, unavailable), unverifiedMatches);
    }

    internal static string NormalizeProcessName(string pattern)
    {
        var fileName = Path.GetFileName(pattern.Trim());
        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^4];
        }
        return fileName;
    }

    internal static bool IsExpectedImage(string actualPath, string expectedPath)
    {
        var actual = Path.GetFullPath(actualPath).TrimEnd(Path.DirectorySeparatorChar);
        var expected = Path.GetFullPath(expectedPath).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var installDirectory = Path.GetDirectoryName(expected);
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return false;
        }

        var prefix = installDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return actual.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
