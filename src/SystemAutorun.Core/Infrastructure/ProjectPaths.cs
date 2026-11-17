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
        if (explicitRoot is not null)
        {
            return ResolveExactRoot(explicitRoot);
        }

        var environmentRoot = Environment.GetEnvironmentVariable("VIGIL_ROOT");
        if (!string.IsNullOrWhiteSpace(environmentRoot))
        {
            return ResolveExactRoot(environmentRoot);
        }

        var candidates = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory };

        foreach (var candidate in candidates)
        {
            var found = FindRoot(candidate);
            if (found is not null)
            {
                return new ProjectPaths(PhysicalDirectory.Resolve(found));
            }
        }

        var userEnvironmentRoot = Environment.GetEnvironmentVariable(
            "VIGIL_ROOT",
            EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(userEnvironmentRoot)
            && File.Exists(Path.Combine(userEnvironmentRoot, "autorun.yaml")))
        {
            return ResolveExactRoot(userEnvironmentRoot);
        }

        var defaultWorkspace = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vigil",
            "workspace");
        if (File.Exists(Path.Combine(defaultWorkspace, "autorun.yaml")))
        {
            return ResolveExactRoot(defaultWorkspace);
        }

        throw new DirectoryNotFoundException(
            "Unable to locate autorun.yaml. Pass --root or set VIGIL_ROOT.");
    }

    private static ProjectPaths ResolveExactRoot(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "autorun.yaml")))
        {
            throw new DirectoryNotFoundException($"Configuration root has no autorun.yaml: {root}");
        }

        return new ProjectPaths(PhysicalDirectory.Resolve(root));
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
