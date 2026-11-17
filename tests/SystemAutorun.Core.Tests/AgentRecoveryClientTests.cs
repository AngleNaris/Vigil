using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Recovery;

namespace SystemAutorun.Core.Tests;

public sealed class AgentRecoveryClientTests
{
    private static readonly string RootId = new('a', 64);
    private static readonly string Revision = new('b', 64);
    private static RecoveryControlSnapshot State(bool initialized = true) => new(Revision,
        initialized ? [new("media", false, IntentId: AgentProtocol.NewOperationId())] : [], initialized);
    private static AgentResponse Response(object? data = null) => new("read", true, data ?? State(), RootId: RootId, Revision: Revision);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ReadsExplicitInitializationAndImmutableIntentWithoutInventingHealth(bool initialized)
    {
        var state = AgentRecoveryClient.ReadState(Response(State(initialized)), RootId);
        Assert.Equal(initialized, state.Initialized); Assert.Equal(Revision, state.Revision);
        if (!initialized) { Assert.Empty(state.Tasks); return; }
        Assert.False(Assert.Single(state.Tasks).Paused);
        Assert.Throws<NotSupportedException>(() => ((IList<RecoveryTaskState>)state.Tasks)[0] = state.Tasks[0] with { Paused = true });
    }

    [Fact]
    public void InitializedEmptyStateIsNotUnknownOrAProtocolFailure()
    {
        var empty = new RecoveryControlSnapshot(Revision, [], true);
        var observed = AgentRecoveryClient.ReadState(Response(empty), RootId);
        Assert.True(observed.Initialized); Assert.Empty(observed.Tasks); Assert.Equal(Revision, observed.Revision);
    }

    [Theory]
    [InlineData(RecoveryAttemptOutcome.Admitted)] [InlineData(RecoveryAttemptOutcome.Succeeded)]
    [InlineData(RecoveryAttemptOutcome.Failed)] [InlineData(RecoveryAttemptOutcome.OutcomeUnknown)]
    public void PreservesAttemptEvidenceAsMetadataNotAnEligibilityDecision(RecoveryAttemptOutcome outcome)
    {
        var now = DateTimeOffset.UtcNow;
        var task = new RecoveryTaskState("媒体库", true, now, AgentProtocol.NewOperationId(), new string('c', 64), now,
            outcome, outcome == RecoveryAttemptOutcome.Admitted ? null : now, "historical evidence", AgentProtocol.NewOperationId());
        var input = new RecoveryControlSnapshot(Revision, [task], true);
        Assert.Equal(task, Assert.Single(AgentRecoveryClient.ReadState(Response(input), RootId).Tasks));
    }

