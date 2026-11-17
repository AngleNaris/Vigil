using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SystemAutorun.Cli;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Recovery;

namespace SystemAutorun.IntegrationTests;

public sealed partial class RecoveryCliPendingTests
{
    [Theory]
    [InlineData("valid", false)] [InlineData("valid", true)]
    [InlineData("corrupt", false)] [InlineData("corrupt", true)]
    [InlineData("directory", false)] [InlineData("directory", true)]
    public async Task ExistingOrUnreadableSameNameHintBlocksBeforeConnectingWithoutACandidateId(string state, bool explicitId)
    {
        using var fixture = new Fixture(); var original = AgentProtocol.NewOperationId();
        if (state == "valid") { _ = await fixture.Pending.ReserveAsync(original, Revision); }
        else if (state == "corrupt") { File.WriteAllText(fixture.PendingPath, "{broken"); }
        else { Directory.CreateDirectory(fixture.PendingPath); }
        var bytes = File.Exists(fixture.PendingPath) ? File.ReadAllBytes(fixture.PendingPath) : null; var connections = 0;
        var app = new CliApplication(_ => { connections++; throw new InvalidOperationException("Blocked pending must precede the factory."); });
        var result = await Run(app, fixture.Root, Resume("MEDIA", Revision, explicitId ? AgentProtocol.NewOperationId() : null));
        Assert.Equal(0, connections); Assert.Equal(state == "valid" ? 6 : 3, result.Exit);
        Assert.Equal(state == "valid" ? "recovery_busy" : "pending_storage_unavailable", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(state == "valid" ? original : null, result.Json.GetProperty("operationId").GetString());
        Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("data").ValueKind); Assert.Null(result.Json.GetProperty("requestId").GetString());
        Assert.DoesNotContain("reserved operationId:", result.Error);
        if (bytes is not null) { Assert.Equal(bytes, File.ReadAllBytes(fixture.PendingPath)); } else { Assert.True(Directory.Exists(fixture.PendingPath)); }
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public async Task CancelledPendingReadDoesNotConnectOrInventAnOperationId()
    {
        using var fixture = new Fixture(); using var stop = new CancellationTokenSource(); stop.Cancel(); var connections = 0;
        var result = await Run(new(_ => { connections++; throw new InvalidOperationException("No connection expected."); }), fixture.Root, Resume("media", Revision), stop.Token);
        Assert.Equal(6, result.Exit); Assert.Equal("cancelled", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Null(result.Json.GetProperty("operationId").GetString()); Assert.Equal(0, connections); Assert.False(File.Exists(fixture.PendingPath));
    }

    [Theory]
    [InlineData("other-name")] [InlineData("other-root")] [InlineData("missing")]
    public async Task MissingOrDifferentScopeHintDoesNotChangeExistingResumeEnvelope(string scope)
    {
        using var fixture = new Fixture(); using var other = new Fixture(); var original = AgentProtocol.NewOperationId(); var id = AgentProtocol.NewOperationId();
        if (scope == "other-name") { _ = await new RecoveryPendingStore(fixture.Paths, "different").ReserveAsync(original, Revision); }
        else if (scope == "other-root") { _ = await other.Pending.ReserveAsync(original, Revision); }
        var requests = new List<CliRequest>();
        var result = await Run(new(root => new Fake(request => { requests.Add(request); return RecoveryAck(root, request); })), fixture.Root, Resume("media", Revision, id));
        Assert.Equal(0, result.Exit); var request = Assert.Single(requests); Assert.Equal("recovery-resume", request.Command);
        Assert.Equal(id, request.OperationId); Assert.Equal(Revision, request.Revision); Assert.Equal("media", request.Name);
        Assert.Equal(id, result.Json.GetProperty("operationId").GetString()); Assert.Equal("preserved", result.Json.GetProperty("data").GetProperty("futureField").GetString());
        Assert.False(File.Exists(fixture.PendingPath));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExistingHintDoesNotTurnReadOnlyStateOrOriginalReceiptIntoAWrite(bool receipt)
    {
        using var fixture = new Fixture(); var original = AgentProtocol.NewOperationId(); _ = await fixture.Pending.ReserveAsync(original, Revision);
        var bytes = File.ReadAllBytes(fixture.PendingPath); var calls = new List<CliRequest>();
        var result = await Run(new(root => new Fake(request => { calls.Add(request); return receipt
            ? new("read", true, null, RootId: AgentProtocol.ForRoot(root).RootId)
            : RecoveryAck(root, request with { Name = "media" }); })), fixture.Root,
            receipt ? ["operation", "show", original] : ["recovery", "state"]);
        Assert.Equal(0, result.Exit); Assert.Equal(receipt ? "operation" : "recovery-state", Assert.Single(calls).Command);
        Assert.Null(calls[0].OperationId); Assert.Equal(bytes, File.ReadAllBytes(fixture.PendingPath));
    }

    [Fact]
    public async Task GuiOriginatedUnknownBlocksIndependentConsoleAndPreservesNewerStopIntent()
    {
        await using var host = new RecoveryWorkflowDispatchFixture(); await host.InitializeAsync(); host.DropResumeAck = true;
        var prepared = (await host.Workflow.PrepareAsync()).Preparation!;
        var unknown = await host.Workflow.SubmitAsync(prepared with { Confirmed = true }, TimeSpan.FromSeconds(3));
        Assert.Equal(RecoveryWorkflowState.OutcomeUnknown, unknown.State); Assert.NotNull(unknown.OperationId); host.DropResumeAck = false;
        await host.Store.SetPausedAsync("media", true, recordIntent: true); var before = await host.Store.ReadAsync();
        var hint = Path.Combine(host.Root, new RecoveryPendingStore(host.Paths, "media").FileName); var bytes = File.ReadAllBytes(hint);
        var requests = host.Requests.Count; var journals = Directory.GetFiles(host.Root, "*.operation.json", SearchOption.AllDirectories).Length;
        var result = await Console(host.Root, Resume("MEDIA", before.Revision, AgentProtocol.NewOperationId())); var after = await host.Store.ReadAsync();
        Assert.True(result.Exit == 6 && after.Revision == before.Revision && host.Requests.Count == requests,
            $"CLI exit={result.Exit}; paused={after.Tasks.Single().Paused}; mutation RPC delta={host.Requests.Count - requests}; revision unchanged={after.Revision == before.Revision}");
        Assert.Equal("recovery_busy", result.Json.GetProperty("error").GetProperty("code").GetString()); Assert.Equal(unknown.OperationId, result.Json.GetProperty("operationId").GetString());
        Assert.Equal(bytes, File.ReadAllBytes(hint)); Assert.True(after.Tasks.Single().Paused);
        Assert.Equal(journals, Directory.GetFiles(host.Root, "*.operation.json", SearchOption.AllDirectories).Length);
        var queried = await Console(host.Root, ["operation", "show", unknown.OperationId!]); Assert.Equal(0, queried.Exit);
        Assert.Equal(unknown.OperationId, queried.Json.GetProperty("data").GetProperty("operationId").GetString());
        var state = await Console(host.Root, ["recovery", "state"]); Assert.Equal(0, state.Exit);
        Assert.Contains("original operationId: " + unknown.OperationId, state.Error);
        Assert.Null(state.Json.GetProperty("operationId").GetString());
        Assert.Equal(before.Revision, state.Json.GetProperty("revision").GetString()); Assert.Equal(bytes, File.ReadAllBytes(hint));
        Assert.Equal(journals, Directory.GetFiles(host.Root, "*.operation.json", SearchOption.AllDirectories).Length);
        Assert.Equal(0, host.WorkloadCalls); Assert.Empty(host.Errors); Assert.Equal(unknown.OperationId, (await host.Workflow.QueryAsync()).OperationId);
        Assert.Equal(RecoveryWorkflowState.OutcomeUnknown, host.Workflow.Current.State); Assert.Equal(bytes, File.ReadAllBytes(hint));
    }

    [Fact]
    public async Task IndependentConsoleRefusesCorruptHintBeforeAnOfflineAgentConnection()
    {
        using var fixture = new Fixture(); File.WriteAllText(fixture.PendingPath, "{broken"); var bytes = File.ReadAllBytes(fixture.PendingPath);
        var result = await Console(fixture.Root, Resume("media", Revision));
        Assert.Equal(3, result.Exit); Assert.Equal("pending_storage_unavailable", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Null(result.Json.GetProperty("operationId").GetString()); Assert.Equal(bytes, File.ReadAllBytes(fixture.PendingPath));
    }

    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static string[] Resume(string name, string revision, string? id = null) => ["recovery", "resume", name, "--expected-revision", revision, "--yes", .. id is null ? Array.Empty<string>() : new[] { "--operation-id", id }];
    private static async Task<Result> Run(CliApplication app, string root, string[] args, CancellationToken token = default)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await app.RunAsync([.. args, "--root", root, "--json", "--timeout", "2"], new StringReader(""), output, error, token);
        return new(exit, Parse(output.ToString()), error.ToString());
    }
    private static async Task<Result> Console(string root, string[] args, int timeout = 2)
    {
        var executable = typeof(RecoveryCliPendingTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(value => value.Key == "CliExecutablePath").Value!;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args.Concat(["--root", root, "--json", "--timeout", timeout.ToString(System.Globalization.CultureInfo.InvariantCulture)])) { info.ArgumentList.Add(argument); }
        using var process = Process.Start(info)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(deadline.Token); return new(process.ExitCode, Parse(await stdout.WaitAsync(deadline.Token)), await stderr.WaitAsync(deadline.Token));
        }
        finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
    }
    private static JsonElement Parse(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private sealed record Result(int Exit, JsonElement Json, string Error);
    private sealed class Fake(Func<CliRequest, AgentResponse> send) : ICliTransport
    { public Task<AgentResponse> SendAsync(CliRequest request, CancellationToken token) => Task.FromResult(send(request)); }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("SystemAutorun-cli-recovery-").FullName;
        internal ProjectPaths Paths { get; }
        internal RecoveryPendingStore Pending { get; }
        internal string PendingPath => Path.Combine(Root, Pending.FileName);
        internal Fixture() { File.WriteAllText(Path.Combine(Root, "autorun.yaml"), "version: 2\ntasks: []\n"); Paths = ProjectPaths.Resolve(Root); Pending = new(Paths, "media"); }
        public void Dispose()
        {
            var directory = new DirectoryInfo(Root);
            if (directory.FullName != Root || directory.Parent!.FullName != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) || !directory.Name.StartsWith("SystemAutorun-cli-recovery-", StringComparison.Ordinal) || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) { throw new IOException("Unexpected fixture root."); }
            Directory.Delete(directory.FullName, recursive: true);
        }
    }
}
