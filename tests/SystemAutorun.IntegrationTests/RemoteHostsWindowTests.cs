using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class RemoteHostsWindowTests
{
    [Fact]
    public async Task AddAndEditDialogsPreserveDraftAndIdentityUntilSaveIsAcknowledged()
    {
        var probe = Environment.GetEnvironmentVariable("SA_SHARED_GUI_PROBE");
        if (probe is not null)
        {
            probe = Path.GetFullPath(probe);
            if (Path.GetDirectoryName(probe) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(probe).StartsWith("SystemAutorun-controls-probe-", StringComparison.Ordinal)
                || !Directory.Exists(probe) || new DirectoryInfo(probe).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Expected an ordinary owned controls probe directory.");
        }
        var root = Directory.CreateTempSubdirectory("SystemAutorun-remote-window-tests-").FullName;
        try
        {
            var knownHosts = Path.Combine(root, "known_hosts");
            await File.WriteAllTextAsync(knownHosts, "# disabled fixture, no actual connections\n");
            await GuiConfigurationControlTests.RunStaAsync(async () =>
            {
                var application = Application.Current as SystemAutorun.App.App;
                if (application is null)
                {
                    application = new SystemAutorun.App.App(runRuntimeStartup: false);
                    application.InitializeComponent();
                }
                var definition = new RemoteHostDefinition
                {
                    Id = "home-storage", Name = "禁用测试节点", Enabled = false, Platform = RemoteHostPlatform.Linux,
                    Host = "192.0.2.10", UserName = "fixture", Authentication = RemoteAuthenticationMode.SshAgent,
                    KnownHostsFile = knownHosts, ConnectTimeoutSeconds = 5, CommandTimeoutSeconds = 10,
                    ProxyUri = "socks5://127.0.0.1:10808",
                };

                // 1. Edit mode pre-fills the target definition (including ProxyUri) and preserves its Id.
                var editAttempts = new List<RemoteHostDefinition[]>();
                var editWindow = new RemoteHostsWindow([definition], hosts =>
                {
                    editAttempts.Add(hosts.ToArray());
                    return Task.CompletedTask;
                }, editing: definition)
                {
                    ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000, Top = -32000,
                };
                try
                {
                    WindowTheme.Install();
                    editWindow.Show();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
                    editWindow.UpdateLayout();
                    Assert.Equal("编辑远程服务器", editWindow.Title);
                    Assert.Equal("保存", editWindow.SaveLabel.Text);
                    Assert.Equal(definition.Name, editWindow.NameTextBox.Text);
                    Assert.Equal(definition.Host, editWindow.HostTextBox.Text);
                    Assert.Equal(definition.UserName, editWindow.UserNameTextBox.Text);
                    Assert.Equal("socks5://127.0.0.1:10808", editWindow.ProxyUriTextBox.Text);
                    editWindow.NameTextBox.Text = "已重命名节点";
                    editWindow.ProxyUriTextBox.Text = "   ";
                    await editWindow.SaveDraftAsync();
                    var savedEdit = Assert.Single(Assert.Single(editAttempts));
                    Assert.Equal(definition.Id, savedEdit.Id);
                    Assert.Equal("已重命名节点", savedEdit.Name);
                    Assert.Null(savedEdit.ProxyUri);
                }
                finally { editWindow.Close(); }

                // 2. Add mode opens blank and preserves draft + generated Id across a conflict retry.
                var attempts = new List<RemoteHostDefinition[]>();
                var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var window = new RemoteHostsWindow([definition], hosts =>
                {
                    attempts.Add(hosts.ToArray());
                    return pending.Task;
                })
                {
                    ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = probe is null ? -32000 : 80, Top = probe is null ? -32000 : 70,
                };
                try
                {
                    WindowTheme.Install();
                    window.Show();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
                    window.UpdateLayout();
                    Assert.Same(application.FindResource(typeof(Window)), window.Style);
                    Assert.Equal(Color.FromRgb(0xD4, 0xD4, 0xDC), Assert.IsType<SolidColorBrush>(window.Foreground).Color);
                    Assert.Equal(Color.FromRgb(0x14, 0x14, 0x17), Assert.IsType<SolidColorBrush>(window.Background).Color);
                    Assert.Equal("添加远程服务器", window.Title);
                    Assert.Equal("添加", window.SaveLabel.Text);
                    Assert.Equal(string.Empty, window.NameTextBox.Text);
                    Assert.Equal(string.Empty, window.HostTextBox.Text);

                    // Approved G2-state-r2: existing borders change color, never width; no focus adorners.
                    Assert.Equal(new Thickness(0, 0, 0, 1), window.NameTextBox.BorderThickness);
                    Assert.Equal(new Thickness(0, 0, 0, 1), window.ProxyUriTextBox.BorderThickness);
                    Assert.Equal(new Thickness(0, 0, 0, 1), window.PlatformComboBox.BorderThickness);
                    Assert.Null(window.NameTextBox.FocusVisualStyle);
                    Assert.Null(window.ProxyUriTextBox.FocusVisualStyle);
                    Assert.Null(window.PlatformComboBox.FocusVisualStyle);
                    Assert.Equal(ColorFocusRole.Border, ColorStateAssist.GetRole(window.NameTextBox));
                    Assert.Equal(ColorFocusRole.Border, ColorStateAssist.GetRole(window.ProxyUriTextBox));
                    Assert.Equal(ColorFocusRole.Border, ColorStateAssist.GetRole(window.PlatformComboBox));
                    Assert.Equal(Visibility.Collapsed, MaterialDesignThemes.Wpf.TextFieldAssist.GetDecorationVisibility(window.NameTextBox));
                    Assert.Equal(Visibility.Collapsed, MaterialDesignThemes.Wpf.TextFieldAssist.GetDecorationVisibility(window.ProxyUriTextBox));
                    Assert.Equal(Visibility.Collapsed, MaterialDesignThemes.Wpf.TextFieldAssist.GetDecorationVisibility(window.PlatformComboBox));
                    var choice = new ComboBoxItem { Style = window.PlatformComboBox.ItemContainerStyle };
                    Assert.Null(choice.FocusVisualStyle);
                    Assert.Equal(ColorFocusRole.Foreground, ColorStateAssist.GetRole(choice));
                    var choiceSurface = Assert.IsType<Border>(choice.Template.LoadContent());
                    Assert.Equal(new Thickness(0), choiceSurface.BorderThickness);
                    var button = new Button { Style = Assert.IsType<Style>(application.FindResource(typeof(Button))) };
                    Assert.Null(button.FocusVisualStyle);
                    Assert.Equal(ColorFocusRole.Border, ColorStateAssist.GetRole(button));
                    Assert.Equal(new Thickness(1), button.BorderThickness);

                    window.NameTextBox.Text = "保留的新增草稿";
                    window.HostTextBox.Text = "192.0.2.26";
                    window.UserNameTextBox.Text = "fixture";
                    window.KnownHostsFileTextBox.Text = knownHosts;
                    window.EnabledCheckBox.IsChecked = false;
                    window.ProxyUriTextBox.Text = "ftp://127.0.0.1:21";
                    await Assert.ThrowsAsync<ArgumentException>(() => window.SaveDraftAsync());
                    window.ProxyUriTextBox.Text = "socks5://127.0.0.1:10808";
                    var first = window.SaveDraftAsync();
                    Assert.False(window.IsEnabled);
                    await Assert.ThrowsAsync<InvalidOperationException>(() => window.SaveDraftAsync());
                    var newId = attempts[0].Single(host => host.Name == "保留的新增草稿").Id;
                    pending.SetException(new AgentCommandException("revision_conflict", "fixture conflict"));
                    await Assert.ThrowsAsync<AgentCommandException>(() => first);
                    Assert.True(window.IsVisible);
                    Assert.True(window.IsEnabled);
                    Assert.Equal("保留的新增草稿", window.NameTextBox.Text);
                    Assert.Empty(window.RemoteHosts);

                    pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var second = window.SaveDraftAsync();
                    Assert.Equal(2, attempts[1].Length);
                    Assert.Equal(newId, attempts[1].Single(host => host.Name == "保留的新增草稿").Id);
                    pending.SetResult();
                    await second;
                    Assert.Equal(2, window.RemoteHosts.Count);
                    Assert.Equal("socks5://127.0.0.1:10808", window.RemoteHosts.Single(host => host.Id == newId).ProxyUri);
                    Assert.Equal(newId, window.SavedHostId);
                    Assert.True(window.IsEnabled);
                }
                finally { window.Close(); }
            }, probe is null ? null : TimeSpan.FromMinutes(17));
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("SystemAutorun-remote-window-tests-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to delete a non-owned remote window fixture.");
            }
            Directory.Delete(resolved, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteHostsWindowRendersProxyFieldCleanlyInBothThemes(bool light)
    {
        var root = Directory.CreateTempSubdirectory("SystemAutorun-remote-window-vis-").FullName;
        try
        {
            var knownHosts = Path.Combine(root, "known_hosts");
            await File.WriteAllTextAsync(knownHosts, "# fixture\n");
            await GuiConfigurationControlTests.RunStaAsync(async () =>
            {
                var application = Application.Current as SystemAutorun.App.App;
                if (application is null)
                {
                    application = new SystemAutorun.App.App(runRuntimeStartup: false);
                    application.InitializeComponent();
                }
                WindowTheme.Install();
                var previousTheme = ThemeService.IsLight ? ThemePreference.Light : ThemePreference.Dark;
                ThemeService.Apply(light ? ThemePreference.Light : ThemePreference.Dark, persist: null);

                var definition = new RemoteHostDefinition
                {
                    Id = "oracle-arm", Name = "Oracle ARM 圣何塞", Enabled = true, Platform = RemoteHostPlatform.Linux,
                    Host = "vps.3efs.com", UserName = "ubuntu", Authentication = RemoteAuthenticationMode.SshAgent,
                    KnownHostsFile = knownHosts, ConnectTimeoutSeconds = 10, CommandTimeoutSeconds = 20,
                    ProxyUri = "socks5://127.0.0.1:10808",
                };
                var window = new RemoteHostsWindow([definition], _ => Task.CompletedTask, editing: definition)
                {
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                };
                ColorStateAssist.SetReduceMotion(window, true);
                try
                {
                    window.Show();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();

                    Assert.True(window.ProxyUriTextBox.IsVisible);
                    Assert.True(window.ProxyUriTextBox.ActualHeight >= 20);
                    Assert.True(window.ActualHeight <= 640);

                    var evidenceDir = Environment.GetEnvironmentVariable("SA_REMOTE_PROXY_EVIDENCE");
                    if (!string.IsNullOrWhiteSpace(evidenceDir))
                    {
                        Directory.CreateDirectory(evidenceDir);
                        var content = (FrameworkElement)window.Content;
                        content.UpdateLayout();
                        var dpi = VisualTreeHelper.GetDpi(content);
                        var width = Math.Max(1, (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX));
                        var height = Math.Max(1, (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY));
                        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                        var visual = new DrawingVisual();
                        using (var dc = visual.RenderOpen())
                        {
                            dc.DrawRectangle(
                                new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                                null,
                                new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                        }
                        bitmap.Render(visual);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        await using var stream = File.Create(Path.Combine(evidenceDir, $"remote-hosts-{(light ? "light" : "dark")}.png"));
                        encoder.Save(stream);
                    }
                }
                finally
                {
                    window.Close();
                    ThemeService.Apply(previousTheme, persist: null);
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
