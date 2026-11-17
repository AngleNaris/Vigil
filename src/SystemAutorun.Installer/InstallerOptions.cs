using System.IO;

namespace SystemAutorun.Installer;

internal sealed class InstallerOptions
{
    internal static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Vigil");

    internal static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vigil", "workspace");

    public bool Uninstall { get; set; }
    public string? UninstallFromDirectory { get; set; }
    public bool Silent { get; set; }
    public bool SandboxRegistry { get; set; }
    public string InstallDir { get; set; } = DefaultInstallDir;
    public string DataDir { get; set; } = DefaultDataDir;
    public bool CreateDesktopShortcut { get; set; } = true;
    public bool CreateStartMenuShortcut { get; set; } = true;
    public bool AddCliToPath { get; set; } = true;
    public bool InitializeEnvRoot { get; set; } = true;
    public bool EnableLogonStartup { get; set; } = true;
    public bool PurgeData { get; set; }
    public string? ScreenshotDirectory { get; set; }

    internal static InstallerOptions Parse(string[] args, bool hasPayload)
    {
        var options = new InstallerOptions
        {
            Uninstall = !hasPayload,
        };

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--uninstall":
                case "/uninstall":
                    options.Uninstall = true;
                    break;
                case "--uninstall-from":
                    if (i + 1 < args.Length)
                    {
                        options.Uninstall = true;
                        options.UninstallFromDirectory = args[++i];
                        options.InstallDir = options.UninstallFromDirectory;
                    }
                    break;
                case "--silent":
                case "/s":
                    options.Silent = true;
                    break;
                case "--sandbox-registry":
                    options.SandboxRegistry = true;
                    break;
                case "--install-dir":
                    if (i + 1 < args.Length)
                    {
                        options.InstallDir = args[++i];
                    }
                    break;
                case "--data-dir":
                    if (i + 1 < args.Length)
                    {
                        options.DataDir = args[++i];
                    }
                    break;
                case "--no-shortcuts":
                    options.CreateDesktopShortcut = false;
                    options.CreateStartMenuShortcut = false;
                    break;
                case "--no-desktop-shortcut":
                    options.CreateDesktopShortcut = false;
                    break;
                case "--no-start-menu":
                    options.CreateStartMenuShortcut = false;
                    break;
                case "--no-path":
                    options.AddCliToPath = false;
                    break;
                case "--no-env":
                    options.InitializeEnvRoot = false;
                    break;
                case "--enable-startup":
                    options.EnableLogonStartup = true;
                    break;
                case "--no-startup":
                    options.EnableLogonStartup = false;
                    break;
                case "--purge-data":
                case "--remove-data":
                    options.PurgeData = true;
                    break;
                case "--screenshot-dir":
                    if (i + 1 < args.Length)
                    {
                        options.ScreenshotDirectory = args[++i];
                    }
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.UninstallFromDirectory))
        {
            options.InstallDir = Path.GetFullPath(options.UninstallFromDirectory);
        }

        return options;
    }
}
