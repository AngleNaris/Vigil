using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Configuration;

namespace SystemAutorun.App.ViewModels;

public partial class MainWindowViewModel
{
    public event EventHandler<string?>? LogViewerRequested;

    [RelayCommand]
    private void ShowLogs(string? taskName) => LogViewerRequested?.Invoke(this, taskName);

    internal LogViewerViewModel CreateLogViewer(string? taskName) => new(_client, _paths, taskName);

    [RelayCommand]
    private async Task OpenLogsAsync()
    {
        if (_dashboardDisposed) { return; }
        try
        {
            // Same live configuration as CLI logs path. Do not advance the editing
            // client's revision cursor or flush/rebase pending settings for a lookup.
            var workspace = await _client.SendAsync<ConfigurationWorkspaceSnapshot>(
                "configuration", timeout: TimeSpan.FromSeconds(3));
            if (_dashboardDisposed) { return; }
            var directory = workspace.Configuration.Settings.LogDirectory;
            Directory.CreateDirectory(directory);
            using var process = _openLogDirectory(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
            StatusMessage = "已请求打开日志目录。";
        }
        catch (Exception exception)
        {
            if (_dashboardDisposed) { return; }
            var message = AgentErrorMessages.Format(exception);
            StatusMessage = $"日志目录未打开：{message}";
            ErrorRequested?.Invoke(this, message);
        }
    }
}
