using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class CompactSidebarTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task BothSidebarsReallyCollapseWithoutClearingSelectionFiltersOrScroll(bool light) => FakeUiMotionTests.WithMain(light, async (model, main) =>
    {
        ColorStateAssist.SetReduceMotion(main, true);
        Seed(model, Enumerable.Range(1, 30).Select(index => Snapshot($"合成服务-{index:00}")).ToArray());
        model.SearchText = "合成服务";
        model.SelectedTask = model.Tasks[12]; var selected = model.SelectedTask;
        main.UpdateLayout();
        var list = main.ServiceList; var scroll = Descendants(list).OfType<ScrollViewer>().First();
        scroll.ScrollToVerticalOffset(8); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var offset = scroll.VerticalOffset;
        foreach (var (width, height) in new[] { (920d, 580d), (960d, 620d), (1320d, 820d), (1620d, 651d) })
        {
            main.Width = width; main.Height = height; main.UpdateLayout();
            var expanded = main.ServicesPage.ColumnDefinitions[0].ActualWidth;
            Assert.InRange(expanded, 240, 340);
            Click(main.ToggleServicesSidebarButton); main.UpdateLayout();
            Assert.False(SidebarAssist.GetIsExpanded(main.ServicesPage)); Assert.False(list.IsVisible);
            Near(SidebarAssist.CollapsedWidth, main.ServicesPage.ColumnDefinitions[0].ActualWidth);
            Assert.True(main.ToggleServicesSidebarButton.IsVisible);
            Assert.Single(Descendants(main.ServicesPage).OfType<Button>(), button => button.IsVisible && AutomationProperties.GetName(button) == "添加服务");
            Capture(main, $"services-collapsed-{width}x{height}", light);
            Click(main.HostsTab); main.UpdateLayout();
            var hosts = main.MonitoringTabs;
            Assert.True(SidebarAssist.GetIsExpanded(hosts.HostPage));
            var host = model.SelectedHostTab;
            Click(hosts.ToggleHostsSidebarButton); main.UpdateLayout();
            Near(SidebarAssist.CollapsedWidth, hosts.HostPage.ColumnDefinitions[0].ActualWidth);
            Assert.False(hosts.HostList.IsVisible);
            Assert.Single(Descendants(hosts.HostPage).OfType<Button>(), button => button.IsVisible && AutomationProperties.GetName(button) == "添加服务器");
            Capture(main, $"hosts-collapsed-{width}x{height}", light);
            Click(main.ServicesTab); main.UpdateLayout();
            Assert.False(SidebarAssist.GetIsExpanded(main.ServicesPage));
            Click(main.ToggleServicesSidebarButton); main.UpdateLayout();
            Near(expanded, main.ServicesPage.ColumnDefinitions[0].ActualWidth);
            Assert.Same(list, main.ServiceList); Assert.Same(selected, list.SelectedItem);
            Assert.Equal("合成服务", model.SearchText); Near(offset, scroll.VerticalOffset);
            Capture(main, $"services-expanded-{width}x{height}", light);
            Click(main.HostsTab); main.UpdateLayout();
            Assert.False(SidebarAssist.GetIsExpanded(hosts.HostPage));
            Click(hosts.ToggleHostsSidebarButton); main.UpdateLayout();
            Assert.Same(host, hosts.HostList.SelectedItem);
            Capture(main, $"hosts-expanded-{width}x{height}", light);
            Click(main.ServicesTab); main.UpdateLayout();
        }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task RowsHaveOnlyNamesAnomalyAndLocalLabelWhileHeaderKeepsAbnormalTruth(bool light) => FakeUiMotionTests.WithMain(light, async (model, main) =>
    {
        ColorStateAssist.SetReduceMotion(main, true);
        Seed(model, [Snapshot("合成健康"), Snapshot("合成异常", "FAIL"), Snapshot("合成禁用", "OFF", false), Snapshot("合成未知", "UNKNOWN"), Snapshot("合成正常")]);
        main.UpdateLayout();
        Assert.Contains(Descendants(main.ServicesPage).OfType<TextBlock>(), text => text.Text == "5项服务");
        foreach (var task in model.Tasks)
        {
            var row = Assert.IsType<ListBoxItem>(main.ServiceList.ItemContainerGenerator.ContainerFromItem(task));
            Assert.Equal(task.Name, Assert.Single(Descendants(row).OfType<TextBlock>()).Text);
            var failIcon = Assert.Single(Descendants(row).OfType<LineIcon>(), icon => AutomationProperties.GetName(icon) == "服务异常");
            var resourceIcon = Assert.Single(Descendants(row).OfType<LineIcon>(), icon => AutomationProperties.GetName(icon) == "资源高占用注意");
            Assert.Equal("Warning", failIcon.Kind); Assert.Equal(task.Status == "FAIL", failIcon.IsVisible);
            Assert.Equal("Warning", resourceIcon.Kind); Assert.Equal(task.HasHighResourceUsage, resourceIcon.IsVisible);
        }
        Capture(main, "services-minimal", light);
        Click(main.HostsTab); main.UpdateLayout();
        Assert.Contains(Descendants(main.MonitoringTabs.HostPage).OfType<TextBlock>(), text => text.Text == "1 个服务器");
        var local = Assert.Single(model.Hosts);
        var hostRow = Assert.IsType<ListBoxItem>(main.MonitoringTabs.HostList.ItemContainerGenerator.ContainerFromItem(local));
        Assert.Equal(new[] { local.Name, "本机" }, Descendants(hostRow).OfType<TextBlock>().Select(text => text.Text));
        var hostResourceIcon = Assert.Single(Descendants(hostRow).OfType<LineIcon>(), icon => AutomationProperties.GetName(icon) == "主机资源高占用注意");
        Assert.Equal("Warning", hostResourceIcon.Kind); Assert.False(hostResourceIcon.IsVisible);
        foreach (var status in new[] { "OK", "FAIL", "UNKNOWN" })
        {
            model.ServerStatus = status; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
            Assert.Equal(status != "OK", main.MonitoringTabs.HostStatusSummary.IsVisible);
            Assert.Equal(status, Descendants(main.MonitoringTabs.HostStatusSummary).OfType<TextBlock>().Single().Text);
            Capture(main, $"host-header-{status}", light);
        }
        Assert.Equal("2天 06:00:00", model.HostUptime);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task MenuAndNestedMenuHaveNoExtraVerticalGuttersAndRetainChecks(bool light) => FakeUiMotionTests.WithMain(light, async (model, main) =>
    {
        ColorStateAssist.SetReduceMotion(main, true);
        var menu = main.SettingsButton.ContextMenu!;
        menu.PlacementTarget = main.SettingsButton; menu.IsOpen = true;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); menu.UpdateLayout();
            Assert.Equal(new Thickness(0), menu.Padding);
            var first = Assert.IsType<MenuItem>(menu.Items[0]);
            Near(1, first.TranslatePoint(new System.Windows.Point(), menu).Y);
            main.AppearanceMenu.IsSubmenuOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var popup = Assert.IsType<Popup>(main.AppearanceMenu.Template.FindName("PART_Popup", main.AppearanceMenu));
            var body = Assert.IsType<Border>(popup.Child); body.UpdateLayout();
            Assert.Equal(0, popup.VerticalOffset); Assert.Equal(new Thickness(0), body.Padding); Assert.Equal(new Thickness(0), body.Margin);
            Assert.Equal(new CornerRadius(0), body.CornerRadius);
            var entries = main.AppearanceMenu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(3, entries.Length);
            Near(1, entries[0].TranslatePoint(new System.Windows.Point(), body).Y);
            Near(1, body.ActualHeight - entries[^1].TranslatePoint(new System.Windows.Point(0, entries[^1].ActualHeight), body).Y);
            var chosen = Assert.Single(entries, entry => entry.IsChecked);
            Assert.Equal(light ? "Light" : "Dark", chosen.Tag);
            Assert.True(Assert.IsType<LineIcon>(chosen.Template.FindName("CheckGlyph", chosen)).IsVisible);
            Assert.NotEqual(((SolidColorBrush)main.AppearanceMenu.Background).Color, Colors.Transparent);
            // Changing the nested model toggle updates the real check template; no preference is written.
            main.AppearanceMenu.IsSubmenuOpen = false;
            var refreshMenu = Assert.IsType<MenuItem>(menu.Items[0]); refreshMenu.IsSubmenuOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var refresh = Assert.IsType<MenuItem>(refreshMenu.Items[0]); var before = model.AutoRefresh;
            refresh.IsChecked = !before; Assert.Equal(!before, model.AutoRefresh);
            Assert.Equal(!before, Assert.IsType<LineIcon>(refresh.Template.FindName("CheckGlyph", refresh)).IsVisible);
            refresh.IsChecked = before;
            var intervals = refreshMenu.Items.OfType<MenuItem>().Skip(1).ToArray();
            Assert.StartsWith("0.5 秒", Assert.IsType<string>(intervals[0].Header));
            Assert.Equal(new[] { "1 秒", "5 秒", "10 秒" }, intervals.Skip(1).Select(item => item.Header));
            Assert.Single(intervals, item => item.IsChecked); Assert.Equal("1 秒", intervals.Single(item => item.IsChecked).Header);
            Assert.False(intervals[0].IsEnabled); // Old/unknown Agent capability is never called half-second.
            refreshMenu.IsSubmenuOpen = false;
            StartupGuiWindowTests.CaptureStateEvidence(menu, $"r111-settings-{(light ? "light" : "dark")}.png");
            StartupGuiWindowTests.CaptureStateEvidence(body, $"r111-submenu-{(light ? "light" : "dark")}.png");
        }
        finally { main.AppearanceMenu.IsSubmenuOpen = false; menu.IsOpen = false; }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task StandardFooterButtonsFillWidthAndSplitOnlyAcrossVisibleActions(bool light) => FakeUiMotionTests.WithMain(light, async (_, main) =>
    {
        ColorStateAssist.SetReduceMotion(main, true);
        foreach (var kind in new[] { "remote", "add", "message", "info", "doctor", "task" })
        {
            var tool = Assert.IsAssignableFrom<ToolWindow>(DialogChromeTests.Create(kind));
            tool.Owner = main; tool.ShowActivated = false; tool.WindowStartupLocation = WindowStartupLocation.Manual; tool.Left = -32000; tool.Top = -32000;
            ColorStateAssist.SetReduceMotion(tool, true);
            try
            {
                tool.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                foreach (var (width, height) in new[] { (920d, 580d), (960d, 620d), (1320d, 820d), (1620d, 651d) })
                {
                    main.Width = width; main.Height = height; main.UpdateLayout(); tool.UpdateLayout();
                    var panel = Assert.Single(Descendants(tool).OfType<UniformGrid>(), grid => grid.Style == tool.FindResource("DialogActionsStyle"));
                    var footer = Assert.IsType<Border>(VisualTreeHelper.GetParent(panel));
                    var buttons = panel.Children.OfType<Button>().Where(button => button.Visibility != Visibility.Collapsed).ToArray();
                    Assert.Equal(kind is "info" or "doctor" or "task" ? 1 : 2, buttons.Length);
                    Near(footer.Padding.Left + footer.BorderThickness.Left, buttons[0].TranslatePoint(new System.Windows.Point(), footer).X);
                    Near(footer.ActualWidth - footer.Padding.Right - footer.BorderThickness.Right,
                        buttons[^1].TranslatePoint(new System.Windows.Point(buttons[^1].ActualWidth, 0), footer).X);
                    foreach (var button in buttons) { Near(buttons[0].ActualWidth, button.ActualWidth); Assert.Null(button.FocusVisualStyle); }
                    if (buttons.Length == 2)
                    {
                        Near(8, buttons[1].TranslatePoint(new System.Windows.Point(), panel).X - buttons[0].TranslatePoint(new System.Windows.Point(buttons[0].ActualWidth, 0), panel).X);
                        Assert.True(buttons[0].IsCancel); Assert.True(buttons[1].IsDefault);
                    }
                    StartupGuiWindowTests.CaptureStateEvidence(tool, $"r111-footer-{kind}-{(light ? "light" : "dark")}-{width}x{height}.png");
                }
            }
            finally { tool.Close(); await tool.ClosureTask; }
        }
    });

    internal static void Seed(MainWindowViewModel model, TaskSnapshot[] tasks)
    {
        model.IsBusy = true;
        try { typeof(MainWindowViewModel).GetMethod("ReplaceTasks", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [tasks, false]); }
        finally { model.IsBusy = false; }
    }

    internal static TaskSnapshot Snapshot(string name, string status = "OK", bool enabled = true) => new(
        status, enabled, status == "OK", name, "windows", "隔离合成样例", "fixture", "fixture", 0, null, [], null, null, null, [],
        new LaunchOptions(RunAsMode.CurrentUser, LaunchTrigger.Manual, WindowMode.Hidden, ProcessLifecycle.Tracked), ExpectedRunning: enabled);

    private static void Click(Button button) => typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);
    private static void Near(double expected, double actual) => Assert.InRange(Math.Abs(expected - actual), 0, .75);
    private static void Capture(FrameworkElement element, string name, bool light) => StartupGuiWindowTests.CaptureStateEvidence(element, $"r111-{name}-{(light ? "light" : "dark")}.png");
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        { var child = VisualTreeHelper.GetChild(root, index); yield return child; foreach (var descendant in Descendants(child)) { yield return descendant; } }
    }
}
