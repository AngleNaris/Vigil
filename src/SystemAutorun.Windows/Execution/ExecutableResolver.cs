namespace SystemAutorun.Windows.Execution;

public static class ExecutableResolver
{
    public static string Resolve(string executable, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var expanded = Environment.ExpandEnvironmentVariables(executable.Trim().Trim('"'));

        if (Path.IsPathRooted(expanded))
        {
            return File.Exists(expanded)
                ? Path.GetFullPath(expanded)
                : throw new FileNotFoundException($"Executable not found: {expanded}", expanded);
        }

        if (expanded.Contains(Path.DirectorySeparatorChar) || expanded.Contains(Path.AltDirectorySeparatorChar))
        {
            var baseDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory;
            var candidate = Path.GetFullPath(Path.Combine(baseDirectory, expanded));
            return File.Exists(candidate)
                ? candidate
                : throw new FileNotFoundException($"Executable not found: {candidate}", candidate);
        }

        var extensions = Path.HasExtension(expanded)
            ? new[] { string.Empty }
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.COM;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Prepend(string.Empty)
                .ToArray();

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), expanded + extension);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        throw new FileNotFoundException($"Executable not found on PATH: {expanded}", expanded);
    }
}
