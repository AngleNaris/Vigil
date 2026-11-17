using System.IO;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;

namespace SystemAutorun.IntegrationTests;

public sealed class GuiRemoteHostControlTests
{
    [Fact]
    public async Task GuiUsesRealPipeAndUnavailableResultsClearMetricsAndObservationTime()
    {
        var online = true;
        var provider = new Reader((host, _) => Task.FromResult(online ? Online(host) : RemoteHostStatusSnapshot.Offline(host, "synthetic failure")));
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(remoteReader: provider);
        var definition = Definition(fixture.Root, "remote");
        var configuration = await Configure(fixture, definition);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var model = Model(fixture, configuration, new AgentRemoteHostReader(fixture.Client, configuration));
            var errors = new List<string>(); model.ErrorRequested += (_, error) => errors.Add(error);
            try
            {
                await model.InitializeAsync();
                await SelectRemote(model, "remote");
                Assert.Equal(1, provider.Calls);
                Assert.True(model.HostStatusAvailable);
                Assert.Equal("synthetic-remote", model.HostName);
                Assert.Equal("12%", model.CpuUsageText);
                Assert.StartsWith("观测于 ", model.HostUpdatedText);
                online = false;
                await model.RefreshCommand.ExecuteAsync(null);
                AssertUnknown(model);
                Assert.Contains("remote_unavailable", model.HostOs);
                Assert.Equal("UNKNOWN", model.SelectedHostTab!.Status);
                Assert.Equal("尚无可用观测", model.SelectedHostTab!.UpdatedText);
                Assert.Empty(errors);
                Assert.Equal(2, provider.Calls);
                Assert.False(File.Exists(definition.KnownHostsFile));
            }
            finally { model.DetachDashboard(); }
        });
    }

    [Fact]
    public async Task GuiReaderRejectsARevisionNotReadByItsConfigurationCursor()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture(remoteReader: new Reader((host, _) => Task.FromResult(Online(host))));
        var definition = Definition(fixture.Root, "remote");
        var configuration = await Configure(fixture, definition);
        var reader = new AgentRemoteHostReader(fixture.Client, configuration);
        Assert.True((await reader.ReadAsync(definition)).Online);
        var other = new AgentConfigurationClient(fixture.Client);
        _ = await other.ReadAsync();
        _ = await other.MutateAsync<ApplicationSettings>("remote-update", payload: definition with { Name = "changed" });
        var failure = await Assert.ThrowsAsync<AgentCommandException>(() => reader.ReadAsync(definition));
        Assert.Equal("configuration_changed", failure.Code);
        var current = await configuration.ReadAsync();
        Assert.True((await reader.ReadAsync(Assert.Single(current.Preferences.RemoteHosts))).Online);
    }

    [Fact]
    public async Task ProtocolFailureClearsPriorSuccessInlineWithoutRetryOrFallback()
    {
        var reader = new Reader((host, _) => Task.FromResult(Online(host)));
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var configuration = await Configure(fixture, Definition(fixture.Root, "remote"));
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var model = Model(fixture, configuration, reader);
            var errors = new List<string>(); model.ErrorRequested += (_, error) => errors.Add(error);
            try
            {
                await model.InitializeAsync(); await SelectRemote(model, "remote");
                Assert.True(model.HostStatusAvailable);
                reader.Read = (_, _) => throw new AgentCommandException("protocol_mismatch", "synthetic malformed reply");
                await model.RefreshCommand.ExecuteAsync(null);
                AssertUnknown(model); Assert.Empty(errors); Assert.Equal(2, reader.Calls);
                Assert.Contains("后台远程观测不可用", model.HostOs);
            }
            finally { model.DetachDashboard(); }
        });
    }

    [Fact]
    public async Task DisabledSelectionNeverCallsAnyRemoteReader()
    {
        var reader = new Reader((_, _) => throw new InvalidOperationException("disabled must not probe"));
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var configuration = await Configure(fixture, Definition(fixture.Root, "remote") with { Enabled = false });
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var model = Model(fixture, configuration, reader);
            try
            {
                await model.InitializeAsync(); await SelectRemote(model, "remote");
                AssertUnknown(model); Assert.Equal(0, reader.Calls); Assert.Contains("禁用", model.HostOs);
            }
            finally { model.DetachDashboard(); }
        });
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public async Task SelectionOrCloseDiscardsALateReplyEvenIfProviderIgnoresCancellation(bool close, bool returnToSame)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<RemoteHostStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Definition(Path.GetTempPath(), "first");
        var reader = new Reader((host, _) =>
        {
            if (host.Id != "first" || started.Task.IsCompleted) { return Task.FromResult(Online(host)); }
            first = host; started.TrySetResult(); return completion.Task; // Deliberately ignores cancellation.
        });
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var configuration = await Configure(fixture, Definition(fixture.Root, "first"), Definition(fixture.Root, "second"));
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var model = Model(fixture, configuration, reader);
            try
            {
                await model.InitializeAsync();
                var firstTab = model.Hosts.Single(tab => tab.Id == "first");
                model.SelectedHostTab = firstTab;
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var refresh = model.RefreshCommand.ExecutionTask!;
                if (close) { model.DetachDashboard(); }
                else
                {
                    model.SelectedHostTab = model.Hosts.Single(tab => tab.Id == "second");
                    if (returnToSame) { model.SelectedHostTab = firstTab; }
                    Assert.False(model.HostStatusAvailable);
                }
                var lateApplied = false;
                model.PropertyChanged += (_, change) =>
                { if (change.PropertyName == nameof(model.HostName) && model.HostName == "late-first-must-not-appear") { lateApplied = true; } };
                completion.SetResult(Online(first) with { MachineName = "late-first-must-not-appear" });
                await refresh.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(lateApplied);
                if (!returnToSame) { Assert.False(firstTab.RemoteSnapshot!.Online); }
                Assert.Equal(close ? 1 : 2, reader.Calls);
                if (close) { AssertUnknown(model); }
                else { Assert.Equal(returnToSame ? "first" : "second", model.SelectedHostTab!.Id); Assert.True(model.HostStatusAvailable); }
            }
            finally
            {
                model.DetachDashboard(); completion.TrySetResult(RemoteHostStatusSnapshot.Offline(first, "owned cleanup"));
                if (model.RefreshCommand.ExecutionTask is { } pending) { await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
            }
        });
    }

    [Fact]
    public async Task BackendOfflineClearsRemoteCachesWithoutLaunchingOrRetryingAProbe()
    {
        var reader = new Reader((host, _) => Task.FromResult(Online(host)));
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var configuration = await Configure(fixture, Definition(fixture.Root, "remote"));
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var model = Model(fixture, configuration, reader);
            var errors = new List<string>(); model.ErrorRequested += (_, error) => errors.Add(error);
            try
            {
                await model.InitializeAsync(); await SelectRemote(model, "remote");
                Assert.True(model.HostStatusAvailable);
                var shutdown = await fixture.Client.SendResponseAsync("shutdown"); Assert.True(shutdown.Ok);
                await model.RefreshCommand.ExecuteAsync(null);
                AssertUnknown(model); Assert.Empty(errors); Assert.Equal(1, reader.Calls);
                Assert.Contains("后台配置不可用", model.HostOs);
            }
            finally { model.DetachDashboard(); }
        });
    }

    [Fact]
    public async Task PausedRefreshDoesNotKeepExpiredRemoteSuccessVisible()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var definition = Definition(fixture.Root, "remote");
        var configuration = await Configure(fixture, definition);
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var model = Model(fixture, configuration, new Reader((host, _) => Task.FromResult(Online(host))));
            try
            {
                await model.InitializeAsync(); await SelectRemote(model, "remote"); model.AutoRefresh = false;
                model.SelectedHostTab!.ApplyRemoteStatus(Online(definition) with { CapturedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
                model.TickDashboard(); AssertUnknown(model); Assert.Contains("过期", model.HostOs);
            }
            finally { model.DetachDashboard(); }
        });
    }

    [Fact]
    public async Task ConfigurationCursorAdvancingWithoutApplyingTheDefinitionCannotPublishTheOldHost()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        var definition = Definition(fixture.Root, "remote");
        var configuration = await Configure(fixture, definition);
        var reader = new Reader(async (host, cancellationToken) =>
        {
            var other = new AgentConfigurationClient(fixture.Client); _ = await other.ReadAsync();
            _ = await other.MutateAsync<ApplicationSettings>("remote-update", payload: host with { Port = 2222 });
            _ = await configuration.ReadAsync(); // The transport cursor advances, but displayed definitions have not.
            return Online(host);
        });
        await GuiConfigurationControlTests.RunStaAsync(async () =>
        {
            var model = Model(fixture, configuration, reader);
            try
            {
                await model.InitializeAsync(); await SelectRemote(model, "remote");
                AssertUnknown(model); Assert.Equal(1, reader.Calls);
                Assert.Contains("配置已变化", model.HostOs);
            }
            finally { model.DetachDashboard(); }
        });
    }

    [Fact]
    public async Task MemoryReadoutNotifiesForAvailabilityAndKeepsMeasuredZeroDistinctFromUnknown()
    {
        await using var fixture = new ConfigurationControlPlaneTests.ServerFixture();
        await GuiConfigurationControlTests.RunStaAsync(() =>
        {
            var model = Model(fixture, new AgentConfigurationClient(fixture.Client),
                new Reader((_, _) => throw new InvalidOperationException("no remote read expected")));
            try
            {
                Assert.Equal("--", model.HostMemoryPercentText);
                var values = new List<string>();
                model.PropertyChanged += (_, change) =>
                { if (change.PropertyName == nameof(model.HostMemoryPercentText)) { values.Add(model.HostMemoryPercentText); } };
                model.HostStatusAvailable = true;
                model.MemoryUsagePercent = 50;
                model.HostStatusAvailable = false;
                Assert.Equal(new[] { "0%", "50%", "--" }, values);
            }
            finally { model.DetachDashboard(); }
            return Task.CompletedTask;
        });
    }

    private static void AssertUnknown(MainWindowViewModel model)
    {
        Assert.False(model.HostStatusAvailable);
        Assert.Equal("--", model.HostUptime); Assert.Equal("--", model.CpuUsageText);
        Assert.Equal("--", model.MemoryUsageText); Assert.Equal("--", model.HostMemoryPercentText);
        Assert.Equal("--", model.StorageUsageText);
        Assert.Equal("--", model.HostThreadCountText); Assert.Equal("--", model.NetworkAddress);
        Assert.Empty(model.Drives); Assert.Empty(model.Networks);
        Assert.Equal("尚无可用观测", model.HostUpdatedText);
    }

    private static MainWindowViewModel Model(ConfigurationControlPlaneTests.ServerFixture fixture,
        AgentConfigurationClient configuration, IRemoteHostReader reader)
    {
        var model = new MainWindowViewModel(fixture.Client, new NativeCacheStore(fixture.Paths.GuiCachePath),
            configuration, fixture.Paths, reader, new DashboardFixtureMonitoringClient(fixture));
        model.InitializeDashboard(); return model;
    }

    private static async Task SelectRemote(MainWindowViewModel model, string id)
    {
        model.SelectedHostTab = model.Hosts.Single(tab => tab.Id == id);
        await model.RefreshCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task<AgentConfigurationClient> Configure(ConfigurationControlPlaneTests.ServerFixture fixture, params RemoteHostDefinition[] definitions)
    {
        var configuration = new AgentConfigurationClient(fixture.Client); _ = await configuration.ReadAsync();
        foreach (var definition in definitions) { _ = await configuration.MutateAsync<ApplicationSettings>("remote-add", payload: definition); }
        return configuration;
    }

    private static RemoteHostDefinition Definition(string root, string id) => new()
    { Id = id, Name = id, Host = "127.0.0.1", Port = 1, UserName = "synthetic", Platform = RemoteHostPlatform.Linux, KnownHostsFile = Path.Combine(root, "never-open-known-hosts") };
    private static RemoteHostStatusSnapshot Online(RemoteHostDefinition host) => new(host.Id, host.Name, host.Platform, host.Host,
        true, null, DateTimeOffset.UtcNow, "synthetic-remote", "synthetic Linux", TimeSpan.FromSeconds(91), 12.0,
        10000, 5000, [], "127.0.0.1", 10, 20, 4, 17, 23);
    private sealed class Reader(Func<RemoteHostDefinition, CancellationToken, Task<RemoteHostStatusSnapshot>> read) : IRemoteHostReader
    {
        public Func<RemoteHostDefinition, CancellationToken, Task<RemoteHostStatusSnapshot>> Read { get; set; } = read;
        public int Calls { get; private set; }
        public Task<RemoteHostStatusSnapshot> ReadAsync(RemoteHostDefinition definition, CancellationToken cancellationToken = default)
        { Calls++; return Read(definition, cancellationToken); }
    }
}
