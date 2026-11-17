using System.IO;
using System.Text.Json;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.IntegrationTests;

// Actual typed Pipe/Agent/configuration + STA view models, but a controlled metadata
// getter. These tests do not claim native data, physical input or screenshot acceptance.
public sealed class StartupGuiControlPlaneTests
{
    internal static StartupEntry Entry => StartupAdoptionControlPlaneTests.Entry;

    [Fact]
    public async Task GuiSharesAgentReviewAndCannotInvokeLegacyNativePathsEvenWithCanModifyTrue()
    {
        var reader = new Reader(); await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: reader);
        var yaml = await File.ReadAllBytesAsync(server.Paths.ConfigPath);
        await Sta(server, async model =>
        {
            await model.InitializeAsync(); model.SelectedEntry = Assert.Single(model.Entries);
            Assert.False(model.IsCached); Assert.Equal("已登记", model.SelectedEntry.StatusDescription);
            Assert.True(model.InspectCommand.CanExecute(null)); Assert.True(model.AddToTasksCommand.CanExecute(null));
            Assert.False(model.DisableCommand.CanExecute(null)); Assert.False(model.RestoreCommand.CanExecute(null));
            model.DisableCommand.Execute(null); model.RestoreCommand.Execute(null);
            await model.InspectCommand.ExecuteAsync(null);
            Assert.True(model.ShowDetails); Assert.Equal(Entry.MetadataDigest, model.Inspection!.MetadataDigest);
            Assert.False(model.Inspection.NativeWriteReady); Assert.Contains("未检查", model.DetailText);
            Assert.Equal(2, reader.Calls); Assert.False(File.Exists(server.Paths.StartupBackupPath));
        });
        Assert.Equal(yaml, await File.ReadAllBytesAsync(server.Paths.ConfigPath));
        Assert.Empty((await server.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration.Tasks);
    }

