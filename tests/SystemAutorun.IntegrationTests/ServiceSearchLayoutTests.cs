using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Models;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class ServiceSearchLayoutTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task HeaderAndServiceDetailsShareTheRequestedAnchors(bool light) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, _) =>
        {
            model.AutoRefresh = false;
            ColorStateAssist.SetReduceMotion(main, true);
            Assert.True(main.MainScene.Focusable);
            Assert.True(main.MainScene.IsVisible && main.MainScene.IsEnabled);
            Assert.False(System.Windows.Input.KeyboardNavigation.GetIsTabStop(main.MainScene));
            Assert.Null(main.MainScene.FocusVisualStyle);
            var rows = new List<object>();
            foreach (var (width, height) in new[] { (920, 580), (1152, 648), (1320, 820) })
            {
                main.Width = width; main.Height = height;
                await Task.Delay(300); await Settle(main);
                var filter = FindButton("筛选服务");
                var refresh = FindButton("刷新服务");
                var settings = FindButton("设置与工具");
                var close = FindButton("收起到托盘");
                var search = Ancestors(main.SearchBox).OfType<Border>().First();
                var caption = Ancestors(search).OfType<Border>().First();
                var windowButtons = Ancestors(close).OfType<Border>().First();
                var serviceHeaderFrame = Ancestors(main.ServiceHeader).OfType<Border>().First();
                var healthFrame = Ancestors(main.ServiceHealthPlot).OfType<Border>().First();
                var healthRegion = Ancestors(healthFrame).OfType<Grid>().First();
                var anchor = Right(windowButtons);
                var result = new
                {
                    light, width, height, anchor,
                    caption.Padding.Left, caption.Padding.Right, actualWindowWidth = main.ActualWidth,
                    headerPaddingLeft = serviceHeaderFrame.Padding.Left, headerPaddingRight = serviceHeaderFrame.Padding.Right,
                    healthMarginLeft = healthRegion.Margin.Left, healthMarginRight = healthRegion.Margin.Right,
                    filterRight = Right(filter), refreshLeft = Left(refresh), refreshRight = Right(refresh),
                    settingsLeft = Left(settings), settingsRight = Right(settings), searchLeft = Left(search), searchRight = Right(search),
                    windowButtonsLeft = Left(windowButtons), actionsRight = Right(main.ServiceActions),
                    uptimeRight = Right(main.ServiceUptime), healthFrameRight = Right(healthFrame),
                    passed = Math.Abs(anchor - (main.ActualWidth - 7)) <= 1.0
                        && Math.Abs(caption.Padding.Left - 18) < .01 && Math.Abs(caption.Padding.Right - 6) < .01
                        && Math.Abs(serviceHeaderFrame.Padding.Left - 6) < .01 && Math.Abs(serviceHeaderFrame.Padding.Right - 6) < .01
                        && Math.Abs(healthRegion.Margin.Left - 6) < .01 && Math.Abs(healthRegion.Margin.Right - 6) < .01
                        && Right(filter) <= Left(refresh) + .1 && Right(refresh) <= Left(settings) + .1
                        && Right(settings) <= Left(search) + .1 && Right(search) <= Left(windowButtons) + .1
                        && Math.Abs(Right(main.ServiceActions) - anchor) <= 1.0
                        && Math.Abs(Right(main.ServiceUptime) - anchor) <= 1.0
                        && Math.Abs(Right(healthFrame) - anchor) <= 1.0
                };
                rows.Add(result);
            }
            Record($"geometry-{(light ? "light" : "dark")}.json", rows);
            using var resultDocument = JsonDocument.Parse(JsonSerializer.Serialize(rows));
            Assert.All(resultDocument.RootElement.EnumerateArray(), row => Assert.True(row.GetProperty("passed").GetBoolean(), row.GetRawText()));
            SidebarAssist.SetIsExpanded(main.ServicesPage, false); await Task.Delay(350); await Settle(main);
            var collapsedRows = new List<object>();
            foreach (var (width, height) in new[] { (920, 580), (1152, 648), (1320, 820) })
            {
                main.Width = width; main.Height = height; await Task.Delay(300); await Settle(main);
                var anchor = Right(Ancestors(FindButton("收起到托盘")).OfType<Border>().First());
                var healthFrame = Ancestors(main.ServiceHealthPlot).OfType<Border>().First();
                collapsedRows.Add(new { light, width, height, anchor, expanded = SidebarAssist.GetIsExpanded(main.ServicesPage),
                    actionsRight = Right(main.ServiceActions), uptimeRight = Right(main.ServiceUptime), healthFrameRight = Right(healthFrame),
                    passed = !SidebarAssist.GetIsExpanded(main.ServicesPage) && Math.Abs(anchor - (main.ActualWidth - 7)) <= 1.0
                        && Math.Abs(Right(main.ServiceActions) - anchor) <= 1.0 && Math.Abs(Right(main.ServiceUptime) - anchor) <= 1.0
                        && Math.Abs(Right(healthFrame) - anchor) <= 1.0 });
            }
            Record($"geometry-collapsed-{(light ? "light" : "dark")}.json", collapsedRows);
            using var collapsedDocument = JsonDocument.Parse(JsonSerializer.Serialize(collapsedRows));
            Assert.All(collapsedDocument.RootElement.EnumerateArray(), row => Assert.True(row.GetProperty("passed").GetBoolean(), row.GetRawText()));
            SidebarAssist.SetIsExpanded(main.ServicesPage, true); await Task.Delay(350); await Settle(main);
            Assert.True(System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(main.SearchSurface));
            foreach (var page in new[] { MainWindowViewModel.OverviewPage, MainWindowViewModel.ExceptionLogsPage, MainWindowViewModel.WorkloadsPage })
            {
                model.CurrentPage = page; await Settle(main);
                var services = page == MainWindowViewModel.WorkloadsPage;
                Assert.Equal(services, main.SearchSurface.IsVisible);
                var filter = Assert.Single(Descendants(main).OfType<Button>(), item => AutomationProperties.GetName(item) == "筛选服务");
                Assert.Equal(services, filter.IsVisible);
                Assert.True(FindButton("刷新服务").IsVisible);
                Assert.True(FindButton("设置与工具").IsVisible);
            }

            Button FindButton(string name) => Assert.Single(Descendants(main).OfType<Button>(),
                item => item.IsVisible && AutomationProperties.GetName(item) == name);
            double Left(FrameworkElement element) => element.TranslatePoint(new Point(), main).X;
            double Right(FrameworkElement element) => element.TranslatePoint(new Point(element.ActualWidth, 0), main).X;
        });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task EmptyTextboxRoundTripsAndPreservesIndependentFilters(bool light) =>
        FakeUiMotionTests.WithMain(light, async (model, main) =>
        {
            model.AutoRefresh = false;
            CompactSidebarTests.Seed(model,
                [CompactSidebarTests.Snapshot("alpha-fixture", "OK"), CompactSidebarTests.Snapshot("beta-fixture", "FAIL") with { Enabled = true, Healthy = false, Type = "windows_service" }]);
            var rows = new List<object>();
            var emptyTitle = Assert.Single(Descendants(main.ServicesPage).OfType<TextBlock>(), text =>
                System.Windows.Data.BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == "DashboardEmptyTitle");
            await Enter("no-match-fixture");
            Add("nonmatch", model.IsEmpty && !model.TasksView.Cast<object>().Any() && emptyTitle.Text == "没有匹配结果");
            await Enter("");
            Add("cleared", string.IsNullOrWhiteSpace(model.SearchText) && !model.IsEmpty && model.TasksView.Cast<object>().Count() == 2
                && emptyTitle.Text != "没有匹配结果");
            await Enter("   ");
            Add("whitespace", string.IsNullOrWhiteSpace(model.SearchText) && !model.IsEmpty && model.TasksView.Cast<object>().Count() == 2);
            model.FilterByStatusCommand.Execute("FAIL");
            await Enter("no-match-fixture");
            Add("filtered-nonmatch", model.IsEmpty && model.SelectedStatusFilter.Value == "FAIL");
            await Enter("");
            Add("filtered-cleared", !model.IsEmpty && model.SelectedStatusFilter.Value == "FAIL" && model.TasksView.Cast<object>().Count() == 1);
            model.SelectDashboardTypeCommand.Execute("windows_service");
            await Enter("no-match-fixture");
            Add("both-filters-nonmatch", model.IsEmpty && model.SelectedStatusFilter.Value == "FAIL" && model.SelectedTypeFilter.Value == "windows_service");
            await Enter("");
            Add("both-filters-cleared", !model.IsEmpty && model.SelectedStatusFilter.Value == "FAIL"
                && model.SelectedTypeFilter.Value == "windows_service" && model.TasksView.Cast<object>().Count() == 1);
            model.SelectDashboardTypeCommand.Execute("windows"); await Settle(main);
            Add("incompatible-filters", model.IsEmpty && model.SelectedStatusFilter.Value == "FAIL"
                && model.SelectedTypeFilter.Value == "windows" && !model.TasksView.Cast<object>().Any());
            model.ClearFiltersCommand.Execute(null);
            model.SelectDashboardTypeCommand.Execute("windows_service");
            await Enter("no-match-fixture"); await Enter("");
            Add("type-filter-cleared", !model.IsEmpty && model.SelectedStatusFilter.Value == ""
                && model.SelectedTypeFilter.Value == "windows_service" && model.TasksView.Cast<object>().Count() == 1);
            model.ClearFiltersCommand.Execute(null);
            model.SearchText = null!; await Settle(main);
            Add("null-query", string.IsNullOrWhiteSpace(main.SearchBox.Text) && !model.IsEmpty && model.TasksView.Cast<object>().Count() == 2);
            CompactSidebarTests.Seed(model, []); await Settle(main);
            Add("no-configured-tasks", model.IsEmpty && model.Tasks.Count == 0 && model.EmptyStateTitle == "暂无启动任务");
            Record($"binding-{(light ? "light" : "dark")}.json", rows);
            using var resultDocument = JsonDocument.Parse(JsonSerializer.Serialize(rows));
            Assert.All(resultDocument.RootElement.EnumerateArray(), row => Assert.True(row.GetProperty("passed").GetBoolean(), row.GetRawText()));

            async Task Enter(string value)
            {
                main.SearchBox.SetCurrentValue(TextBox.TextProperty, value);
                await Settle(main);
            }
            void Add(string kind, bool passed) => rows.Add(new
            {
                kind, passed, text = main.SearchBox.Text, model.SearchText,
                status = model.SelectedStatusFilter.Value, type = model.SelectedTypeFilter.Value,
                tasks = model.Tasks.Count, visible = model.TasksView.Cast<object>().Count(), model.IsEmpty,
                selected = model.SelectedTask?.Name, model.EmptyStateTitle, model.DashboardEmptyTitle, renderedDashboardTitle = emptyTitle.Text
            });
        });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task ClearingVariousQueriesSurvivesSyntheticLiveRefresh(bool light) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, source) =>
        {
            var rows = new List<object>();
            Assert.Equal(2, model.Tasks.Count);
            foreach (var query in new[] { "R121", "合成", "A", "alpha-fixture-not-found", "Ω", "   " })
            {
                main.SearchBox.SetCurrentValue(TextBox.TextProperty, query); await Settle(main);
                source.Apply(model); await Settle(main);
                main.SearchBox.Clear(); await Settle(main);
                await model.RefreshLiveAsync(); await Settle(main);
                var visible = model.TasksView.Cast<object>().Count();
                var passed = main.SearchBox.Text == "" && string.IsNullOrWhiteSpace(model.SearchText)
                    && model.SelectedStatusFilter.Value == "" && model.SelectedTypeFilter.Value == ""
                    && model.Tasks.Count == 2 && visible == 2 && !model.IsEmpty && main.ServiceList.Items.Count == 2;
                rows.Add(new { query, passed, text = main.SearchBox.Text, model.SearchText, model.IsEmpty,
                    tasks = model.Tasks.Count, visible, listItems = main.ServiceList.Items.Count, model.DashboardEmptyTitle });
            }
            Record($"refresh-search-{(light ? "light" : "dark")}.json", rows);
            using var resultDocument = JsonDocument.Parse(JsonSerializer.Serialize(rows));
            Assert.All(resultDocument.RootElement.EnumerateArray(), row => Assert.True(row.GetProperty("passed").GetBoolean(), row.GetRawText()));
        });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task SyntheticTextCompositionUpdatesSearchWithoutNativeInput(bool light) =>
        FakeUiMotionTests.WithMain(light, async (model, main) =>
        {
            model.AutoRefresh = false;
            CompactSidebarTests.Seed(model,
                [CompactSidebarTests.Snapshot("alpha-fixture"), CompactSidebarTests.Snapshot("beta-fixture")]);
            var composition = new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, main.SearchBox, "zz123");
            var handled = System.Windows.Input.TextCompositionManager.StartComposition(composition);
            await Settle(main);
            var outcome = new { light, handled, text = main.SearchBox.Text, model.SearchText, visible = model.TasksView.Cast<object>().Count(),
                model.IsEmpty, main.IsActive, keyboardFocused = main.SearchBox.IsKeyboardFocused };
            Record($"text-composition-{(light ? "light" : "dark")}.json", outcome);
            Assert.Equal("zz123", main.SearchBox.Text);
            Assert.Equal("zz123", model.SearchText);
            Assert.True(model.IsEmpty);
            Assert.Empty(model.TasksView.Cast<object>());
        });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task RoutedBackspaceClearsAnUnmatchedQueryWithoutNativeInput(bool light) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, source) =>
        {
            Assert.True(model.AutoRefresh);
            Assert.Equal(2, model.Tasks.Count);
            foreach (var letter in "zz123")
            {
                var composition = new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, main.SearchBox, letter.ToString());
                Assert.True(System.Windows.Input.TextCompositionManager.StartComposition(composition));
                await Settle(main); source.Apply(model); await model.RefreshLiveAsync(); await Settle(main);
            }
            Assert.Equal("zz123", main.SearchBox.Text);
            Assert.True(model.IsEmpty);
            main.SearchBox.CaretIndex = main.SearchBox.Text.Length;
            var steps = new List<object>();
            for (var remaining = 4; remaining >= 0; remaining--)
            {
                var canExecute = System.Windows.Documents.EditingCommands.Backspace.CanExecute(null, main.SearchBox);
                if (canExecute)
                {
                    System.Windows.Documents.EditingCommands.Backspace.Execute(null, main.SearchBox); await Settle(main);
                    source.Apply(model); await model.RefreshLiveAsync(); await Settle(main);
                }
                var visible = model.TasksView.Cast<object>().Count();
                var expected = remaining == 0 ? 2 : 0;
                steps.Add(new { remaining, canExecute, text = main.SearchBox.Text, model.SearchText, visible,
                    listItems = main.ServiceList.Items.Count, model.IsEmpty, selected = model.SelectedTask?.Name,
                    model.DashboardEmptyTitle, main.IsActive, keyboardFocused = main.SearchBox.IsKeyboardFocused,
                    passed = canExecute && main.SearchBox.Text.Length == remaining && main.SearchBox.Text == model.SearchText
                        && visible == expected && main.ServiceList.Items.Count == expected && model.IsEmpty == (expected == 0) });
                if (!canExecute) { break; }
            }
            Record($"backspace-command-{(light ? "light" : "dark")}.json", steps);
            Assert.Equal(5, steps.Count);
            using var resultDocument = JsonDocument.Parse(JsonSerializer.Serialize(steps));
            Assert.All(resultDocument.RootElement.EnumerateArray(), step => Assert.True(step.GetProperty("passed").GetBoolean(), step.GetRawText()));
        });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task RoutedBackspaceRestoresListAcrossResourceOnlyTicks(bool light) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, _) =>
        {
            model.AutoRefresh = false;
            var snapshotField = typeof(MainWindowViewModel).GetField("_monitoringSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var snapshot = Assert.IsType<AgentMonitoringSnapshot>(snapshotField.GetValue(model));
            var originalRows = model.Tasks.ToArray(); Assert.Equal(2, originalRows.Length);
            foreach (var letter in "zz123")
            {
                var composition = new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, main.SearchBox, letter.ToString());
                Assert.True(System.Windows.Input.TextCompositionManager.StartComposition(composition)); await Settle(main);
            }
            Assert.Equal("zz123", main.SearchBox.Text); Assert.True(model.IsEmpty);
            main.SearchBox.CaretIndex = main.SearchBox.Text.Length;
            var emptyTitle = Assert.Single(Descendants(main.ServicesPage).OfType<TextBlock>(), item =>
                System.Windows.Data.BindingOperations.GetBinding(item, TextBlock.TextProperty)?.Path?.Path == "DashboardEmptyTitle");
            var resets = 0;
            NotifyCollectionChangedEventHandler changed = (_, args) => { if (args.Action == NotifyCollectionChangedAction.Reset) { resets++; } };
            ((INotifyCollectionChanged)model.TasksView).CollectionChanged += changed;
            var steps = new List<object>();
            try
            {
                for (var remaining = 4; remaining >= 0; remaining--)
                {
                    var now = DateTimeOffset.UtcNow;
                    var resourceTick = snapshot with { ReturnedAt = now,
                        Host = snapshot.Host with { ObservedAt = now, AttemptedAt = now,
                            Data = snapshot.Host.Data! with { CapturedAt = now, CpuUsagePercent = 20 + remaining } } };
                    var before = resets; model.ApplyMonitoringSnapshot(resourceTick); var afterTick = resets;
                    var canExecute = System.Windows.Documents.EditingCommands.Backspace.CanExecute(null, main.SearchBox);
                    if (canExecute) { System.Windows.Documents.EditingCommands.Backspace.Execute(null, main.SearchBox); await Settle(main); }
                    var expected = remaining == 0 ? 2 : 0;
                    var visible = model.TasksView.Cast<object>().Count();
                    steps.Add(new { remaining, before, afterTick, canExecute, text = main.SearchBox.Text, model.SearchText,
                        status = model.SelectedStatusFilter.Value, type = model.SelectedTypeFilter.Value,
                        tasks = model.Tasks.Count, visible, listItems = main.ServiceList.Items.Count,
                        listVisible = main.ServiceList.IsVisible, model.IsEmpty, selected = model.SelectedTask?.Name,
                        model.DashboardEmptyTitle, renderedTitle = emptyTitle.Text, main.IsActive,
                        keyboardFocused = main.SearchBox.IsKeyboardFocused, sameRows = originalRows.SequenceEqual(model.Tasks),
                        passed = before == afterTick && canExecute && main.SearchBox.Text.Length == remaining && main.SearchBox.Text == model.SearchText
                            && visible == expected && main.ServiceList.Items.Count == expected && model.IsEmpty == (expected == 0)
                            && originalRows.SequenceEqual(model.Tasks) });
                    if (!canExecute) { break; }
                }
            }
            finally { ((INotifyCollectionChanged)model.TasksView).CollectionChanged -= changed; }
            Record($"backspace-resource-only-{(light ? "light" : "dark")}.json", steps);
            Assert.Equal(5, steps.Count);
            using var resultDocument = JsonDocument.Parse(JsonSerializer.Serialize(steps));
            Assert.All(resultDocument.RootElement.EnumerateArray(), step => Assert.True(step.GetProperty("passed").GetBoolean(), step.GetRawText()));
            Assert.Null(model.SelectedTask);
            Assert.Equal("选择服务", emptyTitle.Text);
        });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task EmptyTextboxRestoresVisibleListWhenHealthExpiresDuringRefresh(bool light) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, _) =>
        {
            model.AutoRefresh = false;
            main.SearchBox.SetCurrentValue(TextBox.TextProperty, "zz123"); await Settle(main);
            Assert.True(model.IsEmpty);
            Assert.Empty(model.TasksView.Cast<object>());
            var snapshotField = typeof(MainWindowViewModel).GetField("_monitoringSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var availableField = typeof(MainWindowViewModel).GetField("_tasksAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var snapshot = Assert.IsType<AgentMonitoringSnapshot>(snapshotField.GetValue(model));
            Assert.True((bool)availableField.GetValue(model)!);
            var expiredAt = DateTimeOffset.UtcNow.AddSeconds(-31);
            snapshotField.SetValue(model, snapshot with { Tasks = snapshot.Tasks with { ObservedAt = expiredAt, AttemptedAt = expiredAt } });
            var resets = 0;
            NotifyCollectionChangedEventHandler changed = (_, args) => { if (args.Action == NotifyCollectionChangedAction.Reset) { resets++; } };
            ((INotifyCollectionChanged)model.TasksView).CollectionChanged += changed;
            try { main.SearchBox.SetCurrentValue(TextBox.TextProperty, ""); }
            finally { ((INotifyCollectionChanged)model.TasksView).CollectionChanged -= changed; }
            await Settle(main);
            var emptyTitle = Assert.Single(Descendants(main.ServicesPage).OfType<TextBlock>(), text =>
                System.Windows.Data.BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == "DashboardEmptyTitle");
            var result = new { light, resets, available = (bool)availableField.GetValue(model)!, text = main.SearchBox.Text,
                model.SearchText, status = model.SelectedStatusFilter.Value, type = model.SelectedTypeFilter.Value,
                tasks = model.Tasks.Count, visible = model.TasksView.Cast<object>().Count(), listItems = main.ServiceList.Items.Count,
                listVisible = main.ServiceList.IsVisible, listOpacity = main.ServiceList.Opacity,
                sidebarExpanded = SidebarAssist.GetIsExpanded(main.ServicesPage), model.IsEmpty, selected = model.SelectedTask?.Name,
                model.DashboardEmptyTitle, renderedTitle = emptyTitle.Text };
            Record($"expiry-ui-{(light ? "light" : "dark")}.json", result);
            Assert.True(resets >= 2, "Expected a view reset and nested health-expiry reconciliation.");
            Assert.False((bool)availableField.GetValue(model)!);
            Assert.Equal("", main.SearchBox.Text); Assert.Equal("", model.SearchText);
            Assert.Equal("", model.SelectedStatusFilter.Value); Assert.Equal("", model.SelectedTypeFilter.Value);
            Assert.Equal(2, model.Tasks.Count); Assert.Equal(2, model.TasksView.Cast<object>().Count());
            Assert.Equal(2, main.ServiceList.Items.Count); Assert.True(main.ServiceList.IsVisible);
            Assert.True(SidebarAssist.GetIsExpanded(main.ServicesPage)); Assert.False(model.IsEmpty);
            Assert.NotEqual("没有匹配结果", emptyTitle.Text);
        });

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject? item)
    {
        for (item = item is null ? null : VisualTreeHelper.GetParent(item); item is not null; item = VisualTreeHelper.GetParent(item))
        { yield return item; }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject item)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(item); index++)
        {
            var child = VisualTreeHelper.GetChild(item, index); yield return child;
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }

    private static async Task Settle(Window main)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.Render);
    }

    private static void Record(string name, object value)
    {
        var directory = Environment.GetEnvironmentVariable("SA_SERVICE_EVIDENCE");
        if (string.IsNullOrEmpty(directory)) { return; }
        Assert.True(Directory.Exists(directory));
        File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
