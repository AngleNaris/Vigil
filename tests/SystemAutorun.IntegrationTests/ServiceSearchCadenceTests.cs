using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Models;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed class ServiceSearchCadenceTests
{
    [Theory]
    [InlineData(false, "zz123", false)] [InlineData(true, "zz123", false)]
    [InlineData(false, "zz123", true)] [InlineData(true, "zz123", true)]
    [InlineData(false, "R121", false)] [InlineData(true, "R121", false)]
    [InlineData(false, "R121", true)] [InlineData(true, "R121", true)]
    public Task RapidTextAndBackspaceKeepEmptyQueryConsistent(bool light, string query, bool expireOnClear) =>
        ResourceDashboardWindowTests.WithResourceWindow(light, async (model, main, _) =>
        {
            model.AutoRefresh = false;
            Assert.False(main.IsActive);
            Assert.Equal(2, model.Tasks.Count);
            var snapshotField = typeof(MainWindowViewModel).GetField("_monitoringSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var availableField = typeof(MainWindowViewModel).GetField("_tasksAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var snapshot = Assert.IsType<AgentMonitoringSnapshot>(snapshotField.GetValue(model));
            Assert.True((bool)availableField.GetValue(model)!);
            var resets = 0;
            var steps = new List<object>();
            NotifyCollectionChangedEventHandler changed = (_, args) => { if (args.Action == NotifyCollectionChangedAction.Reset) { resets++; } };
            ((INotifyCollectionChanged)model.TasksView).CollectionChanged += changed;
            Exception? error = null;
            int? resetsBeforeClear = null;
            try
            {
                foreach (var letter in query)
                {
                    ApplyResourceTick();
                    var composition = new TextComposition(InputManager.Current, main.SearchBox, letter.ToString());
                    if (!TextCompositionManager.StartComposition(composition)) { throw new InvalidOperationException("Synthetic text composition was not handled."); }
                    Capture("type");
                }
                var matchingCount = query == "R121" ? 2 : 0;
                if (main.SearchBox.Text != query || model.SearchText != query || model.TasksView.Cast<object>().Count() != matchingCount)
                { throw new InvalidOperationException("Synthetic typing did not reach the intended matched or unmatched state."); }
                main.SearchBox.CaretIndex = main.SearchBox.Text.Length;
                for (var index = query.Length - 1; index >= 0; index--)
                {
                    ApplyResourceTick();
                    if (index == 0)
                    {
                        resetsBeforeClear = resets;
                        if (expireOnClear)
                        {
                            var expiredAt = DateTimeOffset.UtcNow.AddSeconds(-31);
                            snapshotField.SetValue(model, snapshot with { Tasks = snapshot.Tasks with { ObservedAt = expiredAt, AttemptedAt = expiredAt } });
                            if (!(bool)availableField.GetValue(model)!) { throw new InvalidOperationException("Task availability expired before the intended reset."); }
                        }
                    }
                    if (!EditingCommands.Backspace.CanExecute(null, main.SearchBox)) { throw new InvalidOperationException("Synthetic backspace command was rejected."); }
                    EditingCommands.Backspace.Execute(null, main.SearchBox);
                    Capture("backspace");
                }
            }
            catch (Exception exception) { error = exception; }
            finally { ((INotifyCollectionChanged)model.TasksView).CollectionChanged -= changed; }
            await Dispatcher.Yield(DispatcherPriority.Render);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var settled = new { text = main.SearchBox.Text, model.SearchText, tasks = model.Tasks.Count,
                visible = model.TasksView.Cast<object>().Count(), listItems = main.ServiceList.Items.Count,
                listVisible = main.ServiceList.IsVisible, sidebarExpanded = SidebarAssist.GetIsExpanded(main.ServicesPage),
                model.IsEmpty, selected = model.SelectedTask?.Name, model.DashboardEmptyTitle,
                status = model.SelectedStatusFilter.Value, type = model.SelectedTypeFilter.Value,
                main.IsActive, keyboardFocused = main.SearchBox.IsKeyboardFocused };
            var outcome = new { light, query, expireOnClear, error = error?.ToString(), resets, resetsBeforeClear,
                availableAfter = (bool)availableField.GetValue(model)!, steps, settled };
            var directory = Environment.GetEnvironmentVariable("SA_SEARCH_CADENCE_EVIDENCE");
            if (!string.IsNullOrEmpty(directory))
            {
                Assert.True(Directory.Exists(directory));
                await File.WriteAllTextAsync(Path.Combine(directory, $"{(light ? "light" : "dark")}-{query}-{(expireOnClear ? "expired" : "stable")}.json"),
                    JsonSerializer.Serialize(outcome, new JsonSerializerOptions { WriteIndented = true }));
            }
            Assert.Null(error);
            Assert.Equal(query.Length * 2, steps.Count);
            using var stepDocument = JsonDocument.Parse(JsonSerializer.Serialize(steps));
            Assert.All(stepDocument.RootElement.EnumerateArray(), step =>
            {
                var text = step.GetProperty("text").GetString()!;
                var isEmpty = step.GetProperty("IsEmpty").GetBoolean();
                Assert.Equal(text, step.GetProperty("SearchText").GetString());
                Assert.Equal(text.Length, step.GetProperty("CaretIndex").GetInt32());
                Assert.Equal(step.GetProperty("visible").GetInt32() == 0, isEmpty);
                Assert.Equal(isEmpty ? "没有匹配结果" : "选择服务", step.GetProperty("DashboardEmptyTitle").GetString());
            });
            Assert.Equal("", main.SearchBox.Text); Assert.Equal("", model.SearchText);
            Assert.Equal("", model.SelectedStatusFilter.Value); Assert.Equal("", model.SelectedTypeFilter.Value);
            Assert.Equal(2, model.Tasks.Count); Assert.Equal(2, model.TasksView.Cast<object>().Count());
            Assert.Equal(2, main.ServiceList.Items.Count); Assert.True(main.ServiceList.IsVisible);
            Assert.True(SidebarAssist.GetIsExpanded(main.ServicesPage)); Assert.False(model.IsEmpty);
            Assert.NotEqual("没有匹配结果", model.DashboardEmptyTitle);
            Assert.Equal(!expireOnClear, (bool)availableField.GetValue(model)!);
            if (expireOnClear)
            {
                Assert.NotNull(resetsBeforeClear);
                Assert.True(resets - resetsBeforeClear.Value >= 2, "Expected nested expiry reconciliation on the final backspace.");
            }

            void ApplyResourceTick()
            {
                var now = DateTimeOffset.UtcNow;
                var tick = snapshot with { ReturnedAt = now,
                    Host = snapshot.Host with { ObservedAt = now, AttemptedAt = now,
                        Data = snapshot.Host.Data! with { CapturedAt = now } } };
                var before = resets; model.ApplyMonitoringSnapshot(tick);
                if (before != resets) { throw new InvalidOperationException("Unchanged task observations unexpectedly reset the service view."); }
            }
            void Capture(string stage) => steps.Add(new { stage, text = main.SearchBox.Text, model.SearchText,
                codePoints = main.SearchBox.Text.Select(character => (int)character).ToArray(), main.SearchBox.CaretIndex,
                visible = model.TasksView.Cast<object>().Count(), model.IsEmpty, model.DashboardEmptyTitle, resets });
        });
}
