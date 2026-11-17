using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using Point = System.Windows.Point;

namespace SystemAutorun.IntegrationTests;

public sealed partial class PointerInteractionWindowTests
{
    // Explicit opt-in: never inject into production; the caller must foreground this exact fixture.
    [Fact]
    public Task NativeServiceSearchJourneyIsExplicitAndOwned()
    {
        var directory = Environment.GetEnvironmentVariable("SA_SERVICE_SEARCH_NATIVE");
        if (string.IsNullOrEmpty(directory)) { return Task.CompletedTask; }
        Assert.True(Directory.Exists(directory));
        var light = Environment.GetEnvironmentVariable("SA_SERVICE_SEARCH_LIGHT") == "1";
        var live = Environment.GetEnvironmentVariable("SA_SERVICE_SEARCH_LIVE") == "1";
        var bothThemes = Environment.GetEnvironmentVariable("SA_SERVICE_SEARCH_BOTH_THEMES") == "1";
        var inputOnly = Environment.GetEnvironmentVariable("SA_SERVICE_SEARCH_INPUT_ONLY") == "1";
        var fixtureOnly = Environment.GetEnvironmentVariable("SA_SERVICE_SEARCH_FIXTURE_ONLY") == "1";
        ResourceWindowSource? source = null;
        return FakeUiMotionTests.WithMain(light, async (model, main) =>
        {
            var setup = new List<object>();
            model.AutoRefresh = false;
            CompactSidebarTests.Seed(model,
                [CompactSidebarTests.Snapshot("alpha-fixture", "OK"), CompactSidebarTests.Snapshot("beta-fixture", "FAIL") with { Enabled = true, Healthy = false }]);
            Setup("seed");
            if (live)
            {
                await source!.ConfigureAsync(model); Setup("configured");
                var observed = DateTimeOffset.UtcNow;
                source.Observe(observed, model.Tasks.Select(task => ResourceWindowSource.Synthetic(task, observed, 0)).ToArray(),
                    ResourceWindowSource.SyntheticHost(observed, 0));
                source.Apply(model); model.AutoRefresh = true; Setup("live-started");
            }
            model.CurrentPage = MainWindowViewModel.WorkloadsPage;
            main.Title = "SystemAutorun R123 isolated search test";
            if (!fixtureOnly) { main.Left = 24; main.Top = 24; }
            main.Width = 1152; main.Height = 648;
            await Task.Delay(1400); await Settle(main); Setup("settled");
            await File.WriteAllTextAsync(Path.Combine(directory, "setup.json"), JsonSerializer.Serialize(setup, new JsonSerializerOptions { WriteIndented = true }));
            Assert.Equal(new[] { "alpha-fixture", "beta-fixture" }, model.Tasks.Select(task => task.Name));
            if (fixtureOnly)
            {
                Assert.NotNull(source);
                var fixtureSteps = new List<object>();
                var emptyTitle = Assert.Single(Descendants(main.ServicesPage).OfType<TextBlock>(), item =>
                    System.Windows.Data.BindingOperations.GetBinding(item, TextBlock.TextProperty)?.Path?.Path == "DashboardEmptyTitle");
                foreach (var query in new[] { "a", "al", "alp", "alph", "alpha", "alph", "alp", "al", "a", "", "z", "zz", "zz1", "zz12", "zz123", "zz12", "zz1", "zz", "z", "" })
                {
                    main.SearchBox.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, query); await Settle(main);
                    source.Apply(model); await model.RefreshLiveAsync(); await Settle(main);
                    var expected = model.Tasks.Count(task => new[] { task.Name, task.Type, task.Reason, task.Detail }
                        .Any(value => value.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
                    var visible = model.TasksView.Cast<object>().Count();
                    fixtureSteps.Add(new { at = DateTimeOffset.UtcNow, query, text = main.SearchBox.Text,
                        codePoints = main.SearchBox.Text.Select(c => (int)c).ToArray(), model.SearchText,
                        status = model.SelectedStatusFilter.Value, type = model.SelectedTypeFilter.Value,
                        tasks = model.Tasks.Count, visible, listItems = main.ServiceList.Items.Count,
                        listVisible = main.ServiceList.IsVisible, listOpacity = main.ServiceList.Opacity,
                        sidebarExpanded = SidebarAssist.GetIsExpanded(main.ServicesPage), model.IsEmpty,
                        selected = model.SelectedTask?.Name, model.DashboardEmptyTitle, renderedTitle = emptyTitle.Text,
                        passed = query == main.SearchBox.Text && query == model.SearchText && visible == expected
                            && main.ServiceList.Items.Count == expected && model.IsEmpty == (expected == 0) });
                }
                await File.WriteAllTextAsync(Path.Combine(directory, "fixture-journey.json"), JsonSerializer.Serialize(fixtureSteps, new JsonSerializerOptions { WriteIndented = true }));
                using var fixtureDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "fixture-journey.json")));
                Assert.All(fixtureDocument.RootElement.EnumerateArray(), step => Assert.True(step.GetProperty("passed").GetBoolean(), step.GetRawText()));
                Assert.Equal(2, main.ServiceList.Items.Count);
                return;
            }
            var hwnd = new WindowInteropHelper(main).Handle;
            var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
            await File.WriteAllTextAsync(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId, processStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                hwnd = hwnd.ToInt64(), main.Title, deadline, light, live, bothThemes, inputOnly,
                scope = "Isolated synthetic WPF fixture; exact foreground HWND and pointer recipient required before each native input"
            }, new JsonSerializerOptions { WriteIndented = true }));
            var go = Path.Combine(directory, "go.json");
            while ((!File.Exists(go) || GetForegroundWindow() != hwnd) && DateTimeOffset.UtcNow < deadline) { await Task.Delay(50); }
            Assert.True(File.Exists(go), "Native journey was not armed before its bounded deadline.");
            using (var armDocument = JsonDocument.Parse(await File.ReadAllTextAsync(go)))
            {
                Assert.Equal(Environment.ProcessId, armDocument.RootElement.GetProperty("pid").GetInt32());
                Assert.Equal(hwnd.ToInt64(), armDocument.RootElement.GetProperty("hwnd").GetInt64());
            }
            Assert.Equal(hwnd, GetForegroundWindow());
            Assert.True(GetCursorPos(out var oldCursor));
            var rows = new List<object>();
            var events = new List<object>();
            MouseButtonEventHandler trace = (_, args) => events.Add(new { kind = args.RoutedEvent.Name, args.Timestamp,
                source = (args.OriginalSource as FrameworkElement)?.Name, sourceType = args.OriginalSource?.GetType().Name });
            main.AddHandler(Mouse.PreviewMouseDownEvent, trace, true);
            main.AddHandler(Mouse.PreviewMouseUpEvent, trace, true);
            KeyEventHandler keyTrace = (_, args) => events.Add(new { kind = args.RoutedEvent.Name, args.Timestamp,
                key = args.Key.ToString(), modifiers = Keyboard.Modifiers.ToString(), focused = (Keyboard.FocusedElement as FrameworkElement)?.Name });
            main.AddHandler(Keyboard.PreviewKeyDownEvent, keyTrace, true);
            try
            {
                if (!inputOnly)
                {
                    foreach (var currentLight in bothThemes ? new[] { light, !light } : new[] { light })
                    {
                        Assert.Equal(hwnd, GetForegroundWindow());
                        ThemeService.Apply(currentLight ? ThemePreference.Light : ThemePreference.Dark, persist: null);
                        foreach (var (width, height) in new[] { (920, 580), (1152, 648), (1320, 820) })
                        {
                            Assert.Equal(hwnd, GetForegroundWindow());
                            main.Width = width; main.Height = height; main.Left = 24; main.Top = 24;
                            await Task.Delay(1400); await Settle(main);
                            CaptureNative(main, Path.Combine(directory, $"service-{(currentLight ? "light" : "dark")}-{width}x{height}.png"));
                        }
                    }
                    ThemeService.Apply(light ? ThemePreference.Light : ThemePreference.Dark, persist: null);
                }
                main.Width = 1152; main.Height = 648; main.Left = 24; main.Top = 24;
                await Task.Delay(500); await Settle(main);
                var searchLocal = new Point(main.SearchBox.ActualWidth / 2, main.SearchBox.ActualHeight / 2);
                var searchPoint = main.SearchBox.PointToScreen(searchLocal);
                var searchHit = main.InputHitTest(main.SearchBox.TranslatePoint(searchLocal, main)) as DependencyObject;
                rows.Add(new { kind = "search-hit-diagnostic", passed = true, screen = searchPoint, nativeHit = NativeHit(main, searchPoint),
                    main.IsActive, main.SearchBox.IsVisible, main.SearchBox.IsEnabled, main.SearchBox.Focusable,
                    hit = searchHit?.GetType().Name, hitName = (searchHit as FrameworkElement)?.Name,
                    containsSearch = Ancestors(searchHit).Contains(main.SearchBox) });
                Move(main, searchPoint); Click(main, searchPoint); await Task.Delay(80); await Settle(main);
                Add("focus-search", main.SearchBox.IsKeyboardFocusWithin);
                Assert.True(main.SearchBox.IsKeyboardFocusWithin, "Search field did not receive native focus; no keys sent.");
                // Keep the pointer away from the search tooltip and IME candidate windows during keyboard input.
                var keyboardPoint = main.PointToScreen(new Point(20, main.ActualHeight - 20));
                Assert.Equal(1, NativeHit(main, keyboardPoint)); Move(main, keyboardPoint);
                Key(0, '\u03a9', 4, keyboardPoint); await Task.Delay(80); await Settle(main);
                Add("typed-nonmatch", main.SearchBox.Text.Length > 0 && model.IsEmpty && !model.TasksView.Cast<object>().Any());
                Key(8, '\0', 0, keyboardPoint); await Task.Delay(80); await Settle(main);
                Add("native-cleared", string.IsNullOrWhiteSpace(main.SearchBox.Text) && string.IsNullOrWhiteSpace(model.SearchText)
                    && !model.IsEmpty && model.TasksView.Cast<object>().Count() == 2);
                // Inject committed English characters independently of the user's active IME layout;
                // deletion below still uses individual native Backspace key events.
                foreach (var letter in "alpha")
                { Key(0, letter, 4, keyboardPoint); await Task.Delay(20); }
                await Settle(main);
                Assert.True(string.Equals(main.SearchBox.Text, "alpha", StringComparison.OrdinalIgnoreCase), "The fixture did not receive the intended English input.");
                var deletionStates = new List<object>();
                for (var remaining = 4; remaining >= 0; remaining--)
                {
                    Key(8, '\0', 0, keyboardPoint); await Task.Delay(30); await Settle(main);
                    if (live) { source!.Apply(model); await model.RefreshLiveAsync(); await Settle(main); }
                    var query = model.SearchText ?? "";
                    var expected = model.Tasks.Count(task => new[] { task.Name, task.Type, task.Reason, task.Detail }
                        .Any(value => value.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
                    deletionStates.Add(new { remaining, text = main.SearchBox.Text, model.SearchText, expected,
                        actual = model.TasksView.Cast<object>().Count(), model.IsEmpty });
                    Assert.Equal(remaining, main.SearchBox.Text.Length);
                    Assert.Equal(expected, model.TasksView.Cast<object>().Count());
                    Assert.Equal(expected == 0, model.IsEmpty);
                }
                rows.Add(new { kind = "english-backspace-sequence", passed = main.SearchBox.Text == "" && model.TasksView.Cast<object>().Count() == 2,
                    steps = deletionStates });
                foreach (var letter in "zz123") { Key(0, letter, 4, keyboardPoint); await Task.Delay(20); }
                await Settle(main); Assert.Equal("zz123", main.SearchBox.Text); Assert.True(model.IsEmpty);
                var unmatchedStates = new List<object>();
                var emptyTitle = Assert.Single(Descendants(main.ServicesPage).OfType<TextBlock>(), item =>
                    System.Windows.Data.BindingOperations.GetBinding(item, TextBlock.TextProperty)?.Path?.Path == "DashboardEmptyTitle");
                for (var remaining = 4; remaining >= 0; remaining--)
                {
                    Key(8, '\0', 0, keyboardPoint); await Task.Delay(30); await Settle(main);
                    if (live) { source!.Apply(model); await model.RefreshLiveAsync(); await Settle(main); }
                    var expected = remaining == 0 ? 2 : 0;
                    unmatchedStates.Add(new { remaining, text = main.SearchBox.Text, codePoints = main.SearchBox.Text.Select(c => (int)c).ToArray(),
                        model.SearchText, status = model.SelectedStatusFilter.Value, type = model.SelectedTypeFilter.Value,
                        tasks = model.Tasks.Count, visible = model.TasksView.Cast<object>().Count(), listItems = main.ServiceList.Items.Count,
                        listVisible = main.ServiceList.IsVisible, listOpacity = main.ServiceList.Opacity,
                        sidebarExpanded = SidebarAssist.GetIsExpanded(main.ServicesPage), model.IsEmpty, selected = model.SelectedTask?.Name,
                        model.DashboardEmptyTitle, renderedTitle = emptyTitle.Text });
                    Assert.Equal(remaining, main.SearchBox.Text.Length);
                    Assert.Equal(expected, model.TasksView.Cast<object>().Count());
                    Assert.Equal(expected, main.ServiceList.Items.Count);
                    Assert.Equal(expected == 0, model.IsEmpty);
                }
                rows.Add(new { kind = "unmatched-english-backspace-sequence", passed = main.SearchBox.Text == ""
                    && model.TasksView.Cast<object>().Count() == 2, steps = unmatchedStates });
                model.SelectedTask = model.Tasks.First();
                await Task.Delay(700); await Settle(main);
                Move(main, searchPoint); Click(main, searchPoint); await Task.Delay(80); await Settle(main);
                Assert.True(main.SearchBox.IsKeyboardFocusWithin, "Search field did not regain focus; no outside click sent.");
                Move(main, keyboardPoint); Key(0, 'p', 4, keyboardPoint); await Task.Delay(80); await Settle(main);
                Add("matching-query", main.SearchBox.Text == "p" && model.SearchText == "p" && model.TasksView.Cast<object>().Count() == 1);
                var plot = main.ServiceHealthPlot;
                Assert.True(plot.IsVisible && plot.ActualWidth > 0 && plot.ActualHeight > 0);
                var relative = plot.TranslatePoint(new Point(plot.ActualWidth / 2, Math.Min(15, plot.ActualHeight / 2)), main);
                var hit = main.InputHitTest(relative) as DependencyObject;
                Assert.Contains(plot, Ancestors(hit));
                var outside = plot.PointToScreen(new Point(plot.ActualWidth / 2, Math.Min(15, plot.ActualHeight / 2)));
                Assert.Equal(1, NativeHit(main, outside));
                Move(main, outside); Click(main, outside); await Task.Delay(100); await Settle(main);
                Add("click-nonfocusable-plot", !main.SearchBox.IsKeyboardFocusWithin && Keyboard.FocusedElement is not null
                    && main.SearchBox.Text == "p" && model.SearchText == "p");
                CaptureNative(main, Path.Combine(directory, "service-after-outside-click.png"));
                Move(main, keyboardPoint); OwnTarget(main, keyboardPoint, currentCursor: true); Assert.Equal(hwnd, GetForegroundWindow());
                Send([new() { Type = 1, Data = new() { Keyboard = new() { Key = 0x11 } } }]);
                try
                {
                    OwnTarget(main, keyboardPoint, currentCursor: true); Assert.Equal(hwnd, GetForegroundWindow());
                    Send([new() { Type = 1, Data = new() { Keyboard = new() { Key = 0x46 } } },
                        new() { Type = 1, Data = new() { Keyboard = new() { Key = 0x46, Flags = 2 } } }]);
                }
                finally { Send([new() { Type = 1, Data = new() { Keyboard = new() { Key = 0x11, Flags = 2 } } }]); }
                await Task.Delay(80); await Settle(main);
                Add("ctrl-f", main.SearchBox.IsKeyboardFocusWithin && main.SearchBox.SelectionLength == 1 && main.SearchBox.Text == "p");
                var iconPoint = main.SearchSurface.PointToScreen(new Point(16, main.SearchSurface.ActualHeight / 2));
                Assert.Equal(1, NativeHit(main, iconPoint));
                Move(main, iconPoint); Click(main, iconPoint); await Task.Delay(80); await Settle(main);
                Add("click-search-icon", main.SearchBox.IsKeyboardFocusWithin && main.SearchBox.Text == "p");
                Move(main, keyboardPoint); OwnTarget(main, keyboardPoint, currentCursor: true); Assert.Equal(hwnd, GetForegroundWindow());
                Send([new() { Type = 1, Data = new() { Keyboard = new() { Key = 0x09 } } },
                    new() { Type = 1, Data = new() { Keyboard = new() { Key = 0x09, Flags = 2 } } }]);
                await Task.Delay(80); await Settle(main);
                Add("tab-leaves-search", !main.SearchBox.IsKeyboardFocusWithin && Keyboard.FocusedElement is not null && main.SearchBox.Text == "p");
                Move(main, iconPoint); Click(main, iconPoint); await Task.Delay(80); await Settle(main);
                Add("search-icon-focuses", main.SearchBox.IsKeyboardFocusWithin && main.SearchBox.Text == "p");
                Move(main, keyboardPoint); Key(8, '\0', 0, keyboardPoint); await Task.Delay(80); await Settle(main);
                Assert.Equal(2, model.TasksView.Cast<object>().Count());
                model.SelectedTask = model.Tasks.Last(); await Settle(main);
                Assert.Equal("beta-fixture", model.SelectedTask?.Name);
                var row = Assert.IsType<ListBoxItem>(main.ServiceList.ItemContainerGenerator.ContainerFromItem(model.Tasks.First()));
                var rowPoint = row.PointToScreen(new Point(row.ActualWidth / 2, row.ActualHeight / 2));
                Assert.Equal(1, NativeHit(main, rowPoint));
                Move(main, rowPoint); Click(main, rowPoint); await Task.Delay(80); await Settle(main);
                Add("click-service-row", !main.SearchBox.IsKeyboardFocusWithin && row.IsKeyboardFocusWithin
                    && model.SelectedTask?.Name == "alpha-fixture" && main.SearchBox.Text == "");
                Move(main, searchPoint); Click(main, searchPoint); await Task.Delay(80); await Settle(main);
                Assert.True(main.SearchBox.IsKeyboardFocusWithin, "Search field did not regain focus; no caption click sent.");
                Move(main, keyboardPoint); Key(0, 'p', 4, keyboardPoint); await Task.Delay(80); await Settle(main);
                var filter = Assert.Single(Descendants(main).OfType<System.Windows.Controls.Button>(),
                    item => item.IsVisible && AutomationProperties.GetName(item) == "筛选服务");
                var tabRight = main.ViewTabs.TranslatePoint(new Point(main.ViewTabs.ActualWidth, 0), main).X;
                var filterLeft = filter.TranslatePoint(new Point(), main).X;
                Assert.True(filterLeft - tabRight > 10, "No safe caption gap in this test window.");
                var captionPoint = main.PointToScreen(new Point((tabRight + filterLeft) / 2, 24));
                Assert.Equal(2, NativeHit(main, captionPoint));
                Move(main, captionPoint); Click(main, captionPoint); await Task.Delay(80); await Settle(main);
                Add("click-caption", !main.SearchBox.IsKeyboardFocusWithin && Keyboard.FocusedElement is not null && main.SearchBox.Text == "p");
            }
            finally
            {
                if (GetCursorPos(out var currentCursor))
                {
                    var underCursor = WindowFromPoint(currentCursor); GetWindowThreadProcessId(underCursor, out var underPid);
                    events.Add(new { kind = "final-pointer", currentCursor.X, currentCursor.Y, foreground = GetForegroundWindow().ToInt64(),
                        fixture = hwnd.ToInt64(), underCursor = underCursor.ToInt64(), underRoot = GetAncestor(underCursor, 2).ToInt64(), underPid });
                }
                await File.WriteAllTextAsync(Path.Combine(directory, "journey.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
                await File.WriteAllTextAsync(Path.Combine(directory, "input-trace.json"), JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
                main.RemoveHandler(Mouse.PreviewMouseDownEvent, trace); main.RemoveHandler(Mouse.PreviewMouseUpEvent, trace);
                main.RemoveHandler(Keyboard.PreviewKeyDownEvent, keyTrace);
                if (GetForegroundWindow() == hwnd) { SetCursorPos(oldCursor.X, oldCursor.Y); }
                main.WindowState = WindowState.Minimized;
            }
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "journey.json")));
            Assert.Equal(14, document.RootElement.GetArrayLength());
            var failures = document.RootElement.EnumerateArray().Where(row => !row.GetProperty("passed").GetBoolean()).Select(row => row.GetRawText()).ToArray();
            Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures));

            void Setup(string stage) => setup.Add(new { stage, model.AutoRefresh, model.IsBusy, model.IsRefreshing,
                tasks = model.Tasks.Select(task => new { task.Name, task.Enabled, task.Status }).ToArray(),
                configured = source?.Configuration.Configuration.Tasks.Select(task => new { task.Name, task.Enabled }).ToArray() });
            void Key(ushort key, char scan, uint flags, Point cursor)
            {
                OwnTarget(main, cursor, currentCursor: true);
                Assert.Equal(hwnd, GetForegroundWindow());
                Assert.True(main.SearchBox.IsKeyboardFocused, "Search field lost keyboard focus; no key sent.");
                Send([new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Scan = scan, Flags = flags } } },
                    new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Scan = scan, Flags = flags | 2 } } }]);
            }
            void Add(string kind, bool passed) => rows.Add(new
            {
                kind, passed, text = main.SearchBox.Text, model.SearchText, model.IsEmpty,
                tasks = model.Tasks.Select(task => task.Name).ToArray(), visible = model.TasksView.Cast<object>().Count(), model.DashboardEmptyTitle,
                selected = model.SelectedTask?.Name, model.SelectedStatusFilter, model.SelectedTypeFilter,
                focusWithin = main.SearchBox.IsKeyboardFocusWithin, main.IsActive, main.MainScene.Focusable,
                sceneFocused = main.MainScene.IsKeyboardFocused, focused = (Keyboard.FocusedElement as FrameworkElement)?.Name
            });
        }, TimeSpan.FromMinutes(5), live ? (fixture, configuration) => source = new ResourceWindowSource(fixture, configuration) : null);
    }
}
