using System.IO;
using System.Text.Json;
using SystemAutorun.Agent;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Services;
using SystemAutorun.Windows.Health;
using SystemAutorun.Windows.Startup;
using Fixture = SystemAutorun.IntegrationTests.SelfStartupPlanningSessionTests.Fixture;
using Target = SystemAutorun.Windows.Startup.StartupNativeSlotProbe.SelfStartupTargetLease;

namespace SystemAutorun.IntegrationTests;

public sealed class SelfStartupAgentTests
{
    [Theory]
    [InlineData("empty", true, 1)] [InlineData("empty", false, 0)] [InlineData("missing", true, 2)] [InlineData("missing", false, 0)]
    [InlineData("same", true, 0)] [InlineData("same", false, 1)]
    public async Task RealPipeUsesOneSharedJournalForPreparationMutationInspectionAndReplay(string state, bool enabled, int calls)
    {
        await using var host = new Host();
        if (state == "missing") { host.Owned.Native.DeleteRun(); }
        if (state == "same") { host.Owned.Native.Set(1, host.Owned.TargetBytes); }
        var before = host.Storage(); var preview = await host.Client.SendResponseAsync("startup-self-prepare", enabled ? "enable" : "disable");
        Assert.True(preview.Ok); var prepared = Read<SelfStartupMutationRequest>(preview); Assert.False(prepared.Confirmed);
        AssertStorage(before, host.Storage()); Assert.Null(preview.OperationId);
        var id = AgentProtocol.NewOperationId(); var input = prepared with { Confirmed = true };
        var response = await host.Client.SendResponseAsync(SelfStartupRegistrationService.Command, payload: input, operationId: id);
        Assert.True(response.Ok); Assert.False(response.Replayed); Assert.Equal(id, response.OperationId);
        var evidence = Read<SelfJournalEvidence>(response); Assert.True(evidence.ProtocolCompleted); Assert.Equal(calls, evidence.Calls.Count);
        var receipt = Read<OperationReceipt>(await host.Client.SendResponseAsync("operation", id));
        Assert.Equal(OperationState.Completed, receipt.State); Assert.Equal(host.Journal.InstanceId, receipt.OwnerInstanceId);
        Assert.Single(Directory.EnumerateFiles(host.Paths.RootDirectory, "*.operation.json", SearchOption.AllDirectories));
        Assert.Equal(host.Journal.InstanceId, host.Service.JournalInstanceId); Assert.Same(host.Paths, host.Journal.Paths);
        var native = host.Owned.Native.Read(); var opens = host.Opens; var files = host.Storage();
        var replay = await host.Client.SendResponseAsync(SelfStartupRegistrationService.Command, payload: input, operationId: id);
        Assert.True(replay.Ok); Assert.True(replay.Replayed); Assert.Equal(opens, host.Opens); Assert.Equal(native, host.Owned.Native.Read()); AssertStorage(files, host.Storage());
        foreach (var header in new[] { "case", "root", "version", "timeout" })
        {
            var alias = Change(SelfStartupJournalTests.Envelope(input) with { Command = SelfStartupRegistrationService.Command, OperationId = id }, header);
            Assert.Equal("invalid_argument", (await host.Dispatcher.DispatchAsync(alias, default)).ErrorCode);
            Assert.Equal(opens, host.Opens); AssertStorage(files, host.Storage());
        }
        var conflict = await host.Client.SendResponseAsync(SelfStartupRegistrationService.Command, payload: input with { Enabled = !enabled }, operationId: id);
        Assert.Equal("operation_id_conflict", conflict.ErrorCode); Assert.Equal(native, host.Owned.Native.Read()); await host.Owned.AssertReleased();
    }

