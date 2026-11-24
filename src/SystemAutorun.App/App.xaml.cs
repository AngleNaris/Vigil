using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.App;

public partial class App : System.Windows.Application
{
    private AgentProcessManager? _agentManager;
    private SingleInstanceService? _singleInstance;
    private TrayService? _trayService;
    private MainWindow? _mainWindow;
    private MainWindowViewModel? _mainViewModel;
    private ProjectPaths? _paths;
    private readonly bool _runRuntimeStartup;
    private readonly ToolWindowLifetimes _toolLifetimes = new();

    static App()
    {
        System.Windows.FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(System.Windows.FrameworkElement),
            new System.Windows.FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage("zh-CN")));
    }

    internal void TrackToolWindow(ToolWindow window)
    {
        // Resource-only WPF test Applications span unrelated STA fixtures; they use isolated registries.
        if (_runRuntimeStartup) { _toolLifetimes.Track(window); }
    }

    public App() : this(runRuntimeStartup: true)
    {
    }

    // WPF queues OnStartup in its constructor, not just in Run(). Resource/UI
    // tests must not parse testhost arguments or start production services.
    internal App(bool runRuntimeStartup)
    {
        _runRuntimeStartup = runRuntimeStartup;
    }

    public static App CurrentApp => (App)Current;

    public bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        WindowTheme.Install();
        base.OnStartup(e);
        if (!_runRuntimeStartup)
        {
            return;
        }

        // R106: gray-white or dark palette from the per-user preference (default: follow Windows).
        ThemeService.Install();

        var arguments = new AppArguments(e.Args.Contains("--background", StringComparer.Ordinal), null);
        MainWindow? createdWindow = null;
        try
        {
            arguments = AppArguments.Parse(e.Args);
            _paths = ProjectPaths.Resolve(arguments.RootDirectory);
            var endpoint = AgentProtocol.ForRoot(_paths.RootDirectory);
            _singleInstance = new SingleInstanceService(endpoint.AppInstanceName);
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

            var client = new AgentClient(_paths.RootDirectory);
            _agentManager = new AgentProcessManager(client, _paths);
            var ensureAgentTask = _agentManager.EnsureRunningAsync();

            var configuration = new AgentConfigurationClient(client);
            var viewModel = new MainWindowViewModel(
                client,
                new NativeCacheStore(_paths.GuiCachePath),
                configuration,
                _paths,
                new AgentRemoteHostReader(client, configuration));
            _mainViewModel = viewModel;
            createdWindow = new MainWindow(
                viewModel,
                new AgentStartupClient(client),
                new StartupCacheStore(_paths.StartupCachePath));
            WindowGeometry.Attach(createdWindow);
            _trayService = new TrayService(
                ShowMainWindow,
                () => viewModel.RefreshCommand.Execute(null),
                () =>
                {
                    if (viewModel.DoctorCommand.CanExecute(null))
                    {
                        viewModel.DoctorCommand.Execute(null);
                    }
                },
                () => viewModel.OpenLogsCommand.Execute(null),
                RequestExit);
            await ensureAgentTask;

            _mainWindow = createdWindow;
            MainWindow = createdWindow;
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
                if (_mainWindow is { IsVisible: true })
                {
                    MessageDialog.ShowError(_mainWindow, "启动失败", exception.Message);
                }
                else
                {
                    System.Windows.MessageBox.Show(
                        exception.Message,
                        "Vigil 启动失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }

            _mainWindow ??= createdWindow;
            RequestExit();
        }
    }

    internal void ShowTrayInfo(string title, string message) =>
        _trayService?.ShowInfo(title, message);

    internal void OpenSelfStartup(Window owner, UIElement? returnFocus = null)
    {
        if (_paths is null || IsExiting) { return; }
        try { SelfStartupTool.Open(owner, _paths, returnFocus); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { MessageDialog.ShowError(owner, "登录启动工具不可用", exception.Message); }
    }

    internal RecoveryViewModel? CreateRecoveryModel(string name) =>
        _paths is null || IsExiting ? null : new RecoveryViewModel(_paths, name);

    internal async void RunElevated(string command) => _ = await RunElevatedCommandAsync(command);

    internal async Task<bool> RunElevatedCommandAsync(string command)
    {
        var succeeded = await RunElevatedAsync(command, Array.Empty<string>());
        if (!succeeded && _mainWindow is not null)
        {
            MessageDialog.ShowError(
                _mainWindow,
                "管理员操作失败",
                "提权 Helper 执行失败，详情已写入 %LOCALAPPDATA%\\Vigil\\logs\\elevated.log。");
        }

        return succeeded;
    }

    internal async Task<bool> RunMaintenanceElevationAsync(bool install)
    {
        var command = install ? "install-maintenance" : "uninstall-maintenance";
        var extra = Array.Empty<string>();
        if (install)
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;
            if (!string.IsNullOrWhiteSpace(sid))
            {
                extra = ["--sid", sid];
            }
        }

        return await RunElevatedAsync(command, extra);
    }

    // Compatibility bridge only. Native startup writes stay closed until the
    // protected shared workflow is complete; never ask runas to use the old path.
    internal Task<bool> RunElevatedStartupMutationAsync(StartupEntry entry, string action) => Task.FromResult(false);

    private async Task<bool> RunElevatedAsync(string command, IReadOnlyList<string> extraArguments)
    {
        if (!ElevatedCommandPolicy.IsSupported(command) || _paths is null)
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
                ShowTrayInfo("Vigil", "管理员操作已完成。");
                return true;
            }

            return false;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            ShowTrayInfo("Vigil", "管理员操作已取消。");
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

    internal async void RequestExit() => await RequestExitAsync(Shutdown);

    // The final dispatcher action is injectable so isolated tests never shut down their shared Application.
    internal async Task RequestExitAsync(Action shutdown, ToolWindowLifetimes? lifetimes = null)
    {
        if (IsExiting)
        {
            return;
        }

        IsExiting = true;
        var tools = (lifetimes ?? _toolLifetimes).BeginExit(); // Freeze admission/capture pending lifetimes before the first await.
        var cleanupFailures = new List<Exception>();
        if (_mainViewModel is not null)
        {
            try { await _mainViewModel.FlushSettingsAsync(); }
            catch (Exception exception) { cleanupFailures.Add(new InvalidOperationException("Pending settings save failed.", exception)); }
        }

        var trayService = _trayService;
        _trayService = null;
        CaptureCleanupFailure(() => trayService?.Dispose(), "Tray service", cleanupFailures);

        var mainWindow = _mainWindow;
        _mainWindow = null;
        cleanupFailures.AddRange(await tools.CloseAndDrainAsync(() => mainWindow?.Close()));

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
            shutdown();
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
        if (_mainWindow is null || IsExiting)
        {
            return;
        }

        _mainWindow.ActivateFromBackground();
    }

    private static string ResolveElevatedPath(ProjectPaths paths)
    {
        var configured = Environment.GetEnvironmentVariable("VIGIL_ELEVATED_PATH");
        var candidates = new List<string?>
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, "runtime", "Vigil.Elevated.exe"),
            Path.Combine(AppContext.BaseDirectory, "Vigil.Elevated.exe"),
        };
        var developmentRoot = Path.Combine(paths.RootDirectory, "src", "SystemAutorun.Elevated", "bin");
        if (Directory.Exists(developmentRoot))
        {
            candidates.AddRange(Directory
                .EnumerateFiles(developmentRoot, "Vigil.Elevated.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc));
        }

        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? throw new FileNotFoundException("Vigil.Elevated.exe was not found.");
    }

    private static async Task WriteStartupFailureAsync(string? rootDirectory, Exception exception)
    {
        try
        {
            var directory = string.IsNullOrWhiteSpace(rootDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vigil")
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
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vigil")
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
            var parsed = ProcessLaunchArguments.Parse(arguments, allowBackground: true);
            return new AppArguments(parsed.Background, parsed.RootDirectory);
        }
    }
}
