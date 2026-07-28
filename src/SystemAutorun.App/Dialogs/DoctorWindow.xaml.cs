using SystemAutorun.Core.Models;

namespace SystemAutorun.App.Dialogs;

public partial class DoctorWindow : Window
{
    public DoctorWindow(DoctorReport report)
    {
        InitializeComponent();
        DataContext = report.Checks;
        SummaryText.Text = report.AllOk ? "诊断通过" : $"发现 {report.Failed} 项问题";
        SummaryText.Foreground = (System.Windows.Media.Brush)FindResource(
            report.AllOk ? "Semantic.Brush.StatusHealthy" : "Semantic.Brush.StatusFailed");
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close();
    }
}
