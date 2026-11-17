using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Core.Tests;

public sealed class SelfStartupPendingStoreTests
{
    private const string Id = "019a123456787abc8def0123456789ab";

    [Fact]
    public async Task ReadMissingIsPureAndReservationSurvivesNewInstanceWithoutRecoveryClearCapability()
    {
        using var root = Root(); var paths = new ProjectPaths(root.Path); var store = new SelfStartupPendingStore(paths);
        var before = Files(root); var missing = await store.ReadAsync(); Assert.Equal(SelfStartupPendingState.Missing, missing.State); Assert.False(missing.BlocksSubmission);
        Assert.Equal(before, Files(root)); Assert.Null(missing.Operation);
        var owner = await store.ReserveAsync(Id.ToUpperInvariant(), true); var first = await store.ReadAsync(); var bytes = File.ReadAllBytes(Pending(root));
        Assert.True(first.BlocksSubmission); Assert.Equal(SelfStartupPendingState.Present, first.State); Assert.Equal(owner.Operation, first.Operation);
        Assert.InRange(bytes.Length, 1, SelfStartupPendingStore.MaximumBytes);
        Assert.Equal(Id, first.Operation!.OperationId); Assert.True(first.Operation.Enabled); Assert.Equal(TimeSpan.Zero, first.Operation.ReservedAt.Offset);
        Assert.Equal(AgentProtocol.ForRoot(root.Path).RootId, first.Operation.RootId);
        var recoveredStore = new SelfStartupPendingStore(paths); var recovered = await recoveredStore.ReadAsync();
        Assert.Equal(first, recovered); Assert.Equal(bytes, File.ReadAllBytes(Pending(root)));
        Assert.Empty(typeof(SelfStartupPendingStore.Reservation).GetConstructors());
        Assert.DoesNotContain(typeof(SelfStartupPendingStore).GetMethods(), method => method.Name.Contains("Clear", StringComparison.Ordinal) || method.Name.Contains("Release", StringComparison.Ordinal));
        var refused = await Assert.ThrowsAsync<SelfStartupPendingStoreException>(() => recoveredStore.ReserveAsync(AgentProtocol.NewOperationId(), false));
        Assert.Equal("pending_exists", refused.Code); Assert.Equal(bytes, File.ReadAllBytes(Pending(root)));
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("schema")] [InlineData("root")] [InlineData("operation")] [InlineData("operation-case")] [InlineData("null-operation")]
    [InlineData("missing")] [InlineData("case")] [InlineData("duplicate")] [InlineData("unknown")] [InlineData("confirmed")]
    [InlineData("enabled-type")] [InlineData("time")] [InlineData("offset")] [InlineData("empty")] [InlineData("array")]
    [InlineData("null")] [InlineData("utf8")] [InlineData("size")]
    public async Task MalformedOrForeignHintBlocksWithoutInventingIdentityOrRewritingBytes(string defect)
    {
        using var root = Root(); var json = JsonSerializer.SerializeToNode(Record(root), AgentProtocol.JsonOptions)!.AsObject();
        switch (defect)
        {
            case "schema": json["schemaVersion"] = 2; break;
            case "root": json["rootId"] = new string('0', 64); break;
            case "operation": json["operationId"] = Guid.NewGuid().ToString("N"); break;
            case "operation-case": json["operationId"] = Id.ToUpperInvariant(); break;
            case "null-operation": json["operationId"] = null; break;
            case "missing": json.Remove("enabled"); break;
            case "case": json.Remove("enabled"); json["Enabled"] = true; break;
            case "unknown": json["targetContextId"] = new string('a', 64); break;
            case "confirmed": json["confirmed"] = true; break;
            case "enabled-type": json["enabled"] = "true"; break;
            case "time": json["reservedAt"] = DateTimeOffset.MinValue; break;
            case "offset": json["reservedAt"] = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(8)); break;
        }
        var wire = json.ToJsonString();
        wire = defect switch { "duplicate" => wire[..^1] + ",\"operationId\":\"" + Id + "\"}", "empty" => "", "array" => "[]", "null" => "null", _ => wire };
        var bytes = defect == "utf8" ? new byte[] { 0xff } : defect == "size" ? new byte[SelfStartupPendingStore.MaximumBytes + 1] : Encoding.UTF8.GetBytes(wire);
        File.WriteAllBytes(Pending(root), bytes); var before = Files(root); var store = new SelfStartupPendingStore(new(root.Path));
        var snapshot = await store.ReadAsync(); Assert.True(snapshot.BlocksSubmission); Assert.Equal(SelfStartupPendingState.Unavailable, snapshot.State);
        Assert.Equal("pending_invalid", snapshot.Reason); Assert.Null(snapshot.Operation); Assert.Equal(before, Files(root)); Assert.Equal(bytes, File.ReadAllBytes(Pending(root)));
        var error = await Assert.ThrowsAsync<SelfStartupPendingStoreException>(() => store.ReserveAsync(AgentProtocol.NewOperationId(), false));
        Assert.Equal("pending_invalid", error.Code); Assert.Equal(bytes, File.ReadAllBytes(Pending(root))); Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp"));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("invalid")]
    public async Task InvalidIdIsRejectedBeforeCreatingALockOrTemporary(string? id)
    {
        using var root = Root(); var before = Files(root);
        var error = await Assert.ThrowsAsync<AgentCommandException>(() => new SelfStartupPendingStore(new(root.Path)).ReserveAsync(id!, true));
        Assert.Equal("invalid_operation_id", error.Code); Assert.Equal(before, Files(root));
    }

