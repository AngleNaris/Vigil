using SystemAutorun.App.Services;
using SystemAutorun.Core.Models;
using WindowsOpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace SystemAutorun.App.Dialogs;

public partial class RemoteHostsWindow : ToolWindow
{
    private readonly List<RemoteHostDefinition> _hosts;
    private readonly IReadOnlyList<Choice<RemoteHostPlatform>> _platforms =
    [
        new("Windows", RemoteHostPlatform.Windows),
        new("Linux", RemoteHostPlatform.Linux),
    ];
    private readonly IReadOnlyList<Choice<RemoteAuthenticationMode>> _authenticationModes =
    [
        new("SSH Agent", RemoteAuthenticationMode.SshAgent),
        new("私钥文件", RemoteAuthenticationMode.PrivateKey),
    ];
    private string? _editingId;
    private readonly Func<IReadOnlyList<RemoteHostDefinition>, Task>? _save;
    private bool _saving;

    internal RemoteHostsWindow(IReadOnlyList<RemoteHostDefinition> hosts,
        Func<IReadOnlyList<RemoteHostDefinition>, Task>? save = null,
        RemoteHostDefinition? editing = null)
    {
        InitializeComponent();
        _save = save;
        _hosts = [.. hosts];
        PlatformComboBox.ItemsSource = _platforms;
        AuthenticationComboBox.ItemsSource = _authenticationModes;
        PlatformComboBox.SelectedIndex = 1;
        AuthenticationComboBox.SelectedIndex = 0;
        EnabledCheckBox.IsChecked = true;
        KnownHostsFileTextBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh",
            "known_hosts.vigil");
        if (editing is not null)
        {
            LoadDefinition(editing);
        }
        else
        {
            BeginNew();
        }
    }

    internal IReadOnlyList<RemoteHostDefinition> RemoteHosts { get; private set; } =
        Array.Empty<RemoteHostDefinition>();

    internal string? SavedHostId { get; private set; }

    private void Authentication_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        var usesKey = AuthenticationComboBox.SelectedItem is Choice<RemoteAuthenticationMode>
        {
            Value: RemoteAuthenticationMode.PrivateKey,
        };
        IdentityFileTextBox.IsEnabled = usesKey;
    }

    private void BrowseIdentity_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var dialog = new WindowsOpenFileDialog
        {
            Title = "选择 SSH 私钥",
            CheckFileExists = true,
            Filter = "SSH 私钥|id_*;*.pem;*.key|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            IdentityFileTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseKnownHosts_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var dialog = new WindowsOpenFileDialog
        {
            Title = "选择专用 known_hosts 文件",
            CheckFileExists = true,
            Filter = "known_hosts 文件|known_hosts*|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            KnownHostsFileTextBox.Text = dialog.FileName;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_saving) { return; }
        try
        {
            await SaveDraftAsync();
            if (IsPresentationActive) { DialogResult = true; }
        }
        catch (Exception exception)
        {
            // An accepted close used to remove the HWND immediately. Do not resurrect a
            // result or error dialog while its replacement decorative exit is pending.
            if (!IsPresentationActive) { return; }
            MessageDialog.ShowError(this, exception is ArgumentException or FileNotFoundException or PathTooLongException
                    ? "远程服务器配置无效" : "远程服务器未保存",
                exception is ArgumentException or FileNotFoundException or PathTooLongException
                    ? exception.Message : AgentErrorMessages.Format(exception) + "\n草稿已保留；可取消后重新打开，载入最新配置再核对。");
        }
    }

    internal async Task SaveDraftAsync()
    {
        if (_saving) { throw new InvalidOperationException("A save is already pending."); }
        var definition = ApplyEditorToDraft();
        foreach (var host in _hosts) { RemoteHostDefinitionValidator.Validate(host); }
        var draft = _hosts.ToArray();
        _saving = true;
        IsEnabled = false;
        try
        {
            if (_save is not null) { await _save(draft); }
            RemoteHosts = draft;
            SavedHostId = definition.Id;
        }
        finally
        {
            _saving = false;
            IsEnabled = true;
        }
    }

    private RemoteHostDefinition ApplyEditorToDraft()
    {
        var definition = BuildDefinition();
        var existingIndex = _hosts.FindIndex(item =>
            string.Equals(item.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0) { _hosts[existingIndex] = definition; }
        else { _hosts.Add(definition); }
        // Preserve identity for a new host when an attempted save fails.
        _editingId = definition.Id;
        return definition;
    }

    private RemoteHostDefinition BuildDefinition()
    {
        if (!int.TryParse(PortTextBox.Text.Trim(), out var port))
        {
            throw new ArgumentException("SSH 端口必须是整数。");
        }

        if (!int.TryParse(ConnectTimeoutTextBox.Text.Trim(), out var connectTimeout))
        {
            throw new ArgumentException("连接超时必须是整数。");
        }

        if (!int.TryParse(CommandTimeoutTextBox.Text.Trim(), out var commandTimeout))
        {
            throw new ArgumentException("命令超时必须是整数。");
        }

        var platform = SelectedValue<RemoteHostPlatform>(
            PlatformComboBox,
            "请选择服务器平台。");
        var authentication = SelectedValue<RemoteAuthenticationMode>(
            AuthenticationComboBox,
            "请选择 SSH 身份验证方式。");
        var definition = new RemoteHostDefinition
        {
            Id = _editingId ?? Guid.NewGuid().ToString("N"),
            Name = NameTextBox.Text.Trim(),
            Enabled = EnabledCheckBox.IsChecked == true,
            Platform = platform,
            Host = HostTextBox.Text.Trim(),
            Port = port,
            UserName = UserNameTextBox.Text.Trim(),
            Authentication = authentication,
            IdentityFile = authentication == RemoteAuthenticationMode.PrivateKey
                ? IdentityFileTextBox.Text.Trim()
                : null,
            KnownHostsFile = KnownHostsFileTextBox.Text.Trim(),
            ConnectTimeoutSeconds = connectTimeout,
            CommandTimeoutSeconds = commandTimeout,
        };
        RemoteHostDefinitionValidator.Validate(definition);
        var knownHosts = Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(definition.KnownHostsFile));
        if (!File.Exists(knownHosts))
        {
            throw new FileNotFoundException("专用 known_hosts 文件不存在。", knownHosts);
        }

        if (definition.Authentication == RemoteAuthenticationMode.PrivateKey)
        {
            var identity = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(definition.IdentityFile!));
            if (!File.Exists(identity))
            {
                throw new FileNotFoundException("SSH 私钥文件不存在。", identity);
            }
        }

        return definition;
    }

    private void SetEditorMode(bool creating)
    {
        var title = creating ? "添加远程服务器" : "编辑远程服务器";
        Title = title;
        EditorTitle.Text = title;
        HeaderIcon.Kind = creating ? "Plus" : "Host";
        SaveLabel.Text = creating ? "添加" : "保存";
        SaveIcon.Kind = creating ? "Plus" : "Save";
        System.Windows.Automation.AutomationProperties.SetName(SaveSettingsButton, title);
    }

    private void LoadDefinition(RemoteHostDefinition definition)
    {
        _editingId = definition.Id;
        SetEditorMode(creating: false);
        NameTextBox.Text = definition.Name;
        PlatformComboBox.SelectedItem = _platforms.First(item => item.Value == definition.Platform);
        HostTextBox.Text = definition.Host;
        PortTextBox.Text = definition.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        UserNameTextBox.Text = definition.UserName;
        AuthenticationComboBox.SelectedItem = _authenticationModes.First(item =>
            item.Value == definition.Authentication);
        IdentityFileTextBox.Text = definition.IdentityFile ?? string.Empty;
        KnownHostsFileTextBox.Text = definition.KnownHostsFile;
        ConnectTimeoutTextBox.Text =
            definition.ConnectTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CommandTimeoutTextBox.Text =
            definition.CommandTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        EnabledCheckBox.IsChecked = definition.Enabled;
        NameTextBox.Focus();
    }

    private void BeginNew()
    {
        _editingId = null;
        SetEditorMode(creating: true);
        NameTextBox.Text = string.Empty;
        PlatformComboBox.SelectedItem = _platforms.First(item => item.Value == RemoteHostPlatform.Linux);
        HostTextBox.Text = string.Empty;
        PortTextBox.Text = "22";
        UserNameTextBox.Text = string.Empty;
        AuthenticationComboBox.SelectedItem = _authenticationModes.First(item =>
            item.Value == RemoteAuthenticationMode.SshAgent);
        IdentityFileTextBox.Text = string.Empty;
        KnownHostsFileTextBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh",
            "known_hosts.vigil");
        ConnectTimeoutTextBox.Text = "10";
        CommandTimeoutTextBox.Text = "20";
        EnabledCheckBox.IsChecked = true;
        NameTextBox.Focus();
    }

    private static T SelectedValue<T>(System.Windows.Controls.ComboBox comboBox, string message)
        where T : struct =>
        comboBox.SelectedItem is Choice<T> selected
            ? selected.Value
            : throw new ArgumentException(message);

    private sealed record Choice<T>(string Label, T Value) where T : struct;
}
