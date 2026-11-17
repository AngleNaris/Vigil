using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class MonitoringWindowTests
{
    [Fact]
    public Task MainWindowStopsItsVisualClockWhenHiddenOrMinimizedAndRestartsOnReturn() =>
        WithWindow(MainWindowViewModel.OverviewPage, async (model, main, _) =>
        {
            var clock = Assert.IsType<DispatcherTimer>(typeof(SystemAutorun.App.MainWindow)
                .GetField("_dashboardClock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main));
            Assert.True(model.IsPresentationActive); Assert.True(clock.IsEnabled);
            main.Hide(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(model.IsPresentationActive); Assert.False(clock.IsEnabled);
            main.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(model.IsPresentationActive); Assert.True(clock.IsEnabled);
            main.WindowState = WindowState.Minimized; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(model.IsPresentationActive); Assert.False(clock.IsEnabled);
            main.WindowState = WindowState.Normal; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(model.IsPresentationActive); Assert.True(clock.IsEnabled);
            Assert.False(model.AutoRefresh); Assert.False(model.IsRefreshing);
        });

    [Fact]
    public Task ReturningWhileAnOperationIsBusyRefreshesAsSoonAsItFinishes() =>
        WithWindow(MainWindowViewModel.OverviewPage, async (model, main, _) =>
        {
            model.AutoRefresh = true; model.IsBusy = true;
            main.Hide(); main.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(model.IsRefreshing); Assert.Null(model.RefreshCommand.ExecutionTask);
            model.IsBusy = false;
            var read = model.RefreshCommand.ExecutionTask;
            Assert.NotNull(read); await read.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(model.IsRefreshing); Assert.True(model.IsPresentationActive);
            model.AutoRefresh = false;
        });

    [Fact]
    public Task CompactToolsStaySmallerThanTheirMainOwnerAndExpandedFieldsScroll() =>
        WithWindow(MainWindowViewModel.OverviewPage, async (_, main, _) =>
        {
            Assert.Equal(1152, main.ActualWidth); Assert.Equal(648, main.ActualHeight);
            var add = main.CreateAddProgramWindow();
            add.ShowActivated = false; add.WindowStartupLocation = WindowStartupLocation.Manual; add.Left = -32000; add.Top = -32000;
            ColorStateAssist.SetReduceMotion(add, true);
            var doctor = new DoctorWindow(new DoctorReport([new DoctorCheck("合成尺寸检查", true, "隔离夹具")]))
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            ColorStateAssist.SetReduceMotion(doctor, true);
            try
            {
                add.Show(); doctor.Owner = add; doctor.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(520, add.ActualWidth);
                var advanced = Descendants(add).OfType<Expander>().Single(); advanced.IsExpanded = true;
                foreach (var (width, height) in new[] { (960, 620), (920, 580), (1320, 820), (1620, 651) })
                {
                    main.Width = width; main.Height = height; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    main.UpdateLayout(); add.UpdateLayout(); doctor.UpdateLayout();
                    var scroll = Descendants(add).OfType<ScrollViewer>().First(view => view.Content is StackPanel);
                    foreach (var tool in new Window[] { add, doctor })
                    {
                        Assert.InRange(tool.ActualWidth, 1, main.ActualWidth * .88 + 1);
                        Assert.InRange(tool.ActualHeight, 1, main.ActualHeight * .84 + 1);
                    }
                    StartupGuiWindowTests.CaptureStateEvidence(add, $"r107-add-expanded-owner-{width}x{height}.png");
                    Assert.True(scroll.ViewportHeight > 0, $"viewport={scroll.ViewportHeight}, extent={scroll.ExtentHeight}");
                    Assert.True(scroll.ScrollableHeight > 0, $"expanded={advanced.IsExpanded}, viewport={scroll.ViewportHeight}, extent={scroll.ExtentHeight}, height={add.ActualHeight}");
                    var save = Descendants(add).OfType<Button>().Single(button => button.IsDefault);
                    var bottom = save.TransformToAncestor(add).TransformBounds(new Rect(save.RenderSize)).Bottom;
                    Assert.InRange(bottom, 1, add.ActualHeight);
                }
                doctor.WindowState = WindowState.Maximized; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(WindowState.Normal, doctor.WindowState);
            }
            finally { doctor.Close(); add.Close(); }
        });

    [Fact]
    public Task OrdinaryAddWindowCommitsBeforeItCanClose() =>
        WithWindow(MainWindowViewModel.OverviewPage, (model, main, _) =>
        {
            var dialog = main.CreateAddProgramWindow();
            try
            {
                var save = typeof(AddProgramWindow).GetField("_save", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var callback = Assert.IsType<Func<SystemAutorun.Core.Configuration.AddTaskRequest, Task<bool>>>(save.GetValue(dialog));
                Assert.Same(model, callback.Target); Assert.Equal(nameof(MainWindowViewModel.AddTaskAsync), callback.Method.Name);
                Assert.Same(main, dialog.Owner);
            }
            finally { dialog.Close(); }
            return Task.CompletedTask;
        });

    [Fact]
    public Task HostTabsUseSelectionColorsAndOnlyNamePlusLocalBadge() =>
        WithWindow(MainWindowViewModel.OverviewPage, async (model, main, window) =>
        {
            var host = Assert.Single(model.Hosts);
            var row = Assert.IsType<ListBoxItem>(window.HostList.ItemContainerGenerator.ContainerFromItem(host));
            var surface = Assert.IsType<Border>(row.Template.FindName("TabSurface", row));
            var name = Descendants(row).OfType<TextBlock>().Single(text => text.Text == host.Name);
            var local = Descendants(row).OfType<TextBlock>().Single(text => text.Text == "本机");
            Assert.True(local.IsVisible);
            Assert.Equal(2, Descendants(row).OfType<TextBlock>().Count());
            var hostResourceIcon = Assert.Single(Descendants(row).OfType<LineIcon>(), icon => AutomationProperties.GetName(icon) == "主机资源高占用注意");
            Assert.Equal("Warning", hostResourceIcon.Kind);
            Assert.False(hostResourceIcon.IsVisible);
            Assert.Null(window.HostList.FocusVisualStyle); Assert.Null(row.FocusVisualStyle);
            Assert.Null(row.Template.FindName("SelectionBar", row));
            Assert.Equal(ColorFocusRole.Row, ColorStateAssist.GetRole(row));
            Assert.Equal(new Thickness(0, 0, 0, 1), surface.BorderThickness);
            var selectedTrigger = row.Template.Triggers.OfType<Trigger>().Single(trigger => trigger.Property == ListBoxItem.IsSelectedProperty);
            var hoverTrigger = row.Template.Triggers.OfType<Trigger>().Single(trigger => trigger.Property == UIElement.IsMouseOverProperty);
            Assert.True(row.Template.Triggers.IndexOf(selectedTrigger) > row.Template.Triggers.IndexOf(hoverTrigger));
            Assert.Equal(Color(window, "InteractionOnActive"), Brush(name.Foreground).Color);
            Assert.Same(row.Foreground, name.Foreground);
            var geometry = (row.RenderSize, surface.BorderThickness, name.FontSize, name.FontWeight, local.RenderSize);
            host.ApplyLocalStatus("FAIL", "合成状态样例；未读取生产观测。", null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(host.StatusDetail, AutomationProperties.GetHelpText(row));
            Assert.False(hostResourceIcon.IsVisible);
            // Block refresh while changing the actual bound selection; no remote/native probe is allowed.
            model.IsBusy = true;
            model.SelectedHostTab = null; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(row.IsSelected); Assert.Equal(Color(window, "RowRest"), Brush(name.Foreground).Color);
            Assert.Equal(Color(window, "TextHint"), Brush(local.Foreground).Color);
            Assert.Equal("FAIL", host.Status);
            model.SelectedHostTab = host; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(row.IsSelected); Assert.Equal(Color(window, "InteractionOnActive"), Brush(name.Foreground).Color);
            // Keyboard focus never dims the selected name; it brightens the existing row divider.
            Assert.True(row.Focus()); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(Color(window, "InteractionOnActive"), Brush(name.Foreground).Color);
            Assert.Equal(Color(window, "RowFocusBorder"), Brush(surface.BorderBrush).Color);
            model.IsBusy = false;
            Assert.Equal(geometry, (row.RenderSize, surface.BorderThickness, name.FontSize, name.FontWeight, local.RenderSize));
            Assert.Equal(Color(window, "InteractionOnActive"), Brush(local.Foreground).Color);
            Assert.Equal(Color(window, "InteractionActive"), Brush(surface.Background).Color);
            await DesktopMatrix(main, "monitoring-overview");
        });

    [Theory]
    [InlineData(920, 580, false)] [InlineData(1060, 660, false)] [InlineData(1320, 820, false)] [InlineData(1620, 651, false)]
    [InlineData(920, 580, true)] [InlineData(1060, 660, true)] [InlineData(1320, 820, true)] [InlineData(1620, 651, true)]
    public Task OverviewIdentityRemainsInsideItsHeaderAndOutOfTheSummary(int width, int height, bool longText) =>
        WithWindow(MainWindowViewModel.OverviewPage, async (model, main, window) =>
        {
            model.HostName = longText ? string.Concat(Enumerable.Repeat("合成长主机身份-", 30)) : "合成主机";
            model.HostOs = longText ? string.Concat(Enumerable.Repeat("合成系统版本元数据 ", 25)) : "合成 Windows 系统版本";
            model.HostConnectionText = longText ? "合成连接 ssh://" + new string('h', 240) + ":2222" : "合成本机 Named Pipe Agent";
            main.Width = width; main.Height = height; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
            var name = BoundText(window, nameof(model.HostName)); var os = BoundText(window, nameof(model.HostOs));
            var header = Ancestors(name).OfType<Border>().First(); var grid = Assert.IsType<Grid>(header.Child);
            Assert.DoesNotContain(Descendants(header).OfType<TextBlock>(),
                tb => BindingOperations.GetBinding(tb, TextBlock.TextProperty)?.Path?.Path == nameof(model.HostConnectionText));
            var summary = grid.Children.OfType<StackPanel>().Single(panel => Grid.GetColumn(panel) == 1);
            var status = Ancestors(BoundText(window, nameof(model.ServerStatus))).OfType<Border>().First();
            var dpi = VisualTreeHelper.GetDpi(main); var epsilon = 1 / dpi.DpiScaleY;
            Console.WriteLine($"HEADER width={width} height={height} long={longText} WpfDpi={dpi.PixelsPerInchX},{dpi.PixelsPerInchY} NativeDpi={GetDpiForWindow(new WindowInteropHelper(main).Handle)}");
            StartupGuiWindowTests.CaptureStateEvidence(main, $"monitoring-header-{width}x{height}-{(longText ? "long" : "normal")}.png");
            foreach (var text in new[] { name, os })
            {
                var box = text.TransformToAncestor(header).TransformBounds(new Rect(text.RenderSize));
                Assert.True(box.Top >= header.Padding.Top - epsilon, $"{text.Text}: top={box.Top}; padding={header.Padding.Top}");
                Assert.True(box.Bottom <= header.ActualHeight - header.Padding.Bottom - header.BorderThickness.Bottom + epsilon,
                    $"{text.Text}: bottom={box.Bottom}; content bottom={header.ActualHeight - header.Padding.Bottom - header.BorderThickness.Bottom}");
                Assert.True(box.Right <= summary.TransformToAncestor(header).Transform(new Point()).X + epsilon, "Identity must not overlap summary metrics.");
                Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming); Assert.Equal(text.Text, text.ToolTip);
            }
            var nameRight = name.TransformToAncestor(header).TransformBounds(new Rect(name.RenderSize)).Right;
            var badge = status.TransformToAncestor(header).TransformBounds(new Rect(status.RenderSize));
            Assert.True(nameRight <= badge.Left + epsilon); Assert.True(status.IsVisible); Assert.True(badge.Right <= summary.TransformToAncestor(header).Transform(new Point()).X + epsilon);
            Assert.False(model.IsBusy);
        });

    [Fact]
    public Task LogSplitterUsesItsExistingCaptionAndPreservesBodyAndKeyboardContract() =>
        WithWindow(MainWindowViewModel.ExceptionLogsPage, async (model, main, window) =>
        {
            var entry = new ExceptionLogRowViewModel(new(
                new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(8)), ExceptionLogLevel.Error,
                "isolated-fixture", "合成样例：后台观测失败；仅用于颜色与布局回归。\n保留完整正文，不执行任何生产操作。", "synthetic.log"));
            model.ExceptionLogs.Add(entry); model.SelectedExceptionLog = entry;
            model.ExceptionLogCount = 1; model.ErrorLogCount = 1;
            model.ExceptionLogStatusText = "隔离测试样例；未读取生产日志。";
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var splitter = window.ExceptionDetailsSplitter;
            Assert.Null(splitter.FocusVisualStyle); Assert.True(splitter.Focusable); Assert.True(splitter.IsTabStop);
            Assert.Equal(ColorFocusRole.Caption, ColorStateAssist.GetRole(splitter));
            Assert.Equal(6, splitter.Width); Assert.Equal(GridResizeBehavior.PreviousAndNext, splitter.ResizeBehavior);
            Assert.Equal(GridResizeDirection.Columns, splitter.ResizeDirection); Assert.True(splitter.ShowsPreview);
            Assert.Equal("调整异常列表与详情宽度", AutomationProperties.GetName(splitter));
            Assert.Equal(Color(window, "BorderSubtle"), Brush(splitter.Background).Color);
            Assert.Same(ColorStateAssist.GetCaptionBrush(splitter), window.ExceptionDetailsCaption.Foreground);
            Assert.Equal(Color(window, "InteractionRest"), Brush(window.ExceptionDetailsCaption.Foreground).Color);
            Assert.True(window.ExceptionDetailsText.IsReadOnly);
            Assert.Equal(entry.Message, window.ExceptionDetailsText.Text);
            Assert.Equal(Color(window, "TextPrimary"), Brush(window.ExceptionDetailsText.Foreground).Color);
            Assert.Equal(Color(window, "BorderSubtle"), Brush(window.ExceptionDetailsText.BorderBrush).Color);
            Assert.Equal(new Thickness(1), window.ExceptionDetailsText.BorderThickness);
            Assert.Equal(ColorFocusRole.Border, ColorStateAssist.GetRole(window.ExceptionDetailsText));
            await DesktopMatrix(main, "monitoring-exceptions");
        });

    [Fact]
    public Task ServiceHostAndExceptionTabsArePeerViewsOfTheMainWindow() =>
        WithWindow(MainWindowViewModel.OverviewPage, async (model, main, window) =>
        {
            // R105: three peer tabs in the main caption replace the separate monitoring window.
            var tabs = new[] { main.ServicesTab, main.HostsTab, main.ExceptionLogsTab };
            Assert.Equal(["服务", "主机", "异常日志"], tabs.Select(tab => (string)tab.Content));
            Assert.Empty(main.OwnedWindows.Cast<Window>());
            foreach (var (tab, page, visible) in new[] { (main.ServicesTab, MainWindowViewModel.WorkloadsPage, false),
                (main.ExceptionLogsTab, MainWindowViewModel.ExceptionLogsPage, true), (main.HostsTab, MainWindowViewModel.OverviewPage, true) })
            {
                tab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(page, model.CurrentPage); Assert.Empty(main.OwnedWindows.Cast<Window>());
                Assert.Equal(visible, window.IsVisible); Assert.Equal(!visible, main.ServiceList.IsVisible);
                foreach (var other in tabs)
                {
                    // Selection is the surface plus brighter text: no indicator bar, underline or outline.
                    var surface = Assert.IsType<Border>(other.Template.FindName("Surface", other));
                    var chosen = ReferenceEquals(other, tab);
                    Assert.Equal(chosen, other.Tag is true); Assert.Null(other.FocusVisualStyle);
                    Assert.Equal(chosen ? Color(main, "InteractionOnActive") : Color(main, "InteractionRest"), Brush(other.Foreground).Color);
                    if (chosen) { Assert.Equal(Color(main, "InteractionActive"), Brush(surface.Background).Color); }
                    Assert.Equal(new Thickness(0), other.BorderThickness);
                }
            }
            // Search and filter belong to the service view only.
            Assert.False(main.SearchBox.IsVisible);
            // The two duplicate settings checkboxes moved to the dashboard settings menu only.
            Assert.Empty(Descendants(window).OfType<CheckBox>());
            // Host status is a square line glyph; no round status dot remains in the host list.
            Assert.Empty(Descendants(window.HostList).OfType<Ellipse>());
            // A tool dialog owned by the main window dims it while open, then the scrim clears.
            Assert.Equal(Visibility.Collapsed, main.DialogScrim.Visibility);
            // A real tool dialog type (all dialogs are Window subclasses with their own XAML).
            var dialog = new DoctorWindow(new DoctorReport([new DoctorCheck("合成样例：遮罩", true, "隔离夹具；未连接生产 Agent。")]))
            { Owner = main, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            dialog.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(Visibility.Visible, main.DialogScrim.Visibility);
            dialog.Close();
            await PresenceMotionTests.Until(() => !dialog.IsVisible && !main.DialogScrim.HasAnimatedProperties);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(Visibility.Collapsed, main.DialogScrim.Visibility);
        });
    [Theory]
    [InlineData(false, 920, 580)] [InlineData(false, 960, 620)] [InlineData(false, 1320, 820)]
    [InlineData(true, 920, 580)] [InlineData(true, 960, 620)] [InlineData(true, 1320, 820)]
    public Task OpaqueBackdropSuspendsThePageUntilTheLastNestedToolHasActuallyExited(bool light, double width, double height) =>
        WithWindow(MainWindowViewModel.OverviewPage, async (model, main, _) =>
        {
            var previous = ThemeService.IsLight ? ThemePreference.Light : ThemePreference.Dark;
            var first = Tool(main); DoctorWindow? nested = null;
            try
            {
                ThemeService.Apply(light ? ThemePreference.Light : ThemePreference.Dark, persist: null);
                main.Width = width; main.Height = height;
                ColorStateAssist.SetReduceMotion(main, false);
                var page = model.CurrentPage;
                first.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.True(main.DialogScrim.IsPresented); Assert.True(main.DialogScrim.IsHitTestVisible);
                await PresenceMotionTests.Until(() => !main.DialogScrim.HasAnimatedProperties);
                Assert.Equal(1, main.DialogScrim.Opacity);
                Assert.Equal(Brush(main.FindResource("Semantic.Brush.StatusHealthy")).Color, Brush(first.SummaryText.Foreground).Color);
                Assert.Equal(Brush(main.FindResource("Semantic.Brush.WindowBackground")).Color, Brush(main.DialogScrim.Background).Color);
                var scene = Assert.IsAssignableFrom<FrameworkElement>(main.FindName("MainScene"));
                Assert.Equal(Visibility.Hidden, scene.Visibility); Assert.True(LineMotion.GetIsSuspended(main));
                Assert.False(LineMotion.IsPlaying(scene));
                main.UpdateLayout();
                await PresenceMotionTests.Until(() => !((SystemAutorun.App.Controls.PresencePanel)first.Content).HasAnimatedProperties);
                StartupGuiWindowTests.CaptureStateEvidence(first, $"doctor-{(light ? "light" : "dark")}-{width}-open.png");
                StartupGuiWindowTests.CaptureStateEvidence(main, $"scrim-{(light ? "light" : "dark")}-{width}-open.png");
                nested = Tool(first); nested.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                nested.Close(); await PresenceMotionTests.Until(() => !nested.IsVisible);
                Assert.True(main.DialogScrim.IsPresented); Assert.Equal(Visibility.Hidden, scene.Visibility);
                first.Hide();
                Assert.False(main.DialogScrim.IsPresented); Assert.False(LineMotion.GetIsSuspended(main));
                await PresenceMotionTests.Until(() => !main.DialogScrim.HasAnimatedProperties);
                Assert.Equal(Visibility.Collapsed, main.DialogScrim.Visibility); Assert.Equal(Visibility.Visible, scene.Visibility);
                first.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await PresenceMotionTests.Until(() => !main.DialogScrim.HasAnimatedProperties);
                Assert.True(main.DialogScrim.IsPresented); Assert.Equal(Visibility.Hidden, scene.Visibility);
                Assert.False(LineMotion.IsPlaying(scene));
                await PresenceMotionTests.Until(() => !((SystemAutorun.App.Controls.PresencePanel)first.Content).HasAnimatedProperties);
                first.Close();
                if (first.IsExitPending)
                {
                    Assert.True(main.DialogScrim.IsPresented); Assert.Equal(Visibility.Hidden, scene.Visibility);
                    var activeOwner = typeof(SystemAutorun.App.MainWindow).GetProperty("DialogOwner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    Assert.Same(main, activeOwner.GetValue(main));
                }
                await PresenceMotionTests.Until(() => !first.IsVisible && !main.DialogScrim.HasAnimatedProperties);
                Assert.False(main.DialogScrim.IsPresented); Assert.False(LineMotion.GetIsSuspended(main));
                Assert.Equal(Visibility.Collapsed, main.DialogScrim.Visibility); Assert.Equal(Visibility.Visible, scene.Visibility);
                Assert.Equal(page, model.CurrentPage);
                StartupGuiWindowTests.CaptureStateEvidence(main, $"scrim-{(light ? "light" : "dark")}-{width}-returned.png");
            }
            finally
            {
                if (nested is { IsVisible: true }) { nested.Close(); }
                if (first.IsLoaded) { first.Close(); }
                ThemeService.Apply(previous, persist: null);
            }

            static DoctorWindow Tool(Window owner) => new(new DoctorReport([new DoctorCheck("合成样例：遮罩", true, "隔离夹具；不运行生产操作。")]))
            { Owner = owner, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        });

    [Fact]
    public Task DoctorModalReturnsToMainWithoutLosingFilters() =>
        WithWindow(MainWindowViewModel.OverviewPage, async (model, main, _) =>
        {
            var probe = Environment.GetEnvironmentVariable("SA_TOOL_MOTION_PROBE");
            model.SearchText = "工具返回保留";
            main.Width = 960; main.Height = 620;
            ColorStateAssist.SetReduceMotion(main, false);
            var doctor = new DoctorWindow(new DoctorReport([new DoctorCheck("合成动效样例", true, "隔离窗口旅程；没有执行系统诊断。")]))
            { Owner = main, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            if (probe is not null)
            {
                Assert.False(string.IsNullOrWhiteSpace(probe));
                main.Left = 80; main.Top = 80; doctor.Left = 160; doctor.Top = 140;
                doctor.Title = "SystemAutorun · 隔离工具动效";
            }
            var interaction = main.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await PresenceMotionTests.Until(() => doctor.IsLoaded);
                    var surface = Assert.IsType<SystemAutorun.App.Controls.PresencePanel>(doctor.Content);
                    await PresenceMotionTests.Until(() => !surface.HasAnimatedProperties && !main.DialogScrim.HasAnimatedProperties);
                    Assert.True(main.DialogScrim.IsPresented);
                    Assert.True(doctor.ActualWidth < main.ActualWidth && doctor.ActualHeight < main.ActualHeight);
                    StartupGuiWindowTests.CaptureStateEvidence(doctor, "doctor-modal-open.png");
                    if (probe is not null)
                    {
                        System.IO.Directory.CreateDirectory(probe);
                        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(probe, "ready.txt"),
                            $"pid={Environment.ProcessId}\nmain={new System.Windows.Interop.WindowInteropHelper(main).Handle}\ndoctor={new System.Windows.Interop.WindowInteropHelper(doctor).Handle}\nwidth={doctor.ActualWidth}\nheight={doctor.ActualHeight}\ndpi={VisualTreeHelper.GetDpi(doctor).PixelsPerInchX}\n");
                        await doctor.ClosureTask.WaitAsync(TimeSpan.FromMinutes(5));
                    }
                    else { SystemCommands.CloseWindow(doctor); await doctor.ClosureTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                }
                finally
                {
                    if (!doctor.ClosureTask.IsCompleted)
                    { doctor.CancelPendingClose(); ColorStateAssist.SetReduceMotion(doctor, true); doctor.Close(); }
                }
            }).Task.Unwrap();
            var result = doctor.ShowDialog();
            await interaction;
            Assert.False(result); Assert.True(doctor.ClosureTask.IsCompletedSuccessfully);
            await PresenceMotionTests.Until(() => !main.DialogScrim.HasAnimatedProperties);
            Assert.Equal("工具返回保留", model.SearchText); Assert.False(main.DialogScrim.IsPresented);
            StartupGuiWindowTests.CaptureStateEvidence(main, "doctor-modal-returned.png");
            if (probe is not null)
            {
                await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(probe, "returned.txt"), "modal=false;closed=true;scrim=false;filter=工具返回保留");
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (!System.IO.File.Exists(System.IO.Path.Combine(probe, "finish.txt")))
                { Assert.True(DateTime.UtcNow < deadline, "Native probe must finish within its bounded observation period."); await Task.Delay(100); }
            }
        }, timeout: Environment.GetEnvironmentVariable("SA_TOOL_MOTION_PROBE") is null ? null : TimeSpan.FromMinutes(7));

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task PageTransitionsKeepTheHostAliveUntilLeavesFinishAndRetireOldInputsImmediately(bool reduced) =>
        WithWindow(MainWindowViewModel.OverviewPage, async (model, main, view) =>
        {
            ColorStateAssist.SetReduceMotion(main, reduced);
            model.SearchText = "保留服务筛选"; model.ExceptionLogSearchText = "保留日志筛选";
            var panels = new[] { main.ServicesPage, view.HostPage, view.ExceptionsPage };
            foreach (var (tab, page, incoming) in new[]
            {
                (main.ServicesTab, MainWindowViewModel.WorkloadsPage, main.ServicesPage),
                (main.HostsTab, MainWindowViewModel.OverviewPage, view.HostPage),
                (main.ExceptionLogsTab, MainWindowViewModel.ExceptionLogsPage, view.ExceptionsPage),
                (main.HostsTab, MainWindowViewModel.OverviewPage, view.HostPage),
                (main.ServicesTab, MainWindowViewModel.WorkloadsPage, main.ServicesPage),
                (main.ExceptionLogsTab, MainWindowViewModel.ExceptionLogsPage, view.ExceptionsPage),
                (main.ServicesTab, MainWindowViewModel.WorkloadsPage, main.ServicesPage),
            })
            {
                var outgoing = panels.Take(3).Single(panel => panel.IsPresented);
                tab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                Assert.Equal(page, model.CurrentPage);
                Assert.False(outgoing.IsHitTestVisible); Assert.False(outgoing.IsEnabled);
                Assert.True(incoming.IsHitTestVisible); Assert.True(incoming.IsEnabled); Assert.True(incoming.IsVisible);
                Assert.False(view.HasAnimatedProperties); Assert.False(SurfaceMotion.GetFadeOnShow(incoming));
                if (!reduced && SystemParameters.ClientAreaAnimation)
                {
                    Assert.True(outgoing.HasAnimatedProperties); Assert.False(incoming.HasAnimatedProperties);
                    Assert.Equal(1, incoming.Opacity); // Only child lines/pulses own the entrance.
                    Assert.True(outgoing.IsVisible);
                    Assert.True(view.IsVisible); // Even a departure to services must not cut off its leaves.
                }
                await PresenceMotionTests.Until(() => panels.All(panel => !panel.HasAnimatedProperties));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(Visibility.Collapsed, outgoing.Visibility); Assert.Equal(1, incoming.Opacity);
                Assert.Equal(0, Assert.IsType<TranslateTransform>(incoming.RenderTransform).Y);
                Assert.Equal(page != MainWindowViewModel.WorkloadsPage, view.IsVisible);
                Assert.Equal("保留服务筛选", model.SearchText); Assert.Equal("保留日志筛选", model.ExceptionLogSearchText);
            }
        });

    [Fact]
    public Task RapidPageReturnAndHiddenMainDoNotReplayClocksOrLoseFilters() =>
        WithWindow(MainWindowViewModel.OverviewPage, async (model, main, view) =>
        {
            ColorStateAssist.SetReduceMotion(main, false);
            var panels = new[] { main.ServicesPage, view.HostPage, view.ExceptionsPage };
            main.ServicesTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            if (SystemParameters.ClientAreaAnimation)
            {
                await PresenceMotionTests.Until(() => view.HostPage.Opacity is > .01 and < .99);
                var opacity = view.HostPage.Opacity; var y = Assert.IsType<TranslateTransform>(view.HostPage.RenderTransform).Y;
                main.HostsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(opacity, view.HostPage.Opacity, 8);
                Assert.Equal(y, Assert.IsType<TranslateTransform>(view.HostPage.RenderTransform).Y, 8);
            }
            else { main.HostsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            await PresenceMotionTests.Until(() => panels.All(panel => !panel.HasAnimatedProperties));
            // A normal data/clock update is not navigation and must not animate again.
            model.StatusMessage = "合成状态更新"; model.TickDashboard();
            Assert.All(panels, panel => Assert.False(panel.HasAnimatedProperties));
            main.ExceptionLogsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            ColorStateAssist.SetReduceMotion(main, true);
            Assert.All(panels, panel => Assert.False(panel.HasAnimatedProperties));
            Assert.Equal(1, view.ExceptionsPage.Opacity); Assert.Equal(Visibility.Collapsed, view.HostPage.Visibility);
            ColorStateAssist.SetReduceMotion(main, false);
            main.ServicesTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); main.Hide();
            Assert.All(panels, panel => Assert.False(panel.HasAnimatedProperties));
            main.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.All(panels, panel => Assert.False(panel.HasAnimatedProperties));
            Assert.True(main.ServicesPage.IsVisible); Assert.False(view.IsVisible);
        });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task PageEntranceGrowsLinesAndFlickersGlyphsThenRestoresThemeValues(bool light) =>
        WithWindow(MainWindowViewModel.OverviewPage, async (model, main, view) =>
        {
            var previous = ThemeService.IsLight ? ThemePreference.Light : ThemePreference.Dark;
            try
            {
                ThemeService.Apply(light ? ThemePreference.Light : ThemePreference.Dark, persist: null);
                typeof(MainWindowViewModel).GetMethod("ApplyHostStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(model, [new HostStatusSnapshot("合成主机", "合成 Windows 系统版本", new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.FromHours(8)),
                        TimeSpan.FromHours(54), 37, 32L << 30, 12L << 30,
                        [new DriveStatusSnapshot(@"C:\", "合成系统盘", 512L << 30, 128L << 30), new DriveStatusSnapshot(@"D:\", "合成媒体盘", 4096L << 30, 3600L << 30)],
                        [new NetworkStatusSnapshot("合成以太网", "192.0.2.24", 9L << 30, 2L << 30)], LogicalProcessorCount: 16, ProcessCount: 214, ThreadCount: 2890)]);
                ColorStateAssist.SetReduceMotion(main, false);
                main.ServicesTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!SystemParameters.ClientAreaAnimation)
                {
                    // R109: with system animation off nothing plays at all.
                    main.HostsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(LineMotion.IsPlaying(view.HostPage)); Assert.False(view.HostHeader.HasAnimatedProperties);
                    return;
                }
                await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(main.ServicesPage) && !main.ServicesPage.HasAnimatedProperties && !view.HostPage.IsVisible);
                var subtle = view.FindResource("Semantic.Brush.BorderSubtle");
                main.HostsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(LineMotion.IsPlaying(view.HostPage));
                // Each orthogonal edge is drawn explicitly; the stationary parent does not dilute it.
                Assert.Equal(1, view.HostPage.Opacity); Assert.False(view.HostPage.HasAnimatedProperties);
                Assert.Equal(0, Assert.IsType<TranslateTransform>(view.HostPage.RenderTransform).Y);
                Assert.True(view.HostHeader.HasAnimatedProperties);
                await PresenceMotionTests.Until(() => view.HostHeader.BorderBrush is DrawingBrush);
                var glyphs = Descendants(view.HostPage).OfType<TextBlock>().Where(text => text.IsVisible).ToArray();
                Assert.Contains(glyphs, text => text.HasAnimatedProperties);
                Assert.True(view.HostPage.IsEnabled && view.HostPage.IsHitTestVisible); // Interactive throughout.
                var theme = light ? "light" : "dark";
                foreach (var (at, index) in new[] { 45, 110, 190 }.Select((at, index) => (at, index)))
                {
                    await Task.Delay(index == 0 ? at : at - new[] { 45, 110, 190 }[index - 1]);
                    StartupGuiWindowTests.CaptureStateEvidence(main, $"r110-entrance-{theme}-{index + 1}.png");
                }
                await PresenceMotionTests.Until(() => !LineMotion.IsPlaying(view.HostPage));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                // R117: semantic regions relay; each solid line settles uniformly before clocks release.
                Assert.Same(subtle, view.HostHeader.BorderBrush); Assert.False(view.HostHeader.HasAnimatedProperties);
                Assert.All(glyphs, text => { Assert.False(text.HasAnimatedProperties); Assert.Equal(1, text.Opacity); });
                Assert.True(LineMotion.SpreadMilliseconds + 40 + LineMotion.GrowMilliseconds + LineMotion.SettleMilliseconds <= 900);
                Assert.True(LineMotion.MaximumDelayMilliseconds + 4 * 45 <= 900);
                StartupGuiWindowTests.CaptureStateEvidence(main, $"r110-entrance-{theme}-4-rest.png");

                // A data update or clock tick is not navigation.
                model.StatusMessage = "合成状态更新"; model.TickDashboard();
                Assert.False(LineMotion.IsPlaying(view.HostPage));
                // Reduced motion switched on mid-way ends it at once; switched on before, it never starts.
                main.ExceptionLogsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(LineMotion.IsPlaying(view.ExceptionsPage));
                ColorStateAssist.SetReduceMotion(main, true);
                Assert.False(LineMotion.IsPlaying(view.ExceptionsPage));
                Assert.All(Descendants(view.ExceptionsPage).OfType<FrameworkElement>(), element => Assert.False(element is Border or TextBlock && element.HasAnimatedProperties));
                main.HostsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(LineMotion.IsPlaying(view.HostPage)); Assert.Same(subtle, view.HostHeader.BorderBrush);
            }
            finally { ThemeService.Apply(previous, persist: null); }
        });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task NumericMetricHeadingsReflowWithoutOverlapOrShrinkingText(bool light) =>
        WithWindow(MainWindowViewModel.OverviewPage, async (model, main, view) =>
        {
            var previous = ThemeService.IsLight ? ThemePreference.Light : ThemePreference.Dark;
            try
            {
                ThemeService.Apply(light ? ThemePreference.Light : ThemePreference.Dark, persist: null);
                var apply = typeof(MainWindowViewModel).GetMethod("ApplyHostStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var snapshot = new HostStatusSnapshot("合成几何样例", "合成 Windows / 几何验证", new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.FromHours(8)),
                    TimeSpan.FromHours(1), 100, 32L << 30, 0,
                    [new DriveStatusSnapshot(@"C:\", "合成满盘", 512L << 30, 0)], [], LogicalProcessorCount: 16, ProcessCount: 12, ThreadCount: 48);
                apply.Invoke(model, [snapshot]);
                var headers = Descendants(view).OfType<MetricHeadingPanel>().Where(header => ReferenceEquals(header.DataContext, model)).ToArray();
                Assert.Equal(4, headers.Length);
                static TextBlock ReadoutText(MetricHeadingPanel header) =>
                    header.Children[1] as TextBlock ?? Descendants(header.Children[1]).OfType<TextBlock>().Single();
                var fontSizes = headers.Select(header => ReadoutText(header).FontSize).ToArray();
                Assert.Equal(new[] { 24d, 24d, 16d, 16d }, fontSizes); // Approved CPU/memory and rate readout roles.
                foreach (var width in new[] { 920, 930, 940, 950, 960, 970, 1000, 1320, 1620 })
                {
                    main.Width = width; main.Height = width == 920 ? 580 : 620;
                    foreach (var rate in new double?[] { null, 0, 999.9 * (1L << 30), 999.9 * (1L << 40) })
                    {
                        apply.Invoke(model, [snapshot with { DiskReadBytesPerSecond = rate, NetworkReceiveBytesPerSecond = rate }]);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
                        foreach (var header in headers)
                        {
                            Assert.Equal(2, header.Children.Count);
                            var caption = Assert.IsAssignableFrom<FrameworkElement>(header.Children[0]);
                            var valueContainer = Assert.IsAssignableFrom<FrameworkElement>(header.Children[1]);
                            var value = ReadoutText(header);
                            var captionBounds = caption.TransformToAncestor(header).TransformBounds(new Rect(new System.Windows.Point(), caption.RenderSize));
                            var valueBounds = valueContainer.TransformToAncestor(header).TransformBounds(new Rect(new System.Windows.Point(), valueContainer.RenderSize));
                            Assert.False(captionBounds.IntersectsWith(valueBounds), $"{width}: {value.Text}: {captionBounds} / {valueBounds}");
                            var epsilon = 1 / VisualTreeHelper.GetDpi(main).DpiScaleX;
                            Assert.InRange(Math.Abs(header.ActualWidth - valueBounds.Right), 0, epsilon);
                            Assert.InRange(captionBounds.Left, 0, epsilon);
                            Assert.True(captionBounds.Bottom <= header.ActualHeight + epsilon);
                            Assert.True(valueBounds.Bottom <= header.ActualHeight + epsilon);
                            Assert.Equal(fontSizes[Array.IndexOf(headers, header)], value.FontSize);
                            Assert.Equal(TextTrimming.None, value.TextTrimming);
                            if (rate is not null)
                            {
                                var fullText = new FormattedText(value.Text, System.Globalization.CultureInfo.CurrentUICulture, value.FlowDirection,
                                    new Typeface(value.FontFamily, value.FontStyle, value.FontWeight, value.FontStretch), value.FontSize,
                                    System.Windows.Media.Brushes.Black, VisualTreeHelper.GetDpi(main).PixelsPerDip);
                                Assert.True(value.ActualWidth + epsilon >= fullText.WidthIncludingTrailingWhitespace, $"{width}: clipped {value.Text}");
                            }
                        }
                        if (rate == 999.9 * (1L << 40) && width is 920 or 960 or 1320)
                        { StartupGuiWindowTests.CaptureStateEvidence(main, $"metric-headings-{(light ? "light" : "dark")}-{width}.png"); }
                    }
                }
            }
            finally { ThemeService.Apply(previous, persist: null); }
        });

    [Fact]
    public Task SharedMetricRecipeKeepsBoolSelectionColorOnly() =>
        WithWindow(MainWindowViewModel.OverviewPage, async (_, _, resources) =>
        {
            // The shared recipe has no current page consumer; this is ENG, not a page VIS claim.
            var metric = new Button { Content = "合成读数", Style = (Style)resources.FindResource("MetricButtonStyle") };
            var window = new Window { Title = "Owned state recipes", Content = metric, Width = 320, Height = 240,
                ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            ColorStateAssist.SetReduceMotion(window, true);
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                Assert.Null(metric.FocusVisualStyle); Assert.Equal(ColorFocusRole.Foreground, ColorStateAssist.GetRole(metric));
                Assert.Null(metric.Template.FindName("ActiveBar", metric)); Assert.Null(metric.Template.FindName("SelectionBar", metric));
                var selectedTrigger = metric.Template.Triggers.OfType<Trigger>().Single(trigger => trigger.Property == FrameworkElement.TagProperty);
                var hoverTrigger = metric.Template.Triggers.OfType<Trigger>().Single(trigger => trigger.Property == UIElement.IsMouseOverProperty);
                Assert.True(Assert.IsType<bool>(selectedTrigger.Value));
                Assert.True(metric.Template.Triggers.IndexOf(selectedTrigger) > metric.Template.Triggers.IndexOf(hoverTrigger));
                var geometry = (metric.RenderSize, metric.Padding, metric.BorderThickness, metric.FontSize, metric.FontWeight);
                Assert.Equal(Color(resources, "InteractionRest"), Brush(metric.Foreground).Color);
                metric.Tag = true; await Dispatcher.Yield(DispatcherPriority.DataBind);
                Assert.Equal(Color(resources, "InteractionOnActive"), Brush(metric.Foreground).Color);
                Assert.Equal(Color(resources, "InteractionActive"), Brush(Assert.IsType<Border>(metric.Template.FindName("CellBorder", metric)).Background).Color);
                metric.Tag = false; await Dispatcher.Yield(DispatcherPriority.DataBind);
                Assert.Equal(Color(resources, "InteractionRest"), Brush(metric.Foreground).Color);
                Assert.Equal(geometry, (metric.RenderSize, metric.Padding, metric.BorderThickness, metric.FontSize, metric.FontWeight));
                Assert.Equal(new Thickness(1, 0, 0, 0), metric.BorderThickness);
            }
            finally { window.Close(); }
        });
    [Fact]
    public Task PopulatedOverviewDrawsTruthfulMetersAndTabsInReadingOrder() =>
        WithWindow(MainWindowViewModel.OverviewPage, async (model, main, window) =>
        {
            // Synthetic host snapshot only; no Agent sampling, remote probe or production metrics.
            var at = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(8));
            var snapshot = new HostStatusSnapshot("合成主机", "合成 Windows 系统版本", at, TimeSpan.FromHours(54), 37,
                32L << 30, 12L << 30,
                [new DriveStatusSnapshot(@"C:\", "合成系统盘", 512L << 30, 128L << 30), new DriveStatusSnapshot(@"D:\", "合成媒体盘", 4096L << 30, 3600L << 30)],
                [new NetworkStatusSnapshot("合成以太网", "192.0.2.24", 9L << 30, 2L << 30), new NetworkStatusSnapshot("合成虚拟交换机", "198.51.100.7", 1L << 20, 1L << 19)],
                LogicalProcessorCount: 16, ProcessCount: 214, ThreadCount: 2890);
            typeof(MainWindowViewModel).GetMethod("ApplyHostStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(model, [snapshot]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Assert.Equal("37%", model.CpuUsageText);
            var meters = Descendants(window).OfType<ProgressBar>().Where(bar => bar.IsVisible && bar.Style == window.FindResource("HostMeterStyle")).ToArray();
            Assert.Equal(2, meters.Length); // R121: only two volume-capacity meters, never a fake disk-IO meter.
            Assert.Equal(4, Descendants(window).OfType<DashboardPlot>().Count(plot => plot.IsVisible));
            // R109: whole machine, (384 + 496) GiB used of (512 + 4096) GiB, not the busiest volume.
            Assert.Equal("19%", model.StorageUsageText); Assert.Equal("880 GB / 4.5 TB", model.StorageCapacityText);
            StartupGuiWindowTests.CaptureStateEvidence(main, "r107-host-meters.png");
            foreach (var meter in meters)
            {
                var indicator = Assert.IsType<Border>(meter.Template.FindName("PART_Indicator", meter));
                Assert.True(meter.ActualWidth > 0, $"Meter {meter.Value}% has no width; parent={((FrameworkElement)VisualTreeHelper.GetParent(meter)).RenderSize}");
                Assert.InRange(indicator.ActualWidth / meter.ActualWidth, meter.Value / 100 - .01, meter.Value / 100 + .01);
            }
            Assert.Equal(2, Descendants(window).OfType<TextBlock>().Count(text => text.IsVisible && text.Text.StartsWith("↓ ", StringComparison.Ordinal)));
            await DesktopMatrix(main, "monitoring-populated");

            // Keyboard order follows reading order: tabs and caption, host list, then the assurance actions; nothing hidden is reached.
            main.Width = 1320; main.Height = 820; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
            Assert.True(main.ServicesTab.Focus());
            var order = new List<IInputElement> { main.ServicesTab };
            for (var step = 0; step < 30; step++)
            {
                var current = (UIElement)Keyboard.FocusedElement;
                current.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); await Dispatcher.Yield(DispatcherPriority.Input);
                if (ReferenceEquals(Keyboard.FocusedElement, main.ServicesTab) || order.Contains(Keyboard.FocusedElement)) { break; }
                order.Add(Keyboard.FocusedElement);
            }
            var names = order.Select(element => AutomationProperties.GetName((DependencyObject)element)).ToArray();
            Console.WriteLine("TAB " + string.Join(" > ", names));
            Assert.All(order, element => Assert.True(((UIElement)element).IsVisible));
            Assert.Equal(["服务", "主机", "异常日志", "刷新服务", "设置与工具", "最小化", "最大化或还原", "收起到托盘"], names.Take(8));
            int Index(string name) => Array.IndexOf(names, name);
            Assert.True(Index("添加服务器") > Index("收起到托盘"), string.Join(" > ", names));
            Assert.True(Index(model.Hosts[0].Name) > Index("添加服务器"), string.Join(" > ", names));
            // R109: the assurance column and its two buttons are gone; both tools stay in the settings menu.
            Assert.DoesNotContain("运行系统诊断", names); Assert.DoesNotContain("登录启动…", names);
            Assert.DoesNotContain("搜索服务", names); Assert.DoesNotContain("服务列表", names);        });
    [Fact]
    public Task LightPaletteRecolorsOpenWindowsLiveAndRestoresDark() =>
        WithWindow(MainWindowViewModel.OverviewPage, async (model, main, window) =>
        {
            // R106: gray-white palette. The preference file is a test-owned temp file pinned to dark,
            // so the host's Windows theme never decides this test.
            var preference = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sa-appearance-{Guid.NewGuid():N}.json");
            await System.IO.File.WriteAllTextAsync(preference, "{\"theme\":\"dark\"}");
            var at = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(8));
            typeof(MainWindowViewModel).GetMethod("ApplyHostStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(model, [new HostStatusSnapshot("合成主机", "合成 Windows 系统版本", at, TimeSpan.FromHours(54), 37, 32L << 30, 12L << 30,
                    [new DriveStatusSnapshot(@"C:\", "合成系统盘", 512L << 30, 128L << 30)],
                    [new NetworkStatusSnapshot("合成以太网", "192.0.2.24", 9L << 30, 2L << 30)], LogicalProcessorCount: 16, ProcessCount: 214, ThreadCount: 2890)]);
            try
            {
                ThemeService.Install(preference);
                ThemeService.Apply(ThemePreference.Dark, persist: null);
                var dark = Brush(main.Background).Color;
                ThemeService.Apply(ThemePreference.Light, persist: null);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
                Assert.True(ThemeService.IsLight);
                // The window, its text and the embedded view all moved to the light values without reopening.
                Assert.Equal(System.Windows.Media.Color.FromRgb(0xF4, 0xF4, 0xF5), Brush(main.Background).Color);
                Assert.NotEqual(dark, Brush(main.Background).Color);
                Assert.Equal(System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x1B), Color(main, "TextPrimary"));
                foreach (var (text, minimum) in new[] { ("TextPrimary", 7.0), ("TextSecondary", 4.5), ("TextHint", 4.5), ("StatusFailed", 4.5), ("StatusWarning", 4.5) })
                { Assert.True(Contrast(Color(main, text), Color(main, "WindowBackground")) >= minimum, $"{text} on light background"); }
                Assert.True(Contrast(Color(main, "OnStatus"), Color(main, "StatusFailed")) >= 4.5);
                await DesktopMatrix(main, "light-host");
                main.Width = 960; main.Height = 620;
                main.SelectTab(MainWindowViewModel.ExceptionLogsPage); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
                StartupGuiWindowTests.CaptureStateEvidence(main, "light-exceptions-960x620.png");
                main.SelectTab(MainWindowViewModel.WorkloadsPage); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                StartupGuiWindowTests.CaptureStateEvidence(main, "light-services-960x620.png");
                // The preference file is only written by an explicit Set; Apply(persist: null) leaves it.
                Assert.Equal("{\"theme\":\"dark\"}", await System.IO.File.ReadAllTextAsync(preference));
            }
            finally
            {
                ThemeService.Apply(ThemePreference.Dark, persist: null);
                System.IO.File.Delete(preference);
            }
            Assert.False(ThemeService.IsLight);
            Assert.Equal(System.Windows.Media.Color.FromRgb(0x14, 0x14, 0x17), Brush(main.Background).Color);
            foreach (var (text, minimum) in new[] { ("TextPrimary", 7.0), ("TextSecondary", 4.5), ("TextHint", 4.5) })
            { Assert.True(Contrast(Color(main, text), Color(main, "WindowBackground")) >= minimum, $"{text} on dark background"); }
        });

    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color color)
        {
            static double Channel(byte value) { var c = value / 255d; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }
        var (light, dark) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (light + 0.05) / (dark + 0.05);
    }

    private static async Task WithWindow(string page, Func<MainWindowViewModel, SystemAutorun.App.MainWindow, MonitoringView, Task> action, TimeSpan? timeout = null)
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is not SystemAutorun.App.App)
            { var application = new SystemAutorun.App.App(runRuntimeStartup: false); application.InitializeComponent(); }
            WindowTheme.Install();
            var model = new MainWindowViewModel(fixture.Client, new NativeCacheStore(fixture.Paths.GuiCachePath),
                new AgentConfigurationClient(fixture.Client), fixture.Paths, new NeverRemoteReader()) { AutoRefresh = false };
            // R105: host and exception logs are tabs of the real main window.
            var main = new SystemAutorun.App.MainWindow(model, new AgentStartupClient(fixture.Client), new StartupCacheStore(fixture.Paths.StartupCachePath))
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            ColorStateAssist.SetReduceMotion(main, true);
            try
            {
                main.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                main.SelectTab(page); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
                Assert.Equal(page, model.CurrentPage); Assert.True(main.MonitoringTabs.IsVisible);
                await action(model, main, main.MonitoringTabs);
            }
            finally
            {
                var closing = typeof(SystemAutorun.App.MainWindow).GetMethod("Window_Closing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                main.Closing -= (System.ComponentModel.CancelEventHandler)closing.CreateDelegate(typeof(System.ComponentModel.CancelEventHandler), main);
                main.Close();
            }
        }, timeout);
    }

    private static async Task DesktopMatrix(Window window, string prefix)
    {
        foreach (var viewport in new[] { (Width: 920, Height: 580), (Width: 960, Height: 620), (Width: 1320, Height: 820), (Width: 1620, Height: 651) })
        {
            window.Width = viewport.Width; window.Height = viewport.Height;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(window);
            Assert.InRange(Math.Abs(viewport.Width - window.ActualWidth), 0, 1 / dpi.DpiScaleX);
            Assert.InRange(Math.Abs(viewport.Height - window.ActualHeight), 0, 1 / dpi.DpiScaleY);
            StartupGuiWindowTests.CaptureStateEvidence(window, $"{prefix}-{viewport.Width}x{viewport.Height}.png");
        }
    }    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    private static TextBlock BoundText(DependencyObject root, string property) => Descendants(root).OfType<TextBlock>()
        .Single(text => BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == property);
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject node)
    { while (VisualTreeHelper.GetParent(node) is { } parent) { yield return parent; node = parent; } }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        { var child = VisualTreeHelper.GetChild(root, index); yield return child; foreach (var descendant in Descendants(child)) { yield return descendant; } }
    }
    private static SolidColorBrush Brush(object? value) => Assert.IsType<SolidColorBrush>(value);
    private static Color Color(FrameworkElement element, string role) => Brush(element.FindResource("Semantic.Brush." + role)).Color;
    private sealed class NeverRemoteReader : IRemoteHostReader
    {
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("State window fixtures must never connect to remote hosts.");
    }
}
