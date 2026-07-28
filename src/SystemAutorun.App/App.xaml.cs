using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.App;

public partial class App : System.Windows.Application
{
    private AgentProcessManager? _agentManager;
    private SingleInstanceService? _singleInstance;
    private TrayService? _trayService;
    private MainWindow? _mainWindow;
    private MainWindowViewModel? _mainViewModel;
    private StartupManager? _startupManager;
    private ProjectPaths? _paths;

    public static App CurrentApp => (App)Current;

    public bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var arguments = AppArguments.Parse(e.Args);
        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.IsPrimary)
        {
            if (!arguments.Background)
            {
                try
                {
                    await _singleInstance.NotifyPrimaryAsync();
                }
                catch (Exception exception) when (exception is IOException or TimeoutException)
                {
                    // The primary instance may still be creating its activation pipe.
                }
            }

            await _singleInstance.DisposeAsync();
            _singleInstance = null;
            Shutdown();
            return;
        }

        try
        {
            _paths = ProjectPaths.Resolve(arguments.RootDirectory);
            var client = new AgentClient();
            _agentManager = new AgentProcessManager(client, _paths);
            await _agentManager.EnsureRunningAsync();

            var configurationEditor = new ConfigurationFileEditor();
            var viewModel = new MainWindowViewModel(
                client,
                new NativeCacheStore(_paths.GuiCachePath),
                new GuiSettingsStore(_paths.GuiSettingsPath),
                _paths,
                configurationEditor);
            _mainViewModel = viewModel;
            viewModel.IsSelfStartupEnabled = UserStartupRegistration.IsEnabled(
                Environment.ProcessPath!,
                _paths.RootDirectory);
            _startupManager = new StartupManager(_paths);
            _mainWindow = new MainWindow(
                viewModel,
                _startupManager,
                new StartupCacheStore(_paths.StartupCachePath));
            MainWindow = _mainWindow;
            _trayService = new TrayService(
                ShowMainWindow,
                () => viewModel.RefreshCommand.Execute(null),
                RequestExit);
            _singleInstance.StartListening(() => Dispatcher.BeginInvoke(ShowMainWindow));

            if (!arguments.Background)
            {
                _mainWindow.Show();
            }

            await viewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            await WriteStartupFailureAsync(arguments.RootDirectory, exception);
            if (!arguments.Background)
            {
                if (_mainWindow is not null)
                {
                    MessageDialog.ShowError(_mainWindow, "启动失败", exception.Message);
                }
                else
                {
                    System.Windows.MessageBox.Show(
                        exception.Message,
                        "SystemAutorun 启动失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }

            RequestExit();
        }
    }

    internal void ShowTrayInfo(string title, string message) =>
        _trayService?.ShowInfo(title, message);

    internal void SetSelfStartup(bool enabled)
    {
        if (_paths is null || string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return;
        }

        try
        {
            UserStartupRegistration.SetEnabled(enabled, Environment.ProcessPath, _paths.RootDirectory);
            if (_mainViewModel is not null)
            {
                _mainViewModel.IsSelfStartupEnabled = enabled;
            }

            ShowTrayInfo(
                "SystemAutorun",
                enabled ? "已启用登录后后台启动。" : "已关闭登录后后台启动。");
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            if (_mainViewModel is not null)
            {
                _mainViewModel.IsSelfStartupEnabled = UserStartupRegistration.IsEnabled(
                    Environment.ProcessPath,
                    _paths.RootDirectory);
            }

            if (_mainWindow is not null)
            {
                MessageDialog.ShowError(_mainWindow, "自启动设置失败", exception.Message);
            }
        }
    }

    internal async void RunElevated(string command)
    {
        var succeeded = await RunElevatedAsync(command, Array.Empty<string>());
        if (!succeeded && _mainWindow is not null)
        {
            MessageDialog.ShowError(
                _mainWindow,
                "管理员操作失败",
                "提权 Helper 执行失败，详情已写入 %LOCALAPPDATA%\\SystemAutorun\\logs\\elevated.log。");
        }
    }

    internal Task<bool> RunElevatedStartupMutationAsync(StartupEntry entry, string action)
    {
        return action switch
        {
            "disable" => RunElevatedAsync(
                "startup-disable",
                new[] { "--source", entry.Source, "--name", entry.Name, "--origin", entry.Origin }),
            "restore" when entry.BackupId is not null => RunElevatedAsync(
                "startup-restore",
                new[] { "--backup-id", entry.BackupId }),
            _ => Task.FromResult(false),
        };
    }

    private async Task<bool> RunElevatedAsync(string command, IReadOnlyList<string> extraArguments)
    {
        if (_paths is null)
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveElevatedPath(_paths),
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = _paths.RootDirectory,
            };
            startInfo.ArgumentList.Add(command);
            startInfo.ArgumentList.Add("--root");
            startInfo.ArgumentList.Add(_paths.RootDirectory);
            foreach (var argument in extraArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the elevated helper.");
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
            {
                ShowTrayInfo("SystemAutorun", "管理员操作已完成。");
                return true;
            }

            return false;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            ShowTrayInfo("SystemAutorun", "管理员操作已取消。");
            return false;
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            if (_mainWindow is not null)
            {
                MessageDialog.ShowError(_mainWindow, "管理员操作失败", exception.Message);
            }

            return false;
        }
    }

    internal async void RequestExit()
    {
        if (IsExiting)
        {
            return;
        }

        IsExiting = true;
        var cleanupFailures = new List<Exception>();

        var trayService = _trayService;
        _trayService = null;
        CaptureCleanupFailure(() => trayService?.Dispose(), "Tray service", cleanupFailures);

        var mainWindow = _mainWindow;
        _mainWindow = null;
        CaptureCleanupFailure(() => mainWindow?.Close(), "Main window", cleanupFailures);

        var startupManager = _startupManager;
        _startupManager = null;
        CaptureCleanupFailure(() => startupManager?.Dispose(), "Startup manager", cleanupFailures);

        var agentManager = _agentManager;
        _agentManager = null;
        if (agentManager is not null)
        {
            try
            {
                await agentManager.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(new InvalidOperationException("Agent process cleanup failed.", exception));
            }
        }

        var singleInstance = _singleInstance;
        _singleInstance = null;
        if (singleInstance is not null)
        {
            try
            {
                await singleInstance.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(new InvalidOperationException("Single-instance cleanup failed.", exception));
            }
        }

        try
        {
            if (cleanupFailures.Count > 0)
            {
                await WriteLifecycleFailureAsync(_paths?.RootDirectory, cleanupFailures);
            }
        }
        finally
        {
            Shutdown();
        }
    }

    private static void CaptureCleanupFailure(
        Action cleanup,
        string component,
        ICollection<Exception> failures)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(new InvalidOperationException($"{component} cleanup failed.", exception));
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.ActivateFromBackground();
    }

    private static string ResolveElevatedPath(ProjectPaths paths)
    {
        var configured = Environment.GetEnvironmentVariable("SYSTEMAUTORUN_ELEVATED_PATH");
        var candidates = new List<string?>
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, "runtime", "SystemAutorun.Elevated.exe"),
            Path.Combine(AppContext.BaseDirectory, "SystemAutorun.Elevated.exe"),
        };
        var developmentRoot = Path.Combine(paths.RootDirectory, "src", "SystemAutorun.Elevated", "bin");
        if (Directory.Exists(developmentRoot))
        {
            candidates.AddRange(Directory
                .EnumerateFiles(developmentRoot, "SystemAutorun.Elevated.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc));
        }

        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? throw new FileNotFoundException("SystemAutorun.Elevated.exe was not found.");
    }

    private static async Task WriteStartupFailureAsync(string? rootDirectory, Exception exception)
    {
        try
        {
            var directory = string.IsNullOrWhiteSpace(rootDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemAutorun")
                : Path.Combine(rootDirectory, "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "app-startup-error.log");
            await File.AppendAllTextAsync(
                path,
                $"[{DateTimeOffset.Now:O}] {exception}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch (Exception logException) when (
            logException is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    private static async Task WriteLifecycleFailureAsync(
        string? rootDirectory,
        IReadOnlyCollection<Exception> exceptions)
    {
        try
        {
            var directory = string.IsNullOrWhiteSpace(rootDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemAutorun")
                : Path.Combine(rootDirectory, "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "app-lifecycle.log");
            var message = string.Join(
                Environment.NewLine,
                exceptions.Select(exception => exception.ToString()));
            await File.AppendAllTextAsync(
                path,
                $"[{DateTimeOffset.Now:O}] Exit cleanup reported failures:{Environment.NewLine}{message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // Exit cleanup must complete even when its diagnostic log cannot be written.
        }
    }

    private sealed record AppArguments(bool Background, string? RootDirectory)
    {
        internal static AppArguments Parse(IReadOnlyList<string> arguments)
        {
            var background = false;
            string? rootDirectory = null;
            for (var index = 0; index < arguments.Count; index++)
            {
                switch (arguments[index])
                {
                    case "--background":
                        background = true;
                        break;
                    case "--root" when index + 1 < arguments.Count:
                        rootDirectory = arguments[++index];
                        break;
                }
            }

            return new AppArguments(background, rootDirectory);
        }
    }
}
