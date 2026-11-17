using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SystemAutorun.App.Controls;
using SystemAutorun.App.Dialogs;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.IntegrationTests;

[Collection(nameof(WpfWindowCollection))]
public sealed partial class CliMonitoringTextTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MainWindowAndIndependentConsoleShareExactHistoryWindowsWithoutSamplingOnRead(bool restart)
    {
        var anchor = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using var original = new MonitorFixture(anchor.AddDays(-8));
        var fixture = original;
        var seeded = new List<(DateTimeOffset At, ObservationHealth Health)>();
        string? seededIdentity = null;
        async Task Seed(DateTimeOffset at, HealthResult health)
        {
            fixture.Clock.Now = at; fixture.Source.Health = health;
            var callsBefore = fixture.Source.Calls;
            await fixture.Sample();
            var snapshot = fixture.Monitor.GetSnapshot();
            var row = snapshot.Tasks.Data?.SingleOrDefault();
            var valid = fixture.Source.Calls == callsBefore + 3 && snapshot.Tasks.Available
                && row is not null && row.Name == "fixture" && row.HealthObserved == health.Observed && row.Healthy == health.Healthy
                && snapshot.Persistence.Available && snapshot.Persistence.Error is null && snapshot.Persistence.LastWrittenAt == at;
            Assert.True(valid, valid ? null : $"Synthetic seed failed at {at:O}: {JsonSerializer.Serialize(new { expected = health, callsBefore, fixture.Source.Calls, snapshot }, AgentProtocol.JsonOptions)}");
            seededIdentity ??= SystemAutorun.Core.Monitoring.MonitoringTaskIdentity.For(row!);
            seeded.Add((at, !health.Observed ? ObservationHealth.Unknown : health.Healthy ? ObservationHealth.Healthy : ObservationHealth.Fault));
        }
        foreach (var (at, health) in new[]
        {
            (anchor.AddDays(-8), HealthResult.Ok("fixture", "synthetic")),
            (anchor.AddDays(-2), HealthResult.Fail("fixture", "synthetic")),
            (anchor.AddHours(-1), HealthResult.Ok("fixture", "synthetic")),
            (anchor.AddMinutes(-54), HealthResult.Fail("fixture", "synthetic")),
        })
        {
            for (var index = 0; index < 30; index++)
            { await Seed(at.AddSeconds(index * 10), health); }
            await Seed(at.AddMinutes(5), HealthResult.Unknown("fixture-unknown", "Synthetic observation boundary."));
        }
        // Query parity requires complete input. A later successful sample must not hide an
        // earlier configuration/persistence failure that legitimately left ten seconds unknown.
        var seededFrames = Directory.EnumerateFiles(fixture.Root, "history-v1-*.jsonl", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<MonitoringHistoryFrame>(line, AgentProtocol.JsonOptions)
                ?? throw new InvalidDataException("Null parity history frame."))
            .OrderBy(frame => frame.ObservedAt).ToArray();
        Assert.Equal(124, seededFrames.Length);
        Assert.Equal(seeded.Select(row => row.At).Order(), seededFrames.Select(frame => frame.ObservedAt));
        foreach (var (expected, frame) in seeded.OrderBy(row => row.At).Zip(seededFrames))
        {
            var row = Assert.Single(frame.Tasks);
            Assert.Equal(original.Monitor.GetSnapshot().InstanceId, frame.AgentInstanceId);
            Assert.Equal("fixture", row.TaskName); Assert.Equal(seededIdentity, row.TaskIdentity);
            Assert.Equal(expected.Health, row.Health); Assert.Equal(frame.ObservedAt.AddSeconds(10), row.ValidUntil);
        }
        if (Environment.GetEnvironmentVariable("SA_PARITY_FRAME_EVIDENCE") is { Length: > 0 } frameEvidence)
        {
            Directory.CreateDirectory(frameEvidence);
            await File.WriteAllTextAsync(Path.Combine(frameEvidence, $"seed-{restart}.json"), JsonSerializer.Serialize(seededFrames, AgentProtocol.JsonOptions));
        }
        // Current process evidence is unavailable even though host uptime is known.
        // Subsequent GUI/CLI queries may use wall-clock bounds, but never resample.
        fixture.Clock.Now = DateTimeOffset.UtcNow;
        fixture.Source.FailProcesses = true;
        await fixture.Sample();
        fixture.Clock.Live = true;
        var originalCalls = fixture.Source.Calls;
        var originalInstance = fixture.Monitor.GetSnapshot().InstanceId;
        var originalConfiguration = await File.ReadAllBytesAsync(new ProjectPaths(fixture.Root).ConfigPath);
        var segments = ParityHistorySegments(fixture.Root); Assert.NotEmpty(segments);
        var stableQueryFile = Path.Combine(fixture.Root, "restart-stable.query.json");
        (int Exit, string Text, string Error) beforeRestart = (-1, "", "");
        if (restart)
        {
            var workspace = await new AgentClient(fixture.Root).SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
            var stable = new MonitoringHistoryQuery("fixture", SystemAutorun.Core.Monitoring.MonitoringTaskIdentity.For(workspace.Configuration.GetTask("fixture")),
                anchor.AddDays(-9), anchor.AddMinutes(-49), 120);
            await File.WriteAllTextAsync(stableQueryFile, JsonSerializer.Serialize(stable, AgentProtocol.JsonOptions));
            var before = Fingerprint(fixture.Root);
            beforeRestart = await ProcessCli(fixture.Root, ["monitor", "history", "--file", stableQueryFile, "--json"]);
            Assert.Equal(0, beforeRestart.Exit); Assert.Empty(beforeRestart.Error);
            using var seededEnvelope = JsonDocument.Parse(beforeRestart.Text);
            var seededHistory = seededEnvelope.RootElement.GetProperty("data").Deserialize<MonitoringHistoryResult>(AgentProtocol.JsonOptions)!;
            Assert.Equal(600d, seededHistory.FaultSeconds);
            Assert.Equal(before, Fingerprint(fixture.Root));
            await original.StopAsync();
        }
        // New server/dispatcher, source, monitor, Journal identity and history writer:
        // same physical root, but no old in-memory monitoring/history objects.
        await using var reopened = restart ? new MonitorFixture(DateTimeOffset.UtcNow, original) : null;
        if (reopened is not null)
        {
            fixture = reopened; fixture.Clock.Live = true;
            Assert.NotSame(original.Monitor, fixture.Monitor);
            Assert.Equal(original.Root, fixture.Root); Assert.Equal(original.Endpoint.RootId, fixture.Endpoint.RootId);
            Assert.Equal(segments, ParityHistorySegments(fixture.Root));
            var before = Fingerprint(fixture.Root);
            var coldStatus = await ProcessCli(fixture.Root, ["monitor", "status", "--json"]);
            Assert.Equal(0, coldStatus.Exit); Assert.Empty(coldStatus.Error);
            using var coldEnvelope = JsonDocument.Parse(coldStatus.Text);
            var cold = coldEnvelope.RootElement.GetProperty("data").Deserialize<AgentMonitoringSnapshot>(AgentProtocol.JsonOptions)!;
            Assert.NotEqual(originalInstance, cold.InstanceId);
            Assert.False(cold.Tasks.Available); Assert.False(cold.Processes.Available); Assert.False(cold.Host.Available);
            Assert.Empty(cold.Runtime); Assert.Null(cold.Persistence.LastWrittenAt);
            var coldHistory = await ProcessCli(fixture.Root, ["monitor", "history", "--file", stableQueryFile, "--json"]);
            Assert.Equal(0, coldHistory.Exit); Assert.Empty(coldHistory.Error);
            using var previousEnvelope = JsonDocument.Parse(beforeRestart.Text);
            using var historyEnvelope = JsonDocument.Parse(coldHistory.Text);
            Assert.Equal(previousEnvelope.RootElement.GetProperty("data").GetRawText(), historyEnvelope.RootElement.GetProperty("data").GetRawText());
            Assert.Equal(0, fixture.Source.Calls); Assert.Equal(originalCalls, original.Source.Calls);
            Assert.Equal(before, Fingerprint(fixture.Root));
            Assert.Equal(originalConfiguration, await File.ReadAllBytesAsync(new ProjectPaths(fixture.Root).ConfigPath));
            if (Environment.GetEnvironmentVariable("SA_STATE_GUI_EVIDENCE") is { Length: > 0 } evidence)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, "restart-before.history.console.ndjson"), beforeRestart.Text);
                await File.WriteAllTextAsync(Path.Combine(evidence, "restart-cold.history.console.ndjson"), coldHistory.Text);
                await File.WriteAllTextAsync(Path.Combine(evidence, "restart-cold.status.console.ndjson"), coldStatus.Text);
                await File.WriteAllTextAsync(Path.Combine(evidence, "restart-stable.query.json"), await File.ReadAllTextAsync(stableQueryFile));
                await File.WriteAllTextAsync(Path.Combine(evidence, "restart-stack.json"), JsonSerializer.Serialize(new
                {
                    fixture.Root, fixture.Endpoint.RootId, OriginalInstance = originalInstance, ReopenedInstance = cold.InstanceId,
                    OriginalSamplingCalls = originalCalls, ColdSamplingCalls = fixture.Source.Calls, HistorySegments = segments,
                    ConfigurationBytesUnchanged = true, ProcessRestart = false, HostObjectStackRebuilt = true,
                }, AgentProtocol.JsonOptions));
            }
            fixture.Source.Health = HealthResult.Unknown("fixture-reopened-unknown", "No current process evidence after host stack rebuild.");
            fixture.Source.FailProcesses = true;
            await fixture.Sample();
        }
        var evidencePrefix = restart ? "restart-" : "";
        var calls = fixture.Source.Calls;
        var paths = new ProjectPaths(fixture.Root);
        var client = new AgentClient(fixture.Root);
        var configuration = await client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        var identity = SystemAutorun.Core.Monitoring.MonitoringTaskIdentity.For(configuration.Configuration.GetTask("fixture"));
        var beforeStatus = Fingerprint(fixture.Root);
        var status = await ProcessCli(fixture.Root, ["monitor", "status", "--json"]);
        Assert.Equal(0, status.Exit); Assert.Empty(status.Error);
        using var statusEnvelope = JsonDocument.Parse(status.Text);
        Assert.True(statusEnvelope.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(fixture.Endpoint.RootId, statusEnvelope.RootElement.GetProperty("rootId").GetString());
        var current = statusEnvelope.RootElement.GetProperty("data").Deserialize<AgentMonitoringSnapshot>(AgentProtocol.JsonOptions)!;
        Assert.True(current.Host.Available); Assert.Equal(TimeSpan.FromHours(3), current.Host.Data!.Uptime);
        Assert.False(current.Processes.Available);
        Assert.False(Assert.Single(current.Tasks.Data!).HealthObserved);
        var runtime = Assert.Single(current.Runtime);
        Assert.Equal(identity, runtime.TaskIdentity);
        Assert.Null(runtime.ProcessStartedAt); Assert.Null(runtime.ConsecutiveHealthySince);
        Assert.Equal(calls, fixture.Source.Calls); Assert.Equal(beforeStatus, Fingerprint(fixture.Root));

        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            if (Application.Current is not SystemAutorun.App.App)
            { var app = new SystemAutorun.App.App(runRuntimeStartup: false); app.InitializeComponent(); }
            WindowTheme.Install();
            var model = new MainWindowViewModel(client, new NativeCacheStore(paths.GuiCachePath),
                new AgentConfigurationClient(client), paths, new ParityNeverRemoteReader()) { AutoRefresh = false };
            var main = new SystemAutorun.App.MainWindow(model, new AgentStartupClient(client), new StartupCacheStore(paths.StartupCachePath))
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, Width = 1320, Height = 820 };
            ColorStateAssist.SetReduceMotion(main, true);
            try
            {
                main.Show();
                await model.InitializeAsync();
                await model.DashboardHistoryTask;
                var selected = Assert.IsType<TaskRowViewModel>(model.SelectedTask);
                Assert.Equal("fixture", selected.Name);
                model.SearchText = "fix";
                Assert.Equal("UNKNOWN", selected.Status);
                Assert.NotEqual("--", model.HostUptime);
                Assert.Equal("--", model.DashboardSelectedUptime);
                Assert.Equal("--", model.DashboardCpuText);
                Assert.Equal("--", model.DashboardWorkingSetText);
                Assert.Empty(model.ManagedProcesses);

                foreach (var hours in new[] { 24, 168, 720 })
                {
                    // R119: invoke the actual bound time-range menu item in the unified header.
                    main.HealthWindowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var item = main.HealthWindowButton.ContextMenu!.Items.OfType<MenuItem>().Single(row =>
                        row.Command == model.SelectDashboardWindowCommand && (string?)row.CommandParameter == hours.ToString(CultureInfo.InvariantCulture));
                    ((IInvokeProvider)new MenuItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    main.HealthWindowButton.ContextMenu.IsOpen = false;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    await model.DashboardHistoryTask;
                    await model.ResourceHistoryTask;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    main.UpdateLayout();
                    Assert.Equal(hours, model.DashboardWindowHours);
                    Assert.Equal("fix", model.SearchText);
                    Assert.Same(selected, model.SelectedTask);

                    var query = new MonitoringHistoryQuery("fixture", identity, model.DashboardHistoryEnd.AddHours(-hours), model.DashboardHistoryEnd, 120);
                    var queryFile = Path.Combine(fixture.Root, $"parity-{hours}.query.json");
                    await File.WriteAllTextAsync(queryFile, JsonSerializer.Serialize(query, AgentProtocol.JsonOptions));
                    var before = Fingerprint(fixture.Root);
                    var console = await ProcessCli(fixture.Root, ["monitor", "history", "--file", queryFile, "--json"]);
                    Assert.Equal(0, console.Exit); Assert.Empty(console.Error);
                    using var envelope = JsonDocument.Parse(console.Text);
                    Assert.True(envelope.RootElement.GetProperty("ok").GetBoolean());
                    Assert.Equal(fixture.Endpoint.RootId, envelope.RootElement.GetProperty("rootId").GetString());
                    var history = envelope.RootElement.GetProperty("data").Deserialize<MonitoringHistoryResult>(AgentProtocol.JsonOptions)!;
                    Assert.Equal(query.From, history.From); Assert.Equal(query.To, history.To);
                    Assert.Equal(query.TaskName, history.TaskName); Assert.Equal(query.TaskIdentity, history.TaskIdentity);
                    Assert.Equal(120, history.Buckets.Count);
                    Assert.False(history.Truncated); Assert.Null(history.Warning);
                    var expectedHealthy = hours == 720 ? 600d : 300d; var expectedFault = hours == 24 ? 300d : 600d;
                    var totalsMatch = history.HealthySeconds == expectedHealthy && history.FaultSeconds == expectedFault;
                    Assert.True(totalsMatch, totalsMatch ? null : $"Parity totals: {JsonSerializer.Serialize(new { hours, query, expectedHealthy, expectedFault, history }, AgentProtocol.JsonOptions)}");
                    Assert.Equal(hours * 3600d - history.HealthySeconds - history.FaultSeconds, history.UnknownSeconds);
                    Assert.Equal(history.HealthySeconds * 100 / (history.HealthySeconds + history.FaultSeconds), history.AvailabilityPercent);
                    Assert.Equal((history.HealthySeconds + history.FaultSeconds) * 100 / (hours * 3600d), history.CoveragePercent);
                    Assert.All(history.Intervals, row => Assert.NotEqual(ObservationHealth.Unknown, row.Health));
                    Assert.True((history.To - history.Intervals[^1].To).TotalMinutes > 30);
                    Assert.Contains(history.Intervals.Zip(history.Intervals.Skip(1)), pair => (pair.Second.From - pair.First.To).TotalSeconds >= 60);
                    Assert.Contains(history.Buckets, row => row.AvailabilityPercent is null && row.CpuPercent is null && row.MemoryPercent is null);
                    Assert.Equal(history.AvailabilityPercent!.Value.ToString("0.0", CultureInfo.CurrentCulture) + "%", model.DashboardAvailabilityText);
                    Assert.Equal(history.CoveragePercent.ToString("0.0", CultureInfo.CurrentCulture) + "%", model.DashboardCoverageText);
                    Assert.Equal(history.Intervals.Select(row => new HealthInterval(row.From, row.To,
                        row.Health == ObservationHealth.Healthy ? "healthy" : row.Health == ObservationHealth.Fault ? "failed" : "unknown")), model.DashboardHealthHistory);
                    Assert.Equal(history.Buckets.Select(row => new PlotPoint(row.From + (row.To - row.From) / 2, row.AvailabilityPercent)), model.DashboardAvailabilityTrend);
                    Assert.Equal(history.Buckets.Select(row => new PlotPoint(row.From + (row.To - row.From) / 2, row.CoveragePercent)), model.DashboardCoverageTrend);
                    Assert.Equal(history.Buckets.Select(row => new PlotPoint(row.From + (row.To - row.From) / 2, row.CpuPercent, row.From, row.To)), model.DashboardCpuTrend);
                    Assert.Equal(history.Buckets.Select(row => new PlotPoint(row.From + (row.To - row.From) / 2, row.MemoryPercent, row.From, row.To)), model.DashboardMemoryTrend);

                    Assert.Equal(model.DashboardAvailabilityText, ParityBoundText(main, nameof(model.DashboardAvailabilityText)).Text);
                    // R121 retains exact coverage in the health DTO/model, but removes its standalone header readout.
                    Assert.DoesNotContain(ParityDescendants(main).OfType<TextBlock>(), text =>
                        BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path.Path == nameof(model.DashboardCoverageText));
                    Assert.Equal("--", ParityBoundText(main, nameof(model.DashboardSelectedUptime)).Text);
                    foreach (var plot in ParityDescendants(main).OfType<DashboardPlot>())
                    {
                        if (plot.Mode == "Health")
                        {
                            Assert.Equal(query.To, plot.EndAt);
                            Assert.Equal(hours, plot.WindowHours); Assert.Equal(model.DashboardHealthHistory, plot.Intervals);
                        }
                        else
                        {
                            // R121 resources have their own exact read boundary, not the health query's wall-clock instant.
                            Assert.Equal(model.DashboardResourceEnd, plot.EndAt);
                            Assert.Equal(TimeSpan.FromHours(hours), plot.WindowDuration);
                            var property = BindingOperations.GetBinding(plot, DashboardPlot.PointsProperty)!.Path.Path;
                            var expected = (IReadOnlyList<PlotPoint>)typeof(MainWindowViewModel).GetProperty(property)!.GetValue(model)!;
                            Assert.Equal(expected, plot.Points);
                        }
                    }
                    Assert.Equal(calls, fixture.Source.Calls);
                    Assert.Equal(before, Fingerprint(fixture.Root));
                    StartupGuiWindowTests.CaptureStateEvidence(main, $"{evidencePrefix}uptime-main-{hours}.png");
                    if (Environment.GetEnvironmentVariable("SA_STATE_GUI_EVIDENCE") is { Length: > 0 } evidence)
                    {
                        if (hours == 24) { await File.WriteAllTextAsync(Path.Combine(evidence, $"{evidencePrefix}uptime-status.console.ndjson"), status.Text); }
                        await File.WriteAllTextAsync(Path.Combine(evidence, $"{evidencePrefix}uptime-{hours}.query.json"), JsonSerializer.Serialize(query, AgentProtocol.JsonOptions));
                        await File.WriteAllTextAsync(Path.Combine(evidence, $"{evidencePrefix}uptime-{hours}.console.ndjson"), console.Text);
                        await File.WriteAllTextAsync(Path.Combine(evidence, $"{evidencePrefix}uptime-{hours}.gui.json"), JsonSerializer.Serialize(new
                        {
                            fixture.Root, fixture.Endpoint.RootId, Width = main.ActualWidth, Height = main.ActualHeight,
                            Dpi = VisualTreeHelper.GetDpi(main).DpiScaleX, model.DashboardWindowHours, model.DashboardHistoryEnd,
                            model.DashboardAvailabilityText, model.DashboardCoverageText, model.DashboardHealthHistory,
                            model.DashboardAvailabilityTrend, model.DashboardCoverageTrend, model.DashboardCpuTrend, model.DashboardMemoryTrend,
                            model.DashboardSelectedUptime, model.HostUptime, model.SearchText, SelectedTask = selected.Name,
                            SyntheticObservations = true, RealAgentPipeAndHistoryStore = true, SamplingCalls = fixture.Source.Calls,
                            HostObjectStackRebuilt = restart, OriginalInstance = originalInstance, CurrentInstance = current.InstanceId, ProcessRestart = false,
                        }, AgentProtocol.JsonOptions));
                    }
                }
            }
            finally
            {
                var closing = typeof(SystemAutorun.App.MainWindow).GetMethod("Window_Closing", BindingFlags.Instance | BindingFlags.NonPublic)!;
                main.Closing -= (CancelEventHandler)closing.CreateDelegate(typeof(CancelEventHandler), main);
                main.Close(); model.DetachDashboard();
            }
        });
        Assert.Equal(calls, fixture.Source.Calls); Assert.Empty(fixture.Errors); Assert.Empty(original.Errors);
        Assert.Equal(originalCalls, original.Source.Calls);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.operation.json", SearchOption.AllDirectories));
        Assert.True(await client.PingAsync(TimeSpan.FromSeconds(2)));
    }

    private static string[] ParityHistorySegments(string root) => Directory.GetFiles(root, "history-v1-*.jsonl", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => path + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    private static TextBlock ParityBoundText(DependencyObject root, string property) => ParityDescendants(root).OfType<TextBlock>()
        .Single(text => BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path.Path == property);
    private static IEnumerable<DependencyObject> ParityDescendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        { var child = VisualTreeHelper.GetChild(root, index); yield return child; foreach (var node in ParityDescendants(child)) { yield return node; } }
    }
    private sealed class ParityNeverRemoteReader : IRemoteHostReader
    {
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition host, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Uptime parity must not connect to remote hosts.");
    }
}
