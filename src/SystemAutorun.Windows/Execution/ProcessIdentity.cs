using System.Diagnostics;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Native;

namespace SystemAutorun.Windows.Execution;

public sealed class ProcessIdentity
{
    public IReadOnlyList<int> FindMatchingProcessIds(AutorunTask task)
    {
        var pattern = task.Health.Pattern;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Array.Empty<int>();
        }

        var processName = Path.GetFileNameWithoutExtension(pattern.Trim());
        if (string.IsNullOrWhiteSpace(processName))
        {
            return Array.Empty<int>();
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

        var matches = new List<int>();
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
                if (actualPath is not null && IsExpectedImage(actualPath, expectedPath))
                {
                    matches.Add(process.Id);
                }
            }
        }

        return matches;
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
