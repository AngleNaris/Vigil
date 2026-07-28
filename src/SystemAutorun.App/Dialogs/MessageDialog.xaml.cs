namespace SystemAutorun.App.Dialogs;

public partial class MessageDialog : Wpf.Ui.Controls.FluentWindow
{
    private MessageDialog(string title, string body, bool confirmation)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        BodyText.Text = body;
        CancelButton.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
    }

    public static void ShowError(Window owner, string title, string body) =>
        new MessageDialog(title, body, false) { Owner = owner }.ShowDialog();

    public static void ShowInfo(Window owner, string title, string body) =>
        new MessageDialog(title, body, false) { Owner = owner }.ShowDialog();

    public static bool ShowConfirm(Window owner, string title, string body) =>
        new MessageDialog(title, body, true) { Owner = owner }.ShowDialog() == true;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        DialogResult = false;
    }
}