    [Fact]
    public async Task RunningAndDuplicateAreObservedByTheExactSharedAgentInstanceWithoutNestedJournal()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        await using var host = new Host((owned, ct) => { entered.TrySetResult(); release.Wait(ct); return owned.Package.Open(token: ct); });
        var id = AgentProtocol.NewOperationId(); var request = host.Owned.Request();
        var pending = host.Client.SendResponseAsync(SelfStartupRegistrationService.Command, payload: request, operationId: id, timeout: TimeSpan.FromSeconds(10));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var receipt = Read<OperationReceipt>(await host.Client.SendResponseAsync("operation", id));
            Assert.Equal(OperationState.Running, receipt.State); Assert.Equal(host.Journal.InstanceId, receipt.OwnerInstanceId);
            var duplicate = await host.Client.SendResponseAsync(SelfStartupRegistrationService.Command, payload: request, operationId: id);
            Assert.Equal("operation_in_progress", duplicate.ErrorCode); Assert.True(duplicate.Replayed); Assert.Equal(1, host.Opens);
            Assert.True(await host.Client.PingAsync(TimeSpan.FromSeconds(2)));
        }
        finally { release.Set(); await pending; }
        Assert.True((await pending).Ok); Assert.Equal(OperationState.Completed, Read<OperationReceipt>(await host.Client.SendResponseAsync("operation", id)).State);
    }

    [Theory]
    [InlineData("payload")] [InlineData("operation")] [InlineData("revision")] [InlineData("name")] [InlineData("missing-name")]
    [InlineData("case")] [InlineData("root")] [InlineData("version")] [InlineData("timeout")] [InlineData("timeout-max")]
    public async Task PreparationRejectsUnexpectedInputsBeforePackageOrAdmission(string change)
    {
        await using var host = new Host(); var before = host.Storage();
        var request = new AgentRequest("query", "startup-self-prepare", "enable");
        request = Change(request, change);
        var result = await host.Dispatcher.DispatchAsync(request, default); Assert.Equal("invalid_argument", result.ErrorCode);
        Assert.Equal(0, host.Opens); AssertStorage(before, host.Storage());
    }

    [Theory]
    [InlineData("name")] [InlineData("revision")] [InlineData("case")] [InlineData("root")] [InlineData("version")]
    [InlineData("timeout")] [InlineData("timeout-max")] [InlineData("operation")]
    public async Task MutationHeadersAreCheckedBeforeAdmissionIncludingCachedAliases(string change)
    {
        await using var host = new Host(); var before = host.Storage();
        var request = SelfStartupJournalTests.Envelope(host.Owned.Request()) with { Command = SelfStartupRegistrationService.Command };
        request = Change(request, change);
        var result = await host.Dispatcher.DispatchAsync(request, default);
        Assert.Equal(change == "operation" ? "invalid_operation_id" : "invalid_argument", result.ErrorCode);
        Assert.Equal(0, host.Opens); AssertStorage(before, host.Storage());
    }

    [Fact]
    public async Task ExpensivePreparationIsSingleFlightWithoutBlockingOtherPipeQueriesOrCreatingReceipts()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        await using var host = new Host((owned, ct) => { entered.TrySetResult(); release.Wait(ct); return owned.Package.Open(token: ct); });
        var before = host.Storage(); var pending = host.Client.SendResponseAsync("startup-self-prepare", "enable", timeout: TimeSpan.FromSeconds(10));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var busy = await host.Client.SendResponseAsync("startup-self-prepare", "disable");
            Assert.Equal("configuration_busy", busy.ErrorCode); Assert.Null(busy.OperationId); Assert.Equal(1, host.Opens);
            Assert.True(await host.Client.PingAsync(TimeSpan.FromSeconds(2))); AssertStorage(before, host.Storage());
        }
        finally { release.Set(); await pending; }
        Assert.True((await pending).Ok); Assert.False(Read<SelfStartupMutationRequest>(await pending).Confirmed);
        AssertStorage(before, host.Storage()); Assert.Empty(Directory.EnumerateFiles(host.Paths.RootDirectory, "*.operation.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ConfirmationIsNotSuppliedByAgentAndUnknownPayloadFieldsCannotSelectATarget()
    {
        await using var host = new Host(); var request = host.Owned.Request() with { Confirmed = false };
        var denied = await host.Client.SendResponseAsync(SelfStartupRegistrationService.Command, payload: request, operationId: AgentProtocol.NewOperationId());
        Assert.Equal("not_executed", denied.ErrorCode); Assert.Equal("authorization_required", Read<SelfJournalEvidence>(denied).FailureCode); Assert.Equal(0, host.Opens);
        var fields = JsonSerializer.SerializeToElement(new { schemaVersion = 1, enabled = true, confirmed = true,
            targetContextId = request.TargetContextId, expectedRegistration = request.ExpectedRegistration, path = @"\\offline.invalid\forbidden.exe" }, AgentProtocol.JsonOptions);
        var invalid = await host.Client.SendResponseAsync(SelfStartupRegistrationService.Command, payload: fields, operationId: AgentProtocol.NewOperationId());
        Assert.Equal("not_executed", invalid.ErrorCode); Assert.Empty(Read<SelfJournalEvidence>(invalid).Calls); Assert.Equal(0, host.Opens);
        Assert.False(host.Owned.Native.Read().Registration.Registered);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DispatcherRefusesDifferentJournalInstanceOrRoot(bool wrongRoot)
    {
        using var owned = new Fixture(); using var other = new Fixture(); var paths = SelfStartupJournalTests.Paths(owned);
        var journal = new OperationJournal(wrongRoot ? SelfStartupJournalTests.Paths(other) : paths);
        var independent = new SelfStartupRegistrationService(new SelfStartupJournal(owned.Native.Id, paths,
            ct => owned.Package.Open(token: ct), ct => StartupNativeSlotProbe.SelfStartupAdmissionLease.AcquireAsync(owned.Admission.Scope, ct)));
        Assert.Throws<ArgumentException>(() => CreateDispatcher(paths, journal, independent));
    }

    [Fact]
    public async Task DefaultProductionServiceRefusesTesthostBeforeNativeObservationOrAdmission()
    {
        using var owned = new Fixture(); var paths = SelfStartupJournalTests.Paths(owned); var journal = new OperationJournal(paths);
        var dispatcher = CreateDispatcher(paths, journal); var before = Directory.GetFileSystemEntries(paths.RootDirectory, "*", SearchOption.AllDirectories).Order().ToArray();
        var result = await dispatcher.DispatchAsync(new("prepare", "startup-self-prepare", "enable"), default);
        Assert.Equal("self_target_unavailable", result.ErrorCode); Assert.Equal(before, Directory.GetFileSystemEntries(paths.RootDirectory, "*", SearchOption.AllDirectories).Order());
        Assert.True(AgentProtocol.IsMutation(SelfStartupRegistrationService.Command)); Assert.False(AgentProtocol.IsConfigurationMutation(SelfStartupRegistrationService.Command));
        Assert.False(AgentProtocol.IsMutation("startup-self-prepare"));
    }

    private static AgentRequest Change(AgentRequest request, string change) => change switch
    {
        "payload" => request with { Payload = JsonSerializer.SerializeToElement(new { enabled = true }) },
        "operation" => request with { OperationId = request.Command == "startup-self-prepare" ? AgentProtocol.NewOperationId() : "invalid" },
        "revision" => request with { ExpectedRevision = new string('a', 64) },
        "name" => request with { Name = "unexpected" }, "missing-name" => request with { Name = null },
        "case" => request with { Command = request.Command.ToUpperInvariant() }, "root" => request with { RootId = new string('0', 64) },
        "version" => request with { ProtocolVersion = 1 }, "timeout" => request with { TimeoutMilliseconds = 0 },
        _ => request with { TimeoutMilliseconds = AgentProtocol.MaximumRequestTimeoutMilliseconds + 1 }
    };
    private static T Read<T>(AgentResponse response) { Assert.True(response.Data is JsonElement); return ((JsonElement)response.Data!).Deserialize<T>(AgentProtocol.JsonOptions)!; }
    private static void AssertStorage(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    { Assert.Equal(before.Keys.Order(), after.Keys.Order()); foreach (var row in before) { Assert.Equal(row.Value, after[row.Key]); } }

    private static AgentCommandDispatcher CreateDispatcher(ProjectPaths paths, OperationJournal journal, SelfStartupRegistrationService? service = null) =>
        new(new TaskOrchestrator(new ConfigurationCache(new ConfigurationLoader(), paths), new OverrideStore(), new NeverExecutor(), new NeverProbe(), new FileLog()),
            new HostStatusReader(), new ConfigurationWorkspace(paths), operations: journal, selfRegistration: service);

    internal sealed class Host : IAsyncDisposable
    {
        internal Fixture Owned { get; } = new();
        internal ProjectPaths Paths { get; }
        internal OperationJournal Journal { get; }
        internal SelfStartupRegistrationService Service { get; }
        internal AgentCommandDispatcher Dispatcher { get; }
        internal AgentClient Client { get; }
        internal int Opens;
        private readonly CancellationTokenSource _stop = new(); private readonly Task _server;
        internal Host(Func<Fixture, CancellationToken, Target>? open = null)
        {
            Paths = SelfStartupJournalTests.Paths(Owned);
            File.WriteAllText(Paths.ConfigPath, "version: 1\nsettings:\n  default_startup_delay: 0\ntasks: []\n");
            Journal = new(Paths);
            Service = new(new SelfStartupJournal(Owned.Native.Id, Journal, ct => { Interlocked.Increment(ref Opens); return open is null ? Owned.Package.Open(token: ct) : open(Owned, ct); },
                ct => StartupNativeSlotProbe.SelfStartupAdmissionLease.AcquireAsync(Owned.Admission.Scope, ct)));
            Dispatcher = CreateDispatcher(Paths, Journal, Service); Client = new(Paths.RootDirectory);
            _server = new AgentServer(Dispatcher, _stop, Paths.RootDirectory).RunAsync(_stop.Token);
        }
        internal Dictionary<string, byte[]> Storage() => new[] { Owned.Package.Root, Owned.Admission.Root }
            .SelectMany(root => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            .ToDictionary(path => path, path => Directory.Exists(path) || new FileInfo(path).Length == 0 ? [] : File.ReadAllBytes(path));
        public async ValueTask DisposeAsync()
        { try { _stop.Cancel(); await _server.WaitAsync(TimeSpan.FromSeconds(10)); } finally { _stop.Dispose(); Owned.Dispose(); } }
    }
    private sealed class NeverProbe : IHealthProbe
    { public Task<HealthResult> CheckAsync(AutorunTask task, bool quick, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No workload probes."); }
    private sealed class NeverExecutor : ITaskExecutor
    {
        public Task<CommandResult> StartAsync(AutorunTask task, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No workloads.");
        public Task<CommandResult> StopAsync(AutorunTask task, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No workloads.");
        public IReadOnlyList<int> FindProcessIds(AutorunTask task) => throw new InvalidOperationException("No process matching.");
    }
}
