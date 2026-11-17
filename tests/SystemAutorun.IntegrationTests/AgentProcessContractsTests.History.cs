using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.IntegrationTests;

public sealed partial class AgentProcessContractsTests
{
    [Fact]
    public async Task RealProcessRestartPreservesExactArchivedHistoryAcrossIndependentConsoleWindows()
    {
        await using var fixture = new OwnedAgent();
        var paths = new ProjectPaths(fixture.Root); var endpoint = AgentProtocol.ForRoot(fixture.Root);
        var configuration = await File.ReadAllBytesAsync(paths.ConfigPath);
        var archiveAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        var to = archiveAt.AddSeconds(120);
        const string name = "archived-fixture";
        var identity = new string('a', 64); var archiveInstance = Guid.NewGuid().ToString("N");
        string directory;
        // Synthetic archived task, not a real configured workload. Keep OwnedAgent's
        // empty-task assertion intact, and release the seed writer before launching.
        using (var history = new MonitoringHistoryStore(paths))
        {
            directory = history.DirectoryPath;
            for (var i = 0; i < 6; i++)
            {
                var at = archiveAt.AddSeconds(i * 10);
                var health = i < 2 ? ObservationHealth.Healthy : i < 4 ? ObservationHealth.Fault : ObservationHealth.Unknown;
                await history.AppendAsync(new(1, endpoint.RootId, archiveInstance, at,
                    [new(name, identity, health, i < 4 ? 10 + i * 20 : null,
                        i < 4 ? 100 + i * 200 : null, i < 4 ? 1000 : null, at.AddSeconds(10))]));
            }
        }
        Assert.False(File.Exists(Path.Combine(directory, "writer.lock")));
        var archive = HistoryStamps(directory); Assert.NotEmpty(archive);
        var queries = new Dictionary<int, MonitoringHistoryQuery>();
        foreach (var hours in new[] { 24, 168, 720 })
        {
            var query = new MonitoringHistoryQuery(name, identity, to.AddHours(-hours), to, 120);
            queries.Add(hours, query);
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, $"history-{hours}.query.json"), JsonSerializer.Serialize(query, AgentProtocol.JsonOptions));
            await HistoryEvidence($"history-{hours}.query.json", JsonSerializer.Serialize(query, AgentProtocol.JsonOptions));
        }
        var beforeData = new Dictionary<int, string>();
        await fixture.StartAsync();
        var first = await WaitForMonitoringAsync(fixture.Client, state => state.Persistence.LastWrittenAt is not null && state.Host.Available);
        AssertCurrentEmptyAgent(first, endpoint.RootId);
        var firstNative = NativeWitness(first);
        var firstStatus = await HistoryConsole(fixture.Root, ["monitor", "status", "--json"]);
        AssertConsoleInstance(firstStatus, first);
        await HistoryEvidence("before.status.console.ndjson", firstStatus);
        foreach (var (hours, query) in queries)
        {
            var expected = await fixture.Client.SendAsync<MonitoringHistoryResult>("monitoring-history", payload: query);
            AssertArchive(expected, query, archiveAt);
            var text = await HistoryConsole(fixture.Root, ["monitor", "history", "--file", Path.Combine(fixture.Root, $"history-{hours}.query.json"), "--json"]);
            using var json = JsonDocument.Parse(text);
            Assert.Equal(endpoint.RootId, json.RootElement.GetProperty("rootId").GetString());
            var data = json.RootElement.GetProperty("data");
            // JsonElement envelope serialization may escape '+' in timestamps;
            // normalize typed IPC-vs-CLI values, but keep raw CLI-before/after exact.
            Assert.Equal(JsonSerializer.Serialize(expected, AgentProtocol.JsonOptions),
                JsonSerializer.Serialize(data.Deserialize<MonitoringHistoryResult>(AgentProtocol.JsonOptions), AgentProtocol.JsonOptions));
            beforeData.Add(hours, data.GetRawText());
            await HistoryEvidence($"before.history-{hours}.console.ndjson", text);
        }
        Assert.Equal(archive, Restamp(archive));
        Assert.Equal(configuration, await File.ReadAllBytesAsync(paths.ConfigPath));
        await fixture.StopAsync(); // Real shutdown RPC, awaited OS exit=0 and handle release.
        Assert.False(await fixture.Client.PingAsync(TimeSpan.FromMilliseconds(150)));
        Assert.False(File.Exists(Path.Combine(directory, "writer.lock")));
        var stopped = HistoryStamps(directory);
        var firstFrames = ReadHistoryFrames(directory);
        Assert.Contains(firstFrames, frame => frame.AgentInstanceId == first.InstanceId && frame.Tasks.Count == 0);
        Assert.Equal(archive, Restamp(archive));

        await fixture.StartAsync(); // Same physical root, a new actual executable process.
        var second = await WaitForMonitoringAsync(fixture.Client, state => state.Persistence.LastWrittenAt is not null && state.Host.Available);
        AssertCurrentEmptyAgent(second, endpoint.RootId);
        var secondNative = NativeWitness(second);
        Assert.NotEqual(first.InstanceId, second.InstanceId);
        Assert.True(firstNative.ProcessId != secondNative.ProcessId || firstNative.StartedUtc != secondNative.StartedUtc);
        Assert.True(second.StartedAt > first.StartedAt);
        Assert.NotEqual(archiveInstance, first.InstanceId); Assert.NotEqual(archiveInstance, second.InstanceId);
        var afterStatus = await HistoryConsole(fixture.Root, ["monitor", "status", "--json"]);
        AssertConsoleInstance(afterStatus, second);
        await HistoryEvidence("after.status.console.ndjson", afterStatus);
        foreach (var (hours, query) in queries)
        {
            var expected = await fixture.Client.SendAsync<MonitoringHistoryResult>("monitoring-history", payload: query);
            AssertArchive(expected, query, archiveAt);
            var text = await HistoryConsole(fixture.Root, ["monitor", "history", "--file", Path.Combine(fixture.Root, $"history-{hours}.query.json"), "--json"]);
            using var json = JsonDocument.Parse(text);
            Assert.Equal(endpoint.RootId, json.RootElement.GetProperty("rootId").GetString());
            var data = json.RootElement.GetProperty("data");
            Assert.Equal(beforeData[hours], data.GetRawText());
            // JsonElement envelope serialization may escape '+' in timestamps;
            // normalize typed IPC-vs-CLI values, but keep raw CLI-before/after exact.
            Assert.Equal(JsonSerializer.Serialize(expected, AgentProtocol.JsonOptions),
                JsonSerializer.Serialize(data.Deserialize<MonitoringHistoryResult>(AgentProtocol.JsonOptions), AgentProtocol.JsonOptions));
            await HistoryEvidence($"after.history-{hours}.console.ndjson", text);
        }
        Assert.Equal(stopped, Restamp(stopped));
        Assert.Equal(configuration, await File.ReadAllBytesAsync(paths.ConfigPath));
        await fixture.StopAsync();
        Assert.False(await fixture.Client.PingAsync(TimeSpan.FromMilliseconds(150)));
        Assert.False(File.Exists(Path.Combine(directory, "writer.lock")));
        Assert.Equal(stopped, Restamp(stopped));
        var finalFrames = ReadHistoryFrames(directory);
        Assert.All(finalFrames, frame => Assert.Equal(endpoint.RootId, frame.RootId));
        Assert.Equal(6, finalFrames.Count(frame => frame.AgentInstanceId == archiveInstance));
        Assert.Contains(finalFrames, frame => frame.AgentInstanceId == first.InstanceId && frame.Tasks.Count == 0);
        Assert.Contains(finalFrames, frame => frame.AgentInstanceId == second.InstanceId && frame.Tasks.Count == 0);
        Assert.Equal(configuration, await File.ReadAllBytesAsync(paths.ConfigPath));
        await HistoryEvidence("process-restart.json", JsonSerializer.Serialize(new
        {
            fixture.Root, endpoint.RootId, archiveAt, queryTo = to, archiveInstance,
            processRestart = true, gracefulShutdown = true, firstNative, secondNative,
            firstAgentStartedAt = first.StartedAt, secondAgentStartedAt = second.StartedAt,
            firstLastWrittenAt = first.Persistence.LastWrittenAt, secondLastWrittenAt = second.Persistence.LastWrittenAt,
            seedFrames = 6, firstAgentFrames = finalFrames.Count(frame => frame.AgentInstanceId == first.InstanceId),
            secondAgentFrames = finalFrames.Count(frame => frame.AgentInstanceId == second.InstanceId),
            archiveSegments = archive, preRestartSegments = stopped, finalSegments = HistoryStamps(directory),
            historyDataByteEqual = true, configurationBytesUnchanged = true, writerLeaseReleasedAfterBothStops = true,
            taskConfigurationEmpty = true, syntheticArchive = true, zeroNewSampling = false,
            realWorkloadHistory = false, guiExercised = false, fullG3Complete = false,
        }, AgentProtocol.JsonOptions));
    }

    private static void AssertCurrentEmptyAgent(AgentMonitoringSnapshot state, string rootId)
    {
        Assert.Equal(rootId, state.RootId); Assert.True(state.Persistence.Available); Assert.Null(state.Persistence.Error);
        Assert.True(state.Tasks.Available); Assert.Empty(state.Tasks.Data!); Assert.Empty(state.Runtime);
        Assert.True(state.Host.Available); Assert.NotNull(state.Host.Data); Assert.NotNull(state.Persistence.LastWrittenAt);
    }
    private static void AssertConsoleInstance(string text, AgentMonitoringSnapshot state)
    {
        using var doc = JsonDocument.Parse(text);
        var current = doc.RootElement.GetProperty("data").Deserialize<AgentMonitoringSnapshot>(AgentProtocol.JsonOptions)!;
        Assert.Equal(state.RootId, doc.RootElement.GetProperty("rootId").GetString());
        Assert.Equal(state.InstanceId, current.InstanceId); Assert.Equal(state.ProcessId, current.ProcessId);
        Assert.Equal(state.StartedAt, current.StartedAt); AssertCurrentEmptyAgent(current, state.RootId);
    }
    private static void AssertArchive(MonitoringHistoryResult history, MonitoringHistoryQuery query, DateTimeOffset archiveAt)
    {
        Assert.Equal(query.TaskName, history.TaskName); Assert.Equal(query.TaskIdentity, history.TaskIdentity);
        Assert.Equal(query.From, history.From); Assert.Equal(query.To, history.To);
        Assert.Equal(archiveAt.AddSeconds(50), history.LatestRecordAt); Assert.Equal(archiveAt, history.RetainedFrom);
        Assert.Equal(20, history.HealthySeconds); Assert.Equal(20, history.FaultSeconds);
        Assert.Equal((query.To - query.From).TotalSeconds - 40, history.UnknownSeconds);
        Assert.Equal(50, history.AvailabilityPercent); Assert.Equal(40 * 100d / (query.To - query.From).TotalSeconds, history.CoveragePercent);
        Assert.False(history.Truncated); Assert.Null(history.Warning);
        Assert.Equal([new(archiveAt, archiveAt.AddSeconds(20), ObservationHealth.Healthy),
            new HealthHistoryInterval(archiveAt.AddSeconds(20), archiveAt.AddSeconds(40), ObservationHealth.Fault)], history.Intervals);
        Assert.Equal(120, history.Buckets.Count);
        Assert.Equal(query.From, history.Buckets[0].From); Assert.Equal(query.To, history.Buckets[^1].To);
        for (var i = 0; i < history.Buckets.Count; i++)
        {
            var bucket = history.Buckets[i]; var last = i == history.Buckets.Count - 1;
            if (i > 0) { Assert.Equal(history.Buckets[i - 1].To, bucket.From); }
            Assert.Equal(last ? 20 : 0, bucket.HealthySeconds); Assert.Equal(last ? 20 : 0, bucket.FaultSeconds);
            Assert.Equal((bucket.To - bucket.From).TotalSeconds - (last ? 40 : 0), bucket.UnknownSeconds);
            Assert.Equal(last ? 50d : (double?)null, bucket.AvailabilityPercent);
            Assert.Equal(last ? 40 * 100d / (bucket.To - bucket.From).TotalSeconds : 0, bucket.CoveragePercent);
            Assert.Equal(last ? 40d : (double?)null, bucket.CpuPercent); Assert.Equal(last ? 40d : (double?)null, bucket.MemoryPercent);
        }
    }
    private static ProcessHistoryWitness NativeWitness(AgentMonitoringSnapshot state)
    {
        using var process = Process.GetProcessById(state.ProcessId);
        Assert.False(process.HasExited);
        var executable = typeof(AgentProcessContractsTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(item => item.Key == "AgentExecutablePath").Value!;
        var actual = process.MainModule!.FileName;
        Assert.Equal(Path.GetFullPath(executable), actual, ignoreCase: true);
        return new(state.ProcessId, process.StartTime.ToUniversalTime(), state.InstanceId, actual);
    }
    private static HistorySegmentStamp[] HistoryStamps(string directory) => Restamp(Directory.GetFiles(directory, "history-v1-*.jsonl")
        .Order(StringComparer.Ordinal).Select(path => new HistorySegmentStamp(path, "", 0, 0)));
    private static HistorySegmentStamp[] Restamp(IEnumerable<HistorySegmentStamp> segments) => segments.Select(segment =>
    {
        var file = new FileInfo(segment.Path);
        return new HistorySegmentStamp(file.FullName, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.FullName))), file.Length, file.LastWriteTimeUtc.Ticks);
    }).ToArray();
    private static MonitoringHistoryFrame[] ReadHistoryFrames(string directory) => Directory.GetFiles(directory, "history-v1-*.jsonl")
        .SelectMany(File.ReadAllLines).Select(line => JsonSerializer.Deserialize<MonitoringHistoryFrame>(line, AgentProtocol.JsonOptions)!).ToArray();
    private static async Task<string> HistoryConsole(string root, string[] arguments)
    {
        var executable = typeof(AgentProcessContractsTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(item => item.Key == "CliExecutablePath").Value!;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = root };
        foreach (var arg in arguments.Concat(["--root", root, "--timeout", "5", "--non-interactive"])) { start.ArgumentList.Add(arg); }
        using var process = Process.Start(start)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, process.ExitCode); Assert.Empty(await stderr); var text = await stdout;
            using var doc = JsonDocument.Parse(text); Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Null(doc.RootElement.GetProperty("operationId").GetString());
            return text;
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
    }
    private static Task HistoryEvidence(string name, string text)
    {
        var directory = Environment.GetEnvironmentVariable("SA_PROCESS_HISTORY_EVIDENCE");
        return string.IsNullOrEmpty(directory) ? Task.CompletedTask : File.WriteAllTextAsync(Path.Combine(directory, name), text);
    }
    private sealed record ProcessHistoryWitness(int ProcessId, DateTime StartedUtc, string InstanceId, string Executable);
    private sealed record HistorySegmentStamp(string Path, string Sha256, long Length, long LastWriteUtcTicks);
}
