using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Execution;
using WindowsOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfSelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace SystemAutorun.App.Dialogs;

public partial class AddProgramWindow : SystemAutorun.App.Services.ToolWindow
{
    private readonly IReadOnlyList<Choice<AutorunTaskType>> _taskTypes =
    [
        new("Windows 程序", AutorunTaskType.Windows),
        new("Windows 服务", AutorunTaskType.WindowsService),
        new("WSL 发行版", AutorunTaskType.WslBoot),
        new("WSL systemd 服务", AutorunTaskType.WslSystemd),
        new("WSL 命令", AutorunTaskType.WslCommand),
    ];

    private readonly IReadOnlyList<Choice<HealthCheckType>> _healthTypes =
    [
        new("Windows 进程", HealthCheckType.Process),
        new("TCP 端口", HealthCheckType.Tcp),
        new("Windows 服务", HealthCheckType.WindowsService),
        new("WSL 运行状态", HealthCheckType.WslRunning),
        new("systemd 服务", HealthCheckType.SystemdActive),
        new("WSL 进程", HealthCheckType.WslProcess),
    ];

    private readonly IReadOnlyList<Choice<WindowMode>> _windowModes =
    [
        new("隐藏窗口", WindowMode.Hidden),
        new("托盘模式", WindowMode.Tray),
        new("无控制台", WindowMode.NoConsole),
        new("最小化", WindowMode.Minimized),
        new("普通窗口", WindowMode.Normal),
    ];

    private readonly IReadOnlyList<Choice<LaunchTrigger>> _launchTriggers =
    [
        new("用户登录", LaunchTrigger.Logon),
        new("手动启动", LaunchTrigger.Manual),
        new("系统启动 (SYSTEM)", LaunchTrigger.Boot),
    ];

    private readonly Func<AddTaskRequest, Task<bool>>? _save;
    private bool _saving;

    public AddProgramWindow(StartupEntry? startupEntry = null, Func<AddTaskRequest, Task<bool>>? save = null)
    {
        _save = save;
        Closing += (_, args) => { if (_saving) { args.Cancel = true; } };
        InitializeComponent();
        TaskTypeComboBox.ItemsSource = _taskTypes;
        WindowModeComboBox.ItemsSource = _windowModes;
        TaskTypeComboBox.SelectedIndex = 0;
        WindowModeComboBox.SelectedIndex = 0;

        if (startupEntry is null)
        {
            return;
        }

        NameTextBox.Text = NormalizeTaskName(startupEntry.Name);
        ExecutableTextBox.Text = startupEntry.Target ?? string.Empty;
        ArgumentsTextBox.Text = startupEntry.Arguments ?? string.Empty;
        WorkingDirectoryTextBox.Text = startupEntry.WorkingDirectory
            ?? (startupEntry.Target is null ? string.Empty : Path.GetDirectoryName(startupEntry.Target) ?? string.Empty);
        DescriptionTextBox.Text = startupEntry.Name;
        HealthPatternTextBox.Text = Path.GetFileName(startupEntry.Target);
        // Adopted startup entries may carry arguments; show them rather than hide them under 高级.
        if (!string.IsNullOrWhiteSpace(startupEntry.Arguments)) { AdvancedExpander.IsExpanded = true; }
    }

