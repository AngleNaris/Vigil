namespace SystemAutorun.App.Dialogs;

public partial class MessageDialog : SystemAutorun.App.Services.ToolWindow
{
    private MessageDialog(string title, string body, MessageDialogKind kind)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
        var confirmation = kind == MessageDialogKind.Confirmation;
        CancelButton.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
        DialogIcon.Kind = kind switch
        {
            MessageDialogKind.Error => "Warning",
            MessageDialogKind.Confirmation => "Help",
            _ => "Info",
        };
        DialogIcon.Foreground = (System.Windows.Media.Brush)FindResource(
            kind == MessageDialogKind.Error
                ? "Semantic.Brush.StatusFailed"
                : "Semantic.Brush.TextSecondary");
    }

    public static void ShowError(Window owner, string title, string body) => Show(owner, title, body, MessageDialogKind.Error);
    public static void ShowInfo(Window owner, string title, string body) => Show(owner, title, body, MessageDialogKind.Information);
    public static bool ShowConfirm(Window owner, string title, string body) => Show(owner, title, body, MessageDialogKind.Confirmation);

    private static bool Show(Window owner, string title, string body, MessageDialogKind kind)
    {
        if (System.Windows.Application.Current is App { IsExiting: true }) { return false; }
        return new MessageDialog(title, body, kind) { Owner = owner }.ShowDialog() == true;
    }

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

    private enum MessageDialogKind
    {
        Information,
        Error,
        Confirmation,
    }
}
