using System.Diagnostics;
using System.IO;
using SystemAutorun.Core.Models;
using System.Text.Json;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.IntegrationTests;

[CollectionDefinition(nameof(NativeHostSamplingCollection), DisableParallelization = true)]
public sealed class NativeHostSamplingCollection { }

[Collection(nameof(NativeHostSamplingCollection))]
public sealed class HostResourceSamplingTests
{
    [Fact]
    public async Task RealWindowsCountersNeedTwoSamplesReportActualIoAndLeaveSilenceGaps()
    {
        var root = Directory.CreateTempSubdirectory("SystemAutorun-host-resource-tests-").FullName;
        using var reader = new HostStatusReader();
        var readMilliseconds = new List<double>();
        var observations = new List<HostStatusSnapshot>(); var timeline = new List<object>();
        object? ownedWrite = null;
        async Task<HostStatusSnapshot> ReadMeasuredAsync(string stage)
        {
            var before = DateTimeOffset.UtcNow; var started = Stopwatch.GetTimestamp();
            var snapshot = await reader.ReadAsync();
            var ended = Stopwatch.GetTimestamp(); var after = DateTimeOffset.UtcNow;
            var elapsed = Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds;
            readMilliseconds.Add(elapsed); observations.Add(snapshot);
            // These bracket the disk call; CapturedAt is not its exact PDH gate timestamp.
            timeline.Add(new { stage, before, after, started, ended, elapsed, snapshot.CapturedAt,
                snapshot.DiskIoError, snapshot.NetworkRateError });
            return snapshot;
        }
        void Check(bool condition, string message) => Assert.True(condition, condition ? null
            : $"{message}; timeline={JsonSerializer.Serialize(new { timeline, ownedWrite, frequency = Stopwatch.Frequency })}");
        async Task SaveEvidenceAsync(bool passed)
        {
            if (Environment.GetEnvironmentVariable("SA_HOST_RESOURCE_EVIDENCE") is not { Length: > 0 } evidence) { return; }
            Directory.CreateDirectory(evidence);
            await File.WriteAllTextAsync(Path.Combine(evidence, "real-windows-counters.json"), JsonSerializer.Serialize(new
            {
                passed, realWindowsCounters = true, ownedWriteBytes = 2 * 1024 * 1024,
                source = "Whole-host PDH PhysicalDisk(_Total); rates do not attribute IO to the owned file or independently calibrate scale",
                observations = observations.Select(item => new
                { item.CapturedAt, item.DiskReadBytesPerSecond, item.DiskWriteBytesPerSecond, item.DiskIoError,
                    item.NetworkReceiveBytesPerSecond, item.NetworkSendBytesPerSecond, item.NetworkRateError, interfaceCount = item.Networks.Count }),
                readMilliseconds, timeline, ownedWrite, frequency = Stopwatch.Frequency,
                measurementScope = "HostStatusReader only; not complete Agent sampler latency or presented FPS", managedWorkloadControl = false,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        try
        {
            // Prepare/open before the baseline, not inside the bounded two-sample window.
            var file = Path.Combine(root, "owned-counter-write.bin");
            var bytes = new byte[2 * 1024 * 1024]; Random.Shared.NextBytes(bytes);
            await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            var first = await ReadMeasuredAsync("warmup");
            Assert.Null(first.DiskReadBytesPerSecond); Assert.Null(first.DiskWriteBytesPerSecond);
            Assert.Equal("disk-warming-up", first.DiskIoError);
            Assert.Null(first.NetworkReceiveBytesPerSecond); Assert.Null(first.NetworkSendBytesPerSecond);
            Assert.All(first.Networks, nic => Assert.False(string.IsNullOrWhiteSpace(nic.InterfaceId)));
            // The first supplied timestamp precedes cold PDH query initialization. Establish
            // a fresh post-initialization baseline; retain any genuine cold gap in diagnostics.
            var baseline = await ReadMeasuredAsync("post-warmup-baseline");
            var baselineReturned = Stopwatch.GetTimestamp();
            Check(baseline.DiskIoError is null or "disk-sample-gap", baseline.DiskIoError ?? "Unexpected baseline error.");
            var writeBefore = DateTimeOffset.UtcNow; var writeStarted = Stopwatch.GetTimestamp();
            await output.WriteAsync(bytes); output.Flush(flushToDisk: true);
            var writeEnded = Stopwatch.GetTimestamp(); var writeAfter = DateTimeOffset.UtcNow;
            // Target a cadence from baseline completion; a slow flush must not add a whole extra second.
            var remaining = TimeSpan.FromSeconds(1) - Stopwatch.GetElapsedTime(baselineReturned);
            if (remaining > TimeSpan.Zero) { await Task.Delay(remaining); }
            ownedWrite = new { writeBefore, writeAfter, writeStarted, writeEnded, baselineReturned,
                bytes = bytes.Length, requestedWaitMs = Math.Max(0, remaining.TotalMilliseconds) };
            var measured = await ReadMeasuredAsync("after-owned-write");
            Check(measured.DiskReadBytesPerSecond is >= 0, measured.DiskIoError ?? "Disk read rate missing.");
            Check(measured.DiskWriteBytesPerSecond is > 100, measured.DiskIoError ?? $"PhysicalDisk write rate was {measured.DiskWriteBytesPerSecond}; owned flushed write was 2 MiB.");
            Check(measured.DiskIoError is null, measured.DiskIoError ?? "Disk error missing.");
            Check(measured.NetworkReceiveBytesPerSecond is >= 0, measured.NetworkRateError ?? "Receive rate missing.");
            Check(measured.NetworkSendBytesPerSecond is >= 0, measured.NetworkRateError ?? "Send rate missing.");
            await Task.Delay(TimeSpan.FromSeconds(4));
            var gap = await ReadMeasuredAsync("intentional-silence");
            Check(gap.DiskReadBytesPerSecond is null && gap.DiskWriteBytesPerSecond is null && gap.DiskIoError == "disk-sample-gap", "Disk must preserve the silence gap.");
            Check(gap.NetworkReceiveBytesPerSecond is null && gap.NetworkRateError == "network-sample-gap", "Network must preserve the silence gap.");
            await Task.Delay(TimeSpan.FromSeconds(1));
            var resumed = await ReadMeasuredAsync("resumed");
            Check(resumed.DiskReadBytesPerSecond is >= 0, resumed.DiskIoError ?? "Resumed disk read missing.");
            Check(resumed.DiskWriteBytesPerSecond is >= 0, resumed.DiskIoError ?? "Resumed disk write missing.");
            Check(resumed.NetworkReceiveBytesPerSecond is >= 0, resumed.NetworkRateError ?? "Resumed network rate missing.");
            await SaveEvidenceAsync(passed: true);
        }
        catch (Exception failure)
        {
            try { await SaveEvidenceAsync(passed: false); }
            catch (Exception evidenceFailure) { throw new AggregateException("Sampling and its evidence write both failed.", failure, evidenceFailure); }
            throw;
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("SystemAutorun-host-resource-tests-", StringComparison.Ordinal))
            { throw new InvalidOperationException("Refusing to delete a non-owned host resource fixture."); }
            Directory.Delete(resolved, recursive: true);
        }
    }

    [Fact]
    public async Task DisposedHostReaderCannotContinueSampling()
    {
        var reader = new HostStatusReader(); reader.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadAsync());
    }
}
