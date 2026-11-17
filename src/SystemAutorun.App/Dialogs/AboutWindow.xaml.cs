using System.Diagnostics;
using SystemAutorun.App.Services;

namespace SystemAutorun.App.Dialogs;

public partial class AboutWindow : ToolWindow
{
    private readonly Func<CancellationToken, Task<UpdateCheckResult>>? _customChecker;
    private readonly Action<string>? _openUrl;
    private CancellationTokenSource? _checkCancellation;
    private string _targetUrl = UpdateChecker.RepositoryUrl;
    private bool _checking;

    internal AboutWindow(
        bool autoCheckOnLoad = false,
        Func<CancellationToken, Task<UpdateCheckResult>>? customChecker = null,
        Action<string>? openUrl = null)
    {
        InitializeComponent();
        _customChecker = customChecker;
        _openUrl = openUrl;
        VersionValueText.Text = $"v{UpdateChecker.CurrentVersionString}";
        AuthorValueText.Text = UpdateChecker.AuthorName;
        if (autoCheckOnLoad)
        {
            Loaded += async (_, args) =>
            {
                if (ReferenceEquals(args.OriginalSource, this))
                {
                    await RunCheckUpdateAsync();
                }
            };
        }
        Closed += (_, _) => _checkCancellation?.Cancel();
    }

    internal Task? ActiveCheckTask { get; private set; }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RunCheckUpdateAsync();
    }

    internal async Task RunCheckUpdateAsync()
    {
        if (_checking) { return; }
        _checking = true;
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateLabel.Text = "正在检查…";
        UpdateStatusText.Text = "正在查询 GitHub Releases…";
        _checkCancellation?.Cancel();
        _checkCancellation?.Dispose();
        var cancellation = _checkCancellation = new CancellationTokenSource();

        try
        {
            var task = UpdateChecker.CheckLatestAsync(cancellation.Token, _customChecker);
            ActiveCheckTask = task;
            var result = await task;
            if (!IsPresentationActive || cancellation.IsCancellationRequested) { return; }
            _targetUrl = result.TargetUrl;
            UpdateStatusText.Text = result.Message;
            OpenRepoLabel.Text = result.State == UpdateCheckState.UpdateAvailable ? "打开发布页" : "项目主页";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            _checking = false;
            CheckUpdateButton.IsEnabled = true;
            CheckUpdateLabel.Text = "检查更新";
        }
    }

    private void OpenRepo_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var url = _targetUrl;
        if (_openUrl is not null)
        {
            _openUrl(url);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageDialog.ShowError(this, "无法打开链接", exception.Message);
        }
    }
}
