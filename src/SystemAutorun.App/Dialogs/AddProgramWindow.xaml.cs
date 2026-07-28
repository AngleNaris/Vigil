using Microsoft.Win32;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Execution;

namespace SystemAutorun.App.Dialogs;

public partial class AddProgramWindow : Window
{
    public AddProgramWindow(StartupEntry? startupEntry = null)
    {
        InitializeComponent();
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
    }

    public AddWindowsTaskRequest? Request { get; private set; }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Windows 程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            ExecutableTextBox.Text = dialog.FileName;
            WorkingDirectoryTextBox.Text = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(NameTextBox.Text))
            {
                NameTextBox.Text = NormalizeTaskName(Path.GetFileNameWithoutExtension(dialog.FileName));
            }
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!int.TryParse(DelayTextBox.Text.Trim(), out var delay) || delay < 0)
        {
            MessageDialog.ShowError(this, "输入无效", "启动延迟必须是非负整数。");
            return;
        }

        try
        {
            Request = new AddWindowsTaskRequest(
                NameTextBox.Text.Trim(),
                ExecutableTextBox.Text.Trim(),
                WindowsCommandLine.ParseArguments(ArgumentsTextBox.Text),
                DescriptionTextBox.Text.Trim(),
                WorkingDirectoryTextBox.Text.Trim(),
                delay);
            DialogResult = true;
        }
        catch (Exception exception) when (exception is ArgumentException or System.ComponentModel.Win32Exception)
        {
            MessageDialog.ShowError(this, "输入无效", exception.Message);
        }
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
}
