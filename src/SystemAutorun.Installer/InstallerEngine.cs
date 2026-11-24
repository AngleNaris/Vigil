using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SystemAutorun.Installer;

internal sealed record InstallManifest(
    string InstallDir,
    string DataDir,
    string Version,
    bool DesktopShortcutCreated,
    bool StartMenuShortcutCreated,
    bool AddedCliPath,
    bool SetEnvRoot,
    bool EnabledStartup,
    DateTimeOffset InstalledAtUtc);

internal sealed record ProgressInfo(double Percent, string Message);

internal static class InstallerEngine
{
    internal const string AppVersion = "1.1.0";
    internal const string AppDisplayName = "Vigil";
    internal const string PublisherName = "Casidia";
    private const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Vigil";
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunRegistryValueName = "Vigil";
    private const string LegacyRunRegistryValueName = "SystemAutorun";
    private const string EnvSubKey = @"Environment";
    private const string RootEnvName = "VIGIL_ROOT";
    private const string LegacyRootEnvName = "SYSTEMAUTORUN_ROOT";
    private const string SandboxRootSubKey = @"Software\VigilInstallerTest";
    private const string ManifestFileName = "install-manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static string? ResolveCurrentExecutablePath() =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;

    internal static InstallManifest? TryReadManifest(string installDir)
    {
        try
        {
            var path = Path.Combine(installDir, ManifestFileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path, Encoding.UTF8));
        }
        catch
        {
            return null;
        }
    }

    internal static async Task InstallAsync(
        InstallerOptions options,
        string installerExePath,
        IProgress<ProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!PayloadTrailer.TryReadTrailer(installerExePath, out var baseExeLength, out var payloadLength))
        {
            throw new InvalidDataException("缺少有效载荷 (VIGILPK1)");
        }

        var installDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.InstallDir));
        var dataDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.DataDir));

        progress?.Report(new(3, "停止运行中的进程"));
        if (!options.SandboxRegistry)
        {
            await Task.Run(() => StopProcessesInDirectory(installDir), cancellationToken);
        }

        progress?.Report(new(6, "解压程序文件"));
        Directory.CreateDirectory(installDir);

        var installedKilobytes = await Task.Run(() =>
        {
            using var fs = new FileStream(installerExePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(baseExeLength, SeekOrigin.Begin);
            using var bounded = new BoundedReadStream(fs, payloadLength);
            using var zip = new ZipArchive(bounded, ZipArchiveMode.Read, leaveOpen: true);

            var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToArray();
            var total = Math.Max(1, entries.Length);
            long totalBytes = 0;

            for (var i = 0; i < entries.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[i];
                var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var destPath = Path.GetFullPath(Path.Combine(installDir, relative));
                if (!destPath.StartsWith(installDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"非法路径: {entry.FullName}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                using (var srcStream = entry.Open())
                using (var dstStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    srcStream.CopyTo(dstStream);
                }

                totalBytes += entry.Length;
                if (i == entries.Length - 1 || i % 12 == 0)
                {
                    var pct = 8.0 + (i + 1) * 74.0 / total;
                    progress?.Report(new(pct, $"[{i + 1}/{total}] {entry.FullName}"));
                }
            }

            return (int)Math.Max(1, (totalBytes + baseExeLength) / 1024);
        }, cancellationToken);

        progress?.Report(new(85, "生成卸载程序"));
        var uninstallExePath = Path.Combine(installDir, "Vigil-Uninstall.exe");
        await Task.Run(() => PayloadTrailer.CopyBaseExecutable(installerExePath, uninstallExePath, baseExeLength), cancellationToken);

        progress?.Report(new(89, "初始化工作区"));
        EnsureWorkspaceInitialized(dataDir);

        progress?.Report(new(92, "写入环境变量与快捷方式"));
        if (options.InitializeEnvRoot)
        {
            SetEnvironmentValue(options.SandboxRegistry, RootEnvName, dataDir, RegistryValueKind.String);
            DeleteEnvironmentValueIfExists(options.SandboxRegistry, LegacyRootEnvName);
        }

        var cliDir = Path.Combine(installDir, "cli");
        if (options.AddCliToPath)
        {
            AddDirectoryToUserPath(options.SandboxRegistry, cliDir);
        }

        var mainExePath = Path.Combine(installDir, "Vigil.exe");
        if (options.CreateDesktopShortcut || options.CreateStartMenuShortcut)
        {
            CreateShortcuts(
                options.SandboxRegistry,
                installDir,
                dataDir,
                mainExePath,
                uninstallExePath,
                options.CreateDesktopShortcut,
                options.CreateStartMenuShortcut);
        }

        if (options.EnableLogonStartup)
        {
            SetStartupCommand(options.SandboxRegistry, BuildRunCommand(mainExePath, dataDir));
        }

        var manifest = new InstallManifest(
            installDir,
            dataDir,
            AppVersion,
            options.CreateDesktopShortcut,
            options.CreateStartMenuShortcut,
            options.AddCliToPath,
            options.InitializeEnvRoot,
            options.EnableLogonStartup,
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(installDir, ManifestFileName),
            JsonSerializer.Serialize(manifest, JsonOptions),
            new UTF8Encoding(false));

        progress?.Report(new(96, "注册卸载项"));
        WriteUninstallRegistration(options.SandboxRegistry, installDir, mainExePath, uninstallExePath, installedKilobytes);

        if (!options.SandboxRegistry)
        {
            BroadcastEnvironmentChange();
        }

        progress?.Report(new(100, "安装完成"));
    }

    internal static async Task UninstallAsync(
        InstallerOptions options,
        IProgress<ProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.InstallDir));
        var manifest = TryReadManifest(installDir);
        var dataDir = manifest?.DataDir ?? Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.DataDir));

        progress?.Report(new(10, "停止运行中的进程"));
        if (!options.SandboxRegistry)
        {
            await Task.Run(() => StopProcessesInDirectory(installDir), cancellationToken);
        }

        progress?.Report(new(30, "移除快捷方式"));
        RemoveShortcuts(options.SandboxRegistry, installDir);

        progress?.Report(new(45, "清理环境变量与启动项"));
        RemoveDirectoryFromUserPath(options.SandboxRegistry, Path.Combine(installDir, "cli"));
        RemoveEnvRootIfMatching(options.SandboxRegistry, dataDir);
        RemoveStartupIfMatching(options.SandboxRegistry, installDir);

        progress?.Report(new(60, "移除卸载注册表"));
        DeleteUninstallRegistration(options.SandboxRegistry);

        if (options.PurgeData && Directory.Exists(dataDir))
        {
            progress?.Report(new(75, "删除数据目录"));
            await Task.Run(() => SafeDeleteDirectory(dataDir), cancellationToken);
        }

        progress?.Report(new(88, "删除程序文件"));
        var currentExe = ResolveCurrentExecutablePath();
        var runningInsideTarget = !string.IsNullOrEmpty(currentExe)
            && Path.GetFullPath(currentExe).StartsWith(installDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (Directory.Exists(installDir))
        {
            if (runningInsideTarget)
            {
                await Task.Run(() => DeleteDirectoryExceptFile(installDir, currentExe!), cancellationToken);
                ScheduleDelayedSelfDelete(currentExe!, installDir);
            }
            else
            {
                await Task.Run(() => SafeDeleteDirectory(installDir), cancellationToken);
            }
        }

        if (!options.SandboxRegistry)
        {
            BroadcastEnvironmentChange();
        }

        progress?.Report(new(100, "卸载完成"));
    }

    internal static bool IsRunningInsideInstallDir(string? currentExe, out string installDir)
    {
        installDir = string.Empty;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
        {
            return false;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(currentExe));
        if (string.IsNullOrEmpty(dir))
        {
            return false;
        }

        if (File.Exists(Path.Combine(dir, ManifestFileName))
            || string.Equals(Path.GetFileName(currentExe), "Vigil-Uninstall.exe", StringComparison.OrdinalIgnoreCase))
        {
            installDir = dir;
            return true;
        }

        return false;
    }

    private static void EnsureWorkspaceInitialized(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(Path.Combine(dataDir, "logs"));
        Directory.CreateDirectory(Path.Combine(dataDir, "backups"));
        var configPath = Path.Combine(dataDir, "autorun.yaml");
        if (!File.Exists(configPath))
        {
            var starter = "version: 1\nsettings:\n  log_dir: logs\n  backup_dir: backups\n  default_startup_delay: 5\ntasks: []\n";
            File.WriteAllText(configPath, starter, new UTF8Encoding(false));
        }
    }

    private static string BuildRunCommand(string exePath, string dataDir) =>
        $"\"{exePath}\" --background --root \"{dataDir}\"";

    private static void CreateShortcuts(
        bool sandbox,
        string installDir,
        string dataDir,
        string mainExe,
        string uninstallExe,
        bool desktop,
        bool startMenu)
    {
        if (sandbox)
        {
            var box = Path.Combine(installDir, ".shortcuts");
            if (desktop)
            {
                ShellLinkHelper.CreateShortcut(
                    Path.Combine(box, "Desktop-Vigil.lnk"),
                    mainExe,
                    $"--root \"{dataDir}\"",
                    installDir,
                    "Vigil",
                    mainExe);
            }
            if (startMenu)
            {
                ShellLinkHelper.CreateShortcut(
                    Path.Combine(box, "StartMenu-Vigil.lnk"),
                    mainExe,
                    $"--root \"{dataDir}\"",
                    installDir,
                    "Vigil",
                    mainExe);
                ShellLinkHelper.CreateShortcut(
                    Path.Combine(box, "StartMenu-Uninstall.lnk"),
                    uninstallExe,
                    "--uninstall",
                    installDir,
                    "卸载 Vigil",
                    uninstallExe);
            }
            return;
        }

        if (desktop)
        {
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktopDir))
            {
                desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }
            if (!string.IsNullOrWhiteSpace(desktopDir))
            {
                ShellLinkHelper.CreateShortcut(
                    Path.Combine(desktopDir, "Vigil.lnk"),
                    mainExe,
                    $"--root \"{dataDir}\"",
                    installDir,
                    "Vigil",
                    mainExe);
            }
        }

        if (startMenu)
        {
            var startPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            if (string.IsNullOrWhiteSpace(startPrograms))
            {
                startPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            }
            if (!string.IsNullOrWhiteSpace(startPrograms))
            {
                var folder = Path.Combine(startPrograms, "Vigil");
                ShellLinkHelper.CreateShortcut(
                    Path.Combine(folder, "Vigil.lnk"),
                    mainExe,
                    $"--root \"{dataDir}\"",
                    installDir,
                    "Vigil",
                    mainExe);
                ShellLinkHelper.CreateShortcut(
                    Path.Combine(folder, "卸载 Vigil.lnk"),
                    uninstallExe,
                    "--uninstall",
                    installDir,
                    "卸载 Vigil",
                    uninstallExe);
            }
        }
    }

    private static void RemoveShortcuts(bool sandbox, string installDir)
    {
        if (sandbox)
        {
            SafeDeleteDirectory(Path.Combine(installDir, ".shortcuts"));
            return;
        }

        foreach (var desktopDir in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        })
        {
            if (string.IsNullOrWhiteSpace(desktopDir)) continue;
            SafeDeleteFile(Path.Combine(desktopDir, "Vigil.lnk"));
        }

        foreach (var programsDir in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        })
        {
            if (string.IsNullOrWhiteSpace(programsDir)) continue;
            SafeDeleteDirectory(Path.Combine(programsDir, "Vigil"));
        }
    }

    private static RegistryKey OpenOrCreateEnvKey(bool sandbox, bool writable) =>
        sandbox
            ? (writable
                ? Registry.CurrentUser.CreateSubKey(SandboxRootSubKey + "\\" + EnvSubKey, writable: true)!
                : Registry.CurrentUser.OpenSubKey(SandboxRootSubKey + "\\" + EnvSubKey, writable: false)!)
            : (writable
                ? Registry.CurrentUser.CreateSubKey(EnvSubKey, writable: true)!
                : Registry.CurrentUser.OpenSubKey(EnvSubKey, writable: false)!);

    private static RegistryKey? TryOpenEnvKey(bool sandbox, bool writable) =>
        sandbox
            ? Registry.CurrentUser.OpenSubKey(SandboxRootSubKey + "\\" + EnvSubKey, writable)
            : Registry.CurrentUser.OpenSubKey(EnvSubKey, writable);

    private static void SetEnvironmentValue(bool sandbox, string name, string value, RegistryValueKind kind)
    {
        using var key = OpenOrCreateEnvKey(sandbox, writable: true);
        key.SetValue(name, value, kind);
    }

    private static void DeleteEnvironmentValueIfExists(bool sandbox, string name)
    {
        using var key = TryOpenEnvKey(sandbox, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static void AddDirectoryToUserPath(bool sandbox, string directory)
    {
        var normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        using var key = OpenOrCreateEnvKey(sandbox, writable: true);
        var raw = key.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
        var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var exists = parts.Any(p => string.Equals(
            Path.TrimEndingDirectorySeparator(p), normalizedTarget, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            parts.Add(normalizedTarget);
            key.SetValue("Path", string.Join(';', parts), RegistryValueKind.ExpandString);
        }
    }

    private static void RemoveDirectoryFromUserPath(bool sandbox, string directory)
    {
        using var key = TryOpenEnvKey(sandbox, writable: true);
        if (key is null) return;
        var raw = key.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (string.IsNullOrWhiteSpace(raw)) return;
        var normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var remaining = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !string.Equals(Path.TrimEndingDirectorySeparator(p), normalizedTarget, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (remaining.Length == 0)
        {
            key.DeleteValue("Path", throwOnMissingValue: false);
        }
        else
        {
            key.SetValue("Path", string.Join(';', remaining), RegistryValueKind.ExpandString);
        }
    }

    private static void RemoveEnvRootIfMatching(bool sandbox, string dataDir)
    {
        using var key = TryOpenEnvKey(sandbox, writable: true);
        if (key is null) return;
        foreach (var varName in new[] { RootEnvName, LegacyRootEnvName })
        {
            var current = key.GetValue(varName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (!string.IsNullOrWhiteSpace(current)
                && string.Equals(Path.TrimEndingDirectorySeparator(current), Path.TrimEndingDirectorySeparator(dataDir), StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue(varName, throwOnMissingValue: false);
            }
        }
    }

    private static void SetStartupCommand(bool sandbox, string command)
    {
        var subKey = sandbox ? SandboxRootSubKey + "\\Run" : RunSubKey;
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true)!;
        key.SetValue(RunRegistryValueName, command, RegistryValueKind.String);
        key.DeleteValue(LegacyRunRegistryValueName, throwOnMissingValue: false);
    }

    private static void RemoveStartupIfMatching(bool sandbox, string installDir)
    {
        var subKey = sandbox ? SandboxRootSubKey + "\\Run" : RunSubKey;
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        if (key is null) return;
        foreach (var valueName in new[] { RunRegistryValueName, LegacyRunRegistryValueName })
        {
            var current = key.GetValue(valueName) as string;
            if (!string.IsNullOrWhiteSpace(current)
                && current.Contains(installDir, StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
    }

    private static void WriteUninstallRegistration(
        bool sandbox,
        string installDir,
        string mainExe,
        string uninstallExe,
        int estimatedKb)
    {
        RegistryKey? key = null;
        try
        {
            if (sandbox)
            {
                key = Registry.CurrentUser.CreateSubKey(SandboxRootSubKey + "\\Uninstall\\Vigil", writable: true);
            }
            else
            {
                try
                {
                    key = Registry.LocalMachine.CreateSubKey(UninstallSubKey, writable: true);
                }
                catch (UnauthorizedAccessException)
                {
                    key = Registry.CurrentUser.CreateSubKey(UninstallSubKey, writable: true);
                }
            }

            if (key is null) return;
            key.SetValue("DisplayName", AppDisplayName, RegistryValueKind.String);
            key.SetValue("DisplayVersion", AppVersion, RegistryValueKind.String);
            key.SetValue("Publisher", PublisherName, RegistryValueKind.String);
            key.SetValue("DisplayIcon", mainExe, RegistryValueKind.String);
            key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
            key.SetValue("UninstallString", $"\"{uninstallExe}\" --uninstall", RegistryValueKind.String);
            key.SetValue("QuietUninstallString", $"\"{uninstallExe}\" --uninstall --silent", RegistryValueKind.String);
            key.SetValue("EstimatedSize", estimatedKb, RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        finally
        {
            key?.Dispose();
        }
    }

    private static void DeleteUninstallRegistration(bool sandbox)
    {
        if (sandbox)
        {
            Registry.CurrentUser.DeleteSubKeyTree(SandboxRootSubKey + "\\Uninstall\\Vigil", throwOnMissingSubKey: false);
            return;
        }

        try { Registry.LocalMachine.DeleteSubKeyTree(UninstallSubKey, throwOnMissingSubKey: false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallSubKey, throwOnMissingSubKey: false); } catch { }
    }

    private static void StopProcessesInDirectory(string installDir)
    {
        var prefix = Path.TrimEndingDirectorySeparator(installDir) + Path.DirectorySeparatorChar;
        foreach (var procName in new[] { "Vigil", "Vigil.Agent", "Vigil.Elevated", "vigil", "SystemAutorun", "SystemAutorun.Agent", "SystemAutorun.Elevated", "sautorun" })
        {
            foreach (var proc in Process.GetProcessesByName(procName))
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(5000);
                    }
                }
                catch
                {
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
    }

    private static void DeleteDirectoryExceptFile(string rootDir, string keepFile)
    {
        var keepFull = Path.GetFullPath(keepFile);
        foreach (var file in Directory.EnumerateFiles(rootDir))
        {
            if (!string.Equals(Path.GetFullPath(file), keepFull, StringComparison.OrdinalIgnoreCase))
            {
                SafeDeleteFile(file);
            }
        }
        foreach (var sub in Directory.EnumerateDirectories(rootDir))
        {
            SafeDeleteDirectory(sub);
        }
    }

    private static void ScheduleDelayedSelfDelete(string exePath, string dirPath)
    {
        var script = $"ping 127.0.0.1 -n 3 > nul & del /f /q \"{exePath}\" > nul 2>&1 & rmdir /s /q \"{dirPath}\" > nul 2>&1";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c {script}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void BroadcastEnvironmentChange()
    {
        _ = SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment", 0x0002, 1500, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);
}

internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    internal BoundedReadStream(Stream inner, long length)
    {
        _inner = inner;
        _start = inner.Position;
        _length = length;
        _position = 0;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - _position;
        if (remaining <= 0) return 0;
        var toRead = (int)Math.Min(count, remaining);
        var read = _inner.Read(buffer, offset, toRead);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0 || target > _length)
        {
            throw new IOException("Attempted to seek outside bounded payload stream.");
        }
        _inner.Seek(_start + target, SeekOrigin.Begin);
        _position = target;
        return _position;
    }

    public override void Flush() => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
