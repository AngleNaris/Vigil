using System.IO;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

public sealed partial class DashboardMonitoringTests
{
    [Fact]
    public Task AutomaticFullWorkDoesNotInsertAnExtraSnapshotIntoTheSelectedCadence() => WithModel(async (model, adapter, configuration) =>
    {
        model.CurrentPage = MainWindowViewModel.WorkloadsPage;
        model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration)); await model.ResourceHistoryTask;
        await model.RefreshLiveAsync(); var reads = adapter.SnapshotReads;
        model.SelectAutoRefreshIntervalCommand.Execute("10000");
        model.RequestAutomaticFullRefresh();
        var full = model.RefreshCommand.ExecutionTask; Assert.NotNull(full); await full.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(reads, adapter.SnapshotReads);
        await model.RefreshLiveAsync(); Assert.Equal(reads, adapter.SnapshotReads);
    });

    [Fact]
    public Task LiveSnapshotsContinueWhileFullRefreshWaitsForHistory() => WithModel(async (model, adapter, configuration) =>
    {
        model.CurrentPage = MainWindowViewModel.WorkloadsPage;
        model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration));
        model.IsRefreshing = true; var before = adapter.SnapshotReads;
        try { await model.RefreshLiveAsync(); Assert.Equal(before + 1, adapter.SnapshotReads); }
        finally { model.IsRefreshing = false; }
    });

    [Fact]
    public Task HalfSecondCadenceMeasuresFromReadStartRatherThanDelayedCompletion() => WithModel(async (model, adapter, configuration) =>
    {
        model.CurrentPage = MainWindowViewModel.WorkloadsPage;
        var supported = adapter.Snapshot(configuration) with { RecentResourceSchemaVersion = 2, ResourceSamplingIntervalMilliseconds = 500 };
        model.ApplyMonitoringSnapshot(supported); await model.ResourceHistoryTask;
        model.SelectAutoRefreshIntervalCommand.Execute("500"); await model.ResourceHistoryTask;
        adapter.SnapshotReader = async _ => { await Task.Delay(80); return adapter.Snapshot(configuration) with
            { RecentResourceSchemaVersion = 2, ResourceSamplingIntervalMilliseconds = 500 }; };
        var before = adapter.SnapshotReads; await model.RefreshLiveAsync();
        await Task.Delay(440); await model.RefreshLiveAsync();
        Assert.Equal(before + 2, adapter.SnapshotReads);
    });

    [Fact]
    public Task PausingCancelsAutomaticHistoryTriggeredByAnAgentInstanceChange() => WithModel(async (model, adapter, configuration) =>
    {
        model.CurrentPage = MainWindowViewModel.WorkloadsPage;
        model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration)); await model.DashboardHistoryTask;
        var requested = new TaskCompletionSource<MonitoringHistoryQuery>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<MonitoringHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.HistoryReader = (query, _) => { requested.TrySetResult(query); return reply.Task; };
        var newAgent = adapter.Snapshot(configuration) with { InstanceId = Guid.NewGuid().ToString("N") };
        model.ApplyMonitoringSnapshot(newAgent);
        var query = await requested.Task.WaitAsync(TimeSpan.FromSeconds(3)); var automatic = model.DashboardHistoryTask;
        model.AutoRefresh = false;
        reply.TrySetResult(adapter.History(query, ObservationHealth.Healthy)); await automatic;
        Assert.Empty(model.DashboardHealthHistory); Assert.Contains("已暂停刷新", model.DashboardHistoryHelp);
    });

    [Fact]
    public Task AgentInstanceChangeReconcilesHealthEvenAtTheSameWallClockTime() => WithModel(async (model, adapter, configuration) =>
    {
        model.CurrentPage = MainWindowViewModel.WorkloadsPage;
        var first = adapter.Snapshot(configuration); model.ApplyMonitoringSnapshot(first);
        Assert.Equal("OK", model.Tasks[0].Status);
        var changed = first.Tasks.Data!.Select(row => row.Name == model.Tasks[0].Name
            ? row with { Status = "FAIL", Healthy = false, Reason = "new-agent-observation" } : row).ToArray();
        var second = first with { InstanceId = Guid.NewGuid().ToString("N"), Tasks = first.Tasks with { Data = changed } };
        model.ApplyMonitoringSnapshot(second);
        Assert.Equal("FAIL", model.Tasks[0].Status);
        await model.DashboardHistoryTask;
    });

    [Fact]
    public Task AgentInstanceChangeRejectsAnOlderInFlightHealthHistory() => WithModel(async (model, adapter, configuration) =>
    {
        model.CurrentPage = MainWindowViewModel.WorkloadsPage;
        model.ApplyMonitoringSnapshot(adapter.Snapshot(configuration)); await model.DashboardHistoryTask;
        var requested = new TaskCompletionSource<MonitoringHistoryQuery>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<MonitoringHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.HistoryReader = (query, _) => { requested.TrySetResult(query); return reply.Task; };
        model.AutoRefresh = false; model.SelectDashboardWindowCommand.Execute("168");
        var query = await requested.Task.WaitAsync(TimeSpan.FromSeconds(3)); var stale = model.DashboardHistoryTask;
        var newAgent = adapter.Snapshot(configuration) with { InstanceId = Guid.NewGuid().ToString("N") };
        model.ApplyMonitoringSnapshot(newAgent);
        reply.TrySetResult(adapter.History(query, ObservationHealth.Healthy)); await stale;
        Assert.Empty(model.DashboardHealthHistory); Assert.Contains("后台实例已变更", model.DashboardHistoryHelp);
    });

    [Fact]
    public Task RefreshChoicesRequireRealHalfSecondCapabilityAndRemainSessionLocal() => WithModel(async (model, adapter, configuration) =>
    {
        model.CurrentPage = MainWindowViewModel.WorkloadsPage;
        var oldAgent = adapter.Snapshot(configuration);
        model.ApplyMonitoringSnapshot(oldAgent); await model.ResourceHistoryTask;
        Assert.Equal(1000, model.AutoRefreshMilliseconds); Assert.True(model.RefreshOneSecondSelected);
        Assert.False(model.SelectAutoRefreshIntervalCommand.CanExecute("500"));
        Assert.Contains("待更新", model.RefreshHalfSecondLabel);
        foreach (var milliseconds in new[] { "5000", "10000", "1000" })
        {
            Assert.True(model.SelectAutoRefreshIntervalCommand.CanExecute(milliseconds));
            model.SelectAutoRefreshIntervalCommand.Execute(milliseconds); await model.ResourceHistoryTask;
            Assert.Equal(int.Parse(milliseconds), model.AutoRefreshMilliseconds);
        }
        var upgraded = adapter.Snapshot(configuration) with { RecentResourceSchemaVersion = 2, ResourceSamplingIntervalMilliseconds = 500 };
        model.ApplyMonitoringSnapshot(upgraded); await model.ResourceHistoryTask;
        Assert.True(model.CanRefreshAtHalfSecond); Assert.True(model.SelectAutoRefreshIntervalCommand.CanExecute("500"));
        model.SelectAutoRefreshIntervalCommand.Execute("500"); await model.ResourceHistoryTask;
        Assert.True(model.RefreshHalfSecondSelected); Assert.Equal("0.5s", model.DashboardRefreshLabel);
        Assert.Equal(601, adapter.RecentResourceQueries[^1].BucketCount);
        model.SelectAutoRefreshIntervalCommand.Execute("500"); Assert.True(model.RefreshHalfSecondSelected);
        model.AutoRefresh = false; var reads = adapter.RecentResourceQueries.Count;
        model.SelectAutoRefreshIntervalCommand.Execute("10000"); model.TickDashboard();
        Assert.False(model.AutoRefresh); Assert.True(model.RefreshTenSecondsSelected); Assert.Equal(reads, adapter.RecentResourceQueries.Count);
        model.AutoRefresh = true; await model.ResourceHistoryTask;
        model.SelectAutoRefreshIntervalCommand.Execute("500"); await model.ResourceHistoryTask;
        var changedAgent = adapter.Snapshot(configuration) with { InstanceId = Guid.NewGuid().ToString("N") };
        model.ApplyMonitoringSnapshot(changedAgent); await model.ResourceHistoryTask;
        Assert.False(model.CanRefreshAtHalfSecond); Assert.Equal(1000, model.AutoRefreshMilliseconds);
        Assert.Throws<InvalidDataException>(() => model.ApplyMonitoringSnapshot(changedAgent with { RecentResourceSchemaVersion = 2 }));
    });
}
