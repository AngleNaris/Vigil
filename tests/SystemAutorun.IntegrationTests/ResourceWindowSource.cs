using System.IO;
using SystemAutorun.App.Services;
using SystemAutorun.App.ViewModels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.IntegrationTests;

// An explicit synthetic read-only Agent adapter, not a production sampler or durable store.
internal sealed class ResourceWindowSource(ConfigurationControlPlaneTests.ServerFixture fixture, ConfigurationWorkspaceSnapshot loaded) : IDashboardMonitoringClient
{
    private static readonly DateTimeOffset SyntheticStarted = DateTimeOffset.UtcNow.Date.AddDays(-2);
    private readonly DashboardFixtureMonitoringClient _health = new(fixture);
    private ResourceHistoryBuffer _ring = new();
    private readonly List<MonitoringHistoryFrame> _durable = [];
    private IReadOnlyList<ManagedProcessSnapshot> _processes = [];
    private HostStatusSnapshot? _host;
    private DateTimeOffset _captured;
    private AgentMonitoringSnapshot? _halfSecondHealth;
    internal bool HalfSecondCapability { get; set; }
    internal ConfigurationWorkspaceSnapshot Configuration { get; private set; } = loaded;

    internal async Task ConfigureAsync(MainWindowViewModel model)
    {
        var revision = Configuration.Revision;
        var executable = Path.Combine(fixture.Root, "never-run-r121.exe");
        await File.WriteAllBytesAsync(executable, []); // Invalid executable, owned root; the executor also throws.
        foreach (var task in model.Tasks.Where(row => row.Enabled).Select(row => Definition(row.Snapshot)).ToArray())
        {
            // Real CAS on the owned root, but explicitly no launch/recovery. Its executor also throws.
            var mutation = await fixture.Client.SendAsync<WorkspaceMutation<ConfigurationMutationResult>>("task-add",
                payload: new AddTaskRequest(task.Name, task.Type, task.Description, executable, task.Arguments, task.WorkingDirectory,
                    task.Distro, task.Service, task.Command, StartupDelay: 0, HealthType: task.Health.Type, HealthPattern: task.Health.Pattern,
                    HealthHost: task.Health.Host, HealthPort: task.Health.Port, Launch: task.Launch, ExpectedRunning: true,
                    StartOnAppLaunch: false, AutoRecover: false), expectedRevision: revision);
            revision = mutation.Revision;
        }
        Configuration = await fixture.Client.SendAsync<ConfigurationWorkspaceSnapshot>("configuration");
        await model.RefreshCommand.ExecuteAsync(null);
    }

    internal void Clear() { _ring = new(); _durable.Clear(); _processes = []; _host = null; }

    internal void Observe(DateTimeOffset at, IReadOnlyList<ManagedProcessSnapshot> processes, HostStatusSnapshot? host, bool durable = false)
    {
        _captured = at; _processes = processes; _host = host;
        var rows = Configuration.Configuration.Tasks.Where(task => task.Enabled).Select(task =>
        {
            var identity = MonitoringTaskIdentity.For(task);
            var sample = processes.FirstOrDefault(row => row.TaskName == task.Name && row.TaskIdentity == identity);
            var hasRates = sample is { CpuUsageAvailable: true, ProcessCount: > 0 };
            return new ResourceTaskSample(task.Name, identity, sample?.CapturedAt ?? at, ResourceProcessIdentity.For(sample, at),
                hasRates ? sample!.CpuUsagePercent : null, sample is { ProcessCount: > 0 } ? sample.WorkingSetBytes : null,
                hasRates ? sample!.TotalDiskReadBytesPerSecond : null, hasRates ? sample!.TotalDiskWriteBytesPerSecond : null,
                hasRates ? sample!.TotalNetworkReceiveBytesPerSecond : null, hasRates ? sample!.TotalNetworkSendBytesPerSecond : null);
        }).ToArray();
        HostHistorySample? values = host is null ? null : new(host.CpuUsagePercent, Math.Max(0, host.TotalMemoryBytes - host.AvailableMemoryBytes),
            host.TotalMemoryBytes, host.DiskReadBytesPerSecond, host.DiskWriteBytesPerSecond, host.NetworkReceiveBytesPerSecond, host.NetworkSendBytesPerSecond);
        _ring.Append(new(at, "r121-explicit-fixture", rows, values, host?.CapturedAt));
        if (durable)
        {
            _durable.Add(new(1, fixture.Endpoint.RootId, "r121-explicit-fixture", at,
                rows.Select(row => new TaskHistorySample(row.TaskName, row.TaskIdentity, ObservationHealth.Healthy, row.CpuPercent,
                    row.WorkingSetBytes, ResourceIdentity: row.ProcessIdentity,
                    DiskReadBytesPerSecond: row.DiskReadBytesPerSecond, DiskWriteBytesPerSecond: row.DiskWriteBytesPerSecond,
                    NetworkReceiveBytesPerSecond: row.NetworkReceiveBytesPerSecond, NetworkSendBytesPerSecond: row.NetworkSendBytesPerSecond)).ToArray(),
                Host: values));
        }
    }