    [Fact]
    public async Task UnsupportedAndPartialMetadataRemainVisibleReadOnlyAndNotACompleteSuccessfulInventory()
    {
        var bad = Entry with { Id = "unsupported", Name = "旧版驱动配置", Error = "unsupported_registry_value_kind", Target = null, RawCommand = null, CanModify = true };
        var reader = new Reader { Entries = [bad], Issues = [new("HKLM", "permission_denied")], Truncated = true };
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: reader);
        await Sta(server, async model =>
        {
            await model.InitializeAsync(); model.SelectedEntry = Assert.Single(model.Entries);
            Assert.True(model.HasNotice); Assert.Equal("部分观测", model.ObservationLabel); Assert.Contains("截断", model.CountMessage);
            Assert.Equal("旧版驱动配置", model.SelectedEntry.Name); Assert.Equal("不可读 · 只读", model.SelectedEntry.StatusDescription);
            Assert.Empty(model.SelectedEntry.Target); Assert.False(model.AddToTasksCommand.CanExecute(null));
            Assert.False(model.DisableCommand.CanExecute(null)); Assert.False(model.RestoreCommand.CanExecute(null));
            await model.AddToTasksCommand.ExecuteAsync(null); Assert.Equal(1, reader.Calls);
            model.ShowDiagnosticsCommand.Execute(null); Assert.Contains("permission_denied", model.DetailText);
            Assert.Contains("unsupported_registry_value_kind", model.DetailText);
        });
    }

    [Fact]
    public async Task CacheIsOnlyDisplayWhileCurrentGetterIsPendingAndFreshFailureDoesNotEraseRows()
    {
        var reader = new Reader(); await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: reader);
        var cache = new StartupCacheStore(server.Paths.StartupCachePath);
        await cache.SaveAsync(new([Entry], 1, 0, 0, false));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.BeforeReturn = () => { entered.TrySetResult(); return release.Task; };
        await Sta(server, async model =>
        {
            var pending = model.InitializeAsync();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); model.SelectedEntry = Assert.Single(model.Entries);
                Assert.True(model.IsCached); Assert.True(model.IsBusy); Assert.Contains("缓存", model.ObservationLabel);
                Assert.False(model.AddToTasksCommand.CanExecute(null)); Assert.False(model.InspectCommand.CanExecute(null));
                await model.AddToTasksCommand.ExecuteAsync(null); Assert.Null(model.Inspection);
            }
            finally { release.TrySetResult(); }
            await pending; Assert.False(model.IsCached); Assert.Equal(Entry.Id, model.SelectedEntry!.Id);
            reader.BeforeReturn = null; reader.Failure = new IOException("owned read failure");
            await model.RefreshCommand.ExecuteAsync(null);
            Assert.Single(model.Entries); Assert.True(model.IsCached); Assert.Contains("读取失败", model.ObservationLabel);
            Assert.Contains("未读取不等于", model.StatusMessage); Assert.False(model.InspectCommand.CanExecute(null));
            Assert.False(model.AddToTasksCommand.CanExecute(null)); Assert.Null(model.Inspection);
        });
    }

    [Fact]
    public async Task UnavailableWithoutCacheIsUnknownNotAnEmptySuccessfulScan()
    {
        var reader = new Reader { Failure = new UnauthorizedAccessException("owned denied getter") };
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: reader);
        await Sta(server, async model =>
        {
            await model.InitializeAsync(); Assert.Empty(model.Entries); Assert.True(model.IsEmpty);
            Assert.Equal("启动项状态未知", model.EmptyStateTitle); Assert.Equal("当前不可用", model.ObservationLabel);
            Assert.False(model.AddToTasksCommand.CanExecute(null));
        });
    }

    [Fact]
    public async Task LateInspectionCannotReplaceAChangedSelectionAndRefreshPreservesVisibleSelectionAndFilter()
    {
        var second = Entry with { Id = "second", Name = "fixture-second" };
        var reader = new Reader { Entries = [Entry, second] };
        await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: reader);
        await Sta(server, async model =>
        {
            await model.InitializeAsync(); model.SearchText = "fixture"; model.SelectedFilter = model.Filters[1];
            model.SelectedEntry = model.Entries.First(item => item.Id == Entry.Id);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reader.BeforeReturn = () => { entered.TrySetResult(); return release.Task; };
            var pending = model.InspectCommand.ExecuteAsync(null);
            try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); model.SelectedEntry = model.Entries.Single(item => item.Id == second.Id); }
            finally { release.TrySetResult(); }
            await pending; Assert.Null(model.Inspection); Assert.False(model.ShowDetails);
            reader.BeforeReturn = null;
            await model.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("fixture", model.SearchText); Assert.Equal("active", model.SelectedFilter.Value); Assert.Equal(second.Id, model.SelectedEntry!.Id);
            await model.InspectCommand.ExecuteAsync(null); Assert.Equal(second.Id, model.Inspection!.Entry.Id);
            model.CloseDetailsCommand.Execute(null); Assert.False(model.ShowDetails); Assert.Equal(second.Id, model.SelectedEntry.Id);
        });
    }

    [Fact]
    public async Task AdoptionRequiresFreshSharedReviewAndExplicitConfirmationThenWritesOnlyConfiguration()
    {
        var reader = new Reader(); await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: reader);
        var request = StartupAdoptionControlPlaneTests.Request.Task;
        await Sta(server, async model =>
        {
            await model.InitializeAsync(); model.SelectedEntry = Assert.Single(model.Entries);
            Assert.False(await model.AdoptAsync(request, confirmed: true)); Assert.Equal(1, reader.Calls);
            var events = 0; model.AddProgramRequested += (_, entry) => { Assert.Equal(Entry, entry); events++; };
            await model.AddToTasksCommand.ExecuteAsync(null); Assert.Equal(1, events); Assert.NotNull(model.Inspection);
            Assert.False(await model.AdoptAsync(request, confirmed: false)); Assert.Equal(2, reader.Calls);
            Assert.True(await model.AdoptAsync(request, confirmed: true)); Assert.True(model.ConfigurationChanged);
            Assert.Contains("纳管配置已提交", model.StatusMessage);
            Assert.False(File.Exists(server.Paths.StartupBackupPath)); Assert.Null(model.Inspection);
            Assert.False(model.DisableCommand.CanExecute(null)); Assert.False(model.RestoreCommand.CanExecute(null));
        });
        var configuration = await server.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        Assert.Equal(request.Name, Assert.Single(configuration.Configuration.Tasks).Name);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ChangedSelectedMetadataOrConfigurationCannotCommitAdoption(bool configuration)
    {
        var reader = new Reader(); await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: reader);
        await Sta(server, async model =>
        {
            await model.InitializeAsync(); model.SelectedEntry = Assert.Single(model.Entries); await model.InspectCommand.ExecuteAsync(null);
            if (configuration)
            {
                var client = new AgentConfigurationClient(server.Client); _ = await client.ReadAsync();
                _ = await client.MutateAsync<ApplicationSettings>("settings-patch", payload: new ApplicationSettingsPatch(AutoStartMissingOnLaunch: true));
            }
            else { reader.Entries = [Entry with { Arguments = "changed" }]; }
            Assert.False(await model.AdoptAsync(StartupAdoptionControlPlaneTests.Request.Task, confirmed: true));
            Assert.False(model.ConfigurationChanged); Assert.Null(model.Inspection); Assert.False(model.ShowDetails);
        });
        Assert.Empty((await server.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration")).Configuration.Tasks);
    }

    [Fact]
    public async Task ClosingCallerSuppressesAStillWorkingInspectionAndDoesNotOpenAnyNativeAction()
    {
        var reader = new Reader(); await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: reader);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Sta(server, async model =>
        {
            await model.InitializeAsync(); model.SelectedEntry = Assert.Single(model.Entries);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reader.BeforeReturn = () => { entered.TrySetResult(); return release.Task; };
            var pending = model.InspectCommand.ExecuteAsync(null);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); model.Dispose(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Null(model.Inspection); Assert.False(model.ShowDetails); Assert.False(model.InspectCommand.CanExecute(null));
                var busy = await new AgentStartupClient(server.Client).ReadAsync(new()); Assert.False(busy.Available); Assert.Equal("startup_scan_busy", busy.Reason);
            }
            finally
            {
                release.TrySetResult();
                // Caller cancellation is not worker completion. Wait for the actual
                // Agent workflow gate (including its late workspace reads) before the
                // fixture deletes its root; a reader-return signal is too early.
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var client = new AgentStartupClient(server.Client); StartupInventorySnapshot finished;
                do
                {
                    finished = await client.ReadAsync(new(), deadline.Token);
                    if (finished.Reason == "startup_scan_busy") { await Task.Delay(10, deadline.Token); }
                } while (finished.Reason == "startup_scan_busy");
                Assert.True(finished.Available); Assert.False(finished.HasEntryErrors);
            }
        });
    }

    [Fact]
    public async Task AcceptedBusinessOutcomeUnknownRetainsOriginalIdAndRefreshOrDirectCallsCannotResubmit()
    {
        var reader = new Reader(); await using var server = new ConfigurationControlPlaneTests.ServerFixture(startupReader: reader);
        var yaml = await File.ReadAllBytesAsync(server.Paths.ConfigPath);
        await Sta(server, async model =>
        {
            await model.InitializeAsync(); model.SelectedEntry = Assert.Single(model.Entries); await model.InspectCommand.ExecuteAsync(null);
            // Actual Agent accepts this operation, then its existing Windows config validation
            // reports io_error/unknown. No target file is created or executed.
            var request = new AddTaskRequest("unknown-gui-owned", AutorunTaskType.Windows,
                Executable: Path.Combine(server.Root, "absent-never-run.exe"), HealthPattern: "absent-never-run.exe");
            Assert.False(await model.AdoptAsync(request, confirmed: true));
            var originalId = Assert.IsType<string>(model.PendingOperationId);
            Assert.True(model.HasUnknownAdoption); Assert.True(model.IsCached); Assert.Contains(originalId, model.StatusMessage);
            Assert.False(model.AddToTasksCommand.CanExecute(null)); Assert.Null(model.Inspection);
            Assert.True(model.QueryPendingOperationCommand.CanExecute(null));
            var journal = Directory.EnumerateFiles(server.Root, originalId + ".operation.json", SearchOption.AllDirectories).Single();
            var journalBefore = await File.ReadAllBytesAsync(journal);
            await model.QueryPendingOperationCommand.ExecuteAsync(null);
            Assert.True(model.ShowDetails); Assert.Contains("unknown（结果未知）", model.DetailText);
            Assert.Contains(originalId, model.DetailText); Assert.True(model.HasUnknownAdoption);
            Assert.False(model.ConfigurationChanged); Assert.False(model.AddToTasksCommand.CanExecute(null));
            var receipt = await server.Client.SendAsync<JsonElement>("operation", originalId);
            Assert.Equal("unknown", receipt.GetProperty("state").GetString());
            Assert.Equal(originalId, receipt.GetProperty("operationId").GetString());
            Assert.Equal("startup-adopt", receipt.GetProperty("command").GetString());
            await model.RefreshCommand.ExecuteAsync(null);
            Assert.True(model.HasUnknownAdoption); Assert.Equal(originalId, model.PendingOperationId);
            Assert.Contains(originalId, model.StatusMessage); Assert.False(model.AddToTasksCommand.CanExecute(null));
            await model.InspectCommand.ExecuteAsync(null); Assert.Contains(originalId, model.StatusMessage);
            model.PendingOperationId = AgentProtocol.NewOperationId(); // A replaced display value cannot redirect the query.
            await model.QueryPendingOperationCommand.ExecuteAsync(null);
            Assert.Contains(originalId, model.DetailText); Assert.DoesNotContain(model.PendingOperationId, model.DetailText);
            model.PendingOperationId = null; // Clearing a display property cannot clear the independent fail-closed gate.
            await model.QueryPendingOperationCommand.ExecuteAsync(null);
            Assert.Contains(originalId, model.DetailText); Assert.Contains(originalId, model.StatusMessage);
            Task pending;
            await using (var lease = await RootWriteLease.AcquireAsync(server.Paths))
            {
                pending = model.QueryPendingOperationCommand.ExecuteAsync(null);
                Assert.True(model.IsBusy); model.CloseDetailsCommand.Execute(null);
            }
            await pending;
            Assert.False(model.ShowDetails); // A late read cannot reopen a dismissed detail view.
            Assert.Equal(journalBefore, await File.ReadAllBytesAsync(journal));
            // Deliberate owned-journal fixtures: neither a completed log nor a read error
            // authorizes replay, applies opaque result Data, or replaces the original latch.
            Assert.StartsWith(server.Root + Path.DirectorySeparatorChar, journal, StringComparison.OrdinalIgnoreCase);
            try
            {
                var historical = JsonSerializer.Deserialize<OperationReceipt>(journalBefore, AgentProtocol.JsonOptions)!;
                var completed = historical with { State = OperationState.Completed, Response = new("old-request", true, new { mustNotApply = true }, OperationId: originalId) };
                await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(completed, AgentProtocol.JsonOptions));
                await model.QueryPendingOperationCommand.ExecuteAsync(null);
                Assert.Contains("completed（已记回执，不等于业务成功）", model.DetailText);
                Assert.True(model.HasUnknownAdoption); Assert.False(model.ConfigurationChanged);
                Assert.False(model.AddToTasksCommand.CanExecute(null));
                await File.WriteAllTextAsync(journal, "{}");
                await model.QueryPendingOperationCommand.ExecuteAsync(null);
                Assert.Contains("回执查询失败", model.DetailText); Assert.Contains(originalId, model.DetailText);
                Assert.Contains(originalId, model.StatusMessage); Assert.True(model.HasUnknownAdoption);
                Assert.Equal(journal, Path.GetFullPath(journal)); Assert.Equal(originalId + ".operation.json", Path.GetFileName(journal));
                File.Delete(journal); // Exact ordinary owned fixture path verified above; restored in finally.
                await model.QueryPendingOperationCommand.ExecuteAsync(null);
                Assert.Contains("未找到保留回执", model.DetailText); Assert.Contains("不代表原操作未执行", model.DetailText);
                Assert.True(model.HasUnknownAdoption); Assert.False(model.AddToTasksCommand.CanExecute(null));
            }
            finally { await File.WriteAllBytesAsync(journal, journalBefore); }
            Assert.Single(Directory.EnumerateFiles(server.Root, "*.operation.json", SearchOption.AllDirectories));
            var calls = reader.Calls;
            Assert.False(await model.AdoptAsync(request, confirmed: true));
            await model.AddToTasksCommand.ExecuteAsync(null); Assert.Equal(calls, reader.Calls);
            Assert.True(model.HasUnknownAdoption); Assert.False(model.AddToTasksCommand.CanExecute(null));
        });
        Assert.Equal(yaml, await File.ReadAllBytesAsync(server.Paths.ConfigPath));
    }

    [Theory]
    [InlineData("{\"entries\":null}")] [InlineData("{\"entries\":[null]}")]
    [InlineData("{\"entries\":[],\"readableSources\":7}")] [InlineData("{\"entries\":[],\"sourceIssues\":null}")]
    public async Task MalformedDisplayCacheIsRejectedBeforeBinding(string json)
    {
        await using var server = new ConfigurationControlPlaneTests.ServerFixture();
        await File.WriteAllTextAsync(server.Paths.StartupCachePath, json);
        Assert.Null(await new StartupCacheStore(server.Paths.StartupCachePath).LoadAsync());
    }

    private static Task Sta(ConfigurationControlPlaneTests.ServerFixture server, Func<StartupManagerViewModel, Task> body) =>
        GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            using var model = new StartupManagerViewModel(new(server.Client), new(server.Paths.StartupCachePath)); await body(model);
        });

    internal sealed class Reader : IStartupSnapshotReader
    {
        public StartupEntry[] Entries = [Entry]; public StartupSourceIssue[] Issues = []; public bool Truncated;
        public Exception? Failure; public Func<Task>? BeforeReturn; public int Calls;
        public async Task<StartupManagementSnapshot> ReadAsync(AutorunConfiguration configuration, StartupListQuery query, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (BeforeReturn is { } before) { await before(); }
            if (Failure is { } failure) { throw failure; }
            return new(Entries, Entries.Count(item => !item.IsDisabled), 0, Entries.Count(item => item.IsDisabled), false)
            { ReadableSources = 1, Truncated = Truncated, SourceIssues = Issues };
        }
    }
}
