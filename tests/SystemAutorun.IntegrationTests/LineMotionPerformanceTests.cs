using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using SystemAutorun.App.Services;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class LineMotionPerformanceTests
{
    [Fact]
    public Task FrozenVectorsAreSharedButPlaybackAndTimingAreIndependent() => GuiConfigurationControlTests.RunStaAsync(() =>
    {
        var rest = new SolidColorBrush(Color.FromRgb(91, 92, 93));
        var first = LineMotion.MakeLines(rest, Colors.Red, new Size(321, 45), new Thickness(1), 0);
        var second = LineMotion.MakeLines(rest, Colors.Red, new Size(321, 45), new Thickness(1), 140);
        Assert.NotSame(first, second);
        for (var index = 0; index < first.KeyFrames.Count; index++)
        {
            Assert.NotSame(first.KeyFrames[index], second.KeyFrames[index]);
            Assert.Same(first.KeyFrames[index].Value, second.KeyFrames[index].Value);
            if (index > 0) { Assert.Equal(TimeSpan.FromMilliseconds(140), second.KeyFrames[index].KeyTime.TimeSpan - first.KeyFrames[index].KeyTime.TimeSpan); }
        }
        rest.Opacity = .5;
        var changed = LineMotion.MakeLines(rest, Colors.Red, new Size(321, 45), new Thickness(1), 0);
        Assert.NotSame(first.KeyFrames[5].Value, changed.KeyFrames[5].Value);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ArbitraryWindowSizesCannotGrowTheFrameCacheWithoutBound() => GuiConfigurationControlTests.RunStaAsync(() =>
    {
        for (var index = 0; index < LineMotion.MaximumCachedFrameSets + 20; index++)
        { _ = LineMotion.MakeLines(new SolidColorBrush(Colors.Gray), Colors.Red, new Size(120 + index, 37), new Thickness(1), 0); }
        Assert.InRange(LineMotion.CachedFrameSetCount, 1, LineMotion.MaximumCachedFrameSets);
        return Task.CompletedTask;
    });

    [Fact]
    public Task RepeatedFrozenFramePreparationHasAnObservableAllocationBaseline() =>
        GuiConfigurationControlTests.RunStaAsync(() =>
        {
            var rest = new SolidColorBrush(Color.FromRgb(90, 90, 98)); rest.Freeze();
            const int count = 480;
            for (var index = 0; index < 40; index++) { Prepare(index, false); Prepare(index, true); }
            var samples = new List<object>();
            for (var round = 0; round < 7; round++)
            {
                var cold = Measure(false); var reused = Measure(true);
                Assert.True(reused.Bytes < cold.Bytes, "Frozen-vector reuse must actually reduce managed preparation allocation.");
                samples.Add(new { round, preparations = count,
                    uncachedMilliseconds = cold.Milliseconds, uncachedBytes = cold.Bytes,
                    reusedMilliseconds = reused.Milliseconds, reusedBytes = reused.Bytes });
            }
            if (Environment.GetEnvironmentVariable("SA_MOTION_BENCHMARK") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                File.WriteAllText(output, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
            }
            return Task.CompletedTask;

            (double Milliseconds, long Bytes) Measure(bool reuse)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                for (var index = 0; index < count; index++) { Prepare(index, reuse); }
                watch.Stop();
                return (watch.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated);
            }

            void Prepare(int index, bool reuse)
            {
                var animation = LineMotion.MakeLines(rest, Colors.Red, new Size(180 + index % 8 * 20, 48), new Thickness(1), index % 4 * 40, reuse);
                Assert.Equal(17, animation.KeyFrames.Count);
                Assert.All(animation.KeyFrames.Cast<System.Windows.Media.Animation.ObjectKeyFrame>(), frame => Assert.True(((System.Windows.Media.Brush)frame.Value).IsFrozen));
            }
        });
}
