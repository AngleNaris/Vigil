using System.IO;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.IntegrationTests;

public sealed partial class AgentProcessContractsTests
{
    [Fact]
    public async Task RealAgentRestartRetainsActualHostResourcesWithoutBackfillingTheNewSecondRing()
    {
        var output = Environment.GetEnvironmentVariable("SA_AGENT_PROCESS_RESOURCE_EVIDENCE");
        if (!string.IsNullOrEmpty(output)) { Assert.True(Directory.Exists(output)); }
        await using var fixture = new OwnedAgent(); // Its strict empty-task guard stays unchanged.
        var paths = new ProjectPaths(fixture.Root);
        var originalConfiguration = await File.ReadAllBytesAsync(paths.ConfigPath);
        await fixture.StartAsync();
        var first = await WaitForMonitoringAsync(fixture.Client, state => state.Host.Available && state.Persistence.LastWrittenAt is not null);
        AssertCurrentEmptyAgent(first, AgentProtocol.ForRoot(fixture.Root).RootId);
        var firstNative = NativeWitness(first);
        await ResourceProcessEvidence(output, "first-agent.json", first);
        // No GUI or resource-history consumer is attached during this interval.
        await Task.Delay(TimeSpan.FromSeconds(12));
        var sampled = await WaitForMonitoringAsync(fixture.Client, state => state.Persistence.LastWrittenAt > first.Persistence.LastWrittenAt);
        AssertCurrentEmptyAgent(sampled, first.RootId);
        var end = DateTimeOffset.UtcNow;
        var recentQuery = new ResourceHistoryQuery(end.AddMinutes(-5), end);
        var recent = await fixture.Client.SendAsync<ResourceHistoryResult>("resource-history", payload: recentQuery);
        var preciseRecent = await fixture.Client.SendAsync<RecentResourceHistoryResult>("recent-resources-v2", payload: recentQuery with { BucketCount = 601 });
        Assert.Equal(1, recent.SourceIntervalSeconds); Assert.InRange(recent.Points.Count, 5, 301);
        Assert.All(recent.Points, point => { Assert.Equal($"{first.InstanceId}:0", point.ContinuityId); Assert.True(point.MemoryBytes > 0); });
        Assert.Null(preciseRecent.Points[0].DiskReadBytesPerSecond);
        Assert.True(recent.Points[0].DiskReadBytesPerSecond is null || recent.Points[0].BreakBefore);
        Assert.Contains(recent.Points, point => point.DiskReadBytesPerSecond >= 0 && point.DiskWriteBytesPerSecond >= 0);
        var durableQuery = recentQuery with { Source = ResourceHistorySource.Durable };
        var before = await fixture.Client.SendAsync<ResourceHistoryResult>("resource-history", payload: durableQuery);
        Assert.Equal(10, before.SourceIntervalSeconds); Assert.True(before.BucketSeconds >= 10);
        Assert.True(before.Points.Sum(point => point.SampleCount) >= 2);
        Assert.Contains(before.Points, point => point.SampleCount > 0 && point.MemoryBytes > 0 && point.DiskReadBytesPerSecond >= 0);
        Assert.Contains(before.Points, point => point.SampleCount == 0 && point.BreakBefore && point.MemoryBytes is null);
        await ResourceProcessEvidence(output, "before-recent.json", recent);
        await ResourceProcessEvidence(output, "before-durable.json", before);
        await fixture.StopAsync();
        var directory = Path.Combine(fixture.Root, ".autorun-state", CurrentUserIdentity.Key, "monitoring", first.RootId);
        Assert.False(File.Exists(Path.Combine(directory, "writer.lock")));
        var firstFrames = Directory.GetFiles(directory, "history-v1-*.jsonl").Order().SelectMany(File.ReadLines).ToArray();
        Assert.True(firstFrames.Length >= 2);
        await ResourceProcessEvidence(output, "first-segments.json", firstFrames);

        await fixture.StartAsync();
        var second = await WaitForMonitoringAsync(fixture.Client, state => state.Host.Available && state.Persistence.LastWrittenAt is not null);
        AssertCurrentEmptyAgent(second, first.RootId);
        var secondNative = NativeWitness(second);
        Assert.NotEqual(first.ProcessId, second.ProcessId); Assert.NotEqual(first.InstanceId, second.InstanceId);
        Assert.True(secondNative.StartedUtc > firstNative.StartedUtc);
        var after = await fixture.Client.SendAsync<ResourceHistoryResult>("resource-history", payload: durableQuery);
        Assert.Equal(JsonSerializer.Serialize(before, AgentProtocol.JsonOptions), JsonSerializer.Serialize(after, AgentProtocol.JsonOptions));
        var noBackfill = await fixture.Client.SendAsync<ResourceHistoryResult>("resource-history", payload: recentQuery);
        Assert.Empty(noBackfill.Points); Assert.Null(noBackfill.RetainedFrom); Assert.Null(noBackfill.LatestRecordAt);
        await Task.Delay(TimeSpan.FromSeconds(2));
        var secondEnd = DateTimeOffset.UtcNow;
        var restartedQuery = new ResourceHistoryQuery(secondEnd.AddMinutes(-5), secondEnd);
        var restartedRecent = await fixture.Client.SendAsync<ResourceHistoryResult>("resource-history", payload: restartedQuery);
        var preciseRestarted = await fixture.Client.SendAsync<RecentResourceHistoryResult>("recent-resources-v2", payload: restartedQuery with { BucketCount = 601 });
        Assert.NotEmpty(restartedRecent.Points);
        Assert.All(restartedRecent.Points, point => { Assert.Equal($"{second.InstanceId}:0", point.ContinuityId); Assert.True(point.At >= second.StartedAt); });
        Assert.Null(preciseRestarted.Points[0].DiskReadBytesPerSecond);
        Assert.True(restartedRecent.Points[0].DiskReadBytesPerSecond is null || restartedRecent.Points[0].BreakBefore);
        var combined = await fixture.Client.SendAsync<ResourceHistoryResult>("resource-history", payload: durableQuery with { To = secondEnd });
        Assert.True(combined.Points.Sum(point => point.SampleCount) > before.Points.Sum(point => point.SampleCount));
        Assert.Contains(combined.Points, point => point.SampleCount > 0 && point.At >= second.StartedAt
            && (point.BreakBefore || point.ContinuityId != first.InstanceId));
        await ResourceProcessEvidence(output, "after-durable-fixed-window.json", after);
        await ResourceProcessEvidence(output, "after-recent-old-window.json", noBackfill);
        await ResourceProcessEvidence(output, "after-recent.json", restartedRecent);
        await ResourceProcessEvidence(output, "combined-durable.json", combined);
        await ResourceProcessEvidence(output, "second-agent.json", second);
        await fixture.StopAsync();
        Assert.False(File.Exists(Path.Combine(directory, "writer.lock")));
        Assert.Equal(originalConfiguration, await File.ReadAllBytesAsync(paths.ConfigPath));
        await ResourceProcessEvidence(output, "summary.json", new { firstNative, secondNative,
            firstRecentPoints = recent.Points.Count, secondRecentPoints = restartedRecent.Points.Count,
            firstDurableSamples = before.Points.Sum(point => point.SampleCount), combinedDurableSamples = combined.Points.Sum(point => point.SampleCount),
            fixedWindowByteEqual = true, newRingHasNoOldSamples = true, firstDiskSampleUnknownAfterBothStarts = true,
            realWindowsHostResources = true, taskConfigurationEmpty = true, guiExercised = false,
            workloadControlPerformed = false, recoveryWorkerJourneyProved = false, configurationBytesUnchanged = true,
            writerLeaseReleasedAfterBothStops = true });
    }

    private static Task ResourceProcessEvidence(string? output, string name, object value) => string.IsNullOrEmpty(output)
        ? Task.CompletedTask : File.WriteAllTextAsync(Path.Combine(output, name), JsonSerializer.Serialize(value, AgentProtocol.JsonOptions));
}
