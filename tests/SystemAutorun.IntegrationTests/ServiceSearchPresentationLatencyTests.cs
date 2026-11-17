using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SystemAutorun.App.ViewModels;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class ServiceSearchPresentationLatencyTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task EmptySearchRecordsViewContainersAndPresentationTiming(bool light)
    {
        ResourceWindowSource? source = null;
        return FakeUiMotionTests.WithMain(light, async (model, main) =>
        {
            Assert.NotNull(source);
            main.Width = 1152; main.Height = 648;
            var names = Enumerable.Range(1, 5).Select(index => $"r124-fixture-{index}").ToArray();
            CompactSidebarTests.Seed(model, names.Select(name => CompactSidebarTests.Snapshot(name)).ToArray());
            await source.ConfigureAsync(model);
            var at = DateTimeOffset.UtcNow;
            source.Observe(at, model.Tasks.Select(task => ResourceWindowSource.Synthetic(task, at, 0)).ToArray(), ResourceWindowSource.SyntheticHost(at, 0));
            source.Apply(model); model.AutoRefresh = true;
            await Task.Delay(1400); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(main.IsActive);
            Assert.Equal(5, model.Tasks.Count);
            Assert.Equal(5, model.TasksView.Cast<object>().Count());
            foreach (var letter in "zz123")
            {
                var composition = new TextComposition(InputManager.Current, main.SearchBox, letter.ToString());
                Assert.True(TextCompositionManager.StartComposition(composition));
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("zz123", main.SearchBox.Text); Assert.True(model.IsEmpty);
            main.SearchBox.CaretIndex = main.SearchBox.Text.Length;
            for (var index = 0; index < 4; index++)
            {
                Assert.True(EditingCommands.Backspace.CanExecute(null, main.SearchBox));
                EditingCommands.Backspace.Execute(null, main.SearchBox);
            }
            Assert.Equal("z", main.SearchBox.Text);
            var watch = Stopwatch.StartNew();
            var samples = new List<PresentationSample>();
            Assert.True(EditingCommands.Backspace.CanExecute(null, main.SearchBox));
            EditingCommands.Backspace.Execute(null, main.SearchBox);
            Capture("synchronous");
            await Dispatcher.Yield(DispatcherPriority.Render); Capture("render-queue");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Capture("idle-queue");
            for (var index = 1; index <= 12; index++) { await Task.Delay(100); Capture($"t+{index * 100}ms"); }
            var firstVisible = samples.FirstOrDefault(sample => sample.VisibleRows == 5);
            var directory = Environment.GetEnvironmentVariable("SA_SEARCH_PRESENTATION_EVIDENCE");
            if (!string.IsNullOrEmpty(directory))
            {
                Assert.True(Directory.Exists(directory));
                await File.WriteAllTextAsync(Path.Combine(directory, $"presentation-{(light ? "light" : "dark")}.json"),
                    JsonSerializer.Serialize(new { light, sampleMethod = "inactive synthetic WPF window; no OS input; not desktop composition",
                        firstVisibleMs = firstVisible?.ElapsedMs, samples }, new JsonSerializerOptions { WriteIndented = true }));
            }
            StartupGuiWindowTests.CaptureStateEvidence(main, $"r124-search-after-{(light ? "light" : "dark")}.png");
            Assert.Equal("", main.SearchBox.Text); Assert.Equal("", model.SearchText);
            Assert.Equal(5, model.TasksView.Cast<object>().Count()); Assert.False(model.IsEmpty);
            Assert.NotNull(firstVisible);

            void Capture(string stage)
            {
                var rows = main.ServiceList.Items.Cast<object>().Select(item => main.ServiceList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem).ToArray();
                var scroll = Descendants(main.ServiceList).OfType<ScrollViewer>().FirstOrDefault();
                var visible = rows.Count(row => row is not null && HasVisibleText(row, main.ServiceList));
                samples.Add(new(stage, watch.Elapsed.TotalMilliseconds, main.SearchBox.Text.Length, model.SearchText?.Length,
                    model.Tasks.Count, model.TasksView.Cast<object>().Count(), main.ServiceList.Items.Count,
                    rows.Count(row => row is not null), visible, main.ServiceList.IsVisible, EffectiveOpacity(main.ServiceList),
                    scroll?.VerticalOffset, scroll?.ExtentHeight, scroll?.ViewportHeight, model.IsEmpty, main.IsActive, main.SearchBox.IsKeyboardFocused));
            }
        }, monitoringFactory: (fixture, configuration) => source = new ResourceWindowSource(fixture, configuration));
    }

    internal static bool HasVisibleText(ListBoxItem row, ListBox list)
    {
        if (!row.IsVisible || EffectiveOpacity(row) < .99 || row.ActualHeight <= 0 || row.ActualWidth <= 0) { return false; }
        Rect bounds;
        try { bounds = row.TransformToAncestor(list).TransformBounds(new Rect(row.RenderSize)); }
        catch (InvalidOperationException) { return false; }
        if (!bounds.IntersectsWith(new Rect(list.RenderSize))) { return false; }
        return Descendants(row).OfType<TextBlock>().Any(text => text.IsVisible && !string.IsNullOrEmpty(text.Text)
            && EffectiveOpacity(text) >= .99 && (text.Clip is null || !text.Clip.Bounds.IsEmpty && text.Clip.Bounds.Width > 0));
    }

    private static double EffectiveOpacity(DependencyObject element)
    {
        var opacity = 1d;
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        { if (current is UIElement visual) { opacity *= visual.Opacity; } }
        return opacity;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }

    private sealed record PresentationSample(string Stage, double ElapsedMs, int TextLength, int? SearchLength,
        int Tasks, int ViewItems, int ListItems, int RealizedRows, int VisibleRows, bool ListVisible, double ListOpacity,
        double? ScrollOffset, double? ScrollExtent, double? ScrollViewport, bool IsEmpty, bool IsActive, bool SearchFocused);
}
