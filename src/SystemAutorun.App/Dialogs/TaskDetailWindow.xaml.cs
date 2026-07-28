using SystemAutorun.Core.Models;

namespace SystemAutorun.App.Dialogs;

public partial class TaskDetailWindow : Wpf.Ui.Controls.FluentWindow
{
    public TaskDetailWindow(TaskStatusDetail detail)
    {
        InitializeComponent();
        DataContext = detail;
        ProcessText.Text = detail.Processes.Count == 0 ? "无" : string.Join(", ", detail.Processes);
        LogsText.Text = detail.RecentLogs.Count == 0
            ? "暂无日志。"
            : string.Join(Environment.NewLine, detail.RecentLogs);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close();
    }
}
