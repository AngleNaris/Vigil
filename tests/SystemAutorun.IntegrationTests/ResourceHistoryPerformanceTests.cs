using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.IntegrationTests;

public sealed class ResourceHistoryPerformanceTests
{
    [Fact]
    public async Task ExplicitMatureThirtyDayVolumeMeasuresBoundedStoreQueriesAndRemovesItsDataset()
    {
        var output = Environment.GetEnvironmentVariable("SA_HISTORY_PERFORMANCE_EVIDENCE");
        if (string.IsNullOrEmpty(output)) { return; }
        Assert.True(Directory.Exists(output));
        var root = Directory.CreateTempSubdirectory("SystemAutorun-host-history-schema-").FullName;
        var paths = new ProjectPaths(root); var endpoint = AgentProtocol.ForRoot(root);
        var utc = DateTimeOffset.UtcNow; var end = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
        var clock = new Clock(end); var start = end.AddDays(-30); var instance = Guid.NewGuid().ToString("N");
        var rows = new[] { 'a', 'b', 'c' }.Select(character => new TaskHistorySample("fixture-task-" + character,
            new string(character, 64), ObservationHealth.Healthy, 99, 123456789, 17179869184, ResourceIdentity: new string('d', 64))).ToArray();
        var host = new HostHistorySample(99, 1234567890, 17179869184, 12345678, 12345678, 12345678, 12345678);
        var results = new List<object>(); var succeeded = true;
        try
        {
            using (var store = new MonitoringHistoryStore(paths, clock: clock))
            {
                // Canonical, synthetic sparse archive: 720 hourly chunks, three tasks + host.
                // Ten-second runs have real absent intervals between them. No upsampling or OS data claim.
                MonitoringHistoryFrame Frame(DateTimeOffset at) => new(1, endpoint.RootId, instance, at, rows, Host: host);
                var bytesPerRecord = JsonSerializer.SerializeToUtf8Bytes(Frame(start), AgentProtocol.JsonOptions).Length + 1;
                var framesPerHour = (int)((61L * 1024 * 1024) / (720 * bytesPerRecord));
                Assert.InRange(framesPerHour, 1, 360);
                for (var hour = 0; hour < 720; hour++)
                {
                    var at = start.AddHours(hour);
                    var file = Path.Combine(store.DirectoryPath, $"history-v1-{at:yyyyMMddHH}-{Guid.NewGuid():N}.jsonl");
                    await using var writer = new StreamWriter(file, false, new UTF8Encoding(false)); writer.NewLine = "\n";
                    for (var index = 0; index < framesPerHour; index++)
                    { await writer.WriteLineAsync(JsonSerializer.Serialize(Frame(at.AddSeconds(index * 10)), AgentProtocol.JsonOptions)); }
                }
                var files = Directory.GetFiles(store.DirectoryPath, "history-v1-*.jsonl");
                var bytes = files.Sum(file => new FileInfo(file).Length);
                Assert.InRange(bytes, 59L * 1024 * 1024, store.MaximumBytes);
                using var process = Process.GetCurrentProcess();
                foreach (var days in new[] { 1, 7, 30 }) foreach (var hostQuery in new[] { false, true }) for (var repeat = 0; repeat < 2; repeat++)
                {
                    var cpu = process.TotalProcessorTime; var allocated = GC.GetTotalAllocatedBytes();
                    var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray(); var timer = Stopwatch.StartNew();
                    ResourceHistoryResult? result = null; string? error = null;
                    try
                    {
                        result = await store.QueryResourcesAsync(new(end.AddDays(-days), end, 1000,
                            hostQuery ? null : rows[0].TaskName, hostQuery ? null : rows[0].TaskIdentity, ResourceHistorySource.Durable), instance);
                        Assert.Equal(1000, result.Points.Count); Assert.Contains(result.Points, point => point.BreakBefore);
                        Assert.Contains(result.Points, point => point.SampleCount > 0); Assert.False(result.Truncated, result.Warning);
                    }
                    catch (Exception exception) { succeeded = false; error = exception.GetType().Name + ": " + exception.Message; }
                    process.Refresh();
                    results.Add(new { days, hostQuery, repeat, elapsedMs = timer.Elapsed.TotalMilliseconds,
                        cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                        collections = Enumerable.Range(0, 3).Select(index => GC.CollectionCount(index) - collections[index]).ToArray(),
                        workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64,
                        points = result?.Points.Count, known = result?.Points.Count(point => point.SampleCount > 0), error });
                }
                await File.WriteAllTextAsync(Path.Combine(output, "mature-store.json"), JsonSerializer.Serialize(new
                {
                    scope = "Direct real Store queries over a canonical synthetic 30-day sparse volume, not Agent RPC/presented FPS or uninterrupted 30-day monitoring",
                    files = files.Length, bytes, frames = 720 * framesPerHour, tasks = 3, windowDays = 30, results,
                    recordBytes = bytesPerRecord, maximumStoreBytes = store.MaximumBytes,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            Assert.True(succeeded, "Mature Store query failed; bounded measurements and errors are retained in evidence.");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("SystemAutorun-host-history-schema-", StringComparison.Ordinal)
                || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
            { throw new InvalidOperationException("Refusing non-owned mature dataset cleanup."); }
            Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }
}
