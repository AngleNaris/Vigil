using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Core.Tests;

public sealed class OperationJournalTests
{
    [Fact]
    public async Task SameIntentReplaysAcrossNewRequestAndJournalInstances()
    {
        using var root = new OwnedRoot();
        var journal = new OperationJournal(root.Paths);
        var request = Request();
        var calls = 0;
        Task<AgentResponse> Execute(CancellationToken _) => Task.FromResult(new AgentResponse(request.Id, true, new { calls = ++calls }));
        var first = await journal.RunAsync(request, Execute);
        var again = await new OperationJournal(root.Paths).RunAsync(request with { Id = "second", TimeoutMilliseconds = 1 }, Execute);
        Assert.True(first.Ok);
        Assert.False(first.Replayed);
        Assert.True(again.Ok);
        Assert.True(again.Replayed);
        Assert.Equal("second", again.Id);
        Assert.Equal(request.OperationId, again.OperationId);
        Assert.Equal(1, calls);
        Assert.Equal(OperationState.Completed, (await journal.GetAsync(request.OperationId!))!.State);
    }

    [Fact]
    public async Task FingerprintOrdersPropertiesButRejectsDifferentArguments()
    {
        using var root = new OwnedRoot();
        var journal = new OperationJournal(root.Paths);
        var request = Request() with { Payload = JsonSerializer.Deserialize<JsonElement>("{\"b\":2,\"a\":1,\"marker\":\"SYNTHETIC_NOT_PERSISTED\"}") };
        var calls = 0;
        Task<AgentResponse> Execute(CancellationToken _) => Task.FromResult(new AgentResponse(request.Id, true, ++calls));
        _ = await journal.RunAsync(request, Execute);
        var reordered = request with { Payload = JsonSerializer.Deserialize<JsonElement>("{\"marker\":\"SYNTHETIC_NOT_PERSISTED\",\"a\":1,\"b\":2}") };
        Assert.True((await journal.RunAsync(reordered, Execute)).Replayed);
        var conflict = await Assert.ThrowsAsync<AgentCommandException>(() => journal.RunAsync(request with { Name = "different" }, Execute));
        Assert.Equal("operation_id_conflict", conflict.Code);
        Assert.Equal(1, calls);
        Assert.DoesNotContain("SYNTHETIC_NOT_PERSISTED", await File.ReadAllTextAsync(root.ReceiptPath(request.OperationId!)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentRetryDoesNotRunAgainAndAnotherInstanceCannotClaimPendingWork()
    {
        using var root = new OwnedRoot();
        var journal = new OperationJournal(root.Paths);
        var request = Request();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var first = journal.RunAsync(request, async _ =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            await release.Task;
            return new AgentResponse(request.Id, true, new { completed = true });
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(OperationState.Running, (await journal.GetAsync(request.OperationId!))!.State);
            var retry = await journal.RunAsync(request, _ => throw new InvalidOperationException("Must not execute twice."));
            Assert.Equal("operation_in_progress", retry.ErrorCode);
            var anotherInstance = new OperationJournal(root.Paths);
            Assert.Equal(OperationState.Unknown, (await anotherInstance.GetAsync(request.OperationId!))!.State);
            var unknown = await anotherInstance.RunAsync(request, _ => throw new InvalidOperationException("Must not reclaim pending work."));
            Assert.Equal("outcome_unknown", unknown.ErrorCode);
        }
        finally
        {
            release.TrySetResult();
            await first;
        }

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task InterruptedExecutionIsUnknownAndNeverAutomaticallyRetried()
    {
        using var root = new OwnedRoot();
        var journal = new OperationJournal(root.Paths);
        var request = Request();
        var calls = 0;
        var response = await journal.RunAsync(request, _ =>
        {
            calls++;
            throw new OperationCanceledException("simulated interruption after possible side effect");
        });
        Assert.Equal("outcome_unknown", response.ErrorCode);
        Assert.Equal(OperationState.Unknown, (await journal.GetAsync(request.OperationId!))!.State);
        var replay = await new OperationJournal(root.Paths).RunAsync(request, _ => throw new InvalidOperationException("Do not retry."));
        Assert.Equal("outcome_unknown", replay.ErrorCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PossiblePostSideEffectIoFailureIsReportedAsUnknownImmediately()
    {
        using var root = new OwnedRoot();
        var journal = new OperationJournal(root.Paths);
        var request = Request();
        var response = await journal.RunAsync(request, _ => Task.FromResult(
            new AgentResponse(request.Id, false, Error: "simulated post-commit I/O failure", ErrorCode: "io_error")));
        Assert.Equal("outcome_unknown", response.ErrorCode);
        Assert.Equal(request.OperationId, response.OperationId);
        Assert.Equal(OperationState.Unknown, (await journal.GetAsync(request.OperationId!))!.State);
        Assert.Equal("outcome_unknown", (await journal.RunAsync(request,
            _ => throw new InvalidOperationException("An unknown operation is never reexecuted."))).ErrorCode);
    }

    [Fact]
    public async Task ExpiryWatermarkSurvivesClockRollback()
    {
        using var root = new OwnedRoot();
        var time = new TestTime();
        var journal = new OperationJournal(root.Paths, time);
        var old = Request(time.Now);
        _ = await journal.RunAsync(old, _ => Task.FromResult(new AgentResponse(old.Id, true, 1)));
        time.Now += TimeSpan.FromDays(8);
        var recent = Request(time.Now);
        _ = await journal.RunAsync(recent, _ => Task.FromResult(new AgentResponse(recent.Id, true, 2)));
        Assert.Null(await journal.GetAsync(old.OperationId!));
        time.Now -= TimeSpan.FromDays(8);
        var expired = await Assert.ThrowsAsync<AgentCommandException>(() =>
            new OperationJournal(root.Paths, time).RunAsync(old, _ => throw new InvalidOperationException("Never execute an expired ID.")));
        Assert.Equal("operation_expired", expired.Code);
    }

    [Fact]
    public async Task ClockForwardJumpWithoutRetirementsDoesNotWedgeFreshOperations()
    {
        using var root = new OwnedRoot();
        var time = new TestTime();
        var correct = time.Now;
        time.Now += TimeSpan.FromDays(30);
        var future = Request(time.Now);
        _ = await new OperationJournal(root.Paths, time).RunAsync(future,
            _ => Task.FromResult(new AgentResponse(future.Id, true, 1)));
        time.Now = correct;
        var fresh = Request(time.Now);
        var result = await new OperationJournal(root.Paths, time).RunAsync(fresh,
            _ => Task.FromResult(new AgentResponse(fresh.Id, true, 2)));
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task OrphanedJournalTemporaryFilesAreRemovedUnderLeaseOnly()
    {
        using var root = new OwnedRoot();
        Directory.CreateDirectory(root.OperationsDirectory);
        var id = AgentProtocol.NewOperationId();
        var abandonedReceipt = root.ReceiptPath(id) + $".{Guid.NewGuid():N}.tmp";
        var abandonedMetadata = Path.Combine(root.OperationsDirectory, $"metadata.json.{Guid.NewGuid():N}.tmp");
        var unrelated = Path.Combine(root.OperationsDirectory, "user-owned.tmp");
        await File.WriteAllTextAsync(abandonedReceipt, new string('x', 16000));
        await File.WriteAllTextAsync(abandonedMetadata, "unfinished");
        await File.WriteAllTextAsync(unrelated, "do not touch");
        Assert.Null(await new OperationJournal(root.Paths).GetAsync(id));
        Assert.False(File.Exists(abandonedReceipt));
        Assert.False(File.Exists(abandonedMetadata));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task CapacityRejectsNewWorkWithoutEvictingFreshReceipts()
    {
        using var root = new OwnedRoot();
        var journal = new OperationJournal(root.Paths, limits: new(MaximumEntries: 1));
        var first = Request();
        _ = await journal.RunAsync(first, _ => Task.FromResult(new AgentResponse(first.Id, true, 1)));
        var full = await Assert.ThrowsAsync<AgentCommandException>(() =>
            journal.RunAsync(Request(), _ => throw new InvalidOperationException("Capacity rejection must precede execution.")));
        Assert.Equal("operation_store_full", full.Code);
        Assert.NotNull(await journal.GetAsync(first.OperationId!));
        Assert.True((await journal.RunAsync(first, _ => throw new InvalidOperationException("Must replay."))).Replayed);
    }

    [Fact]
    public async Task PendingOperationsReserveTheirMaximumCompletionSize()
    {
        using var root = new OwnedRoot();
        var journal = new OperationJournal(root.Paths, limits: new(MaximumBytes: 256 * 1024));
        var first = Request();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = journal.RunAsync(first, async _ =>
        {
            entered.SetResult();
            await release.Task;
            return new AgentResponse(first.Id, true, 1);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var full = await Assert.ThrowsAsync<AgentCommandException>(() =>
                journal.RunAsync(Request(), _ => throw new InvalidOperationException("No unreserved side effects.")));
            Assert.Equal("operation_store_full", full.Code);
        }
        finally
        {
            release.TrySetResult();
            await pending;
        }
    }

    [Fact]
    public async Task OversizedResultKeepsKnownCompletionWithoutReexecution()
    {
        using var root = new OwnedRoot();
        var journal = new OperationJournal(root.Paths, limits: new(MaximumBytes: 65536, MaximumReceiptBytes: 16384, MaximumResultBytes: 128));
        var request = Request();
        _ = await journal.RunAsync(request, _ => Task.FromResult(new AgentResponse(request.Id, true, new { text = new string('x', 1000) })));
        var receipt = (await journal.GetAsync(request.OperationId!))!;
        Assert.Equal(OperationState.Completed, receipt.State);
        Assert.True(receipt.ResultOmitted);
        Assert.True(receipt.Response!.Ok);
        Assert.Null(receipt.Response.Data);
        var replay = await journal.RunAsync(request, _ => throw new InvalidOperationException("Must not recreate an omitted result."));
        Assert.Equal("operation_result_unavailable", replay.ErrorCode);
    }

    [Fact]
    public async Task CorruptReceiptFailsClosed()
    {
        using var root = new OwnedRoot();
        var journal = new OperationJournal(root.Paths);
        var request = Request();
        _ = await journal.RunAsync(request, _ => Task.FromResult(new AgentResponse(request.Id, true, 1)));
        await File.WriteAllTextAsync(root.ReceiptPath(request.OperationId!), "{broken");
        var failure = await Assert.ThrowsAsync<AgentCommandException>(() =>
            journal.RunAsync(request, _ => throw new InvalidOperationException("Corruption must not authorize execution.")));
        Assert.Equal("operation_store_corrupt", failure.Code);
    }

    [Fact]
    public async Task InvalidAndFutureIdsAreRejectedBeforeAdmission()
    {
        using var root = new OwnedRoot();
        var journal = new OperationJournal(root.Paths);
        foreach (var id in new[] { "not-an-id", Guid.NewGuid().ToString("N"), Guid.CreateVersion7(DateTimeOffset.UtcNow.AddHours(1)).ToString("N") })
        {
            var invalid = await Assert.ThrowsAsync<AgentCommandException>(() =>
                journal.RunAsync(Request() with { OperationId = id }, _ => throw new InvalidOperationException("Invalid IDs must not execute.")));
            Assert.Equal("invalid_operation_id", invalid.Code);
        }

        Assert.False(Directory.Exists(root.OperationsDirectory));
    }

    private static AgentRequest Request(DateTimeOffset? now = null) => new(
        Guid.NewGuid().ToString("N"), "settings-patch", OperationId: Guid.CreateVersion7(now ?? DateTimeOffset.UtcNow).ToString("N"));

    private sealed class TestTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class OwnedRoot : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("SystemAutorun-operation-tests-").FullName;
        public ProjectPaths Paths => new(Root);
        public string OperationsDirectory => Path.Combine(Root, ".autorun-state", CurrentUserIdentity.Key, "operations");
        public string ReceiptPath(string id) => Path.Combine(OperationsDirectory, OperationJournal.NormalizeId(id) + ".operation.json");
        public OwnedRoot() => File.WriteAllText(Paths.ConfigPath, "version: 1\ntasks: []\n");
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("SystemAutorun-operation-tests-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to delete a non-owned operation fixture.");
            }
            Directory.Delete(resolved, recursive: true);
        }
    }
}