    [Theory]
    [InlineData("revision")] [InlineData("initialized")] [InlineData("tasks")] [InlineData("taskName")]
    [InlineData("paused")] [InlineData("pausedAt")] [InlineData("attemptId")] [InlineData("taskIdentity")]
    [InlineData("lastAttemptAt")] [InlineData("outcome")] [InlineData("completedAt")] [InlineData("detail")] [InlineData("intentId")]
    public void MissingPropertiesNeverBecomeConstructorDefaults(string field)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(State(), AgentProtocol.JsonOptions))!.AsObject();
        if (field is "revision" or "initialized" or "tasks") { node.Remove(field); }
        else { node["tasks"]![0]!.AsObject().Remove(field); }
        AssertInvalidData(node);
    }

    [Theory]
    [InlineData("null")] [InlineData("array")] [InlineData("revision")] [InlineData("revision-case")]
    [InlineData("tasks-null")] [InlineData("task-null")] [InlineData("tasks-bound")] [InlineData("snapshot-bound")]
    [InlineData("missing-with-tasks")] [InlineData("duplicate-root")] [InlineData("duplicate-task")]
    [InlineData("duplicate-name")] [InlineData("name-empty")] [InlineData("name-control")] [InlineData("name-bound")]
    [InlineData("paused-type")] [InlineData("time-type")] [InlineData("intent")] [InlineData("intent-case")]
    [InlineData("attempt")] [InlineData("identity")] [InlineData("outcome-future")] [InlineData("outcome-number")]
    [InlineData("detail-bound")] [InlineData("detail-type")] [InlineData("pause-without-time")] [InlineData("time-without-pause")]
    [InlineData("attempt-fields-without-id")] [InlineData("attempt-without-fields")] [InlineData("admitted-completed")] [InlineData("finished-without-completion")]
    public void InvalidSnapshotIsProtocolFailureOnReadAndOriginalIdUnknownAfterMutation(string defect)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(State(), AgentProtocol.JsonOptions))!.AsObject(); var task = node["tasks"]![0]!.AsObject();
        switch (defect)
        {
            case "null": AssertInvalidData(null); return;
            case "array": AssertInvalidData(new JsonArray()); return;
            case "revision": node["revision"] = "bad"; break;
            case "revision-case": node["revision"] = Revision.ToUpperInvariant(); break;
            case "tasks-null": node["tasks"] = null; break;
            case "task-null": node["tasks"] = new JsonArray((JsonNode?)null); break;
            case "tasks-bound": node["tasks"] = new JsonArray(Enumerable.Range(0, 1025).Select(index => JsonSerializer.SerializeToNode(new RecoveryTaskState($"task{index}"), AgentProtocol.JsonOptions)).ToArray()); break;
            case "snapshot-bound": node["extra"] = new string('x', RecoveryControlStore.MaximumBytes + 513); break;
            case "missing-with-tasks": node["initialized"] = false; break;
            case "duplicate-root": node["Initialized"] = true; break;
            case "duplicate-task": task["Paused"] = false; break;
            case "duplicate-name": node["tasks"]!.AsArray().Add(task.DeepClone()); node["tasks"]![1]!["taskName"] = "MEDIA"; break;
            case "name-empty": task["taskName"] = " "; break;
            case "name-control": task["taskName"] = "media\n"; break;
            case "name-bound": task["taskName"] = new string('x', 257); break;
            case "paused-type": task["paused"] = "false"; break;
            case "time-type": task["pausedAt"] = "not-a-time"; break;
            case "intent": task["intentId"] = "not-an-id"; break;
            case "intent-case": task["intentId"] = task["intentId"]!.GetValue<string>().ToUpperInvariant(); break;
            case "attempt": task["attemptId"] = "not-an-id"; break;
            case "identity": task["taskIdentity"] = "bad"; break;
            case "outcome-future": task["outcome"] = "future_outcome"; break;
            case "outcome-number": task["outcome"] = 0; break;
            case "detail-bound": task["detail"] = new string('x', 513); break;
            case "detail-type": task["detail"] = false; break;
            case "pause-without-time": task["paused"] = true; break;
            case "time-without-pause": task["pausedAt"] = DateTimeOffset.UtcNow; break;
            case "attempt-fields-without-id": task["detail"] = "orphan evidence"; break;
            case "attempt-without-fields": task["attemptId"] = AgentProtocol.NewOperationId(); break;
            case "admitted-completed":
            case "finished-without-completion":
                task["attemptId"] = AgentProtocol.NewOperationId(); task["taskIdentity"] = new string('c', 64);
                task["lastAttemptAt"] = DateTimeOffset.UtcNow; task["outcome"] = defect == "admitted-completed" ? "admitted" : "succeeded";
                if (defect == "admitted-completed") { task["completedAt"] = DateTimeOffset.UtcNow; }
                break;
        }
        AssertInvalidData(node);
    }

    private static void AssertInvalidData(JsonNode? node)
    {
        var response = Response() with { Data = JsonSerializer.SerializeToElement(node, AgentProtocol.JsonOptions) };
        Assert.Equal("protocol_mismatch", Assert.Throws<AgentCommandException>(() => AgentRecoveryClient.ReadState(response, RootId)).Code);
        var id = AgentProtocol.NewOperationId();
        var unknown = Assert.Throws<AgentOutcomeUnknownException>(() => AgentRecoveryClient.ReadAcknowledgement(response with { OperationId = id }, "media", id, RootId));
        Assert.Equal(id, unknown.OperationId); Assert.Equal("read", unknown.RequestId);
    }

    [Theory]
    [InlineData("version")] [InlineData("root")] [InlineData("operation")] [InlineData("id")] [InlineData("id-bound")]
    [InlineData("revision")] [InlineData("error")] [InlineData("code")] [InlineData("code-missing")] [InlineData("code-case")]
    public void InvalidReadEnvelopeCannotBeUsedAsState(string defect)
    {
        var response = Defect(Response(), defect);
        Assert.Equal("protocol_mismatch", Assert.Throws<AgentCommandException>(() => AgentRecoveryClient.ReadState(response, RootId)).Code);
    }

    [Theory]
    [InlineData("version")] [InlineData("root")] [InlineData("operation")] [InlineData("id")] [InlineData("id-bound")]
    [InlineData("revision")] [InlineData("error")] [InlineData("code")] [InlineData("code-missing")] [InlineData("code-case")]
    public void InvalidMutationEnvelopeRetainsOriginalIdAsUnknown(string defect)
    {
        var id = AgentProtocol.NewOperationId(); var response = Defect(Response() with { OperationId = id }, defect);
        Assert.Equal(id, Assert.Throws<AgentOutcomeUnknownException>(() => AgentRecoveryClient.ReadAcknowledgement(response, "media", id, RootId)).OperationId);
    }

    private static AgentResponse Defect(AgentResponse response, string defect) => defect switch
    {
        "version" => response with { ProtocolVersion = 1 }, "root" => response with { RootId = new string('c', 64) },
        "operation" => response with { OperationId = AgentProtocol.NewOperationId() }, "id" => response with { Id = "" },
        "id-bound" => response with { Id = new string('x', 129) }, "revision" => response with { Revision = new string('c', 64) },
        "error" => response with { Error = "contradictory error" }, "code" => response with { ErrorCode = "outcome_unknown" },
        "code-missing" => response with { Ok = false, ErrorCode = null }, _ => response with { Ok = false, ErrorCode = "BAD" },
    };

    [Fact]
    public void ReplayedAckIsHistoricalIntentAndReadCannotBeReplay()
    {
        var id = AgentProtocol.NewOperationId(); var response = Response() with { Replayed = true, OperationId = id };
        var ack = AgentRecoveryClient.ReadAcknowledgement(response, "MEDIA", id.ToUpperInvariant(), RootId);
        Assert.True(ack.Replayed); Assert.Equal(id, ack.OperationId); Assert.False(Assert.Single(Assert.IsType<RecoveryControlSnapshot>(ack.Data).Tasks).Paused);
        Assert.Equal("protocol_mismatch", Assert.Throws<AgentCommandException>(() => AgentRecoveryClient.ReadState(response with { OperationId = null }, RootId)).Code);
    }

    [Theory]
    [InlineData("target")] [InlineData("paused")] [InlineData("intent")] [InlineData("uninitialized")]
    public void AckMustIdentifyTheUnpausedTargetIntent(string defect)
    {
        var state = State(); var entry = Assert.Single(state.Tasks);
        state = defect switch
        {
            "target" => state with { Tasks = [entry with { TaskName = "another" }] }, "paused" => state with { Tasks = [entry with { Paused = true }] },
            "intent" => state with { Tasks = [entry with { IntentId = null }] }, _ => State(false),
        };
        var id = AgentProtocol.NewOperationId();
        Assert.Throws<AgentOutcomeUnknownException>(() => AgentRecoveryClient.ReadAcknowledgement(Response(state) with { OperationId = id }, "media", id, RootId));
    }

    [Theory]
    [InlineData("outcome_unknown")] [InlineData("operation_in_progress")] [InlineData("operation_result_unavailable")]
    [InlineData("recovery_revision_conflict")] [InlineData("authorization_required")]
    public void ErrorCodesAndReplayMetadataStayIntactWithoutApplyingFailureData(string code)
    {
        var id = AgentProtocol.NewOperationId(); var response = Response() with { Ok = false, ErrorCode = code, OperationId = id, Replayed = true };
        var ack = AgentRecoveryClient.ReadAcknowledgement(response, "media", id, RootId);
        Assert.False(ack.Ok); Assert.Equal(code, ack.ErrorCode); Assert.True(ack.Replayed); Assert.Null(ack.Data);
        Assert.Equal(code, Assert.Throws<AgentCommandException>(() => AgentRecoveryClient.ReadState(response with { OperationId = null, Replayed = false }, RootId)).Code);
    }

    [Theory]
    [InlineData("name")] [InlineData("revision")] [InlineData("confirmation")] [InlineData("operation")]
    public async Task InvalidInputCannotResolveRootOrCreateAnOperation(string defect)
    {
        using var root = new TempDirectory(); // Missing autorun.yaml makes accidental transport entry observable.
        var client = new AgentRecoveryClient(new(root.Path));
        if (defect == "name") { await Assert.ThrowsAsync<ArgumentException>(() => client.SubmitResumeAsync("\n", Revision, AgentProtocol.NewOperationId(), true)); }
        if (defect == "revision") { await Assert.ThrowsAsync<ArgumentException>(() => client.SubmitResumeAsync("media", "bad", AgentProtocol.NewOperationId(), true)); }
        if (defect == "confirmation") { Assert.Equal("authorization_required", (await Assert.ThrowsAsync<AgentCommandException>(() => client.SubmitResumeAsync("media", Revision, AgentProtocol.NewOperationId(), false))).Code); }
        if (defect == "operation") { Assert.Equal("invalid_operation_id", (await Assert.ThrowsAsync<AgentCommandException>(() => client.SubmitResumeAsync("media", Revision, "bad", true))).Code); }
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BeforeWriteTimeoutOrCancellationIsNotUnknown(bool cancelled)
    {
        using var root = new TempDirectory(); root.Write("autorun.yaml", "version: 1\ntasks: []\n");
        using var cancel = new CancellationTokenSource(); if (cancelled) { cancel.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AgentRecoveryClient(new(root.Path))
            .SubmitResumeAsync("media", Revision, AgentProtocol.NewOperationId(), true, TimeSpan.FromMilliseconds(30), cancel.Token));
        Assert.Single(Directory.EnumerateFileSystemEntries(root.Path));
    }

    [Theory]
    [InlineData("missing")] [InlineData("duplicate")] [InlineData("version-type")] [InlineData("id-type")]
    [InlineData("root-type")] [InlineData("revision-type")] [InlineData("operation-type")] [InlineData("array")]
    public async Task WireReadEnvelopeRequiresSingleDataAndCorrectFieldTypes(string defect)
    {
        using var root = new TempDirectory(); root.Write("autorun.yaml", "version: 1\ntasks: []\n");
        var endpoint = AgentProtocol.ForRoot(root.Path); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NamedPipeServerStream(endpoint.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serving = Serve();
        try
        {
            var error = await Assert.ThrowsAsync<AgentCommandException>(() => new AgentRecoveryClient(new(root.Path)).ReadAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("protocol_mismatch", error.Code); Assert.Null(error.OperationId); await serving;
        }
        finally { deadline.Cancel(); await serving; }
        Assert.Single(Directory.EnumerateFileSystemEntries(root.Path));
        async Task Serve()
        {
            await server.WaitForConnectionAsync(deadline.Token); using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            var request = JsonSerializer.Deserialize<AgentRequest>((await reader.ReadLineAsync(deadline.Token))!, AgentProtocol.JsonOptions)!;
            Assert.Equal("recovery-state", request.Command); Assert.Null(request.OperationId); Assert.Null(request.ExpectedRevision);
            var line = JsonSerializer.Serialize(Response() with { Id = request.Id, RootId = endpoint.RootId }, AgentProtocol.JsonOptions);
            if (defect == "duplicate") { line = line[..^1] + ",\"data\":null}"; }
            else if (defect == "array") { line = "[]"; }
            else
            {
                var node = JsonNode.Parse(line)!.AsObject();
                switch (defect)
                {
                    case "missing": node.Remove("data"); break;
                    case "version-type": node["protocolVersion"] = "2"; break;
                    case "id-type": node["id"] = 17; break;
                    case "root-type": node["rootId"] = 17; break;
                    case "revision-type": node["revision"] = 17; break;
                    case "operation-type": node["operationId"] = 17; break;
                }
                line = node.ToJsonString();
            }
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(line.AsMemory(), deadline.Token);
        }
    }

    [Theory]
    [InlineData("drop")] [InlineData("data")] [InlineData("data-missing")] [InlineData("data-duplicate")]
    [InlineData("operation")] [InlineData("root")] [InlineData("revision")]
    public async Task ActualWireWriteThenLossOrInvalidAckIsOneSendWithOriginalIdentity(string defect)
    {
        using var root = new TempDirectory(); root.Write("autorun.yaml", "version: 1\ntasks: []\n");
        var endpoint = AgentProtocol.ForRoot(root.Path); var id = AgentProtocol.NewOperationId(); var received = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NamedPipeServerStream(endpoint.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serving = Serve();
        try
        {
            var failure = await Assert.ThrowsAsync<AgentOutcomeUnknownException>(() => new AgentRecoveryClient(new(root.Path))
                .SubmitResumeAsync("media", Revision, id.ToUpperInvariant(), true, TimeSpan.FromSeconds(5)));
            Assert.Equal(id, failure.OperationId); Assert.NotNull(failure.RequestId); await serving; Assert.Equal(1, received);
        }
        finally { deadline.Cancel(); await serving; }
        Assert.Single(Directory.EnumerateFileSystemEntries(root.Path));
        async Task Serve()
        {
            await server.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            var request = JsonSerializer.Deserialize<AgentRequest>((await reader.ReadLineAsync(deadline.Token))!, AgentProtocol.JsonOptions)!;
            Interlocked.Increment(ref received); Assert.Equal(id, request.OperationId); Assert.Equal("recovery-resume", request.Command);
            Assert.Equal("media", request.Name); Assert.Equal(Revision, request.ExpectedRevision); Assert.Null(request.Payload); Assert.Equal(endpoint.RootId, request.RootId);
            if (defect == "drop") { server.Disconnect(); return; }
            var response = Response() with { Id = request.Id, RootId = endpoint.RootId, OperationId = id };
            response = defect == "data" ? response with { Data = new { revision = Revision, initialized = true } }
                : defect is "data-missing" or "data-duplicate" ? response : Defect(response, defect);
            var line = JsonSerializer.Serialize(response, AgentProtocol.JsonOptions);
            if (defect == "data-duplicate") { line = line[..^1] + ",\"data\":null}"; }
            if (defect == "data-missing") { var node = JsonNode.Parse(line)!.AsObject(); node.Remove("data"); line = node.ToJsonString(); }
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(line.AsMemory(), deadline.Token);
        }
    }
}
