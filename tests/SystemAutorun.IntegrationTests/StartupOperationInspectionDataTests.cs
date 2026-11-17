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

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup", "prepared", true)] [InlineData("Startup", "native", true)]
    [InlineData("Startup", "committed", true)] [InlineData("Startup", "finalized", true)]
    [InlineData("Common Startup", "prepared", true)] [InlineData("Common Startup", "native", true)]
    [InlineData("Common Startup", "committed", true)] [InlineData("Common Startup", "finalized", true)]
    [InlineData("Startup", "committed", false)] [InlineData("Common Startup", "prepared", false)]
    public async Task OperationDiagnosticDataActualConsoleReadsActualUnknownJournalAndProtectedAssociationWithoutWriting(string source, string phase, bool json)
    {
        using var store = HistoryTransactionStore(source); var fixture = await PrepareOperationEvidence(store, phase, source);
        var paths = JournalEvidencePaths(store, fixture.Receipt.OperationId); var orphan = paths.Receipt + "." + Guid.NewGuid().ToString("N") + ".tmp";
        VerifyEvidenceOwnedPath(store.History.Owned, orphan); File.WriteAllBytes(orphan, [1, 9, 7]);
        var access = new ReconciliationAccess(store.History.Owned.Native);
        await using var server = new OperationDiagnosticServer(store, new StartupOperationInspectionReader(store.History.Owned.Paths, access, () => store.DirectoryPath));
        var before = OperationEvidenceImages(store);
        var sdk = await new AgentStartupClient(server.Client).InspectOperationAsync(fixture.Receipt.OperationId);
        Assert.Equal("unknown", sdk.ObservedReceiptState); Assert.Equal(phase == "finalized" ? "none" : "observed", sdk.Evidence!.State);
        var result = await RunOperationDiagnosticConsole(store, fixture.Receipt.OperationId, json);
        Assert.Equal(6, result.Exit);
        if (json)
        {
            Assert.Empty(result.Error); using var document = JsonDocument.Parse(result.Output); var envelope = document.RootElement;
            Assert.False(envelope.GetProperty("ok").GetBoolean()); Assert.Equal("startup_operation_unknown", envelope.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(AgentProtocol.ForRoot(store.History.Owned.Root).RootId, envelope.GetProperty("rootId").GetString()); Assert.Equal(JsonValueKind.Null, envelope.GetProperty("operationId").ValueKind);
            var observed = AgentStartupClient.ReadOperationInspection(new("console-typed-check", true, envelope.GetProperty("data").Clone()), fixture.Receipt.OperationId);
            Assert.Equal(sdk.Evidence!.AssociationState, observed.Evidence!.AssociationState); Assert.True(observed.DiagnosticOnly);
            Assert.Equal(phase == "finalized" ? null : fixture.Candidate.Binding.RecordId, observed.Evidence.RecordId);
        }
        else
        {
            Assert.Contains("receipt on disk: unknown | conservative observation: unknown", result.Output);
            Assert.Contains("Diagnostic only", result.Output); Assert.Contains("not original success", result.Output); Assert.Contains("startup_operation_unknown", result.Error);
        }
        Assert.Equal(new byte[] { 1, 9, 7 }, File.ReadAllBytes(orphan)); AssertOperationEvidenceImages(store, before);
        Assert.False(sdk.NativeWriteReady); Assert.False(sdk.ExecutionRequested); Assert.False(sdk.TransactionFinalized); Assert.False(sdk.MayFinalize); Assert.False(sdk.ReceiptUpdated); Assert.False(sdk.ReplayRequested);
    }

    [Fact]
    public async Task OperationDiagnosticDataSyntheticCompletedPreparedReceiptIsDiagnosticZeroNotFinalizationAuthority()
    {
        using var store = HistoryTransactionStore(); using var manager = store.History.Owned.Manager(); var request = await HistoryOperationRequest(store, await DraftEntry(store, manager));
        await RunHistoryOwnerContext(new OperationJournal(store.History.Owned.Paths), request, async (context, token) =>
        {
            await using var owner = await manager.PrepareHistoryDisableAsync(context, () => store.DirectoryPath, token);
            return new(context.Request.Id, true); // Explicitly synthetic callback success, still only prepared.
        });
        var access = new ReconciliationAccess(store.History.Owned.Native);
        await using var server = new OperationDiagnosticServer(store, new StartupOperationInspectionReader(store.History.Owned.Paths, access, () => store.DirectoryPath));
        var before = OperationEvidenceImages(store); var result = await RunOperationDiagnosticConsole(store, request.OperationId!);
        Assert.Equal(0, result.Exit); using var document = JsonDocument.Parse(result.Output); var data = document.RootElement.GetProperty("data");
        Assert.Equal("completed", data.GetProperty("observedReceiptState").GetString()); Assert.Equal("associated_candidate", data.GetProperty("evidence").GetProperty("associationState").GetString());
        Assert.False(data.GetProperty("transactionFinalized").GetBoolean()); Assert.False(data.GetProperty("mayFinalize").GetBoolean()); AssertOperationEvidenceImages(store, before);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OperationDiagnosticDataMissingIdNeverScansOtherReceiptsOrCreatesJournalDirectories(bool otherExists)
    {
        using var store = HistoryTransactionStore(); if (otherExists) { await PrepareOperationEvidence(store, "committed"); }
        var access = new ReconciliationAccess(store.History.Owned.Native);
        await using var server = new OperationDiagnosticServer(store, new StartupOperationInspectionReader(store.History.Owned.Paths, access, () => store.DirectoryPath));
        var before = OperationEvidenceImages(store); var directories = Directory.GetDirectories(store.History.Owned.Root, "*", SearchOption.AllDirectories);
        var result = await RunOperationDiagnosticConsole(store, AgentProtocol.NewOperationId());
        Assert.Equal(1, result.Exit); using var document = JsonDocument.Parse(result.Output); var data = document.RootElement.GetProperty("data");
        Assert.Equal("missing", data.GetProperty("journalState").GetString()); Assert.Equal(JsonValueKind.Null, data.GetProperty("evidence").ValueKind); Assert.Equal(0, access.DirectoryCalls);
        AssertOperationEvidenceImages(store, before); Assert.Equal(directories, Directory.GetDirectories(store.History.Owned.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OperationDiagnosticDataOtherActualJournalCommandIsExplicitlyUnsupportedWithoutNativeObservation()
    {
        using var store = HistoryTransactionStore(); var id = AgentProtocol.NewOperationId(); var journal = new OperationJournal(store.History.Owned.Paths);
        await journal.RunAsync(new("non-startup", "start", "fixture", OperationId: id), _ => Task.FromResult(new AgentResponse("non-startup", true)));
        var access = new ReconciliationAccess(store.History.Owned.Native);
        await using var server = new OperationDiagnosticServer(store, new StartupOperationInspectionReader(store.History.Owned.Paths, access, () => store.DirectoryPath));
        var before = OperationEvidenceImages(store); var result = await RunOperationDiagnosticConsole(store, id);
        Assert.Equal(1, result.Exit); using var document = JsonDocument.Parse(result.Output); var data = document.RootElement.GetProperty("data");
        Assert.Equal("history_journal_command_unsupported", data.GetProperty("journalReason").GetString()); Assert.Equal(0, access.DirectoryCalls); AssertOperationEvidenceImages(store, before);
    }

    [Fact]
    public async Task OperationDiagnosticDataAdapterCancellationRetainsAdmissionUntilRealNativeWorkAndCleanupEnd()
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed");
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var access = new ReconciliationAccess(store.History.Owned.Native) { Map = source =>
        { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) { throw new TimeoutException("Owned gate timed out."); } return store.History.Owned.Native.GetShortcutDirectory(source); } };
        var reader = new StartupOperationInspectionReader(store.History.Owned.Paths, access, () => store.DirectoryPath); using var cancellation = new CancellationTokenSource();
        var workflow = reader.ReadAsync(fixture.Receipt.OperationId, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel(); Assert.False(workflow.IsCompleted);
            var busy = await reader.ReadAsync(fixture.Receipt.OperationId); Assert.Equal("busy", busy.JournalState); Assert.Null(busy.Evidence);
        }
        finally { release.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await workflow); }
        var result = await reader.ReadAsync(fixture.Receipt.OperationId); Assert.Equal("observed", result.JournalState);
    }

    [Fact]
    public async Task OperationDiagnosticDataActualConsoleTimeoutDoesNotReleaseAgentAdmissionBeforeWorkerDrains()
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed");
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var access = new ReconciliationAccess(store.History.Owned.Native) { Map = source =>
        { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) { throw new TimeoutException("Owned gate timed out."); } return store.History.Owned.Native.GetShortcutDirectory(source); } };
        var tracking = new DiagnosticTrackingReader(new StartupOperationInspectionReader(store.History.Owned.Paths, access, () => store.DirectoryPath));
        await using var server = new OperationDiagnosticServer(store, tracking);
        var console = RunOwnedCliConsole(["startup", "operation", "inspect", fixture.Receipt.OperationId, "--root", store.History.Owned.Root, "--timeout", "2", "--json"], "");
        Task<StartupOperationInspectionSnapshot>? workflow = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); workflow = await tracking.FirstWorkflow.Task.WaitAsync(TimeSpan.FromSeconds(5)); _ = Assert.Single(tracking.Workflows);
            var result = await console; Assert.Equal(6, result.Exit); Assert.False(workflow.IsCompleted);
            using var document = JsonDocument.Parse(result.Output); Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("data").ValueKind);
            var busy = await new AgentStartupClient(server.Client).InspectOperationAsync(fixture.Receipt.OperationId); Assert.Equal("busy", busy.JournalState); Assert.Null(busy.Evidence);
        }
        finally
        {
            release.Set(); await console;
            if (workflow is not null) { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await workflow); }
        }
        var available = await new AgentStartupClient(server.Client).InspectOperationAsync(fixture.Receipt.OperationId); Assert.Equal("observed", available.JournalState);
    }

    [Theory]
    [InlineData("payload")] [InlineData("revision")] [InlineData("mutation-id")]
    public async Task OperationDiagnosticDataRpcRejectsNonQueryInputsBeforeReadingNativeState(string mode)
    {
        using var store = HistoryTransactionStore(); var fixture = await PrepareOperationEvidence(store, "committed"); var access = new ReconciliationAccess(store.History.Owned.Native);
        await using var server = new OperationDiagnosticServer(store, new StartupOperationInspectionReader(store.History.Owned.Paths, access, () => store.DirectoryPath));
        var before = OperationEvidenceImages(store);
        var reply = mode == "mutation-id" ? await SendDiagnosticRawMutationIdentity(store, fixture.Receipt.OperationId)
            : await server.Client.SendResponseAsync("startup-operation-inspect", name: fixture.Receipt.OperationId,
                payload: mode == "payload" ? new { path = "not-accepted" } : null,
                expectedRevision: mode == "revision" ? new string('a', 64) : null);
        Assert.False(reply.Ok); Assert.Equal("invalid_argument", reply.ErrorCode); Assert.Null(reply.Data); Assert.Equal(0, access.DirectoryCalls); AssertOperationEvidenceImages(store, before);
    }

    private sealed class DiagnosticTrackingReader(IStartupOperationInspectionReader inner) : IStartupOperationInspectionReader
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<Task<StartupOperationInspectionSnapshot>> Workflows = new();
        internal readonly TaskCompletionSource<Task<StartupOperationInspectionSnapshot>> FirstWorkflow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StartupOperationInspectionSnapshot> ReadAsync(string operationId, CancellationToken token = default)
        { var workflow = inner.ReadAsync(operationId, token); Workflows.Enqueue(workflow); FirstWorkflow.TrySetResult(workflow); return workflow; }
    }

    private static async Task<AgentResponse> SendDiagnosticRawMutationIdentity(HistoryStoreFixture store, string id)
    {
        var endpoint = AgentProtocol.ForRoot(store.History.Owned.Root);
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", endpoint.PipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        using var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var request = new AgentRequest(Guid.NewGuid().ToString("N"), "startup-operation-inspect", id, RootId: endpoint.RootId, OperationId: AgentProtocol.NewOperationId());
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, AgentProtocol.JsonOptions));
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.NotNull(line);
        return JsonSerializer.Deserialize<AgentResponse>(line, AgentProtocol.JsonOptions)!;
    }

    private static Task<(int Exit, string Output, string Error)> RunOperationDiagnosticConsole(HistoryStoreFixture store, string id, bool json = true) =>
        RunOwnedCliConsole(["startup", "operation", "inspect", id, "--root", store.History.Owned.Root, ..(json ? new[] { "--json" } : Array.Empty<string>())], "");

    private sealed class OperationDiagnosticServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _shutdown = new(); private readonly Task _server;
        internal AgentClient Client { get; }
        internal OperationDiagnosticServer(HistoryStoreFixture store, IStartupOperationInspectionReader reader)
        {
            var paths = store.History.Owned.Paths; Client = new(paths.RootDirectory);
            var cache = new ConfigurationCache(new ConfigurationLoader(), paths);
            var orchestrator = new TaskOrchestrator(cache, new OverrideStore(), new DiagnosticNeverExecutor(), new DiagnosticNeverProbe(), new FileLog());
            var dispatcher = new AgentCommandDispatcher(orchestrator, new HostStatusReader(), new ConfigurationWorkspace(paths), startupOperationReader: reader);
            _server = new AgentServer(dispatcher, _shutdown, paths.RootDirectory).RunAsync(_shutdown.Token);
        }
        public async ValueTask DisposeAsync()
        {
            try { _shutdown.Cancel(); await _server.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { _shutdown.Dispose(); } // Store, not this server, owns and deletes the verified fixture root.
        }
    }
    private sealed class DiagnosticNeverProbe : IHealthProbe
    { public Task<HealthResult> CheckAsync(AutorunTask task, bool quick, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Diagnostic must not probe workloads."); }
    private sealed class DiagnosticNeverExecutor : ITaskExecutor
    {
        public Task<CommandResult> StartAsync(AutorunTask task, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Diagnostic must not start workloads.");
        public Task<CommandResult> StopAsync(AutorunTask task, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Diagnostic must not stop workloads.");
        public IReadOnlyList<int> FindProcessIds(AutorunTask task) => throw new InvalidOperationException("Diagnostic must not inspect workload processes.");
    }
}
