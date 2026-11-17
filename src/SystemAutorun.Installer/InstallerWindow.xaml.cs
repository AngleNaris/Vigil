using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;

namespace SystemAutorun.Installer;

public partial class InstallerWindow : Window
{
    private enum PageKind
    {
        Config,
        Uninstall,
        Progress,
        Done,
    }

    private readonly InstallerOptions _options;
    private readonly string _currentExePath;
    private readonly List<string> _logLines = new();
    private PageKind _page;
    private double _currentPercent;
    private bool _running;

    internal InstallerWindow(InstallerOptions options, string currentExePath)
    {
        InitializeComponent();
        _options = options;
        _currentExePath = currentExePath;

        SourceInitialized += (_, _) => InstallerTheme.ApplyWindowFrame(this);
        ProgressTrack.SizeChanged += (_, _) => UpdateProgressBarWidth(_currentPercent);

        ConfigureInitialState();
    }

    private void ConfigureInitialState()
    {
        VersionBadgeText.Text = $"v{InstallerEngine.AppVersion}";

        if (_options.Uninstall)
        {
            Title = "Vigil // UNINSTALL";
            HeaderModeText.Text = "UNINSTALL";
            var installDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.InstallDir));
            UninstallPathText.Text = installDir;
            RemoveDataCheck.IsChecked = _options.PurgeData;
            SetPage(PageKind.Uninstall);
        }
        else
        {
            Title = "Vigil // SETUP";
            HeaderModeText.Text = "SETUP";
            InstallDirBox.Text = _options.InstallDir;
            DataDirBox.Text = _options.DataDir;
            DesktopShortcutCheck.IsChecked = _options.CreateDesktopShortcut;
            StartMenuCheck.IsChecked = _options.CreateStartMenuShortcut;
            AddToPathCheck.IsChecked = _options.AddCliToPath || _options.InitializeEnvRoot;
            LaunchOnStartupCheck.IsChecked = _options.EnableLogonStartup;
            SetPage(PageKind.Config);
        }
    }

    private void SetPage(PageKind page)
    {
        _page = page;
        ConfigPage.Visibility = page == PageKind.Config ? Visibility.Visible : Visibility.Collapsed;
        UninstallPage.Visibility = page == PageKind.Uninstall ? Visibility.Visible : Visibility.Collapsed;
        ProgressPage.Visibility = page == PageKind.Progress ? Visibility.Visible : Visibility.Collapsed;
        DonePage.Visibility = page == PageKind.Done ? Visibility.Visible : Visibility.Collapsed;

        switch (page)
        {
            case PageKind.Config:
                CancelButton.Visibility = Visibility.Visible;
                CancelButton.IsEnabled = true;
                PrimaryActionButton.Visibility = Visibility.Visible;
                PrimaryActionButton.IsEnabled = true;
                PrimaryActionButton.Content = "安装";
                FooterButtonGrid.Columns = 2;
                break;

            case PageKind.Uninstall:
                CancelButton.Visibility = Visibility.Visible;
                CancelButton.IsEnabled = true;
                PrimaryActionButton.Visibility = Visibility.Visible;
                PrimaryActionButton.IsEnabled = true;
                PrimaryActionButton.Content = "卸载";
                FooterButtonGrid.Columns = 2;
                break;

            case PageKind.Progress:
                CancelButton.Visibility = Visibility.Visible;
                CancelButton.IsEnabled = false;
                PrimaryActionButton.Visibility = Visibility.Visible;
                PrimaryActionButton.IsEnabled = false;
                PrimaryActionButton.Content = _options.Uninstall ? "卸载中" : "安装中";
                FooterButtonGrid.Columns = 2;
                break;

            case PageKind.Done:
                CancelButton.Visibility = Visibility.Collapsed;
                PrimaryActionButton.Visibility = Visibility.Visible;
                PrimaryActionButton.IsEnabled = true;
                PrimaryActionButton.Content = "完成";
                FooterButtonGrid.Columns = 1;
                break;
        }
    }

    internal void SetProgressPreviewState()
    {
        _currentPercent = 64;
        ProgressStepText.Text = "[28/42] runtime/Vigil.Agent.exe";
        ProgressPercentText.Text = "64%";
        ProgressLogText.Text = string.Join(Environment.NewLine,
            "停止运行中的进程",
            "解压程序文件",
            "[12/42] Vigil.exe",
            "[24/42] cli/vigil.exe",
            "[28/42] runtime/Vigil.Agent.exe");
        SetPage(PageKind.Progress);
        UpdateLayout();
        UpdateProgressBarWidth(64);
    }

    internal void SetCompletedPreviewState()
    {
        DoneTitleText.Text = _options.Uninstall ? "卸载完成" : "安装完成";
        DoneLocationLabel.Text = _options.Uninstall ? "移除位置" : "安装位置";
        DoneInstallDirText.Text = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.InstallDir));
        DoneCliRow.Visibility = _options.Uninstall ? Visibility.Collapsed : Visibility.Visible;
        LaunchNowCheck.Visibility = _options.Uninstall ? Visibility.Collapsed : Visibility.Visible;
        SetPage(PageKind.Done);
    }

    internal async Task RenderForScreenshotAsync(string path)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.Render);

        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight));
        var rtb = new RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!_running)
        {
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_running)
        {
            Close();
        }
    }

    private void BrowseInstall_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "安装目录",
            InitialDirectory = Directory.Exists(InstallDirBox.Text)
                ? InstallDirBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            InstallDirBox.Text = Path.Combine(dlg.FolderName.TrimEnd('\\'), "Vigil");
        }
    }

    private void BrowseData_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "数据目录",
            InitialDirectory = Directory.Exists(DataDirBox.Text)
                ? DataDirBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            DataDirBox.Text = dlg.FolderName;
        }
    }

    private async void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (_page == PageKind.Done)
        {
            FinishAndClose();
            return;
        }

        if (!_options.Uninstall)
        {
            var installDir = InstallDirBox.Text.Trim();
            var dataDir = DataDirBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(installDir) || string.IsNullOrWhiteSpace(dataDir))
            {
                MessageBox.Show(this, "请填写安装目录与数据目录。", "Vigil", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _options.InstallDir = installDir;
            _options.DataDir = dataDir;
            _options.CreateDesktopShortcut = DesktopShortcutCheck.IsChecked == true;
            _options.CreateStartMenuShortcut = StartMenuCheck.IsChecked == true;
            var writeEnvAndPath = AddToPathCheck.IsChecked == true;
            _options.AddCliToPath = writeEnvAndPath;
            _options.InitializeEnvRoot = writeEnvAndPath;
            _options.EnableLogonStartup = LaunchOnStartupCheck.IsChecked == true;
        }
        else
        {
            _options.PurgeData = RemoveDataCheck.IsChecked == true;
        }

        _running = true;
        _currentPercent = 0;
        _logLines.Clear();
        ProgressStepText.Text = _options.Uninstall ? "正在卸载…" : "正在安装…";
        ProgressPercentText.Text = "0%";
        ProgressLogText.Text = string.Empty;
        UpdateProgressBarWidth(0);
        SetPage(PageKind.Progress);

        var progress = new Progress<ProgressInfo>(info =>
        {
            _currentPercent = Math.Clamp(info.Percent, 0, 100);
            ProgressStepText.Text = info.Message;
            ProgressPercentText.Text = $"{_currentPercent:0}%";
            UpdateProgressBarWidth(_currentPercent);

            if (_logLines.Count == 0 || _logLines[^1] != info.Message)
            {
                _logLines.Add(info.Message);
                if (_logLines.Count > 5)
                {
                    _logLines.RemoveAt(0);
                }
                ProgressLogText.Text = string.Join(Environment.NewLine, _logLines);
            }
        });

        try
        {
            if (_options.Uninstall)
            {
                await InstallerEngine.UninstallAsync(_options, progress);
                App.ScheduleTempHelperCleanupIfNeeded(_options, _currentExePath);
            }
            else
            {
                await InstallerEngine.InstallAsync(_options, _currentExePath, progress);
            }

            SetCompletedPreviewState();
        }
        catch (Exception ex)
        {
            SetPage(_options.Uninstall ? PageKind.Uninstall : PageKind.Config);
            MessageBox.Show(this, ex.ToString(), _options.Uninstall ? "卸载失败" : "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _running = false;
        }
    }

    private void FinishAndClose()
    {
        if (!_options.Uninstall && LaunchNowCheck.IsChecked == true)
        {
            var exePath = Path.Combine(_options.InstallDir, "Vigil.exe");
            if (File.Exists(exePath))
            {
                try
                {
                    var psi = new ProcessStartInfo(exePath)
                    {
                        WorkingDirectory = _options.InstallDir,
                        UseShellExecute = true,
                    };
                    psi.ArgumentList.Add("--root");
                    psi.ArgumentList.Add(_options.DataDir);
                    Process.Start(psi);
                }
                catch
                {
                }
            }
        }

        Close();
    }

    private void UpdateProgressBarWidth(double percent)
    {
        var trackWidth = Math.Max(0, ProgressTrack.ActualWidth);
        ProgressFill.Width = trackWidth * (Math.Clamp(percent, 0, 100) / 100.0);
    }
}
