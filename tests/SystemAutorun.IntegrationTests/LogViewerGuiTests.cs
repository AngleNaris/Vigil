using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Cli;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class LogViewerGuiTests
{
    private const string Stamp = "[2026-11-03 09:42:16 +08:00] ";

    [Fact]
    public async Task RealPipeFiltersPagesAndPreservesRawEventsWithoutChangingConfiguration()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); await Prepare(fixture);
        var before = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            await using var model = new LogViewerViewModel(fixture.Client, fixture.Paths, "媒体库");
            await model.InitializeAsync();
            Assert.Equal("control", model.SelectedSource!.Source.Origin); Assert.False(model.IsFollowing);
            Select(model, "business"); await model.RefreshAsync();
            Assert.Equal(4, model.Entries.Count); Assert.Equal("元数据请求超时", model.Entries[1].Summary);
            Assert.Contains("IOException", model.Entries[2].Raw);
            Assert.Contains("\n  at DemoScanner", model.Entries[2].Raw); Assert.Equal("无法读取媒体目录", model.Entries[2].Summary);
            Assert.Equal(model.Page!.Entries.Select(item => item.Message), model.Entries.Select(item => item.Raw));
            model.SelectedLevel = "error"; Assert.Null(model.Page); Assert.Empty(model.Entries);
            await model.RefreshAsync(); Assert.Single(model.Entries); Assert.Equal("error", model.Entries[0].Level);
            model.SinceText = "2026-11-03 09:00"; await model.RefreshAsync();
            Assert.Null(model.Page); Assert.Contains("时区", model.StatusMessage);
            model.SinceText = "2026-11-03 09:00 +08:00"; await model.RefreshAsync(); Assert.Single(model.Entries);
            model.SelectedLevel = "全部级别"; model.SinceText = ""; model.PageSize = 50;
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "business.log"), string.Concat(Enumerable.Range(0, 120).Select(index => Stamp + $"INFO event-{index}\n")));
            await model.FromStartCommand.ExecuteAsync(null); Assert.Equal(50, model.Entries.Count); Assert.True(model.NextCommand.CanExecute(null));
            Assert.Contains("event-0", model.Entries[0].Raw);
            await model.NextCommand.ExecuteAsync(null); Assert.Equal(50, model.Entries.Count); Assert.Contains("event-50", model.Entries[0].Raw);
            await model.NextCommand.ExecuteAsync(null); Assert.Equal(20, model.Entries.Count); Assert.False(model.NextCommand.CanExecute(null));
            await model.RecentCommand.ExecuteAsync(null); Assert.Equal(50, model.Entries.Count); Assert.Contains("event-70", model.Entries[0].Raw);
        });
        var after = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        Assert.Equal(before.Revision, after.Revision); Assert.False(fixture.IsStopping);
    }

    [Fact]
    public async Task FollowUsesIncrementalCursorAndEmptyPollRetainsTheDisplayedExportPage()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); await Prepare(fixture);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            await using var model = new LogViewerViewModel(fixture.Client, fixture.Paths, "媒体库", TimeSpan.FromMilliseconds(20));
            await model.InitializeAsync(); Select(model, "business"); await model.RefreshAsync();
            var initial = model.Page;
            await model.FollowCommand.ExecuteAsync(null); await Task.Delay(160); await model.PauseAsync();
            Assert.Same(initial, model.Page); Assert.Equal(4, model.Entries.Count); Assert.True(model.CanExport);
            await model.FollowCommand.ExecuteAsync(null);
            await File.AppendAllTextAsync(Path.Combine(fixture.Root, "business.log"), Stamp + "INFO appended-by-owned-test\n");
            await Until(() => model.Entries.Any(item => item.Raw.Contains("appended-by-owned-test")));
            await model.PauseAsync(); Assert.Single(model.Entries); Assert.True(model.FollowTask.IsCompleted);
            var path = Path.Combine(fixture.Root, "follow-page.ndjson"); Assert.True(await model.ExportAsync(path));
            using var exported = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(model.Page!.Entries.Count, exported.RootElement.GetProperty("data").GetProperty("entries").GetArrayLength());
            Assert.Contains("appended-by-owned-test", exported.RootElement.GetProperty("data").GetProperty("entries")[0].GetProperty("message").GetString());
            await model.FollowCommand.ExecuteAsync(null); model.SelectedLevel = "error";
            Assert.False(model.IsFollowing); Assert.Null(model.Page); await model.FollowTask;
            await model.RefreshAsync(); Assert.Single(model.Entries); Assert.Equal("error", model.Entries[0].Level);
        });
        Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2))); Assert.False(fixture.IsStopping);
    }

    [Theory]
    [InlineData("wsl", "provider_unsupported")]
    [InlineData("events", "provider_unsupported")]
    [InlineData("missing", "source_missing")]
    public async Task UnsupportedAndMissingSourcesAreNotEmptySuccessOrImplicitStart(string id, string reason)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); await Prepare(fixture);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            await using var model = new LogViewerViewModel(fixture.Client, fixture.Paths);
            await model.InitializeAsync(); Select(model, id); await model.RefreshAsync();
            Assert.False(model.Page!.Available); Assert.Equal(reason, model.Page.Reason); Assert.Contains(reason, model.StatusMessage);
            Assert.Empty(model.Entries); Assert.False(model.CanExport); Assert.False(model.FollowCommand.CanExecute(null));
        });
        Assert.False(fixture.IsStopping); Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ExportMatchesCliEnvelopeAndNeverOverwritesOrWritesOnPreCancellation()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); await Prepare(fixture);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            await using var model = new LogViewerViewModel(fixture.Client, fixture.Paths, "媒体库");
            await model.InitializeAsync(); Select(model, "business"); await model.RefreshAsync();
            var path = Path.Combine(fixture.Root, "page.ndjson"); Assert.True(await model.ExportAsync(path));
            var original = await File.ReadAllBytesAsync(path);
            Assert.False(await model.ExportAsync(path)); Assert.Equal(original, await File.ReadAllBytesAsync(path));
            var yaml = await File.ReadAllBytesAsync(fixture.Paths.ConfigPath);
            Assert.False(await model.ExportAsync(fixture.Paths.ConfigPath)); Assert.Equal(yaml, await File.ReadAllBytesAsync(fixture.Paths.ConfigPath));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var absent = Path.Combine(fixture.Root, "cancelled.ndjson"); Assert.False(await model.ExportAsync(absent, cancelled.Token)); Assert.False(File.Exists(absent));
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.Equal(0, await new CliApplication().RunAsync(["logs", "--source", "business", "--root", fixture.Root, "--json"], TextReader.Null, output, error));
            using var gui = JsonDocument.Parse(original); using var cli = JsonDocument.Parse(output.ToString());
            Assert.Equal(cli.RootElement.EnumerateObject().Select(item => item.Name), gui.RootElement.EnumerateObject().Select(item => item.Name));
            Assert.Equal(cli.RootElement.GetProperty("rootId").GetString(), gui.RootElement.GetProperty("rootId").GetString());
            Assert.Equal(cli.RootElement.GetProperty("data").GetProperty("entries").GetRawText(), gui.RootElement.GetProperty("data").GetProperty("entries").GetRawText());
        });
    }

    [Fact]
    public async Task ApprovedWindowRendersDesktopMatrixAndOwnsItsReadAndCloseLifecycle()
    {
        var probe = Environment.GetEnvironmentVariable("SA_LOG_GUI_PROBE");
        if (probe is not null)
        {
            probe = Path.GetFullPath(probe);
            if (Path.GetDirectoryName(probe) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(probe).StartsWith("SystemAutorun-logs-probe-", StringComparison.Ordinal)
                || !Directory.Exists(probe) || new DirectoryInfo(probe).Attributes.HasFlag(FileAttributes.ReparsePoint))
            { throw new InvalidOperationException("Expected an ordinary owned logs probe directory."); }
        }
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); await Prepare(fixture);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is not SystemAutorun.App.App) { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
            WindowTheme.Install();
            await using var model = new LogViewerViewModel(fixture.Client, fixture.Paths, "媒体库");
            var window = new LogViewerWindow(model) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = probe is null ? -32000 : 80, Top = probe is null ? -32000 : 70 };
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await window.InitializationTask;
                Assert.Equal("媒体库", window.TaskBox.SelectedItem); Assert.Equal("媒体库", window.TaskBox.SelectionBoxItem);
                Assert.Equal(2, window.SourceBox.Items.Count);
                window.SourceBox.SelectedItem = model.Sources.Single(item => item.Source.Id == "business");
                await model.RefreshCommand.ExecuteAsync(null); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal("媒体库", window.TaskBox.SelectedItem); Assert.Equal("媒体库", window.TaskBox.SelectionBoxItem);
                Assert.Contains(Descendants(window.TaskBox).OfType<TextBlock>(), text => text.IsVisible && text.Text == "媒体库");
                Assert.Equal("business", Assert.IsType<LogSourceChoice>(window.SourceBox.SelectedItem).Source.Id);
                window.EventList.SelectedIndex = 2;
                Assert.Equal(800, window.Width); Assert.Equal(500, window.Height); Assert.Equal(760, window.MinWidth);
                foreach (var viewport in new[] { (Width: 760, Height: 460), (Width: 800, Height: 500), (Width: 1120, Height: 700), (Width: 1320, Height: 820), (Width: 1620, Height: 651) })
                {
                    window.Width = viewport.Width; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    window.Height = viewport.Height; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                    Assert.Equal(460, window.MinHeight); Assert.False(window.ShowInTaskbar);
                    Assert.True(window.EventList.ActualHeight >= 48); // At least one complete compact event row; longer raw text scrolls.
                    if (viewport.Height >= 650) { Assert.True(window.EventList.ActualHeight > 200); }
                    foreach (var button in Descendants(window).OfType<Button>().Where(button => button.IsVisible))
                    {
                        Assert.Null(button.FocusVisualStyle);
                        var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                        Assert.True(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1, $"Clipped {AutomationProperties.GetName(button)}: {bounds}");
                    }
                    Assert.Contains(Descendants(window).OfType<TextBox>(), box => box.IsReadOnly && box.Text.Contains("IOException"));
                    var capture = probe ?? Environment.GetEnvironmentVariable("SA_LOG_GUI_EVIDENCE");
                    if (capture is not null) { Capture(window, Path.Combine(capture, $"logs-{viewport.Width}x{viewport.Height}.png")); }
                }
                Assert.Same(model.FollowCommand, window.FollowButton.Command); Assert.Same(model.FromStartCommand, window.FromStartButton.Command);
                if (probe is not null)
                {
                    window.Width = 1320; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.Height = 820;
                    await File.WriteAllTextAsync(Path.Combine(probe, "ready.txt"), $"Owned WPF logs probe\nPID={Environment.ProcessId}\nRoot={fixture.Root}\nDPI={VisualTreeHelper.GetDpi(window).DpiScaleX}\nLG-F1 synthetic log file; real pipe/reader; NeverRunExecutor\n");
                    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    window.Closed += (_, _) => closed.TrySetResult(); await closed.Task.WaitAsync(TimeSpan.FromMinutes(15));
                }
                else { window.BackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                await window.ShutdownTask; Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2)));
            }
            finally { window.Close(); await window.ShutdownTask; }
        }, probe is null ? null : TimeSpan.FromMinutes(17));
        Assert.False(fixture.IsStopping);
    }

    [Fact]
    public async Task MainWindowRemovesLogViewerEntriesAndPreservesOpenLogsDirectoryAndExceptionTab()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); await Prepare(fixture);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is not SystemAutorun.App.App) { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
            WindowTheme.Install();
            var model = new MainWindowViewModel(fixture.Client, new NativeCacheStore(fixture.Paths.GuiCachePath),
                new AgentConfigurationClient(fixture.Client), fixture.Paths, new NeverRemoteReader()) { AutoRefresh = false };
            var main = new SystemAutorun.App.MainWindow(model, new AgentStartupClient(fixture.Client), new StartupCacheStore(fixture.Paths.StartupCachePath))
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            try
            {
                main.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Null(main.FindName("ServiceLogsButton"));
                Assert.True(main.ExceptionLogsTab.IsVisible);
                var menu = main.SettingsButton.ContextMenu!;
                menu.PlacementTarget = main.SettingsButton;
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                var settingsItems = menu.Items.OfType<MenuItem>().ToArray();
                Assert.DoesNotContain(settingsItems, item => Equals(item.Header, "日志工具") || Equals(item.Header, "日志查看器"));
                var openDir = Assert.Single(settingsItems, item => Equals(item.Header, "打开日志目录"));
                Assert.Same(model.OpenLogsCommand, openDir.Command);
                var serviceMenuItems = main.ServiceList.ContextMenu!.Items.OfType<MenuItem>().ToArray();
                Assert.DoesNotContain(serviceMenuItems, item => Equals(item.Header, "服务日志"));
            }
            finally
            {
                var method = typeof(SystemAutorun.App.MainWindow).GetMethod("Window_Closing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                main.Closing -= (System.ComponentModel.CancelEventHandler)method.CreateDelegate(typeof(System.ComponentModel.CancelEventHandler), main);
                main.Close(); model.DetachDashboard();
            }
        });
        Assert.False(fixture.IsStopping);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ModalLogViewerFollowThenReturnsCleanly(bool taskScoped, bool escape)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); await Prepare(fixture);
        var before = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is not SystemAutorun.App.App) { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
            WindowTheme.Install();
            var owner = new Window { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, Width = 1320, Height = 820 };
            await using var logs = new LogViewerViewModel(fixture.Client, fixture.Paths, taskScoped ? "媒体库" : null);
            var child = new LogViewerWindow(logs) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            var closed = 0;
            try
            {
                owner.Show();
                child.Owner = owner;
                child.Closed += (_, _) => ++closed;
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
                {
                    try
                    {
                        await child.InitializationTask;
                        Assert.Equal(taskScoped ? "媒体库" : LogViewerViewModel.GlobalSources, logs.SelectedTask);
                        Assert.Equal("control", logs.SelectedSource!.Source.Origin); Assert.False(logs.IsFollowing);
                        Assert.Equal(taskScoped ? "媒体库" : null, logs.SelectedSource.Source.TaskName);
                        child.SourceBox.SelectedItem = logs.Sources.Single(item => item.Source.Id == "business");
                        await logs.RefreshCommand.ExecuteAsync(null); Assert.Equal(4, logs.Entries.Count);
                        Assert.Same(logs.FollowCommand, child.FollowButton.Command); await logs.FollowCommand.ExecuteAsync(null);
                        var marker = $"owned-journey-{(taskScoped ? "service" : "global")}-{(escape ? "escape" : "back")}";
                        await File.AppendAllTextAsync(Path.Combine(fixture.Root, "business.log"), Stamp + "INFO " + marker + "\n");
                        await Until(() => logs.Entries.Any(item => item.Raw.Contains(marker, StringComparison.Ordinal)));
                        Assert.True(logs.IsFollowing); Assert.False(logs.FollowTask.IsCompleted);
                        if (escape)
                        {
                            var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                                PresentationSource.FromVisual(child)!, Environment.TickCount, System.Windows.Input.Key.Escape)
                            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                            child.RaiseEvent(key); Assert.True(key.Handled);
                        }
                        else { child.BackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                        await child.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(3)); completed.TrySetResult();
                    }
                    catch (Exception error) { completed.TrySetException(error); child.Close(); }
                }));
                _ = child.ShowDialog();
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(1, closed); Assert.False(child.IsVisible);
                Assert.True(child.ShutdownTask.IsCompletedSuccessfully); Assert.True(logs.FollowTask.IsCompletedSuccessfully);
                Assert.False(logs.IsFollowing); Assert.False(logs.IsBusy); Assert.False(logs.CanExport);
                Assert.True(owner.IsVisible); Assert.False(owner.Dispatcher.HasShutdownStarted);
            }
            finally
            {
                if (!child.ClosureTask.IsCompleted) { child.Close(); }
                await child.ShutdownTask;
                await logs.DisposeAsync();
                owner.Close();
            }
        });
        Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2))); Assert.False(fixture.IsStopping);
        var after = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration"); Assert.Equal(before.Revision, after.Revision);
    }

    [Fact]
    public async Task UnknownTimestampTruncationAndLossyUtf8RemainVisibleRatherThanFabricated()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); await Prepare(fixture);
        var bytes = System.Text.Encoding.UTF8.GetBytes("undated event\n" + Stamp + "INFO " + new string('x', 5000) + "\n" + Stamp + "ERROR damaged ");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "business.log"), [.. bytes, 0xff, 10]);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            await using var model = new LogViewerViewModel(fixture.Client, fixture.Paths, "媒体库");
            await model.InitializeAsync(); Select(model, "business"); await model.RefreshAsync();
            Assert.Contains(model.Entries, row => row.Time == "未知时间");
            Assert.Contains(model.Entries, row => row.Entry.Truncated); Assert.Contains(model.Entries, row => row.Entry.LossyUtf8);
            Assert.Contains("截断", model.StatusMessage); Assert.Contains("UTF-8", model.StatusMessage);
            model.SinceText = "2026-11-03 09:00 +08:00"; await model.RefreshAsync();
            Assert.True(model.Page!.UnknownTimestampExcluded > 0); Assert.Contains("排除未知时间", model.StatusMessage);
            Assert.DoesNotContain(model.Entries, row => row.Entry.Timestamp is null);
        });
    }

    [Fact]
    public async Task TruncationStopsFollowAndRequiresAnExplicitNewReadOrigin()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); await Prepare(fixture);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            await using var model = new LogViewerViewModel(fixture.Client, fixture.Paths, "媒体库", TimeSpan.FromMilliseconds(20));
            await model.InitializeAsync(); Select(model, "business"); await model.RefreshAsync();
            await model.FollowCommand.ExecuteAsync(null);
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "business.log"), Stamp + "INFO replaced\n");
            await Until(() => !model.IsFollowing); await model.FollowTask;
            Assert.False(model.Page!.Available); Assert.Contains("source_truncated_or_replaced", model.StatusMessage);
            Assert.False(model.FollowCommand.CanExecute(null)); Assert.False(model.CanExport);
            await model.RecentCommand.ExecuteAsync(null); Assert.True(model.Page!.Available); Assert.Contains("replaced", Assert.Single(model.Entries).Raw);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerClosingStopsOwnedFollowWithoutStoppingTheAgent(bool modal)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(); await Prepare(fixture);
        var before = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is not SystemAutorun.App.App) { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
            WindowTheme.Install();
            await using var model = new LogViewerViewModel(fixture.Client, fixture.Paths, "媒体库");
            var owner = new Window { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, Width = 400, Height = 300 };
            var window = new LogViewerWindow(model) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, Width = 1320, Height = 820 };
            var closing = 0; var closed = 0;
            var evidence = Environment.GetEnvironmentVariable("SA_LOG_LIFETIME_EVIDENCE"); var trace = new List<string>();
            void Mark(string state)
            {
                trace.Add($"{DateTimeOffset.UtcNow:O} {state}");
                if (evidence is not null) { File.WriteAllLines(Path.Combine(evidence, $"lifetime-{(modal ? "modal" : "modeless")}.txt"), trace); }
            }
            window.Loaded += (_, _) => Mark("loaded");
            window.Closing += (_, _) => { ++closing; Mark("closing"); };
            window.Closed += (_, _) => { ++closed; Mark("closed"); };
            async Task CloseOwnerAsync()
            {
                Mark("read-initialization"); await window.InitializationTask; Mark("initialized");
                Select(model, "business"); await model.RefreshAsync(); Mark("business-read");
                await model.FollowCommand.ExecuteAsync(null);
                Assert.True(model.IsFollowing); Assert.False(model.FollowTask.IsCompleted); Mark("following");
                if (evidence is not null) { Capture(window, Path.Combine(evidence, $"following-{(modal ? "modal" : "modeless")}.png")); }
                Mark("owner-close"); owner.Close(); Mark("owner-closed"); await window.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(3)); Mark("shutdown-complete");
                Assert.False(window.IsVisible); Assert.Equal(0, closing); Assert.Equal(1, closed);
                Assert.True(model.FollowTask.IsCompleted); Assert.False(model.IsFollowing); Assert.False(model.CanExport);
                var page = model.Page; await model.RefreshAsync(); Assert.Same(page, model.Page); Mark("disposed-model-read-is-noop");
            }
            try
            {
                owner.Show(); window.Owner = owner;
                if (modal)
                {
                    var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _ = owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
                    {
                        try { await CloseOwnerAsync(); completed.TrySetResult(); }
                        catch (Exception error)
                        {
                            Mark("failure: " + error);
                            completed.TrySetException(error);
                            window.Close();
                        }
                        finally
                        {
                            // WPF #6757 leaves the modal frame alive after owner WM_DESTROY.
                            // Assert owned-resource cleanup first; only then tear down this test STA.
                            // This is not evidence that modal return or native focus restoration works.
                            Mark("test-sta-teardown-after-resource-assertions");
                            owner.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                        }
                    }));
                    Mark("show-dialog"); _ = window.ShowDialog(); Mark("dialog-returned"); await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
                else
                {
                    window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await CloseOwnerAsync();
                }
            }
            finally
            {
                Mark("teardown-start"); window.Close(); await window.ShutdownTask; Mark("teardown-window-stopped");
                owner.Close(); await model.DisposeAsync(); Mark("teardown-model-stopped");
            }
        }, TimeSpan.FromSeconds(15));
        Assert.True(await fixture.Client.PingAsync(TimeSpan.FromSeconds(2))); Assert.False(fixture.IsStopping);
        var after = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        Assert.Equal(before.Revision, after.Revision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ClosingDuringOfflineConnectWaitsForOwnedReadWithoutStartingAnAgent(int closeMode)
    {
        var root = Directory.CreateTempSubdirectory("SystemAutorun-logs-offline-").FullName;
        var config = Path.Combine(root, "autorun.yaml");
        const string original = "tasks: []\n";
        File.WriteAllText(config, original, new System.Text.UTF8Encoding(false));
        try
        {
            await GuiConfigurationControlTests.RunStaAsync(async () =>
            {
                if (Application.Current is not SystemAutorun.App.App) { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
                WindowTheme.Install();
                await using var model = new LogViewerViewModel(new AgentClient(root), new SystemAutorun.Core.Infrastructure.ProjectPaths(root));
                var owner = new Window { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, Width = 400, Height = 300 };
                var window = new LogViewerWindow(model) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
                var closed = 0; window.Closed += (_, _) => ++closed;
                try
                {
                    owner.Show(); window.Owner = owner;
                    window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.True(model.IsBusy); Assert.False(window.InitializationTask.IsCompleted); Assert.Null(model.Page);
                    if (closeMode != 1) { window.Close(); }
                    if (closeMode != 0) { owner.Close(); }
                    await window.ShutdownTask.WaitAsync(TimeSpan.FromSeconds(3));
                    Assert.True(window.InitializationTask.IsCompleted); Assert.False(window.IsVisible); Assert.False(model.CanExport);
                    Assert.Equal(1, closed); var read = model.RefreshAsync(); Assert.True(read.IsCompletedSuccessfully);
                    Assert.False(model.IsBusy); Assert.Equal(original, File.ReadAllText(config));
                    Assert.Equal([config], Directory.EnumerateFileSystemEntries(root).ToArray());
                }
                finally { window.Close(); await window.ShutdownTask; owner.Close(); }
            });
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(resolved).StartsWith("SystemAutorun-logs-offline-", StringComparison.Ordinal)
                || new DirectoryInfo(resolved).Attributes.HasFlag(FileAttributes.ReparsePoint))
            { throw new InvalidOperationException("Refusing non-owned offline fixture cleanup."); }
            Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class NeverRemoteReader : SystemAutorun.Core.Services.IRemoteHostReader
    {
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Log window tests never connect to remote hosts.");
    }

    private static void Select(LogViewerViewModel model, string id) => model.SelectedSource = model.Sources.Single(item => item.Source.Id == id);
    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) { await Task.Delay(20, deadline.Token); }
    }
    private static async Task Prepare(ConfigurationControlPlaneTests.ServerFixture fixture)
    {
        var snapshot = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var added = await fixture.Client.SendAsync<WorkspaceMutation<ConfigurationMutationResult>>("task-add", payload: new AddTaskRequest("媒体库", AutorunTaskType.WslBoot, Distro: "never-launch-for-logs", HealthType: HealthCheckType.WslRunning), expectedRevision: snapshot.Revision);
        await fixture.Client.SendAsync<WorkspaceMutation<ApplicationSettings>>("settings-set", payload: new ApplicationSettings { LogSources =
        [new() { Id = "business", TaskName = "媒体库", Path = "business.log" }, new() { Id = "missing", Path = "missing.log" }, new() { Id = "wsl", Kind = "wsl-journal" }, new() { Id = "events", Kind = "windows-event" }] }, expectedRevision: added.Revision);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "business.log"), "[2026-11-03 09:42:11 +08:00] INFO 扫描开始\n[2026-11-03 09:42:14 +08:00] WARN 元数据请求超时\n" + Stamp + "ERROR 无法读取媒体目录\n  System.IO.IOException: synthetic D:\\DemoMedia\n  at DemoScanner.Read()\n[2026-11-03 09:42:20 +08:00] INFO 等待下次扫描\n");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var descendant in Descendants(child)) { yield return descendant; } }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(path); encoder.Save(output);
    }
}
