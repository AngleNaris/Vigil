namespace SystemAutorun.Core.Infrastructure;

public sealed record ProjectPaths(string RootDirectory)
{
    public string ConfigPath => Path.Combine(RootDirectory, "autorun.yaml");

    public string OverridesPath => Path.Combine(RootDirectory, "autorun.overrides.json");

    public string GuiCachePath => Path.Combine(RootDirectory, "autorun.gui.cache.json");

    public string GuiSettingsPath => Path.Combine(RootDirectory, "autorun.gui.settings.json");

    public string StartupCachePath => Path.Combine(RootDirectory, "autorun.startup.native.cache.json");

    public string StartupBackupPath => Path.Combine(RootDirectory, "autorun.startup.backup.json");

    public static ProjectPaths Resolve(string? explicitRoot = null)
    {
        var candidates = new[]
        {
            explicitRoot,
            Environment.GetEnvironmentVariable("SYSTEMAUTORUN_ROOT"),
            Environment.CurrentDirectory,
            AppContext.BaseDirectory,
        };

        foreach (var candidate in candidates)
        {
            var found = FindRoot(candidate);
            if (found is not null)
            {
                return new ProjectPaths(found);
            }
        }

        throw new DirectoryNotFoundException(
            "Unable to locate autorun.yaml. Pass --root or set SYSTEMAUTORUN_ROOT.");
    }

    private static string? FindRoot(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var directory = new DirectoryInfo(Path.GetFullPath(candidate));
        if (directory.Exists && File.Exists(Path.Combine(directory.FullName, "autorun.yaml")))
        {
            return directory.FullName;
        }

        for (var depth = 0; directory is not null && depth < 10; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "autorun.yaml")))
            {
                return directory.FullName;
            }
        }

        return null;
    }
}
