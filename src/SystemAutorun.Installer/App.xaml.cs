using System.Diagnostics;
using System.IO;
using System.Windows;

namespace SystemAutorun.Installer;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var currentExe = InstallerEngine.ResolveCurrentExecutablePath() ?? string.Empty;
        var hasPayload = PayloadTrailer.TryReadTrailer(currentExe, out _, out _);
        var options = InstallerOptions.Parse(e.Args, hasPayload);

        if (!string.IsNullOrWhiteSpace(options.ScreenshotDirectory))
        {
            await RenderScreenshotsAsync(options.ScreenshotDirectory);
            Shutdown(0);
            return;
        }

        // Interactive uninstall launched directly from the installation directory must re-launch
        // from %TEMP% so Windows unlocks Vigil-Uninstall.exe and the whole folder can be removed.
        if (options.Uninstall
            && !options.Silent
            && string.IsNullOrWhiteSpace(options.UninstallFromDirectory)
            && InstallerEngine.IsRunningInsideInstallDir(currentExe, out var installDir))
        {
            if (RelaunchFromTemp(currentExe, installDir, e.Args))
            {
                Shutdown(0);
                return;
            }
        }

        if (options.Silent)
        {
            var code = await RunSilentAsync(options, currentExe);
            Shutdown(code);
            return;
        }

        InstallerTheme.Apply(Resources);
        var window = new InstallerWindow(options, currentExe);
        MainWindow = window;
        window.Show();
    }

    private static bool RelaunchFromTemp(string currentExe, string installDir, string[] originalArgs)
    {
        try
        {
            var tempExe = Path.Combine(Path.GetTempPath(), $"Vigil-Uninstall-{Guid.NewGuid():N}.exe");
            File.Copy(currentExe, tempExe, overwrite: true);

            var forwarded = new List<string> { "--uninstall", "--uninstall-from", installDir };
            foreach (var arg in originalArgs)
            {
                if (!string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(arg, "/uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    forwarded.Add(arg);
                }
            }

            var psi = new ProcessStartInfo(tempExe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetTempPath(),
            };
            foreach (var item in forwarded)
            {
                psi.ArgumentList.Add(item);
            }
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> RunSilentAsync(InstallerOptions options, string currentExe)
    {
        try
        {
            if (options.Uninstall)
            {
                await InstallerEngine.UninstallAsync(options);
            }
            else
            {
                await InstallerEngine.InstallAsync(options, currentExe);
            }

            ScheduleTempHelperCleanupIfNeeded(options, currentExe);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    internal static void ScheduleTempHelperCleanupIfNeeded(InstallerOptions options, string currentExe)
    {
        if (string.IsNullOrWhiteSpace(options.UninstallFromDirectory)
            || string.IsNullOrWhiteSpace(currentExe)
            || !currentExe.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & del /f /q \"{currentExe}\" > nul 2>&1")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch
        {
        }
    }

    private async Task RenderScreenshotsAsync(string outDir)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Directory.CreateDirectory(outDir);
        foreach (var light in new[] { false, true })
        {
            var tag = light ? "light" : "dark";
            InstallerTheme.Apply(Resources, light);

            var installWin = new InstallerWindow(new InstallerOptions { Uninstall = false }, string.Empty)
            {
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            installWin.Show();
            await installWin.RenderForScreenshotAsync(Path.Combine(outDir, $"install-{tag}.png"));
            installWin.SetProgressPreviewState();
            await installWin.RenderForScreenshotAsync(Path.Combine(outDir, $"progress-{tag}.png"));
            installWin.SetCompletedPreviewState();
            await installWin.RenderForScreenshotAsync(Path.Combine(outDir, $"install-done-{tag}.png"));
            installWin.Close();

            var uninstallWin = new InstallerWindow(new InstallerOptions { Uninstall = true }, string.Empty)
            {
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            uninstallWin.Show();
            await uninstallWin.RenderForScreenshotAsync(Path.Combine(outDir, $"uninstall-{tag}.png"));
            uninstallWin.Close();
        }
    }
}