    [Fact]
    public async Task MissingRootOrLockedHintIsUnavailableNotMissing()
    {
        using var root = Root();
        Assert.Equal(SelfStartupPendingState.Unavailable, (await new SelfStartupPendingStore(new(Path.Combine(root.Path, "absent"))).ReadAsync()).State);
        File.WriteAllBytes(Pending(root), JsonSerializer.SerializeToUtf8Bytes(Record(root), AgentProtocol.JsonOptions));
        var bytes = File.ReadAllBytes(Pending(root));
        using (var blocked = new FileStream(Pending(root), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var store = new SelfStartupPendingStore(new(root.Path)); var snapshot = await store.ReadAsync();
            Assert.True(snapshot.BlocksSubmission); Assert.Equal("pending_storage_unavailable", snapshot.Reason); Assert.Null(snapshot.Operation);
            Assert.Equal("pending_storage_unavailable", (await Assert.ThrowsAsync<SelfStartupPendingStoreException>(() => store.ReserveAsync(Id, false))).Code);
        }
        Assert.Equal(bytes, File.ReadAllBytes(Pending(root)));
    }

    [Fact]
    public async Task DirectoryAtPendingLeafIsNotAnEmptyStore()
    {
        using var root = Root(); Directory.CreateDirectory(Pending(root)); var store = new SelfStartupPendingStore(new(root.Path));
        var snapshot = await store.ReadAsync(); Assert.True(snapshot.BlocksSubmission); Assert.Null(snapshot.Operation);
        await Assert.ThrowsAsync<SelfStartupPendingStoreException>(() => store.ReserveAsync(Id, true)); Assert.True(Directory.Exists(Pending(root)));
    }

    [Fact]
    public async Task ConcurrentInstancesReserveOnlyOneOriginalIdWithoutOverwrite()
    {
        using var root = Root(); var ids = new[] { Id, AgentProtocol.NewOperationId() };
        var results = await Task.WhenAll(ids.Select(async id =>
        {
            try { return (Owner: await new SelfStartupPendingStore(new(root.Path)).ReserveAsync(id, true), Code: (string?)null); }
            catch (SelfStartupPendingStoreException error) { return (Owner: (SelfStartupPendingStore.Reservation?)null, Code: error.Code); }
        }));
        var owner = Assert.Single(results, result => result.Owner is not null).Owner!;
        Assert.Equal("pending_exists", Assert.Single(results, result => result.Owner is null).Code);
        Assert.Equal(owner.Operation, (await new SelfStartupPendingStore(new(root.Path)).ReadAsync()).Operation);
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp"));
    }

    [Fact]
    public async Task BusyCooperativeLeaseBlocksReserveButReadStaysReadOnly()
    {
        using var root = Root(); var paths = new ProjectPaths(root.Path); await using var held = await RootWriteLease.AcquireAsync(paths);
        var store = new SelfStartupPendingStore(paths); var before = Files(root); Assert.False((await store.ReadAsync()).BlocksSubmission); Assert.Equal(before, Files(root));
        var error = await Assert.ThrowsAsync<SelfStartupPendingStoreException>(() => store.ReserveAsync(Id, true, leaseTimeout: TimeSpan.FromMilliseconds(30)));
        Assert.Equal("pending_storage_unavailable", error.Code); Assert.False(File.Exists(Pending(root))); Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("flushed")] [InlineData("published")]
    public async Task StorageFailureNeverReturnsASendCapabilityAndNeverRollsBackPublishedHint(string stage)
    {
        using var root = Root(); var store = Instrumented(root, current => { if (current == stage) { throw new IOException("owned checkpoint"); } });
        await Assert.ThrowsAsync<SelfStartupPendingStoreException>(() => store.ReserveAsync(Id, true));
        var fresh = new SelfStartupPendingStore(new(root.Path)); var snapshot = await fresh.ReadAsync();
        Assert.Equal(stage == "flushed" ? SelfStartupPendingState.Missing : SelfStartupPendingState.Present, snapshot.State);
        if (stage == "published")
        {
            Assert.Equal(Id, snapshot.Operation!.OperationId); Assert.True(snapshot.BlocksSubmission);
            Assert.Equal("pending_exists", (await Assert.ThrowsAsync<SelfStartupPendingStoreException>(() => fresh.ReserveAsync(AgentProtocol.NewOperationId(), false))).Code);
        }
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("before")] [InlineData("flushed")] [InlineData("published")]
    public async Task CancellationNeverAuthorizesSendOrDeletesPublishedHint(string stage)
    {
        using var root = Root(); using var stop = new CancellationTokenSource(); if (stage == "before") { stop.Cancel(); }
        var store = Instrumented(root, current => { if (current == stage) { stop.Cancel(); } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReserveAsync(Id, false, stop.Token));
        var snapshot = await new SelfStartupPendingStore(new(root.Path)).ReadAsync();
        Assert.Equal(stage == "published" ? SelfStartupPendingState.Present : SelfStartupPendingState.Missing, snapshot.State);
        if (snapshot.Operation is not null) { Assert.Equal(Id, snapshot.Operation.OperationId); }
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp"));
        if (stage == "before") { Assert.False(File.Exists(Path.Combine(root.Path, RootWriteLease.FileName))); }
    }

    [Fact]
    public async Task FreshOwnerCanReleaseOnceAfterProvenNoSendButReadCannotRelease()
    {
        using var root = Root(); var store = new SelfStartupPendingStore(new(root.Path)); var owner = await store.ReserveAsync(Id, false);
        // No Agent/client was invoked, so this test independently knows it was not sent.
        await owner.ReleaseAfterKnownOutcomeAsync(); Assert.False((await store.ReadAsync()).BlocksSubmission);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ReleaseAfterKnownOutcomeAsync());
        var next = await store.ReserveAsync(AgentProtocol.NewOperationId(), true); Assert.NotEqual(Id, next.Operation.OperationId);
    }

    [Theory]
    [InlineData("whitespace")] [InlineData("direction")] [InlineData("identity")] [InlineData("corrupt")] [InlineData("missing")]
    public async Task FreshOwnerCannotClearChangedOrMissingBytesEvenWithTheSameId(string change)
    {
        using var root = Root(); var store = new SelfStartupPendingStore(new(root.Path)); var owner = await store.ReserveAsync(Id, true);
        var original = File.ReadAllText(Pending(root));
        if (change == "missing") { File.Delete(Pending(root)); }
        else { File.WriteAllText(Pending(root), change switch { "whitespace" => original + " ", "direction" => original.Replace("\"enabled\":true", "\"enabled\":false", StringComparison.Ordinal), "identity" => original.Replace(Id, AgentProtocol.NewOperationId(), StringComparison.Ordinal), _ => "invalid" }); }
        var expected = change == "missing" ? null : File.ReadAllBytes(Pending(root));
        Assert.Equal("pending_changed", (await Assert.ThrowsAsync<SelfStartupPendingStoreException>(() => owner.ReleaseAfterKnownOutcomeAsync())).Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ReleaseAfterKnownOutcomeAsync());
        if (expected is not null) { Assert.Equal(expected, File.ReadAllBytes(Pending(root))); } else { Assert.False(File.Exists(Pending(root))); }
    }

    [Fact]
    public async Task ReadOnlyPendingMakesClearFailClosedWithoutAttributeFallbackOrRetry()
    {
        using var root = Root(); var owner = await new SelfStartupPendingStore(new(root.Path)).ReserveAsync(Id, true); var attributes = File.GetAttributes(Pending(root));
        try
        {
            File.SetAttributes(Pending(root), attributes | FileAttributes.ReadOnly);
            Assert.Equal("pending_storage_unavailable", (await Assert.ThrowsAsync<SelfStartupPendingStoreException>(() => owner.ReleaseAfterKnownOutcomeAsync())).Code);
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ReleaseAfterKnownOutcomeAsync());
            Assert.True((File.GetAttributes(Pending(root)) & FileAttributes.ReadOnly) != 0);
            Assert.True((await new SelfStartupPendingStore(new(root.Path)).ReadAsync()).BlocksSubmission);
        }
        finally { File.SetAttributes(Pending(root), attributes); }
    }

