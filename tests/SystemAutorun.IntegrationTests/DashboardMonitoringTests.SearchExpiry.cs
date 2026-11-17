using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

public sealed partial class DashboardMonitoringTests
{
    [Fact]
    public async Task ClearingSearchRestoresRowsAcrossResourceOnlyTicks()
    {
        await WithModel(async (model, adapter, configuration) =>
        {
            var observedAt = DateTimeOffset.UtcNow;
            var fresh = adapter.Snapshot(configuration, observedAt);
            model.ApplyMonitoringSnapshot(fresh);
            var rows = model.Tasks.ToArray();
            Assert.Equal(2, rows.Length);
            model.SearchText = "zz123";
            Assert.Empty(model.TasksView.Cast<object>());
            var resets = 0;
            NotifyCollectionChangedEventHandler changed = (_, args) => { if (args.Action == NotifyCollectionChangedAction.Reset) { resets++; } };
            ((INotifyCollectionChanged)model.TasksView).CollectionChanged += changed;
            var steps = new List<object>();
            try
            {
                var index = 0;
                foreach (var query in new[] { "zz12", "zz1", "zz", "z", "" })
                {
                    if (index > 0) { await Task.Delay(1050); } // Each resource-only read occurs at the selected cadence.
                    var now = DateTimeOffset.UtcNow;
                    var resources = fresh with
                    {
                        ReturnedAt = now,
                        Host = fresh.Host with { ObservedAt = now, AttemptedAt = now,
                            Data = fresh.Host.Data! with { CapturedAt = now, CpuUsagePercent = 20 + index++ } }
                    };
                    adapter.SnapshotReader = _ => Task.FromResult(resources);
                    var beforeReads = adapter.SnapshotReads;
                    var beforeResets = resets;
                    await model.RefreshLiveAsync();
                    var afterTick = resets;
                    model.SearchText = query;
                    var expected = query.Length == 0 ? 2 : 0;
                    var visible = model.TasksView.Cast<object>().Count();
                    steps.Add(new { query, reads = adapter.SnapshotReads - beforeReads, beforeResets, afterTick,
                        tasks = model.Tasks.Count, visible, model.IsEmpty, selected = model.SelectedTask?.Name,
                        model.DashboardEmptyTitle, sameRows = rows.SequenceEqual(model.Tasks),
                        passed = adapter.SnapshotReads == beforeReads + 1 && beforeResets == afterTick
                            && rows.SequenceEqual(model.Tasks) && visible == expected && model.IsEmpty == (expected == 0) });
                }
            }
            finally { ((INotifyCollectionChanged)model.TasksView).CollectionChanged -= changed; }
            var directory = Environment.GetEnvironmentVariable("SA_SEARCH_EXPIRY_EVIDENCE");
            if (!string.IsNullOrEmpty(directory))
            {
                Assert.True(Directory.Exists(directory));
                await File.WriteAllTextAsync(Path.Combine(directory, "resource-only-ticks.json"), JsonSerializer.Serialize(steps, new JsonSerializerOptions { WriteIndented = true }));
            }
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(steps));
            Assert.All(document.RootElement.EnumerateArray(), step => Assert.True(step.GetProperty("passed").GetBoolean(), step.GetRawText()));
            Assert.Null(model.SelectedTask);
            Assert.Equal("选择服务", model.DashboardEmptyTitle);
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ClearingSearchKeepsRowsWhenHealthExpiresDuringViewRefresh(bool filtered)
    {
        await WithModel(async (model, adapter, configuration) =>
        {
            var fresh = adapter.Snapshot(configuration);
            model.ApplyMonitoringSnapshot(fresh);
            Assert.True(TasksAvailable());
            Assert.Equal(2, model.Tasks.Count);
            if (filtered)
            {
                model.FilterByStatusCommand.Execute("OK");
                model.SelectDashboardTypeCommand.Execute("windows");
            }
            model.SearchText = "zz123";
            Assert.True(model.IsEmpty);
            Assert.Empty(model.TasksView.Cast<object>());

            // Model the clock crossing the 30-second freshness boundary immediately before the
            // collection view emits its reset. No production state, sampler, or clock is changed.
            var expiredAt = DateTimeOffset.UtcNow.AddSeconds(-31);
            var expired = fresh with { Tasks = fresh.Tasks with { ObservedAt = expiredAt, AttemptedAt = expiredAt } };
            Field("_monitoringSnapshot").SetValue(model, expired);
            Assert.True(TasksAvailable());
            var resets = 0;
            var observed = new List<object>();
            NotifyCollectionChangedEventHandler changed = (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Reset) { resets++; }
                observed.Add(new { action = args.Action.ToString(), model.SearchText, model.IsEmpty,
                    available = TasksAvailable(), tasks = model.Tasks.Count });
            };
            ((INotifyCollectionChanged)model.TasksView).CollectionChanged += changed;
            Exception? error = null;
            try { model.SearchText = ""; }
            catch (Exception exception) { error = exception; }
            finally { ((INotifyCollectionChanged)model.TasksView).CollectionChanged -= changed; }
            var outcome = new
            {
                filtered, error = error?.ToString(), resets, available = TasksAvailable(), search = model.SearchText,
                status = model.SelectedStatusFilter.Value, type = model.SelectedTypeFilter.Value,
                tasks = model.Tasks.Select(row => new { row.Name, row.Status }).ToArray(),
                visible = model.TasksView.Cast<object>().Count(), model.IsEmpty, selected = model.SelectedTask?.Name,
                model.DashboardEmptyTitle, observed
            };
            var directory = Environment.GetEnvironmentVariable("SA_SEARCH_EXPIRY_EVIDENCE");
            if (!string.IsNullOrEmpty(directory))
            {
                Assert.True(Directory.Exists(directory));
                await File.WriteAllTextAsync(Path.Combine(directory, $"health-expiry-{(filtered ? "filtered" : "all")}.json"), JsonSerializer.Serialize(outcome, new JsonSerializerOptions { WriteIndented = true }));
            }
            Assert.Null(error);
            Assert.False(TasksAvailable());
            Assert.True(resets >= 2, "Expected both the original view reset and the nested health-expiry reconciliation.");
            Assert.Equal("", model.SearchText);
            Assert.Equal(filtered ? "OK" : "", model.SelectedStatusFilter.Value);
            Assert.Equal(filtered ? "windows" : "", model.SelectedTypeFilter.Value);
            Assert.Equal(2, model.Tasks.Count);
            Assert.Equal(filtered ? 0 : 2, model.TasksView.Cast<object>().Count());
            Assert.Equal(filtered, model.IsEmpty);
            if (filtered) { Assert.Equal("没有匹配结果", model.DashboardEmptyTitle); }
            else { Assert.NotEqual("没有匹配结果", model.DashboardEmptyTitle); }

            bool TasksAvailable() => (bool)Field("_tasksAvailable").GetValue(model)!;
            static FieldInfo Field(string name) => typeof(MainWindowViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(MainWindowViewModel).FullName, name);
        });
    }
}