    internal void Apply(MainWindowViewModel model) => model.ApplyMonitoringSnapshot(Snapshot());

    private AgentMonitoringSnapshot Snapshot()
    {
        var snapshot = _health.Snapshot(Configuration);
        if (HalfSecondCapability)
        {
            if (_halfSecondHealth is null || _halfSecondHealth.ConfigurationRevision != Configuration.Revision
                || snapshot.ReturnedAt - _halfSecondHealth.ReturnedAt >= TimeSpan.FromSeconds(10)) { _halfSecondHealth = snapshot; }
            snapshot = snapshot with { Tasks = _halfSecondHealth.Tasks, RecentResourceSchemaVersion = 2, ResourceSamplingIntervalMilliseconds = 500 };
        }
        if (_processes.Count == 0 && _host is null) { return snapshot; }
        return snapshot with
        {
            Processes = new(true, _captured, _captured, _processes, null),
            Host = new(_host is not null, _captured, _captured, _host, _host is null ? "fixture host capture unavailable" : null),
        };
    }

    public Task<AgentMonitoringSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Snapshot()); }
    internal Func<MonitoringHistoryQuery, CancellationToken, Task<MonitoringHistoryResult>>? HistoryReader { get; set; }
    public Task<MonitoringHistoryResult> ReadHistoryAsync(MonitoringHistoryQuery query, CancellationToken cancellationToken = default) =>
        HistoryReader?.Invoke(query, cancellationToken) ?? _health.ReadHistoryAsync(query, cancellationToken);
    public Task<RecentResourceHistoryResult> ReadRecentResourcesAsync(ResourceHistoryQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HalfSecondCapability) { throw new InvalidOperationException("Synthetic half-second capability is disabled."); }
        return Task.FromResult(_ring.QueryPrecise(query, DateTimeOffset.UtcNow, 500, _health.Snapshot(Configuration).InstanceId));
    }
    public Task<ResourceHistoryResult> ReadResourceHistoryAsync(ResourceHistoryQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(query.Source == ResourceHistorySource.RecentSeconds ? _ring.Query(query, DateTimeOffset.UtcNow)
            : ResourceHistoryProjection.ProjectDurable(_durable, query, DateTimeOffset.UtcNow));
    }

    internal static AutorunTask Definition(TaskSnapshot row) => new(row.Name, Enum.GetValues<AutorunTaskType>().Single(type => type.ToKey() == row.Type), row.Enabled, row.Description, row.Executable ?? @"C:\never-executed-r121-fixture.exe",
        row.Arguments, row.WorkingDirectory, row.Distro, row.Service, row.Command, row.DependsOn, row.StartupDelay,
        row.Health ?? new(HealthCheckType.Process, "never-executed-r121-fixture.exe", null, null, 1, null, null), row.Launch, row.ExpectedRunning, row.StartOnAppLaunch, row.AutoRecover);

    internal static ManagedProcessSnapshot Synthetic(TaskRowViewModel task, DateTimeOffset at, int phase, bool constant = false) =>
        new(task.Name, "R121 synthetic presentation only", task.Type, true, true, true, "OK", "synthetic", "No process is started", at,
            [new(987654, "never-started-r121-fixture", null, SyntheticStarted,
                constant ? .04 : .04 + Math.Sin(phase / 11d) * .025,
                (80L << 20) + (constant ? 0 : (long)(Math.Sin(phase / 27d) * (7L << 20))),
                1,
                1,
                true,
                constant ? 0 : 1024 * (12 + Math.Sin(phase / 13d) * 6),
                constant ? 0 : 1024 * (4 + Math.Cos(phase / 9d) * 2),
                constant ? 0 : 1024 * (32 + Math.Sin(phase / 17d) * 10),
                constant ? 0 : 1024 * (8 + Math.Cos(phase / 23d) * 4))],
            TaskIdentity: MonitoringTaskIdentity.For(task.Snapshot));

    internal static HostStatusSnapshot SyntheticHost(DateTimeOffset at, int phase) => new("R121 合成主机", "合成 Windows / 非生产记录", at,
        TimeSpan.FromHours(54), 37 + Math.Sin(phase / 21d) * 4, 32L << 30, (12L << 30) + (long)(Math.Sin(phase / 37d) * (1L << 28)),
        [new(@"C:\", "合成系统盘", 512L << 30, 128L << 30)], [new("合成以太网", "192.0.2.24", 9L << 30, 2L << 30)], 16, 214, 2890,
        1024 * (24 + Math.Sin(phase / 13d) * 12), 1024 * (8 + Math.Cos(phase / 9d) * 4),
        1024 * (80 + Math.Sin(phase / 17d) * 24), 1024 * (16 + Math.Cos(phase / 23d) * 8));
}
