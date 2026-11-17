using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

// Isolated offscreen WPF: this does not inject OS keyboard events or exercise TSF/IME.
[Collection(nameof(WpfWindowCollection))]
public sealed class ServiceSearchExpiryPresentationTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public Task ClearingSearchRendersListWithoutASecondMonitoringRefresh(bool light, bool expireHealth) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, _) =>
        {
            model.AutoRefresh = false;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(2, model.Tasks.Count);
            main.SearchBox.SetCurrentValue(TextBox.TextProperty, "zz123");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Empty(model.TasksView.Cast<object>());
            Assert.Equal("zz123", model.SearchText);

            if (expireHealth)
            {
                var field = typeof(MainWindowViewModel).GetField("_monitoringSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var snapshot = Assert.IsType<AgentMonitoringSnapshot>(field.GetValue(model));
                var expiredAt = DateTimeOffset.UtcNow.AddSeconds(-31);
                field.SetValue(model, snapshot with { Tasks = snapshot.Tasks with { ObservedAt = expiredAt, AttemptedAt = expiredAt } });
            }

            var watch = Stopwatch.StartNew();
            var resets = new List<double>();
            var stages = new List<object>();
            NotifyCollectionChangedEventHandler changed = (_, args) =>
            { if (args.Action == NotifyCollectionChangedAction.Reset) { resets.Add(watch.Elapsed.TotalMilliseconds); } };
            ((INotifyCollectionChanged)model.TasksView).CollectionChanged += changed;
            try
            {
                main.SearchBox.SetCurrentValue(TextBox.TextProperty, "");
                Capture("text-changed");
                await Dispatcher.Yield(DispatcherPriority.Render); Capture("render-queue");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Capture("idle-queue");
            }
            finally { ((INotifyCollectionChanged)model.TasksView).CollectionChanged -= changed; }

            Assert.Equal("", main.SearchBox.Text); Assert.Equal("", model.SearchText);
            Assert.Equal(2, model.TasksView.Cast<object>().Count());
            Assert.Equal(2, main.ServiceList.Items.Count);
            Assert.True(resets.Count >= (expireHealth ? 2 : 1));
            var list = main.ServiceList;
            var visible = list.Items.Cast<object>().Select(item => list.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem)
                .Where(row => row is not null && ServiceSearchPresentationLatencyTests.HasVisibleText(row, list)).ToArray();
            Assert.Equal(2, visible.Length);

            var dpi = VisualTreeHelper.GetDpi(list);
            // RenderTargetBitmap.Render(visual) includes the root visual's own layout offset.
            var rootOffset = VisualTreeHelper.GetOffset(list);
            var width = (int)Math.Ceiling(list.ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Ceiling(list.ActualHeight * dpi.DpiScaleY);
            Assert.True(width > 0 && height > 0);
            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(list);
            var bitmapAtMs = watch.Elapsed.TotalMilliseconds;
            var stride = checked(width * 4); var pixels = new byte[checked(stride * height)];
            bitmap.CopyPixels(pixels, stride, 0);
            var words = visible.Select(row =>
            {
                var text = Descendants(row!).OfType<TextBlock>().Single(item => item.Text == ((TaskRowViewModel)row!.DataContext).Name);
                var bounds = text.TransformToAncestor(list).TransformBounds(new Rect(text.RenderSize));
                var x0 = Math.Clamp((int)Math.Floor((bounds.Left + rootOffset.X) * dpi.DpiScaleX), 0, width - 1);
                var x1 = Math.Clamp((int)Math.Ceiling((bounds.Right + rootOffset.X) * dpi.DpiScaleX), 0, width);
                var y0 = Math.Clamp((int)Math.Floor((bounds.Top + rootOffset.Y) * dpi.DpiScaleY), 0, height - 1);
                var y1 = Math.Clamp((int)Math.Ceiling((bounds.Bottom + rootOffset.Y) * dpi.DpiScaleY), 0, height);
                var distinct = new HashSet<uint>();
                for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    var offset = y * stride + x * 4;
                    distinct.Add(BitConverter.ToUInt32(pixels, offset));
                    if (distinct.Count > 2) { break; }
                }
                return new { text = text.Text, distinctPixels = distinct.Count, bounds = new { bounds.X, bounds.Y, bounds.Width, bounds.Height }, x0, x1, y0, y1 };
            }).ToArray();

            var directory = Environment.GetEnvironmentVariable("SA_SEARCH_EXPIRY_EVIDENCE");
            if (!string.IsNullOrEmpty(directory))
            {
                Assert.True(Directory.Exists(directory));
                var name = $"search-expiry-{(light ? "light" : "dark")}-{(expireHealth ? "expired" : "fresh")}";
                await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
                { light, expireHealth, method = "offscreen WPF, programmatic TextBox change; no native keyboard/IME or desktop timing", rootOffset = new { rootOffset.X, rootOffset.Y }, resets, stages, bitmapAtMs, words },
                    new JsonSerializerOptions { WriteIndented = true }));
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                await using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
            }
            Assert.All(words, word => Assert.True(word.distinctPixels > 2, $"No visible text pixels for {word.text}: {JsonSerializer.Serialize(word)}"));

            void Capture(string stage)
            {
                var rows = listRows();
                stages.Add(new { stage, elapsedMs = watch.Elapsed.TotalMilliseconds, textbox = main.SearchBox.Text,
                    model.SearchText, viewItems = model.TasksView.Cast<object>().Count(), listItems = main.ServiceList.Items.Count,
                    realized = rows.Count(row => row is not null), visible = rows.Count(row => row is not null && ServiceSearchPresentationLatencyTests.HasVisibleText(row, main.ServiceList)),
                    main.ServiceList.ItemContainerGenerator.Status });
            }
            ListBoxItem?[] listRows() => main.ServiceList.Items.Cast<object>()
                .Select(item => main.ServiceList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem).ToArray();
        });

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public Task ClearingSearchRestoresScrollableViewportAndRetainsIndependentFilters(bool light, bool filtered) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, _) =>
        {
            model.AutoRefresh = false;
            var tasks = Enumerable.Range(1, 120).Select(index =>
                CompactSidebarTests.Snapshot($"R125 synthetic {index:D3}", index % 3 == 0 ? "FAIL" : "OK") with
                { Type = index % 2 == 0 ? "windows" : "wsl_boot" }).ToArray();
            var expected = filtered ? tasks.Count(task => task.Status == "OK" && task.Type == "windows") : tasks.Length;
            CompactSidebarTests.Seed(model, tasks);
            if (filtered)
            {
                model.FilterByStatusCommand.Execute("OK");
                model.SelectDashboardTypeCommand.Execute("windows");
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var list = main.ServiceList;
            var scroll = Assert.Single(Descendants(list).OfType<ScrollViewer>());
            scroll.ScrollToEnd();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var offsetBeforeSearch = scroll.VerticalOffset;
            Assert.True(offsetBeforeSearch > 0);
            main.SearchBox.SetCurrentValue(TextBox.TextProperty, "zz123");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Empty(model.TasksView.Cast<object>());
            main.SearchBox.CaretIndex = main.SearchBox.Text.Length;
            for (var i = 0; i < 4; i++)
            {
                Assert.True(System.Windows.Documents.EditingCommands.Backspace.CanExecute(null, main.SearchBox));
                System.Windows.Documents.EditingCommands.Backspace.Execute(null, main.SearchBox);
            }
            var watch = Stopwatch.StartNew();
            Assert.True(System.Windows.Documents.EditingCommands.Backspace.CanExecute(null, main.SearchBox));
            System.Windows.Documents.EditingCommands.Backspace.Execute(null, main.SearchBox);
            var immediateItems = model.TasksView.Cast<object>().Count();
            var immediateContainers = Containers().Count(row => row is not null);
            await Dispatcher.Yield(DispatcherPriority.Render);
            var renderMs = watch.Elapsed.TotalMilliseconds;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var rows = Containers();
            var visible = rows.Where(row => row is not null && ServiceSearchPresentationLatencyTests.HasVisibleText(row, list))
                .Select(row => ((TaskRowViewModel)row!.DataContext).Name).ToArray();
            Assert.Equal("", main.SearchBox.Text); Assert.Equal("", model.SearchText);
            Assert.Equal(expected, immediateItems); Assert.Equal(expected, model.TasksView.Cast<object>().Count());
            Assert.Equal(expected, list.Items.Count); Assert.NotEmpty(visible);
            Assert.Equal(filtered ? "OK" : "", model.SelectedStatusFilter.Value);
            Assert.Equal(filtered ? "windows" : "", model.SelectedTypeFilter.Value);

            var dpi = VisualTreeHelper.GetDpi(list);
            var width = (int)Math.Ceiling(list.ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Ceiling(list.ActualHeight * dpi.DpiScaleY);
            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(list);
            var directory = Environment.GetEnvironmentVariable("SA_SEARCH_SCROLL_EVIDENCE");
            if (!string.IsNullOrEmpty(directory))
            {
                Assert.True(Directory.Exists(directory));
                var name = $"search-scroll-{(light ? "light" : "dark")}-{(filtered ? "filtered" : "unfiltered")}";
                await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
                {
                    light, filtered, method = "offscreen WPF; synthetic items and routed commands; no OS keyboard/IME or desktop timing",
                    offsetBeforeSearch, offsetAfterClear = scroll.VerticalOffset, scroll.CanContentScroll, scroll.ViewportHeight,
                    virtualizing = VirtualizingPanel.GetIsVirtualizing(list),
                    itemsHost = Descendants(list).OfType<Panel>().FirstOrDefault(panel => panel.IsItemsHost)?.GetType().Name,
                    immediateItems, immediateContainers, renderMs, bitmapAtMs = watch.Elapsed.TotalMilliseconds,
                    realized = rows.Count(row => row is not null), visible
                }, new JsonSerializerOptions { WriteIndented = true }));
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                await using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
            }

            ListBoxItem?[] Containers() => list.Items.Cast<object>()
                .Select(item => list.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem).ToArray();
        });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }
}
