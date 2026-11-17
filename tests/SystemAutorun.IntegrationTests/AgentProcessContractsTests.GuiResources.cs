using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.IntegrationTests;

public sealed partial class AgentProcessContractsTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealAgentKeepsUnselectedTaskHistoryAcrossNormalGuiProcessExitAndRelaunch(bool light)
    {
        var output = Environment.GetEnvironmentVariable("SA_AGENT_GUI_RESOURCE_EVIDENCE");
        if (!string.IsNullOrEmpty(output)) { Assert.True(Directory.Exists(output)); }
        var theme = light ? "light" : "dark";
        var fixture = await R121PolicyProcessFixture.CreateAsync(automationEnabled: false);
        object? summary = null;
        try
        {
            if (!string.IsNullOrEmpty(output))
            { output = Directory.CreateDirectory(Path.Combine(output, theme + "-" + Path.GetFileName(fixture.Root)[^32..])).FullName; }
            var originalConfiguration = await File.ReadAllBytesAsync(fixture.Paths.ConfigPath);
            var pausedBytes = await File.ReadAllBytesAsync(fixture.Control.FilePath);
            var configuration = await new ConfigurationLoader().LoadAsync(fixture.Root);
            var identityB = MonitoringTaskIdentity.For(configuration.GetTask("owned-recovery"));
            using var workerA = fixture.StartWorker("launch"); using var workerB = fixture.StartWorker("recovery");
            var witnessA = await fixture.WaitWorkerAsync("launch"); var witnessB = await fixture.WaitWorkerAsync("recovery");
            Assert.Equal(workerA.Id, witnessA.Pid); Assert.Equal(workerB.Id, witnessB.Pid);
            await fixture.StartAgentAsync();
            var initial = await WaitForMonitoringAsync(fixture.Client, state => state.Processes.Available && state.Host.Available
                && state.Processes.Data!.Count == 2 && state.Processes.Data.All(task => task.Processes.Count == 1 && task.WorkingSetBytes > 0));
            var agent = NativeWitness(initial);
            Assert.Equal(witnessB.Pid, Assert.Single(initial.Processes.Data!.Single(task => task.TaskName == "owned-recovery").Processes).ProcessId);
            await ResourceProcessEvidence(output, theme + "-initial-agent.json", initial);

            await using var firstGui = new OwnedR121ResourceGui(fixture.Root, "first-" + theme);
            var firstA = await firstGui.ReadAsync("a");
            AssertGui(firstA, "owned-launch", witnessA.Pid); Assert.Equal(new[] { "owned-launch" }, firstA.SelectedNames);
            await ResourceProcessEvidence(output, theme + "-first-gui-a.json", firstA);
            await Task.Delay(TimeSpan.FromSeconds(3)); // Agent must sample B while the only GUI stays on A.
            var beforeClose = await RecentB();
            Assert.Contains(beforeClose.Points, point => point.At > firstA.At && point.MemoryBytes > 0);
            await ResourceProcessEvidence(output, theme + "-b-before-ever-selected.json", beforeClose);
            var firstExit = await firstGui.ExitNormallyAsync();
            Assert.Equal(new[] { "owned-launch" }, firstExit.GetProperty("selectedNames").Deserialize<string[]>());
            var closedAt = DateTimeOffset.UtcNow;
            Assert.Equal(agent, NativeWitness(await fixture.Client.SendAsync<AgentMonitoringSnapshot>("monitoring-snapshot")));
            await ResourceProcessEvidence(output, theme + "-first-normal-exit.json", firstExit);

            await Task.Delay(TimeSpan.FromSeconds(12)); // Deliberate real 1-second sampling / 10-second flush interval, no GUI process alive.
            var closedEnd = DateTimeOffset.UtcNow;
            var whileClosed = await RecentB();
            var closedPoints = whileClosed.Points.Where(point => point.At > closedAt && point.At < closedEnd && point.MemoryBytes > 0).ToArray();
            Assert.True(closedPoints.Length >= 5, "B must retain multiple real captures while no GUI process exists.");
            var closedDurable = await fixture.Client.SendAsync<ResourceHistoryResult>("resource-history", payload:
                new ResourceHistoryQuery(closedAt, closedEnd, 301, "owned-recovery", identityB, ResourceHistorySource.Durable));
            Assert.Equal(10, closedDurable.SourceIntervalSeconds);
            Assert.Contains(closedDurable.Points, point => point.SampleCount > 0 && point.MemoryBytes > 0);
            var afterClosed = await fixture.Client.SendAsync<AgentMonitoringSnapshot>("monitoring-snapshot");
            Assert.Equal(agent, NativeWitness(afterClosed)); Assert.True(afterClosed.Persistence.LastWrittenAt > closedAt);
            await ResourceProcessEvidence(output, theme + "-b-while-gui-closed-recent.json", whileClosed);
            await ResourceProcessEvidence(output, theme + "-b-while-gui-closed-durable.json", closedDurable);

            await using var secondGui = new OwnedR121ResourceGui(fixture.Root, "second-" + theme);
            var secondA = await secondGui.ReadAsync("a");
            Assert.NotEqual(firstA.Pid, secondA.Pid); Assert.True(secondA.StartedUtc > firstA.StartedUtc);
            AssertGui(secondA, "owned-launch", witnessA.Pid); Assert.Equal(new[] { "owned-launch" }, secondA.SelectedNames);
            await ResourceProcessEvidence(output, theme + "-second-gui-before-b.json", secondA);
            await secondGui.RequestSelectionAsync();
            var firstB = await secondGui.ReadAsync("b-live");
            AssertGui(firstB, "owned-recovery", witnessB.Pid);
            Assert.Equal(new[] { "owned-launch", "owned-recovery" }, firstB.SelectedNames);
            Assert.Equal(1, firstB.Resource.SourceIntervalSeconds);
            foreach (var point in beforeClose.Points.Where(point => point.MemoryBytes > 0).Concat(closedPoints))
            { Assert.Contains(point, firstB.Resource.Points); }
            await CompareCapturedPrefix(firstB.Resource);
            var historyB = await secondGui.ReadAsync("b-history");
            AssertGui(historyB, "owned-recovery", witnessB.Pid);
            Assert.Equal(10, historyB.Resource.SourceIntervalSeconds); Assert.True(historyB.Resource.BucketSeconds >= 10);
            var historyKnown = Assert.Single(historyB.Resource.Points, point => point.SampleCount > 0);
            Assert.True(historyKnown.SampleCount >= closedDurable.Points.Sum(point => point.SampleCount));
            Assert.True(historyKnown.At >= closedDurable.Points.Where(point => point.SampleCount > 0).Max(point => point.At));
            await CompareCapturedPrefix(historyB.Resource);
            var finalB = await secondGui.ReadAsync("b-final-live"); AssertGui(finalB, "owned-recovery", witnessB.Pid);
            await ResourceProcessEvidence(output, theme + "-second-gui-first-b-live.json", firstB);
            await ResourceProcessEvidence(output, theme + "-second-gui-b-history.json", historyB);
            await ResourceProcessEvidence(output, theme + "-second-gui-final-b-live.json", finalB);
            if (Environment.GetEnvironmentVariable("SA_AGENT_GUI_RESOURCE_VISIBLE") == "1")
            { await Task.Delay(TimeSpan.FromSeconds(40)); } // Bounded optional pixel-inspection hold; never an FPS benchmark.
            var secondExit = await secondGui.ExitNormallyAsync();
            Assert.Equal(new[] { "owned-launch", "owned-recovery" }, secondExit.GetProperty("selectedNames").Deserialize<string[]>());
            await ResourceProcessEvidence(output, theme + "-second-normal-exit.json", secondExit);
            var finalAgent = await fixture.Client.SendAsync<AgentMonitoringSnapshot>("monitoring-snapshot");
            Assert.Equal(agent, NativeWitness(finalAgent));
            Assert.Equal(2, fixture.ReadWitnesses().Length); Assert.True(fixture.IsRunning(witnessA)); Assert.True(fixture.IsRunning(witnessB));
            Assert.Equal(originalConfiguration, await File.ReadAllBytesAsync(fixture.Paths.ConfigPath));
            Assert.Equal(pausedBytes, await File.ReadAllBytesAsync(fixture.Control.FilePath));
            await fixture.StopAgentAsync();
            foreach (var pair in new[] { (workerA, witnessA), (workerB, witnessB) })
            {
                await File.WriteAllTextAsync(Path.Combine(fixture.Root, $"exit-{pair.Item2.Pid}"), "owned GUI resource test cleanup");
                await pair.Item1.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, pair.Item1.ExitCode);
            }
            summary = new { theme, root = fixture.Root, agent, witnessA, witnessB, guiOne = new { firstA.Pid, firstA.StartedUtc, firstA.Image },
                guiTwo = new { secondA.Pid, secondA.StartedUtc, secondA.Image }, closedAt, closedEnd, closedSamples = closedPoints.Length,
                capturedKnownRecentAndDurablePrefixesMatchAgent = true, preCloseAndClosedRecentSamplesRetainedAtFirstBSelection = true,
                actualWpfPlotBindingsEqualAgentProjection = true,
                actualAppRequestExitClosingAndShutdown = true, firstGuiFallbackKills = firstGui.FallbackKills,
                secondGuiFallbackKills = secondGui.FallbackKills, agentIdentityStableAcrossBothGuiExits = true,
                automationPoliciesExplicitlyDisabled = true, workerWitnessCount = fixture.ReadWitnesses().Length,
                configurationAndRecoveryBytesUnchanged = true, physicalInput = false, productionWorkloadsTouched = false };

            async Task<ResourceHistoryResult> RecentB()
            {
                var end = DateTimeOffset.UtcNow;
                return await fixture.Client.SendAsync<ResourceHistoryResult>("resource-history", payload:
                    new ResourceHistoryQuery(end.AddMinutes(-5), end, 301, "owned-recovery", identityB));
            }
            void AssertGui(R121GuiObservation gui, string name, int workerPid)
            {
                Assert.Equal(name, gui.Selected); Assert.Equal(light, gui.Light);
                Assert.Equal(agent.ProcessId, gui.AgentPid); Assert.Equal(agent.InstanceId, gui.AgentInstanceId);
                Assert.Equal(new[] { workerPid }, gui.SelectedProcessIds); Assert.NotEqual(0, gui.Hwnd);
                Assert.Contains(gui.Memory, point => point.Value > 0); Assert.NotEqual("--", gui.WorkingSetText);
            }
            async Task CompareCapturedPrefix(ResourceHistoryResult gui)
            {
                var reread = await fixture.Client.SendAsync<ResourceHistoryResult>("resource-history", payload:
                    new ResourceHistoryQuery(gui.From, gui.LatestRecordAt!.Value, 301, gui.TaskName, gui.TaskIdentity, gui.Source));
                // A live query's To is not a snapshot revision: in-flight newer captures may append later.
                // Compare the already captured prefix, not the still-open tail. This <90s fixture occupies
                // one known ~287s durable bucket; changing To to the last capture does not split its data.
                if (gui.Source == ResourceHistorySource.RecentSeconds)
                {
                    foreach (var point in gui.Points.Where(point => point.At <= gui.LatestRecordAt.Value))
                    { Assert.Contains(point, reread.Points); }
                }
                else
                {
                    var expected = Assert.Single(gui.Points, point => point.SampleCount > 0);
                    Assert.Equal(expected, Assert.Single(reread.Points, point => point.SampleCount > 0));
                }
            }
        }
        finally { await fixture.DisposeAsync(); }
        Assert.False(Directory.Exists(fixture.Root)); Assert.Equal(0, fixture.FallbackKills);
        await ResourceProcessEvidence(output, theme + "-summary.json", new { journey = summary, fixtureRootRemoved = true, cleanupFallbackKills = fixture.FallbackKills });
    }

    private sealed class OwnedR121ResourceGui : IAsyncDisposable
    {
        private readonly string _root, _session, _image, _hash;
        private readonly Process _process;
        private readonly DateTime _started;
        private readonly Task<string> _stdout, _stderr;
        internal int FallbackKills { get; private set; }
        internal OwnedR121ResourceGui(string root, string session)
        {
            _root = R121PolicyProcessFixture.RequireRoot(root); _session = session;
            _image = R121ResourceGuiHost.Image(root, session);
            Assert.False(File.Exists(_image));
            var source = Path.ChangeExtension(typeof(StartupMutationAdapterTests).Assembly.Location, ".exe");
            _hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
            Process? started = null;
            try
            {
                File.Copy(source, _image);
                var start = new ProcessStartInfo(_image)
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var value in new[] { "--r121-resource-gui", root, session }) { start.ArgumentList.Add(value); }
                started = Process.Start(start) ?? throw new IOException("Owned GUI process did not start.");
                _started = started.StartTime.ToUniversalTime(); _stdout = started.StandardOutput.ReadToEndAsync(); _stderr = started.StandardError.ReadToEndAsync();
                _process = started;
            }
            catch
            {
                if (started is not null && !started.HasExited)
                {
                    Assert.Equal(_image, started.MainModule!.FileName); started.Kill(entireProcessTree: true); started.WaitForExit();
                }
                started?.Dispose();
                Assert.Equal(R121ResourceGuiHost.Image(_root, _session), _image);
                if (File.Exists(_image))
                { Assert.False((File.GetAttributes(_image) & FileAttributes.ReparsePoint) != 0); File.Delete(_image); }
                throw;
            }
        }
        internal Task RequestSelectionAsync() => File.WriteAllTextAsync(R121ResourceGuiHost.FileName(_root, _session, "select-b"), "owned semantic WPF selection");
        internal async Task<R121GuiObservation> ReadAsync(string phase)
        {
            var watch = Stopwatch.StartNew(); var path = R121ResourceGuiHost.FileName(_root, _session, phase);
            while (watch.Elapsed < TimeSpan.FromSeconds(18))
            {
                var error = Path.Combine(_root, $"gui-{_session}.error.txt");
                if (File.Exists(error)) { throw new InvalidOperationException(await File.ReadAllTextAsync(error)); }
                if (File.Exists(path))
                {
                    var result = JsonSerializer.Deserialize<R121GuiObservation>(await File.ReadAllTextAsync(path), AgentProtocol.JsonOptions)!;
                    Assert.Equal(_process.Id, result.Pid); Assert.Equal(_started, result.StartedUtc); Assert.Equal(_image, result.Image);
                    return result;
                }
                if (_process.HasExited) { throw new IOException($"Owned GUI exited {_process.ExitCode}: {await _stderr} {await _stdout}"); }
                await Task.Delay(50);
            }
            throw new TimeoutException($"Owned GUI {_session}/{phase} observation did not arrive.");
        }
        internal async Task<JsonElement> ExitNormallyAsync()
        {
            var request = R121ResourceGuiHost.FileName(_root, _session, "exit-request");
            if (!File.Exists(request)) { await File.WriteAllTextAsync(request, "normal App.RequestExit only"); }
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            Assert.True(_process.ExitCode == 0, $"Owned GUI exit {_process.ExitCode}: {await _stderr} {await _stdout}");
            var exit = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(R121ResourceGuiHost.FileName(_root, _session, "exit")), AgentProtocol.JsonOptions);
            Assert.True(exit.GetProperty("isExiting").GetBoolean()); Assert.True(exit.GetProperty("requestedExit").GetBoolean());
            Assert.Equal(1, exit.GetProperty("closed").GetInt32()); Assert.Equal(0, exit.GetProperty("starterCalls").GetInt32());
            Assert.True(exit.GetProperty("cancelledUserClose").GetBoolean()); Assert.True(exit.GetProperty("closeAttempts").GetInt32() >= 2);
            var lifecycleLog = Path.Combine(_root, "logs", "app-lifecycle.log");
            Assert.False(File.Exists(lifecycleLog), File.Exists(lifecycleLog) ? await File.ReadAllTextAsync(lifecycleLog) : "Unexpected lifecycle log.");
            return exit;
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    try { await ExitNormallyAsync(); }
                    catch
                    {
                        if (!_process.HasExited)
                        {
                            Assert.Equal(_started, _process.StartTime.ToUniversalTime()); Assert.Equal(_image, _process.MainModule!.FileName);
                            _process.Kill(entireProcessTree: true); FallbackKills++; await _process.WaitForExitAsync();
                        }
                    }
                }
            }
            finally
            {
                _process.Dispose(); Assert.Equal(R121ResourceGuiHost.Image(_root, _session), _image);
                Assert.Equal(_hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_image))));
                Assert.False((File.GetAttributes(_image) & FileAttributes.ReparsePoint) != 0); File.Delete(_image);
            }
        }
    }
}