    public AddTaskRequest? Request { get; private set; }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var dialog = new WindowsOpenFileDialog
        {
            Filter = "Windows 程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            ExecutableTextBox.Text = dialog.FileName;
            WorkingDirectoryTextBox.Text = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
            HealthPatternTextBox.Text = Path.GetFileName(dialog.FileName);
            if (string.IsNullOrWhiteSpace(NameTextBox.Text))
            {
                NameTextBox.Text = NormalizeTaskName(Path.GetFileNameWithoutExtension(dialog.FileName));
            }
        }
    }

    private void TaskType_SelectionChanged(object sender, WpfSelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (TaskTypeComboBox.SelectedItem is not Choice<AutorunTaskType> selected)
        {
            return;
        }

        var isWindowsProgram = selected.Value == AutorunTaskType.Windows;
        var isWindowsService = selected.Value == AutorunTaskType.WindowsService;
        var isWsl = selected.Value is AutorunTaskType.WslBoot
            or AutorunTaskType.WslSystemd
            or AutorunTaskType.WslCommand;
        ProgramPathPanel.Visibility = isWindowsProgram ? Visibility.Visible : Visibility.Collapsed;
        WindowsTargetPanel.Visibility = isWindowsProgram ? Visibility.Visible : Visibility.Collapsed;
        WindowsServiceTargetPanel.Visibility = isWindowsService ? Visibility.Visible : Visibility.Collapsed;
        WslTargetPanel.Visibility = isWsl ? Visibility.Visible : Visibility.Collapsed;
        ServiceTargetPanel.Visibility = selected.Value == AutorunTaskType.WslSystemd
            ? Visibility.Visible
            : Visibility.Collapsed;
        CommandTargetPanel.Visibility = selected.Value == AutorunTaskType.WslCommand
            ? Visibility.Visible
            : Visibility.Collapsed;

        var healthChoices = selected.Value switch
        {
            AutorunTaskType.Windows => Choices(
                HealthCheckType.Process,
                HealthCheckType.Tcp),
            AutorunTaskType.WindowsService => Choices(
                HealthCheckType.WindowsService,
                HealthCheckType.Tcp),
            AutorunTaskType.WslBoot => Choices(
                HealthCheckType.WslRunning,
                HealthCheckType.Tcp),
            AutorunTaskType.WslSystemd => Choices(
                HealthCheckType.SystemdActive,
                HealthCheckType.Tcp,
                HealthCheckType.WslProcess),
            AutorunTaskType.WslCommand => Choices(
                HealthCheckType.WslProcess,
                HealthCheckType.Tcp),
            _ => _healthTypes,
        };
        HealthTypeComboBox.ItemsSource = healthChoices;

        var defaultHealth = selected.Value switch
        {
            AutorunTaskType.Windows => HealthCheckType.Process,
            AutorunTaskType.WindowsService => HealthCheckType.WindowsService,
            AutorunTaskType.WslBoot => HealthCheckType.WslRunning,
            AutorunTaskType.WslSystemd => HealthCheckType.SystemdActive,
            AutorunTaskType.WslCommand => HealthCheckType.WslProcess,
            _ => HealthCheckType.Process,
        };
        HealthTypeComboBox.SelectedItem = healthChoices.First(item => item.Value == defaultHealth);

        var launchChoices = selected.Value is AutorunTaskType.Windows or AutorunTaskType.WindowsService
            ? _launchTriggers
            : Choices(LaunchTrigger.Logon, LaunchTrigger.Manual);
        LaunchTriggerComboBox.ItemsSource = launchChoices;
        LaunchTriggerComboBox.SelectedItem = launchChoices.First(item => item.Value == LaunchTrigger.Logon);
    }

    private void HealthType_SelectionChanged(object sender, WpfSelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (HealthTypeComboBox.SelectedItem is not Choice<HealthCheckType> selected)
        {
            return;
        }

        HealthPatternPanel.Visibility = selected.Value is HealthCheckType.Process or HealthCheckType.WslProcess
            ? Visibility.Visible
            : Visibility.Collapsed;
        TcpHealthPanel.Visibility = selected.Value == HealthCheckType.Tcp
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_saving) { return; }
        if (!int.TryParse(DelayTextBox.Text.Trim(), out var delay) || delay < 0)
        {
            AdvancedExpander.IsExpanded = true;
            MessageDialog.ShowError(this, "输入无效", "启动延迟必须是非负整数。");
            return;
        }

        if (!int.TryParse(HealthTimeoutTextBox.Text.Trim(), out var timeout) || timeout <= 0)
        {
            AdvancedExpander.IsExpanded = true;
            MessageDialog.ShowError(this, "输入无效", "健康检查超时必须是正整数。");
            return;
        }

        try
        {
            var type = SelectedTaskType();
            var healthType = SelectedHealthType();
            var trigger = SelectedLaunchTrigger();
            if (type is not (AutorunTaskType.Windows or AutorunTaskType.WindowsService)
                && trigger == LaunchTrigger.Boot)
            {
                throw new ArgumentException("WSL 工作负载不能使用 SYSTEM 启动；请选择用户登录或手动启动。");
            }

            int? port = null;
            if (healthType == HealthCheckType.Tcp)
            {
                if (!int.TryParse(HealthPortTextBox.Text.Trim(), out var parsedPort)
                    || parsedPort is < 1 or > 65535)
                {
                    AdvancedExpander.IsExpanded = true;
                    throw new ArgumentException("TCP 端口必须是 1 到 65535 之间的整数。");
                }

                port = parsedPort;
            }

            var runAs = trigger == LaunchTrigger.Boot ? RunAsMode.System : RunAsMode.CurrentUser;
            var windowMode = type == AutorunTaskType.Windows
                ? SelectedWindowMode()
                : WindowMode.NoConsole;
            var serviceName = type == AutorunTaskType.WindowsService
                ? WindowsServiceNameTextBox.Text.Trim()
                : ServiceTextBox.Text.Trim();
            Request = new AddTaskRequest(
                NameTextBox.Text.Trim(),
                type,
                DescriptionTextBox.Text.Trim(),
                ExecutableTextBox.Text.Trim(),
                type == AutorunTaskType.Windows
                    ? WindowsCommandLine.ParseArguments(ArgumentsTextBox.Text)
                    : Array.Empty<string>(),
                WorkingDirectoryTextBox.Text.Trim(),
                DistroTextBox.Text.Trim(),
                serviceName,
                CommandTextBox.Text.Trim(),
                ParseDependencies(DependsOnTextBox.Text),
                delay,
                healthType,
                HealthPatternTextBox.Text.Trim(),
                HealthHostTextBox.Text.Trim(),
                port,
                timeout,
                new LaunchOptions(
                    runAs,
                    trigger,
                    windowMode,
                    type == AutorunTaskType.WindowsService
                        ? ProcessLifecycle.Service
                        : ProcessLifecycle.Tracked),
                ExpectedRunning: true,
                StartOnAppLaunch: trigger == LaunchTrigger.Logon && StartOnAppLaunchCheckBox.IsChecked == true,
                AutoRecover: trigger == LaunchTrigger.Logon && AutoRecoverCheckBox.IsChecked == true);
            if (_save is not null)
            {
                // A rejected/unknown adoption stays in the SAME form with all draft fields.
                // Existing callers without a commit callback retain their original behavior.
                _saving = true;
                try
                {
                    var submission = _save(Request);
                    IsEnabled = false;
                    if (!await submission) { return; }
                }
                catch (Exception exception)
                {
                    IsEnabled = true;
                    MessageDialog.ShowError(this, "提交状态提示 · 输入已保留", exception.Message);
                    return;
                }
                finally { IsEnabled = true; _saving = false; }
            }
            DialogResult = true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or System.ComponentModel.Win32Exception)
        {
            MessageDialog.ShowError(this, "输入无效", exception.Message);
        }
    }

    private AutorunTaskType SelectedTaskType() =>
        SelectedValue<AutorunTaskType>(TaskTypeComboBox, "请选择工作负载类型。");

    private HealthCheckType SelectedHealthType() =>
        SelectedValue<HealthCheckType>(HealthTypeComboBox, "请选择健康检查类型。");

    private LaunchTrigger SelectedLaunchTrigger() =>
        SelectedValue<LaunchTrigger>(LaunchTriggerComboBox, "请选择启动时机。");

    private WindowMode SelectedWindowMode() =>
        SelectedValue<WindowMode>(WindowModeComboBox, "请选择窗口模式。");

    private static T SelectedValue<T>(WpfComboBox comboBox, string message) where T : struct =>
        comboBox.SelectedItem is Choice<T> selected
            ? selected.Value
            : throw new ArgumentException(message);

    private static IReadOnlyList<string> ParseDependencies(string value) =>
        value.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private IReadOnlyList<Choice<T>> Choices<T>(params T[] values) where T : struct
    {
        IReadOnlyList<Choice<T>> source = typeof(T) == typeof(HealthCheckType)
            ? (IReadOnlyList<Choice<T>>)(object)_healthTypes
            : (IReadOnlyList<Choice<T>>)(object)_launchTriggers;
        return source.Where(item => values.Contains(item.Value)).ToArray();
    }

    private static string NormalizeTaskName(string value)
    {
        var normalized = new string(value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }

        return normalized.Trim('-');
    }

    private sealed record Choice<T>(string Label, T Value) where T : struct;
}
