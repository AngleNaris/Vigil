using System.IO;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.IntegrationTests;

public sealed partial class DashboardMonitoringTests
{
    [Fact]
    public Task LiveResourcesComeOnlyFromAgentAndHiddenPresentationDoesNotBreakCaptureContinuity() => WithModel(async (model, adapter, configuration) =>
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var frames = ResourceFrames(configuration, at, 2).ToList();
        adapter.ResourceReader = (query, _) => Task.FromResult(ResourceHistoryProjection.ProjectRecent(frames, query, DateTimeOffset.UtcNow));
        model.CurrentPage = MainWindowViewModel.WorkloadsPage;
        model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration)); await model.ResourceHistoryTask;
        Assert.Equal(2, model.DashboardResourceCpu.Count); Assert.Equal(.04, model.DashboardResourceCpu[0].Value);
        Assert.Equal(80L << 20, model.DashboardResourceMemory[0].Value); // Snapshot fake's current CPU is 12.5, not this series.
        var published = model.DashboardResourceCpu;
        for (var index = 0; index < 5; index++) { model.TickDashboard(); }
        Assert.Same(published, model.DashboardResourceCpu);
        model.SetPresentationActive(false); var reads = adapter.ResourceQueries.Count;
        frames.Add(ResourceFrames(configuration, at.AddSeconds(2), 1)[0]);
        frames.Add(ResourceFrames(configuration, at.AddSeconds(3), 1)[0]);
        model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration)); model.TickDashboard();
        Assert.Equal(reads, adapter.ResourceQueries.Count); Assert.Same(published, model.DashboardResourceCpu);
        await Task.Delay(1100); // The automatic read is due; returning early does not bypass the selected cadence.
        model.SetPresentationActive(true); await model.ResourceHistoryTask;
        Assert.Equal(4, model.DashboardResourceCpu.Count); Assert.False(model.DashboardResourceCpu[2].BreakBefore);
        var resumed = model.DashboardResourceCpu;
        model.SetPresentationActive(false); model.SetPresentationActive(true); await model.ResourceHistoryTask;
        Assert.Same(resumed, model.DashboardResourceCpu); // Unchanged effective samples do not replay geometry.
    });

    [Fact]
    public Task UnselectedServiceHistoryComesFromTheAgentAndMemoryStaysBytesInAllWindows() => WithModel(async (model, adapter, configuration) =>
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-2);
        var frames = ResourceFrames(configuration, at, 2);
        adapter.ResourceReader = (query, _) => Task.FromResult(query.Source == ResourceHistorySource.RecentSeconds
            ? ResourceHistoryProjection.ProjectRecent(frames, query, DateTimeOffset.UtcNow)
            : ResourceHistoryProjection.ProjectDurable([new(1, "root", "agent", query.To.AddMinutes(-1),
                [new(query.TaskName!, query.TaskIdentity!, ObservationHealth.Unknown, .04, 80L << 20, 1L << 30)])], query, DateTimeOffset.UtcNow));
        model.CurrentPage = MainWindowViewModel.WorkloadsPage;
        model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration)); await model.ResourceHistoryTask;
        model.SelectedTask = model.Tasks.Single(row => row.Name == "beta"); await model.ResourceHistoryTask;
        Assert.Equal(2, model.DashboardResourceCpu.Count); Assert.Equal(.22, model.DashboardResourceCpu[0].Value);
        Assert.Equal(at, model.DashboardResourceCpu[0].At); // Existed before B was ever selected.
        Assert.True(model.DashboardResourceMemoryBytes); Assert.Contains("字节", model.DashboardResourceHelp);
        Assert.Equal(0, model.DashboardWindowHours); Assert.False(model.DashboardHistoryMode);
        Assert.Equal(TimeSpan.FromMinutes(5), model.DashboardResourceDuration); Assert.Equal("最近5分钟", model.DashboardResourceCaption);
        Assert.Equal(ResourceHistorySource.RecentSeconds, adapter.ResourceQueries[^1].Source);
        foreach (var hours in new[] { 24, 168, 720 })
        {
            model.SelectDashboardWindowCommand.Execute(hours.ToString()); await model.ResourceHistoryTask;
            Assert.True(model.DashboardHistoryMode); Assert.Equal(TimeSpan.FromHours(hours), model.DashboardResourceDuration);
            Assert.Equal(80L << 20, Assert.Single(model.DashboardResourceMemory, point => point.Value is not null).Value);
            Assert.Equal(ResourceHistorySource.Durable, adapter.ResourceQueries[^1].Source);
        }
        model.SelectDashboardWindowCommand.Execute("0"); await model.ResourceHistoryTask;
        Assert.Equal(TimeSpan.FromMinutes(5), model.DashboardResourceDuration); Assert.Equal(.22, model.DashboardResourceCpu[0].Value);
        adapter.ResourceReader = (query, _) => Task.FromResult(ResourceHistoryProjection.ProjectRecent([], query, DateTimeOffset.UtcNow));
        var snapshot = adapter.Snapshot(configuration);
        model.ApplyMonitoringSnapshot(snapshot with { InstanceId = Guid.NewGuid().ToString("N") }); await model.ResourceHistoryTask;
        Assert.Empty(model.DashboardResourceCpu); // Restarted Agent has an empty seconds ring, not snapshot/backfilled seconds.
    });

    [Fact]
    public Task StaleResourceRepliesCannotWinAnAToBToASelectionRace() => WithModel(async (model, adapter, configuration) =>
    {
        var pending = new List<(ResourceHistoryQuery Query, CancellationToken Token, TaskCompletionSource<ResourceHistoryResult> Reply)>();
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.ResourceReader = (query, token) =>
        {
            var reply = new TaskCompletionSource<ResourceHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add((query, token, reply)); if (pending.Count == 2) { secondStarted.TrySetResult(); }
            return reply.Task; // Deliberately ignores cancellation.
        };
        model.CurrentPage = MainWindowViewModel.WorkloadsPage;
        model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration)); var first = model.ResourceHistoryTask;
        model.SelectedTask = model.Tasks.Single(row => row.Name == "beta");
        model.SelectedTask = model.Tasks.Single(row => row.Name == "alpha");
        var frames = ResourceFrames(configuration, DateTimeOffset.UtcNow.AddMinutes(-1), 2);
        try
        {
            Assert.Single(pending); Assert.True(pending[0].Token.IsCancellationRequested); Assert.Empty(model.DashboardResourceCpu);
            pending[0].Reply.SetResult(ResourceHistoryProjection.ProjectRecent(frames, pending[0].Query, DateTimeOffset.UtcNow));
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(model.DashboardResourceCpu); Assert.Equal("alpha", pending[1].Query.TaskName);
            pending[1].Reply.SetResult(ResourceHistoryProjection.ProjectRecent(frames, pending[1].Query, DateTimeOffset.UtcNow));
            await first; await model.ResourceHistoryTask;
            Assert.Equal(2, pending.Count); Assert.Equal(.04, model.DashboardResourceCpu[0].Value); Assert.Equal("alpha", model.SelectedTask?.Name);
        }
        finally
        {
            model.AutoRefresh = false;
            foreach (var item in pending.ToArray()) { item.Reply.TrySetResult(ResourceHistoryProjection.ProjectRecent([], item.Query, DateTimeOffset.UtcNow)); }
            await first.WaitAsync(TimeSpan.FromSeconds(2));
        }
    });

    [Fact]
    public Task StaleResourceRepliesCannotWinAWindowRoundTrip() => WithModel(async (model, adapter, configuration) =>
    {
        var pending = new List<(ResourceHistoryQuery Query, CancellationToken Token, TaskCompletionSource<ResourceHistoryResult> Reply)>();
        var nextStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.ResourceReader = (query, token) =>
        {
            var reply = new TaskCompletionSource<ResourceHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add((query, token, reply)); if (pending.Count == 2) { nextStarted.TrySetResult(); }
            return reply.Task; // Transport deliberately ignores cancellation.
        };
        model.CurrentPage = MainWindowViewModel.WorkloadsPage;
        model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration)); var first = model.ResourceHistoryTask;
        model.SelectDashboardWindowCommand.Execute("168"); model.SelectDashboardWindowCommand.Execute("720");
        model.SelectDashboardWindowCommand.Execute("0");
        var frames = ResourceFrames(configuration, DateTimeOffset.UtcNow.AddMinutes(-1), 2);
        ResourceHistoryResult Result(ResourceHistoryQuery query) => query.Source == ResourceHistorySource.RecentSeconds
            ? ResourceHistoryProjection.ProjectRecent(frames, query, DateTimeOffset.UtcNow)
            : ResourceHistoryProjection.ProjectDurable([], query, DateTimeOffset.UtcNow);
        try
        {
            Assert.Single(pending); Assert.True(pending[0].Token.IsCancellationRequested);
            pending[0].Reply.SetResult(Result(pending[0].Query)); await nextStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(model.DashboardResourceCpu); Assert.Equal(ResourceHistorySource.RecentSeconds, pending[1].Query.Source);
            pending[1].Reply.SetResult(Result(pending[1].Query)); await first; await model.ResourceHistoryTask;
            Assert.Equal(2, pending.Count); Assert.Equal(0, model.DashboardWindowHours);
            Assert.Equal(TimeSpan.FromMinutes(5), model.DashboardResourceDuration); Assert.Equal(.04, model.DashboardResourceCpu[0].Value);
        }
        finally
        {
            model.AutoRefresh = false;
            foreach (var item in pending.ToArray()) { item.Reply.TrySetResult(Result(item.Query)); }
            await first.WaitAsync(TimeSpan.FromSeconds(2));
        }
    });

    [Fact]
    public Task HostAndServicePlotsNeverBorrowEachOthersSeriesOrReadInvisibleLogPages() => WithModel(async (model, adapter, configuration) =>
    {
        var frames = ResourceFrames(configuration, DateTimeOffset.UtcNow.AddMinutes(-1), 2);
        adapter.ResourceReader = (query, _) => Task.FromResult(ResourceHistoryProjection.ProjectRecent(frames, query, DateTimeOffset.UtcNow));
        model.CurrentPage = MainWindowViewModel.WorkloadsPage;
        model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration)); await model.ResourceHistoryTask;
        Assert.Empty(model.HostResourceCpu);
        model.CurrentPage = MainWindowViewModel.OverviewPage; await model.ResourceHistoryTask;
        Assert.Empty(model.DashboardResourceCpu); Assert.Equal(.5, model.HostResourceCpu[0].Value);
        Assert.Equal(1000, model.HostResourceMemory[0].Value); Assert.Equal(1, model.HostResourceDiskRead[0].Value);
        Assert.Equal(2, model.HostResourceDiskWrite[0].Value); Assert.Equal(3, model.HostResourceReceive[0].Value); Assert.Equal(4, model.HostResourceSend[0].Value);
        Assert.Null(adapter.ResourceQueries[^1].TaskName); Assert.Contains("虚拟接口", model.HostResourceHelp);
        model.CurrentPage = MainWindowViewModel.ExceptionLogsPage; var reads = adapter.ResourceQueries.Count;
        for (var index = 0; index < 5; index++) { model.TickDashboard(); }
        Assert.Equal(reads, adapter.ResourceQueries.Count); Assert.Empty(model.HostResourceCpu);
    });

    [Fact]
    public Task PausedRefreshStillHonorsASelectionQueuedWhileBusy() => WithModel(async (model, adapter, configuration) =>
    {
        var frames = ResourceFrames(configuration, DateTimeOffset.UtcNow.AddMinutes(-1), 2);
        adapter.ResourceReader = (query, _) => Task.FromResult(ResourceHistoryProjection.ProjectRecent(frames, query, DateTimeOffset.UtcNow));
        model.CurrentPage = MainWindowViewModel.WorkloadsPage;
        model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration)); await model.ResourceHistoryTask;
        model.AutoRefresh = false; model.IsBusy = true;
        model.SelectedTask = model.Tasks.Single(row => row.Name == "beta"); Assert.Empty(model.DashboardResourceCpu);
        model.IsBusy = false; await model.ResourceHistoryTask;
        Assert.Equal(.22, model.DashboardResourceCpu[0].Value);
        var reads = adapter.ResourceQueries.Count; model.TickDashboard(); Assert.Equal(reads, adapter.ResourceQueries.Count);
    });

    [Fact]
    public Task ResourceErrorsAndMalformedRepliesRetainOnlySameTargetTimestampedDataWithoutFallback() => WithModel(async (model, adapter, configuration) =>
    {
        var frames = ResourceFrames(configuration, DateTimeOffset.UtcNow.AddMinutes(-1), 2);
        adapter.ResourceReader = (query, _) => Task.FromResult(ResourceHistoryProjection.ProjectRecent(frames, query, DateTimeOffset.UtcNow));
        model.CurrentPage = MainWindowViewModel.WorkloadsPage;
        model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration)); await model.ResourceHistoryTask;
        var published = model.DashboardResourceCpu; var end = model.DashboardResourceEnd;
        adapter.ResourceReader = (_, _) => throw new IOException("fixture-history-unavailable");
        model.AutoRefresh = false; model.AutoRefresh = true; await model.ResourceHistoryTask;
        Assert.Same(published, model.DashboardResourceCpu); Assert.Equal(end, model.DashboardResourceEnd);
        Assert.Contains("fixture-history-unavailable", model.DashboardResourceHelp);
        model.SelectedTask = model.Tasks.Single(row => row.Name == "beta"); await model.ResourceHistoryTask;
        Assert.Empty(model.DashboardResourceCpu);
        adapter.ResourceReader = (query, _) => Task.FromResult(ResourceHistoryProjection.ProjectRecent(frames, query, DateTimeOffset.UtcNow) with { TaskName = "not-beta" });
        model.AutoRefresh = false; model.AutoRefresh = true; await model.ResourceHistoryTask;
        Assert.Empty(model.DashboardResourceCpu); Assert.Contains("不可用", model.DashboardResourceHelp);
    });

    [Fact]
    public void LegacyOneSecondRepliesCannotConnectAcrossAnUnmarkedMissingCapture()
    {
        var now = DateTimeOffset.UtcNow; var query = new ResourceHistoryQuery(now.AddSeconds(-5), now, 301);
        var frames = new[] { new ResourceHistoryFrame(now.AddSeconds(-4), "fixture-agent", [], new(10, 1000), now.AddSeconds(-4)),
            new ResourceHistoryFrame(now.AddSeconds(-2), "fixture-agent", [], new(20, 1000), now.AddSeconds(-2)) };
        var legacy = ResourceHistoryProjection.ProjectRecent(frames, query, now);
        Assert.False(legacy.Points[1].BreakBefore); ResourceHistoryResponseValidator.Validate(legacy, query);
        var plots = ResourcePlotSeries.From(legacy);
        Assert.True(plots.Cpu[1].BreakBefore); Assert.True(plots.Memory[1].BreakBefore);
    }

    [Fact]
    public void DurablePlotBucketsKeepFractionalTickBoundariesAdjacentAndPreserveMetricNulls()
    {
        var to = DateTimeOffset.UtcNow; var query = new ResourceHistoryQuery(to.AddDays(-1), to, 301, Source: ResourceHistorySource.Durable);
        var result = ResourceHistoryProjection.ProjectDurable([new(1, "root", "agent", to.AddSeconds(-10), [], Host: new(.5, 1000))], query, to);
        ResourceHistoryResponseValidator.Validate(result, query);
        var plots = ResourcePlotSeries.From(result);
        Assert.Equal(301, plots.Cpu.Count);
        for (var index = 1; index < plots.Cpu.Count; index++)
        { Assert.Equal(plots.Cpu[index - 1].To, plots.Cpu[index].From); Assert.True(plots.Cpu[index].At > plots.Cpu[index].From); }
        Assert.All(plots.DiskRead, point => Assert.Null(point.Value)); Assert.Equal(1000, plots.Memory[^1].Value);
        Assert.True(plots.Cpu[^1].BreakBefore); Assert.Equal(query.To, plots.Cpu[^1].To);
    }

    private static ResourceHistoryFrame[] ResourceFrames(ConfigurationWorkspaceSnapshot configuration, DateTimeOffset at, int count) =>
        Enumerable.Range(0, count).Select(index => new ResourceHistoryFrame(at.AddSeconds(index), "fixture-agent",
            configuration.Configuration.Tasks.Select(task => new ResourceTaskSample(task.Name, MonitoringTaskIdentity.For(task), at.AddSeconds(index),
                new string('a', 64), task.Name == "alpha" ? .04 : .22, 80L << 20)).ToArray(), new(CpuPercent: .5, MemoryUsedBytes: 1000, DiskReadBytesPerSecond: 1, DiskWriteBytesPerSecond: 2,
                NetworkReceiveBytesPerSecond: 3, NetworkSendBytesPerSecond: 4), at.AddSeconds(index))).ToArray();
}
