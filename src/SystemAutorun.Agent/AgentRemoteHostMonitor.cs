using System.Threading.Channels;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;
using SystemAutorun.Core.Services;

namespace SystemAutorun.Agent;

internal sealed class AgentRemoteHostMonitor : IDisposable, IAsyncDisposable
{
    private const int MaximumMonitoredHosts = 32;
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(25);
    internal static readonly TimeSpan BackgroundSamplingInterval = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan ForegroundLeaseDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan[] BackoffSteps =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
        TimeSpan.FromSeconds(300),
    ];

    private readonly ConfigurationWorkspace _workspace;
    private readonly IRemoteHostStreamSource _source;
    private readonly string _instanceId;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _samplingInterval;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly Channel<byte> _wakeup = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, HostWorker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private Task? _runTask;
    private string? _appliedRevision;
    private int _disposed;

    internal AgentRemoteHostMonitor(
        ConfigurationWorkspace workspace,
        IRemoteHostStreamSource source,
        string instanceId,
        TimeProvider? clock = null,
        TimeSpan? samplingInterval = null)
    {
        if (!Guid.TryParseExact(instanceId, "N", out _))
        {
            throw new ArgumentException("Invalid Agent instance ID.", nameof(instanceId));
        }
        _workspace = workspace;
        _source = source;
        _instanceId = instanceId;
        _clock = clock ?? TimeProvider.System;
        _samplingInterval = samplingInterval ?? AgentMonitor.SamplingInterval;
        if (_samplingInterval <= TimeSpan.Zero || _samplingInterval > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(samplingInterval));
        }
    }

    private TimeSpan BackgroundInterval => _samplingInterval > BackgroundSamplingInterval
        ? _samplingInterval
        : BackgroundSamplingInterval;

    internal void InvalidateConfiguration()
    {
        if (Volatile.Read(ref _disposed) != 0) { return; }
        _wakeup.Writer.TryWrite(1);
    }

    internal Task RunAsync(CancellationToken cancellationToken)
    {
        var task = RunLoopCoreAsync(cancellationToken);
        lock (_gate) { _runTask = task; }
        return task;
    }

    private async Task RunLoopCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, cancellationToken);
        try
        {
            using var timer = new PeriodicTimer(SyncInterval, _clock);
            await SyncWorkersAsync(linked.Token).ConfigureAwait(false);
            Task<byte>? wakeTask = null;
            Task<bool>? tickTask = null;
            while (!linked.IsCancellationRequested)
            {
                wakeTask ??= _wakeup.Reader.ReadAsync(linked.Token).AsTask();
                tickTask ??= timer.WaitForNextTickAsync(linked.Token).AsTask();
                var completed = await Task.WhenAny(wakeTask, tickTask).ConfigureAwait(false);
                if (ReferenceEquals(completed, wakeTask))
                {
                    await wakeTask.ConfigureAwait(false);
                    wakeTask = null;
                    while (_wakeup.Reader.TryRead(out _)) { }
                }
                else
                {
                    await tickTask.ConfigureAwait(false);
                    tickTask = null;
                }
                await SyncWorkersAsync(linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            await DrainWorkersAsync().ConfigureAwait(false);
        }
    }

    internal async Task SyncWorkersOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_lifetimeCts.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) { return; }
        await SyncWorkersAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SyncWorkersAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0 || _lifetimeCts.IsCancellationRequested || cancellationToken.IsCancellationRequested) { return; }
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        List<HostWorker> toStop = [];
        try
        {
            ConfigurationWorkspaceSnapshot workspace;
            try { workspace = await _workspace.ReadAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not OperationCanceledException)
            { return; }

            var enabled = workspace.Preferences.RemoteHosts
                .Where(host => host.Enabled)
                .Take(MaximumMonitoredHosts)
                .ToArray();
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_lifetimeCts.IsCancellationRequested) { return; }
            var parentToken = _lifetimeCts.Token;

            lock (_gate)
            {
                _appliedRevision = workspace.Revision;
                foreach (var definition in enabled)
                {
                    var identity = MonitoringRemoteHostIdentity.For(definition);
                    var normalizedProxy = RemoteHostDefinitionValidator.NormalizeProxyUri(definition.ProxyUri);
                    wanted.Add(definition.Id);
                    if (_workers.TryGetValue(definition.Id, out var existing))
                    {
                        if (existing.HostIdentity == identity
                            && string.Equals(existing.NormalizedProxyUri, normalizedProxy, StringComparison.OrdinalIgnoreCase))
                        {
                            existing.UpdateDefinition(definition);
                            continue;
                        }
                        _workers.Remove(definition.Id);
                        toStop.Add(existing);
                    }

                    var worker = new HostWorker(this, definition, identity, normalizedProxy, parentToken);
                    _workers[definition.Id] = worker;
                    worker.Start();
                }

                foreach (var key in _workers.Keys.Where(id => !wanted.Contains(id)).ToArray())
                {
                    if (_workers.Remove(key, out var removed))
                    {
                        toStop.Add(removed);
                    }
                }
            }
        }
        finally
        {
            _syncGate.Release();
        }

        await StopWorkersAsync(toStop).ConfigureAwait(false);
    }

    internal async Task<RemoteHostStatusSnapshot> ReadCurrentAsync(
        RemoteHostDefinition definition,
        string revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.Enabled) { return RemoteHostStatusSnapshot.Offline(definition, "host_disabled"); }
        var identity = MonitoringRemoteHostIdentity.For(definition);
        var worker = GetOrSyncWorker(definition, identity);
        if (worker is null)
        {
            await SyncWorkersAsync(cancellationToken).ConfigureAwait(false);
            worker = GetOrSyncWorker(definition, identity);
            if (worker is null)
            {
                return RemoteHostStatusSnapshot.Offline(definition, "remote_unavailable");
            }
        }

        TaskCompletionSource<bool>? pending = null;
        var now = _clock.GetUtcNow();
        worker.MarkForegroundActive(now.Add(ForegroundLeaseDuration));
        lock (worker.Sync)
        {
            if (worker.LatestOnlineSnapshot is { } fresh && IsFresh(fresh.CapturedAt, now))
            {
                return fresh with { Name = definition.Name };
            }
            if ((!worker.HasCompletedFirstAttempt || worker.Connecting) && worker.PendingSignal is { } activeSignal)
            {
                pending = activeSignal;
            }
            else
            {
                return RemoteHostStatusSnapshot.Offline(
                    definition,
                    worker.LastError ?? (worker.HasCompletedFirstAttempt
                        ? "正在重新建立远程 SSH 流…"
                        : "等待远程主机首次观测。"));
            }
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(definition.ConnectTimeoutSeconds + 2, 2, 12)));
        try { await pending.Task.WaitAsync(budget.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }

        now = _clock.GetUtcNow();
        lock (worker.Sync)
        {
            if (worker.LatestOnlineSnapshot is { } fresh && IsFresh(fresh.CapturedAt, now))
            {
                return fresh with { Name = definition.Name };
            }
            return RemoteHostStatusSnapshot.Offline(definition, worker.LastError ?? "等待远程主机首次观测。");
        }
    }

    internal IReadOnlyList<RemoteHostHistorySample>? CaptureDurableSamples(ConfigurationWorkspaceSnapshot? configuration, DateTimeOffset at)
    {
        if (configuration is null) { return null; }
        var enabled = configuration.Preferences.RemoteHosts.Where(host => host.Enabled).ToArray();
        if (enabled.Length == 0) { return null; }

        var list = new List<RemoteHostHistorySample>();
        lock (_gate)
        {
            foreach (var definition in enabled)
            {
                var identity = MonitoringRemoteHostIdentity.For(definition);
                if (!_workers.TryGetValue(definition.Id, out var worker) || worker.HostIdentity != identity) { continue; }
                lock (worker.Sync)
                {
                    if (worker.LatestOnlineSnapshot is { Online: true } snapshot
                        && snapshot.CapturedAt <= at.AddSeconds(1) && at - snapshot.CapturedAt <= FreshWindow)
                    {
                        list.Add(new RemoteHostHistorySample(definition.Id, identity, worker.SessionEpoch, ToHostHistory(snapshot)));
                    }
                }
            }
        }

        return list.Count > 0 ? list : null;
    }

    internal IReadOnlyList<RemoteHostStatusSnapshot> GetFreshSnapshots(DateTimeOffset now)
    {
        HostWorker[] workers;
        lock (_gate)
        {
            if (_workers.Count == 0) { return []; }
            workers = _workers.Values.ToArray();
        }

        var list = new List<RemoteHostStatusSnapshot>(workers.Length);
        foreach (var worker in workers)
        {
            lock (worker.Sync)
            {
                if (worker.Definition.Enabled
                    && worker.LatestOnlineSnapshot is { Online: true } snapshot
                    && IsFresh(snapshot.CapturedAt, now))
                {
                    list.Add(snapshot with { Name = worker.Definition.Name });
                }
            }
        }
        if (list.Count > 1)
        {
            list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id));
        }
        return list;
    }

    internal async Task<ResourceHistoryResult> QueryRecentAsync(ResourceHistoryQuery query, DateTimeOffset now, bool compatible, CancellationToken cancellationToken)
    {
        var worker = await ResolveWorkerForQueryAsync(query, now, cancellationToken).ConfigureAwait(false);
        if (worker is null)
        {
            return ResourceHistoryProjection.ProjectRecent([], query, now);
        }
        worker.MarkForegroundActive(now.Add(ForegroundLeaseDuration));
        return compatible ? worker.History.QueryCompatible(query, now) : worker.History.Query(query, now);
    }

    internal async Task<RecentResourceHistoryResult> QueryRecentPreciseAsync(ResourceHistoryQuery query, DateTimeOffset now, int intervalMilliseconds, string agentInstanceId, CancellationToken cancellationToken)
    {
        var worker = await ResolveWorkerForQueryAsync(query, now, cancellationToken).ConfigureAwait(false);
        if (worker is null)
        {
            return ResourceHistoryProjection.ProjectRecentPrecise([], query, now, intervalMilliseconds, agentInstanceId);
        }
        worker.MarkForegroundActive(now.Add(ForegroundLeaseDuration));
        return worker.History.QueryPrecise(query, now, intervalMilliseconds, agentInstanceId);
    }

    private async Task<HostWorker?> ResolveWorkerForQueryAsync(ResourceHistoryQuery query, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ResourceHistoryProjection.ValidateQuery(query, now);
        var hostId = query.RemoteHostId
            ?? throw new ArgumentException("Remote host query requires RemoteHostId.");
        var snapshot = await _workspace.ReadAsync(cancellationToken).ConfigureAwait(false);
        var definition = snapshot.Preferences.RemoteHosts.FirstOrDefault(host => string.Equals(host.Id, hostId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Remote host ID '{hostId}' is not configured.");
        if (!definition.Enabled) { return null; }
        var identity = MonitoringRemoteHostIdentity.For(definition);
        return GetOrSyncWorker(definition, identity);
    }

    private HostWorker? GetOrSyncWorker(RemoteHostDefinition definition, string identity)
    {
        var normalizedProxy = RemoteHostDefinitionValidator.NormalizeProxyUri(definition.ProxyUri);
        lock (_gate)
        {
            return _workers.TryGetValue(definition.Id, out var worker)
                && worker.HostIdentity == identity
                && string.Equals(worker.NormalizedProxyUri, normalizedProxy, StringComparison.OrdinalIgnoreCase)
                ? worker
                : null;
        }
    }

    private bool IsFresh(DateTimeOffset capturedAt, DateTimeOffset now) =>
        capturedAt <= now.AddSeconds(5) && now - capturedAt <= FreshWindow;

    internal static HostHistorySample? ToHostHistory(RemoteHostStatusSnapshot? snapshot) => snapshot is not { Online: true }
        ? null
        : ToHostHistory(snapshot.CpuUsagePercent, snapshot.TotalMemoryBytes, snapshot.AvailableMemoryBytes,
            snapshot.DiskReadBytesPerSecond, snapshot.DiskWriteBytesPerSecond,
            snapshot.NetworkReceiveBytesPerSecond, snapshot.NetworkSendBytesPerSecond);

    internal static HostHistorySample ToHostHistory(
        double cpuUsagePercent,
        long totalMemoryBytes,
        long availableMemoryBytes,
        double? diskReadBytesPerSecond,
        double? diskWriteBytesPerSecond,
        double? networkReceiveBytesPerSecond,
        double? networkSendBytesPerSecond)
    {
        static double? Rate(double? value) => value is { } rate && double.IsFinite(rate) && rate >= 0 ? rate : null;
        return new(
            double.IsFinite(cpuUsagePercent) && cpuUsagePercent is >= 0 and <= 100 ? cpuUsagePercent : null,
            totalMemoryBytes > 0 && availableMemoryBytes >= 0 && availableMemoryBytes <= totalMemoryBytes
                ? totalMemoryBytes - availableMemoryBytes : null,
            totalMemoryBytes > 0 ? totalMemoryBytes : null,
            Rate(diskReadBytesPerSecond),
            Rate(diskWriteBytesPerSecond),
            Rate(networkReceiveBytesPerSecond),
            Rate(networkSendBytesPerSecond));
    }

    private async Task DrainWorkersAsync()
    {
        List<HostWorker> remaining;
        lock (_gate)
        {
            remaining = _workers.Values.ToList();
            _workers.Clear();
        }
        await StopWorkersAsync(remaining).ConfigureAwait(false);
    }

    private static async Task StopWorkersAsync(IEnumerable<HostWorker> workers)
    {
        var tasks = new List<Task>();
        foreach (var worker in workers)
        {
            worker.Cancel();
            if (worker.LoopTask is { } loop) { tasks.Add(loop); }
        }
        if (tasks.Count > 0)
        {
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        foreach (var worker in workers) { worker.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        _lifetimeCts.Cancel();
        Task? run;
        lock (_gate) { run = _runTask; }
        if (run is not null)
        {
            try { await run.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        await DrainWorkersAsync().ConfigureAwait(false);
        _lifetimeCts.Dispose();
        _syncGate.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class HostWorker : IDisposable
    {
        private readonly AgentRemoteHostMonitor _owner;
        private readonly CancellationTokenSource _cts;
        private DateTimeOffset? _foregroundUntilUtc;
        private ChannelWriter<TimeSpan>? _activeRateWriter;
        private TimeSpan _lastSentInterval;

        internal HostWorker(
            AgentRemoteHostMonitor owner,
            RemoteHostDefinition definition,
            string identity,
            string normalizedProxyUri,
            CancellationToken parentToken)
        {
            _owner = owner;
            Definition = definition;
            HostIdentity = identity;
            NormalizedProxyUri = normalizedProxyUri;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        }

        internal object Sync { get; } = new();
        internal string HostIdentity { get; }
        internal string NormalizedProxyUri { get; }
        internal RemoteHostDefinition Definition { get; private set; }
        internal ResourceHistoryBuffer History { get; } = new(maximumFrames: 601, maximumBytes: 4L * 1024 * 1024, maximumRows: 10);
        internal Task? LoopTask { get; private set; }
        internal long SessionEpoch { get; private set; }
        internal RemoteHostStatusSnapshot? LatestOnlineSnapshot { get; private set; }
        internal string? LastError { get; private set; }
        internal bool Connecting { get; private set; }
        internal bool HasCompletedFirstAttempt { get; private set; }
        internal DateTimeOffset? LastAttemptCompletedAt { get; private set; }
        internal TaskCompletionSource<bool>? PendingSignal { get; private set; }

        internal void Start() => LoopTask = Task.Run(() => RunLoopAsync(_cts.Token));

        internal void UpdateDefinition(RemoteHostDefinition definition)
        {
            lock (Sync) { Definition = definition; }
        }

        internal void MarkForegroundActive(DateTimeOffset untilUtc)
        {
            lock (Sync)
            {
                if (_foregroundUntilUtc is null || untilUtc > _foregroundUntilUtc.Value)
                {
                    _foregroundUntilUtc = untilUtc;
                }
                PushDesiredIntervalLocked(_owner._samplingInterval);
            }
        }

        internal void SyncDesiredSamplingInterval(DateTimeOffset now)
        {
            lock (Sync)
            {
                var desired = _foregroundUntilUtc is { } until && now < until
                    ? _owner._samplingInterval
                    : _owner.BackgroundInterval;
                PushDesiredIntervalLocked(desired);
            }
        }

        private void PushDesiredIntervalLocked(TimeSpan desired)
        {
            if (_activeRateWriter is not null && desired != _lastSentInterval)
            {
                _lastSentInterval = desired;
                _activeRateWriter.TryWrite(desired);
            }
        }

        private TimeSpan AttachRateChannel(Channel<TimeSpan> channel)
        {
            lock (Sync)
            {
                var now = _owner._clock.GetUtcNow();
                var initial = _foregroundUntilUtc is { } until && now < until
                    ? _owner._samplingInterval
                    : _owner.BackgroundInterval;
                _activeRateWriter = channel.Writer;
                _lastSentInterval = initial;
                return initial;
            }
        }

        private void DetachRateChannel(ChannelWriter<TimeSpan> writer)
        {
            lock (Sync)
            {
                if (ReferenceEquals(_activeRateWriter, writer))
                {
                    _activeRateWriter = null;
                }
            }
            writer.TryComplete();
        }

        internal void Cancel() => _cts.Cancel();

        private async Task RunLoopAsync(CancellationToken token)
        {
            var backoffIndex = 0;
            while (!token.IsCancellationRequested)
            {
                RemoteHostDefinition current;
                TaskCompletionSource<bool> signal;
                long epoch;
                lock (Sync)
                {
                    current = Definition;
                    Connecting = true;
                    SessionEpoch++;
                    epoch = SessionEpoch;
                    PendingSignal = signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                DateTimeOffset? connectedAt = null;
                var receivedSample = false;
                var rateChannel = Channel.CreateBounded<TimeSpan>(new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                });
                var initialInterval = AttachRateChannel(rateChannel);
                try
                {
                    var stream = _owner._source is IRemoteHostAdaptiveStreamSource adaptive
                        ? adaptive.StreamAdaptiveAsync(current, initialInterval, rateChannel.Reader.ReadAllAsync(token), token)
                        : _owner._source.StreamAsync(current, initialInterval, token);
                    await foreach (var sample in stream.WithCancellation(token).ConfigureAwait(false))
                    {
                        var now = _owner._clock.GetUtcNow();
                        SyncDesiredSamplingInterval(now);
                        var at = sample.CapturedAt > now && sample.CapturedAt <= now.AddMilliseconds(500)
                            ? now
                            : sample.CapturedAt;
                        if (at > now.AddSeconds(1)) { continue; }

                        if (!receivedSample)
                        {
                            receivedSample = true;
                            connectedAt = now;
                        }
                        if (connectedAt is { } since && now - since >= TimeSpan.FromSeconds(60))
                        {
                            backoffIndex = 0;
                        }

                        var hostSample = ToHostHistory(sample);
                        lock (Sync)
                        {
                            var stamped = sample with { Name = Definition.Name, CapturedAt = at };
                            LatestOnlineSnapshot = stamped;
                            LastError = null;
                            Connecting = false;
                            HasCompletedFirstAttempt = true;
                            if (hostSample is not null)
                            {
                                History.Append(new ResourceHistoryFrame(at, $"{_owner._instanceId}:{HostIdentity}:{epoch}", [], hostSample, at));
                            }
                        }
                        signal.TrySetResult(true);
                    }
                    if (!token.IsCancellationRequested)
                    {
                        SetOffline("SSH telemetry stream ended unexpectedly.");
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    SetOffline(exception.Message);
                }
                finally
                {
                    DetachRateChannel(rateChannel.Writer);
                    signal.TrySetResult(false);
                }

                if (token.IsCancellationRequested) { break; }

                var stableConnection = receivedSample
                    && connectedAt is { } okSince
                    && _owner._clock.GetUtcNow() - okSince >= TimeSpan.FromSeconds(60);
                TimeSpan delay;
                if (stableConnection)
                {
                    backoffIndex = 0;
                    delay = BackoffSteps[0];
                }
                else
                {
                    delay = BackoffSteps[Math.Min(backoffIndex, BackoffSteps.Length - 1)];
                    if (backoffIndex < BackoffSteps.Length - 1) { backoffIndex++; }
                }
                if (ReferenceEquals(_owner._clock, TimeProvider.System))
                {
                    var jitterMultiplier = 1.0 + Random.Shared.NextDouble() * 0.25;
                    delay = TimeSpan.FromMilliseconds(Math.Round(delay.TotalMilliseconds * jitterMultiplier));
                }

                try
                {
                    await Task.Delay(delay, _owner._clock, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }

        private void SetOffline(string error)
        {
            lock (Sync)
            {
                LatestOnlineSnapshot = null;
                LastError = error;
                Connecting = false;
                HasCompletedFirstAttempt = true;
                LastAttemptCompletedAt = _owner._clock.GetUtcNow();
            }
        }

        public void Dispose() => _cts.Dispose();
    }
}