    [Fact]
    public async Task RootCopiedHintCannotAuthorizeAnotherRootOrBeOverwritten()
    {
        using var first = Root(); using var second = Root(); var owner = await new SelfStartupPendingStore(new(first.Path)).ReserveAsync(Id, true);
        File.Copy(Pending(first), Pending(second)); var bytes = File.ReadAllBytes(Pending(second)); var other = new SelfStartupPendingStore(new(second.Path));
        Assert.Equal("pending_invalid", (await other.ReadAsync()).Reason);
        await Assert.ThrowsAsync<SelfStartupPendingStoreException>(() => other.ReserveAsync(AgentProtocol.NewOperationId(), false));
        Assert.Equal(bytes, File.ReadAllBytes(Pending(second))); Assert.True((await new SelfStartupPendingStore(new(first.Path)).ReadAsync()).BlocksSubmission);
    }

    private static TempDirectory Root() { var root = new TempDirectory(); root.Write("autorun.yaml", "version: 1\ntasks: []\n"); return root; }
    private static string Pending(TempDirectory root) => Path.Combine(root.Path, SelfStartupPendingStore.FileName);
    private static string[] Files(TempDirectory root) => Directory.GetFileSystemEntries(root.Path).Order(StringComparer.Ordinal).ToArray();
    private static SelfStartupPendingOperation Record(TempDirectory root) => new()
    { SchemaVersion = 1, RootId = AgentProtocol.ForRoot(root.Path).RootId, OperationId = Id, Enabled = true, ReservedAt = DateTimeOffset.UtcNow };
    private static SelfStartupPendingStore Instrumented(TempDirectory root, Action<string> checkpoint) =>
        (SelfStartupPendingStore)typeof(SelfStartupPendingStore).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(ProjectPaths), typeof(Action<string>)], null)!.Invoke([new ProjectPaths(root.Path), checkpoint]);
}
