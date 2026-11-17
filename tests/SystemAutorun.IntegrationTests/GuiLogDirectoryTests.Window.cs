using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SystemAutorun.App.Services;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.IntegrationTests;

public sealed partial class GuiLogDirectoryTests
{
    [Theory]
    [InlineData(1120, 700)]
    [InlineData(1320, 820)]
    [InlineData(1620, 651)]
    public async Task ActualMainMenuBindingInvokesTheDirectoryCommandWithoutLaunchingExplorer(int width, int height)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        await File.WriteAllTextAsync(fixture.Paths.ConfigPath, Yaml("菜单 日志"));
        var console = await ConsolePath(fixture.Root);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is not SystemAutorun.App.App) { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
            WindowTheme.Install();
            var launches = new List<System.Diagnostics.ProcessStartInfo>(); var errors = new List<string>();
            var model = Model(fixture.Client, fixture.Paths, new(fixture.Client), info => { launches.Add(info); return null; }, errors);
            model.SearchText = "保留筛选";
            var main = new SystemAutorun.App.MainWindow(model, new AgentStartupClient(fixture.Client), new StartupCacheStore(fixture.Paths.StartupCachePath))
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, Width = width, Height = height };
            try
            {
                main.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                // Let the existing width-dependent minimum settle before requesting
                // the approved wide height; initial XAML MinHeight is 700.
                main.Height = height; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var menu = main.SettingsButton.ContextMenu!;
                menu.PlacementTarget = main.SettingsButton;
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "日志工具") || Equals(item.Header, "日志查看器"));
                var entry = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "打开日志目录"));
                Assert.Same(model.OpenLogsCommand, entry.Command); Assert.True(entry.IsEnabled);
                // Exercise the real bound MenuItem semantic action. Keep its Popup closed:
                // WPF can reposition an offscreen Popup into the user's visible desktop.
                var peer = new MenuItemAutomationPeer(entry);
                Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var execution = model.OpenLogsCommand.ExecutionTask; Assert.NotNull(execution);
                await execution.WaitAsync(TimeSpan.FromSeconds(8));
                AssertLaunch(Assert.Single(launches), console.Directory); Assert.Empty(errors);
                Assert.Equal("已请求打开日志目录。", model.StatusMessage);
                Assert.Equal("保留筛选", model.SearchText); Assert.True(main.IsVisible);
                // Non-100% DPI rounds the window to whole device pixels (651 DIP at 150% = 976.5 px); allow that one pixel.
                var windowDpi = VisualTreeHelper.GetDpi(main);
                Assert.InRange(Math.Abs(width - main.ActualWidth), 0, 1 / windowDpi.DpiScaleX); Assert.InRange(Math.Abs(height - main.ActualHeight), 0, 1 / windowDpi.DpiScaleY);
                Assert.Null(entry.FocusVisualStyle); Assert.Empty(main.OwnedWindows.Cast<Window>());
                var directory = Environment.GetEnvironmentVariable("SA_LOG_GUI_EVIDENCE");
                if (directory is not null)
                {
                    main.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(main);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(main.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(main.ActualHeight * dpi.DpiScaleY),
                        96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                    bitmap.Render(main); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(Path.Combine(directory, $"directory-main-{width}.png"))) { encoder.Save(stream); }
                    await Evidence($"directory-main-{width}.json", new { fixture.Root, fixture.Endpoint.RootId, width, height, dpi = dpi.DpiScaleX,
                        consolePath = console.Directory, openedPath = launches[0].FileName, model.StatusMessage, model.SearchText,
                        semanticMenuInvoke = true, popupShown = false, actualShellStarted = false, physicalKeyboardOrPointer = false });
                }
            }
            finally { main.Closing += (_, args) => args.Cancel = false; main.Close(); model.DetachDashboard(); }
        });
        Assert.Empty(fixture.ObservedErrors);
    }
}
