using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SystemAutorun.Agent;
using SystemAutorun.Core.Configuration;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Recovery;
using SystemAutorun.Core.Services;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.IntegrationTests;

// Real Dispatcher/controller/store/Journal, test-owned Pipe framing for withholding
// an ACK. This is NOT the production AgentServer. Hooks run OUTSIDE the Journal;
// assertions/failures are awaited by DisposeAsync and cannot become fake Unknowns.
internal sealed class RecoveryWorkflowDispatchFixture : IAsyncDisposable
{
    internal string Root { get; } = Directory.CreateTempSubdirectory("SystemAutorun-r90-workflow-").FullName;
    internal ProjectPaths Paths { get; }
    internal AgentEndpoint Endpoint { get; }
    internal AgentClient Client { get; }
    internal RecoveryControlStore Store { get; }
    internal OperationJournal Journal { get; }
    internal ConcurrentQueue<AgentRequest> Requests { get; } = new();
    internal ConcurrentQueue<(string Command, Exception Error)> Errors { get; } = new();
    internal Func<AgentRequest, Task>? BeforeDispatch { get; set; }
    internal Func<AgentRequest, AgentResponse, Task>? AfterDispatch { get; set; }
    internal bool DropResumeAck { get; set; }
    internal int WorkloadCalls => _noWork.Calls;
    internal CancellationToken Token => _stop.Token;
    internal RecoveryWorkflow Workflow => RecoveryWorkflow.ForTask(Paths, "media");
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
    private readonly NoWork _noWork = new();
    private readonly Task _server;
    internal RecoveryWorkflowDispatchFixture(int maximumResultBytes = 32768)
    {
        File.WriteAllText(Path.Combine(Root, "autorun.yaml"), RecoveryWorkflowIntegrationTests.Configuration);
        Paths = ProjectPaths.Resolve(Root); Endpoint = AgentProtocol.ForRoot(Paths.RootDirectory); Client = new(Root); Store = new(Paths);
        var workspace = new ConfigurationWorkspace(Paths); var cache = new ConfigurationCache(new ConfigurationLoader(), Paths);
        Journal = new(Paths, limits: new OperationJournalLimits(MaximumResultBytes: maximumResultBytes));
        var dispatcher = new AgentCommandDispatcher(new TaskOrchestrator(cache, new OverrideStore(), _noWork, _noWork, new FileLog()),
            new HostStatusReader(), workspace, operations: Journal, errorObserver: (request, error) => Errors.Enqueue((request.Command, error)));
        _server = Task.Run(async () =>
        {
            while (true)
            {
                await using var pipe = new NamedPipeServerStream(Endpoint.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token); using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(_stop.Token); if (line is null) { continue; }
                var request = JsonSerializer.Deserialize<AgentRequest>(line, AgentProtocol.JsonOptions)!; Requests.Enqueue(request);
                if (BeforeDispatch is { } before) { await before(request); }
                var response = await dispatcher.DispatchAsync(request, _stop.Token);
                if (AfterDispatch is { } after) { await after(request, response); }
                if (DropResumeAck && request.Command == "recovery-resume") { continue; }
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(JsonSerializer.Serialize(response with { RootId = Endpoint.RootId }, AgentProtocol.JsonOptions).AsMemory(), _stop.Token);
            }
        });
    }
    internal async Task InitializeAsync()
    { var before = await Store.ReadAsync(); await Store.InitializePausedAsync(new[] { "media" }, before.Revision); }
    public async ValueTask DisposeAsync()
    {
        try { _stop.Cancel(); try { await _server.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) when (_stop.IsCancellationRequested) { } }
        finally
        {
            _stop.Dispose(); var absolute = Path.GetFullPath(Root);
            if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(absolute).StartsWith("SystemAutorun-r90-workflow-", StringComparison.Ordinal)
                || (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0)
            { throw new InvalidOperationException("Refusing cleanup outside the owned ordinary workflow fixture."); }
            Directory.Delete(absolute, recursive: true);
        }
    }
    private sealed class NoWork : ITaskExecutor, IHealthProbe
    {
        internal int Calls;
        private InvalidOperationException Deny() { Interlocked.Increment(ref Calls); return new("Recovery workflow must not execute/probe a workload."); }
        public Task<CommandResult> StartAsync(AutorunTask task, CancellationToken cancellationToken = default) => throw Deny();
        public Task<CommandResult> StopAsync(AutorunTask task, CancellationToken cancellationToken = default) => throw Deny();
        public IReadOnlyList<int> FindProcessIds(AutorunTask task) => throw Deny();
        public Task<HealthResult> CheckAsync(AutorunTask task, bool quick, CancellationToken cancellationToken = default) => throw Deny();
    }
}
