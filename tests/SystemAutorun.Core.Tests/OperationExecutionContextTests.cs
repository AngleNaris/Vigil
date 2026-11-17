using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Core.Tests;

public sealed class OperationExecutionContextTests
{
    [Fact]
    public async Task SnapshotSurvivesOriginalDocumentDisposalBeforeAdmissionAndMatchesTheActualReceipt()
    {
        using var root = new OwnedRoot(); using var other = new OwnedRoot(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var document = JsonDocument.Parse("{\"nested\":{\"value\":7},\"marker\":\"SYNTHETIC_CONTEXT_NO_PERSIST\"}");
        var time = new FixedTime(DateTimeOffset.UtcNow); var journal = new OperationJournal(root.Paths, time);
        var request = Request(time.Now) with { Payload = document.RootElement, ExpectedRevision = new string('a', 64) };
        request = request with { OperationId = request.OperationId!.ToUpperInvariant() };
        OperationExecutionContext? captured = null; Task<AgentResponse>? workflow = null; AgentResponse? response = null;
        var held = await RootWriteLease.AcquireAsync(root.Paths);
        try
        {
            workflow = journal.RunWithContextAsync(request, async (context, token) =>
            {
                captured = context; Assert.True(context.IsActive); context.RequireActiveFor(root.Paths);
                context.RequireActiveFor(new ProjectPaths(Path.Combine(root.Root, ".")));
                Assert.Throws<ArgumentException>(() => context.RequireActiveFor(other.Paths));
                Assert.Equal(request.Id, context.Request.Id); Assert.Equal(request.ExpectedRevision, context.Request.ExpectedRevision);
                Assert.Equal(7, context.Request.Payload!.Value.GetProperty("nested").GetProperty("value").GetInt32());
                Assert.Equal(OperationJournal.NormalizeId(request.OperationId), context.OperationId);
                Assert.Equal(context.OperationId, context.Request.OperationId); Assert.Equal(journal.InstanceId, context.OwnerInstanceId);
                await using (var probe = await RootWriteLease.AcquireAsync(root.Paths, token, TimeSpan.FromMilliseconds(250))) { probe.Require(root.Paths); }
                var pending = await journal.GetAsync(context.OperationId, token); Assert.NotNull(pending);
                Assert.Equal(OperationState.Running, pending.State); Assert.Equal(pending.Fingerprint, context.Fingerprint);
                Assert.Equal(pending.AcceptedAt, context.AcceptedAt); Assert.Equal(time.Now, context.AcceptedAt);
                return new AgentResponse(context.Request.Id, true, new SerializationProbe(context));
            }, deadline.Token);
            document.Dispose(); Assert.Null(captured); // Admission is still blocked, so clone must already exist.
        }
        finally
        {
            await held.DisposeAsync(); if (workflow is not null) { response = await workflow; }
        }
        Assert.NotNull(response); Assert.True(response.Ok); Assert.NotNull(captured); Assert.False(captured.IsActive);
        Assert.Throws<InvalidOperationException>(() => captured.RequireActiveFor(root.Paths));
        Assert.Equal(7, captured.Request.Payload!.Value.GetProperty("nested").GetProperty("value").GetInt32());
        var receipt = await journal.GetAsync(captured.OperationId); Assert.Equal(OperationState.Completed, receipt!.State);
        var data = Assert.IsType<JsonElement>(receipt.Response!.Data); Assert.False(data.GetProperty("contextActiveAtSerialization").GetBoolean());
        Assert.DoesNotContain("SYNTHETIC_CONTEXT_NO_PERSIST", await File.ReadAllTextAsync(root.ReceiptPath(captured.OperationId)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("cancel")]
    [InlineData("known-failure")]
    [InlineData("uncertain-response")]
    [InlineData("null-response")]
    public async Task EveryCallbackExitEndsItsOwnScopeBeforeReceiptAndReplay(string mode)
    {
        using var root = new OwnedRoot(); using var cancellation = new CancellationTokenSource();
        var journal = new OperationJournal(root.Paths); var request = Request(); OperationExecutionContext? captured = null; var calls = 0;
        var response = await journal.RunWithContextAsync(request, (context, token) =>
        {
            calls++; captured = context; context.RequireActiveFor(root.Paths);
            if (mode == "throw") { throw new IOException("Owned callback fault."); }
            if (mode == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            if (mode == "null-response") { return Task.FromResult<AgentResponse>(null!); } // A broken callback cannot prove no effects.
            return Task.FromResult(new AgentResponse(request.Id, false, ErrorCode: mode == "known-failure" ? "revision_conflict" : "synthetic_uncertain"));
        }, cancellation.Token);
        Assert.NotNull(captured); Assert.False(captured.IsActive); Assert.Throws<InvalidOperationException>(() => captured.RequireActiveFor(root.Paths));
        Assert.False(response.Ok); Assert.Equal(mode == "known-failure" ? "revision_conflict" : "outcome_unknown", response.ErrorCode);
        var replay = await new OperationJournal(root.Paths).RunWithContextAsync(request, (_, _) => { calls++; throw new InvalidOperationException("Must not issue another context."); });
        Assert.True(replay.Replayed); Assert.Equal(response.ErrorCode, replay.ErrorCode); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacyAndContextApisShareExactlyTheSameReceiptAdmission(bool contextFirst)
    {
        using var root = new OwnedRoot(); var journal = new OperationJournal(root.Paths); var request = Request(); var calls = 0;
        Task<AgentResponse> Execute(CancellationToken _) => Task.FromResult(new AgentResponse(request.Id, true, ++calls));
        var first = contextFirst ? await journal.RunWithContextAsync(request, (_, token) => Execute(token)) : await journal.RunAsync(request, Execute);
        var second = contextFirst ? await journal.RunAsync(request, Execute) : await journal.RunWithContextAsync(request, (_, token) => Execute(token));
        Assert.True(first.Ok); Assert.False(first.Replayed); Assert.True(second.Ok); Assert.True(second.Replayed); Assert.Equal(1, calls);
        Assert.Single(Directory.GetFiles(root.OperationsDirectory, "*.operation.json"));
    }

    [Fact]
    public async Task FingerprintKeepsItsExistingCanonicalProjectionAndIgnoresUnboundHeaders()
    {
        using var root = new OwnedRoot(); var journal = new OperationJournal(root.Paths); var request = Request() with
        { Payload = JsonSerializer.Deserialize<JsonElement>("{\"b\":[2,3],\"a\":1}"), ExpectedRevision = new string('b', 64) };
        OperationExecutionContext? first = null; OperationExecutionContext? second = null;
        Assert.True((await journal.RunWithContextAsync(request, (context, _) => { first = context; return Task.FromResult(new AgentResponse(context.Request.Id, true)); })).Ok);
        var changed = request with { Id = "different-response-id", Command = request.Command.ToUpperInvariant(), TimeoutMilliseconds = 1,
            ProtocolVersion = AgentProtocol.Version + 1, RootId = "not-an-authority", OperationId = Guid.CreateVersion7().ToString("N"),
            Payload = JsonSerializer.Deserialize<JsonElement>("{\"a\":1,\"b\":[2,3]}") };
        Assert.True((await journal.RunWithContextAsync(changed, (context, _) => { second = context; return Task.FromResult(new AgentResponse(context.Request.Id, true)); })).Ok);
        Assert.NotNull(first); Assert.NotNull(second); Assert.Equal(first.Fingerprint, second.Fingerprint); Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.Equal(changed.RootId, second.Request.RootId); Assert.Equal(changed.ProtocolVersion, second.Request.ProtocolVersion);
        Assert.Equal(changed.TimeoutMilliseconds, second.Request.TimeoutMilliseconds); // These remain data, not authenticated headers.
        var conflict = await Assert.ThrowsAsync<AgentCommandException>(() => journal.RunWithContextAsync(request with { Name = "changed" }, (_, _) => throw new InvalidOperationException("Conflict must precede a context.")));
        Assert.Equal("operation_id_conflict", conflict.Code);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("name")]
    [InlineData("revision")]
    [InlineData("payload")]
    public async Task ChangingAnyBoundFieldChangesTheSnapshotFingerprint(string field)
    {
        using var root = new OwnedRoot(); var journal = new OperationJournal(root.Paths);
        var request = Request() with { Name = "original", ExpectedRevision = new string('c', 64), Payload = JsonSerializer.SerializeToElement(new { value = 1 }) };
        OperationExecutionContext? first = null; OperationExecutionContext? second = null;
        await journal.RunWithContextAsync(request, (context, _) => { first = context; return Task.FromResult(new AgentResponse(request.Id, true)); });
        var changed = request with { OperationId = Guid.CreateVersion7().ToString("N"), Command = field == "command" ? "start" : request.Command,
            Name = field == "name" ? "changed" : request.Name, ExpectedRevision = field == "revision" ? new string('d', 64) : request.ExpectedRevision,
            Payload = field == "payload" ? JsonSerializer.SerializeToElement(new { value = 2 }) : request.Payload };
        await journal.RunWithContextAsync(changed, (context, _) => { second = context; return Task.FromResult(new AgentResponse(changed.Id, true)); });
        Assert.NotNull(first); Assert.NotNull(second); Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public async Task AReceiptPublicationFailureCannotKeepTheCallbackContextLive()
    {
        using var root = new OwnedRoot(); var journal = new OperationJournal(root.Paths); var request = Request();
        OperationExecutionContext? captured = null; FileStream? blocker = null; var calls = 0;
        try
        {
            var error = await Assert.ThrowsAsync<AgentOutcomeUnknownException>(() => journal.RunWithContextAsync(request, (context, _) =>
            {
                captured = context; calls++; blocker = new FileStream(root.ReceiptPath(context.OperationId), FileMode.Open, FileAccess.Read, FileShare.Read);
                return Task.FromResult(new AgentResponse(request.Id, true));
            }));
            Assert.Equal(request.OperationId, error.OperationId);
        }
        finally { blocker?.Dispose(); }
        Assert.NotNull(captured); Assert.False(captured.IsActive); Assert.Throws<InvalidOperationException>(() => captured.RequireActiveFor(root.Paths));
        var pending = JsonSerializer.Deserialize<OperationReceipt>(await File.ReadAllTextAsync(root.ReceiptPath(request.OperationId!)), AgentProtocol.JsonOptions);
        Assert.NotNull(pending); Assert.Equal(OperationState.Running, pending.State); Assert.Null(pending.Response);
        var replay = await new OperationJournal(root.Paths).RunWithContextAsync(request, (_, _) => { calls++; throw new InvalidOperationException("Pending must not issue a scope."); });
        Assert.True(replay.Replayed); Assert.Equal("outcome_unknown", replay.ErrorCode); Assert.Equal(1, calls);
        Assert.Empty(Directory.GetFiles(root.OperationsDirectory, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentAndForeignInstanceRetriesNeverReceiveTheLiveContext()
    {
        using var root = new OwnedRoot(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var journal = new OperationJournal(root.Paths); var request = Request(); OperationExecutionContext? captured = null; var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = journal.RunWithContextAsync(request, async (context, token) =>
        {
            captured = context; calls++; entered.TrySetResult(); await release.Task.WaitAsync(token); context.RequireActiveFor(root.Paths);
            return new AgentResponse(request.Id, true);
        }, deadline.Token);
        AgentResponse? response = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.NotNull(captured); Assert.True(captured.IsActive);
            var same = await journal.RunWithContextAsync(request, (_, _) => { calls++; throw new InvalidOperationException("Duplicate context."); });
            Assert.True(same.Replayed); Assert.Equal("operation_in_progress", same.ErrorCode);
            var foreign = await new OperationJournal(root.Paths).RunWithContextAsync(request, (_, _) => { calls++; throw new InvalidOperationException("Foreign context."); });
            Assert.True(foreign.Replayed); Assert.Equal("outcome_unknown", foreign.ErrorCode); Assert.True(captured.IsActive);
        }
        finally { release.TrySetResult(); if (!entered.Task.IsCompleted) { deadline.Cancel(); } response = await workflow; }
        Assert.True(response.Ok); Assert.NotNull(captured); Assert.False(captured.IsActive); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DisposedPayloadFailsBeforeAnyJournalAdmissionOrContext()
    {
        using var root = new OwnedRoot(); var document = JsonDocument.Parse("{\"value\":1}"); var request = Request() with { Payload = document.RootElement }; document.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => new OperationJournal(root.Paths).RunWithContextAsync(request, (_, _) => throw new InvalidOperationException("Disposed input must not execute.")));
        Assert.False(Directory.Exists(root.OperationsDirectory));
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("00112233445546778899aabbccddeeff")]
    public async Task InvalidIdsCannotIssueAContextOrCreateAReceipt(string id)
    {
        using var root = new OwnedRoot(); var request = Request() with { OperationId = id };
        var error = await Assert.ThrowsAsync<AgentCommandException>(() => new OperationJournal(root.Paths).RunWithContextAsync(request, (_, _) => throw new InvalidOperationException("Invalid id.")));
        Assert.Equal("invalid_operation_id", error.Code); Assert.False(Directory.Exists(root.OperationsDirectory));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OutOfWindowIdsCannotIssueAContext(bool future)
    {
        using var root = new OwnedRoot(); var time = new FixedTime(DateTimeOffset.UtcNow); var journal = new OperationJournal(root.Paths, time);
        var request = Request(future ? time.Now.AddMinutes(6) : time.Now.AddDays(-8));
        var error = await Assert.ThrowsAsync<AgentCommandException>(() => journal.RunWithContextAsync(request, (_, _) => throw new InvalidOperationException("No context outside retention window.")));
        Assert.Equal(future ? "invalid_operation_id" : "operation_expired", error.Code); Assert.False(Directory.Exists(root.OperationsDirectory));
    }

    [Fact]
    public async Task CapacityRejectionSharesTheLegacyReservationWithoutIssuingAContext()
    {
        using var root = new OwnedRoot(); var journal = new OperationJournal(root.Paths, limits: new OperationJournalLimits(MaximumEntries: 1));
        var first = Request(); Assert.True((await journal.RunAsync(first, _ => Task.FromResult(new AgentResponse(first.Id, true)))).Ok);
        var next = Request(); var error = await Assert.ThrowsAsync<AgentCommandException>(() => journal.RunWithContextAsync(next, (_, _) => throw new InvalidOperationException("No reserved capacity.")));
        Assert.Equal("operation_store_full", error.Code); Assert.Single(Directory.GetFiles(root.OperationsDirectory, "*.operation.json")); Assert.Null(await journal.GetAsync(next.OperationId!));
    }

    [Fact]
    public async Task MissingContextApiArgumentsFailBeforeAdmission()
    {
        using var root = new OwnedRoot(); var journal = new OperationJournal(root.Paths);
        await Assert.ThrowsAsync<ArgumentNullException>(() => journal.RunWithContextAsync(null!, (_, _) => throw new InvalidOperationException("Missing request.")));
        await Assert.ThrowsAsync<ArgumentNullException>(() => journal.RunWithContextAsync(Request(), null!));
        Assert.False(Directory.Exists(root.OperationsDirectory));
    }

    [Fact]
    public async Task PreCancelledAdmissionCannotIssueAContext()
    {
        using var root = new OwnedRoot(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OperationJournal(root.Paths).RunWithContextAsync(Request(), (_, _) => throw new InvalidOperationException("Cancelled admission."), cancellation.Token));
        Assert.False(Directory.Exists(root.OperationsDirectory));
    }

    [Fact]
    public async Task ContextHasNoPublicConstructionMutationOrRequestSerialization()
    {
        Assert.True(typeof(OperationExecutionContext).IsSealed); Assert.Empty(typeof(OperationExecutionContext).GetConstructors());
        Assert.All(typeof(OperationExecutionContext).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(OperationExecutionContext)));
        using var root = new OwnedRoot(); var journal = new OperationJournal(root.Paths); var request = Request() with { Payload = JsonSerializer.SerializeToElement(new { marker = "SYNTHETIC_CONTEXT_NO_PERSIST" }) };
        var response = await journal.RunWithContextAsync(request, (context, _) => Task.FromResult(new AgentResponse(request.Id, true, context)));
        Assert.True(response.Ok); var receipt = await journal.GetAsync(request.OperationId!);
        var data = Assert.IsType<JsonElement>(receipt!.Response!.Data); Assert.False(data.GetProperty("isActive").GetBoolean()); Assert.False(data.TryGetProperty("request", out _));
        Assert.DoesNotContain("SYNTHETIC_CONTEXT_NO_PERSIST", await File.ReadAllTextAsync(root.ReceiptPath(request.OperationId!)), StringComparison.Ordinal);
    }

    private static AgentRequest Request(DateTimeOffset? now = null) => new(Guid.NewGuid().ToString("N"), "settings-patch",
        OperationId: Guid.CreateVersion7(now ?? DateTimeOffset.UtcNow).ToString("N"));
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now { get; } = now; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class SerializationProbe(OperationExecutionContext context) { public bool ContextActiveAtSerialization => context.IsActive; }
    private sealed class OwnedRoot : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("SystemAutorun-context-tests-").FullName;
        public ProjectPaths Paths => new(Root);
        public string OperationsDirectory => Path.Combine(Root, ".autorun-state", CurrentUserIdentity.Key, "operations");
        public string ReceiptPath(string id) => Path.Combine(OperationsDirectory, OperationJournal.NormalizeId(id) + ".operation.json");
        public OwnedRoot() => File.WriteAllText(Paths.ConfigPath, "version: 1\ntasks: []\n");
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
                || !Path.GetFileName(resolved).StartsWith("SystemAutorun-context-tests-", StringComparison.Ordinal)
                || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
            { throw new InvalidOperationException("Refusing non-owned context fixture cleanup."); }
            Directory.Delete(resolved, recursive: true);
        }
    }
}
