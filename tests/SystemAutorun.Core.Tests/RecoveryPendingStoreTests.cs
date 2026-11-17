using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Recovery;

namespace SystemAutorun.Core.Tests;

public sealed class RecoveryPendingStoreTests
{
    private static readonly string Revision = new('a', 64);
    private const string Id = "019a123456787abc8def0123456789ab";
    private static TempDirectory Root() { var root = new TempDirectory(); root.Write("autorun.yaml", "version: 1\ntasks: []\n"); return root; }
    private static RecoveryPendingStore Store(TempDirectory root, string name = "media") => new(new(root.Path), name);
    private static string Pending(TempDirectory root, RecoveryPendingStore store) => Path.Combine(root.Path, store.FileName);

    [Fact]
    public async Task PureReadAndRestartPreserveExactOriginalIntentButRecoverNoClearCapability()
    {
        using var root = Root(); var store = Store(root); var before = Directory.GetFileSystemEntries(root.Path);
        var missing = await store.ReadAsync(); Assert.Equal(RecoveryPendingState.Missing, missing.State); Assert.False(missing.BlocksSubmission);
        Assert.Equal(before, Directory.GetFileSystemEntries(root.Path));
        var owner = await store.ReserveAsync(Id.ToUpperInvariant(), Revision); var first = await store.ReadAsync(); var bytes = File.ReadAllBytes(Pending(root, store));
        Assert.Equal(owner.Operation, first.Operation); Assert.True(first.BlocksSubmission); Assert.Equal(Id, first.Operation!.OperationId);
        Assert.Equal(Revision, first.Operation.ExpectedRevision); Assert.Equal("media", first.Operation.TaskName);
        Assert.Equal(CurrentUserIdentity.Key, first.Operation.SubjectKey); Assert.Equal(AgentProtocol.ForRoot(root.Path).RootId, first.Operation.RootId);
        Assert.Equal(TimeSpan.Zero, first.Operation.ReservedAt.Offset); Assert.Equal(first, await Store(root, "MEDIA").ReadAsync());
        Assert.Equal(bytes, File.ReadAllBytes(Pending(root, store))); Assert.Empty(typeof(RecoveryPendingStore.Reservation).GetConstructors());
        Assert.DoesNotContain(typeof(RecoveryPendingStore).GetMethods(), method => method.Name.Contains("Clear", StringComparison.Ordinal) || method.Name.Contains("Release", StringComparison.Ordinal));
        Assert.Equal("pending_exists", (await Assert.ThrowsAsync<RecoveryPendingStoreException>(() => Store(root, "MEDIA").ReserveAsync(AgentProtocol.NewOperationId(), Revision))).Code);
        Assert.Equal(bytes, File.ReadAllBytes(Pending(root, store))); Assert.Empty(Directory.GetFiles(root.Path, "*.tmp"));
    }

    [Fact]
    public async Task DifferentNamesDoNotSharePendingBlockAndFreshCapabilityIsSingleAttempt()
    {
        using var root = Root(); var a = Store(root); var b = Store(root, "another");
        var first = await a.ReserveAsync(Id, Revision); var second = await b.ReserveAsync(AgentProtocol.NewOperationId(), Revision);
        Assert.NotEqual(a.FileName, b.FileName); Assert.True((await a.ReadAsync()).BlocksSubmission); Assert.True((await b.ReadAsync()).BlocksSubmission);
        await first.ReleaseAfterKnownOutcomeAsync(); Assert.Equal(RecoveryPendingState.Missing, (await a.ReadAsync()).State);
        Assert.Equal(second.Operation, (await b.ReadAsync()).Operation); await second.ReleaseAfterKnownOutcomeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.ReleaseAfterKnownOutcomeAsync());
        Assert.False((await b.ReadAsync()).BlocksSubmission);
    }

    [Theory]
    [InlineData("schemaVersion")] [InlineData("rootId")] [InlineData("subjectKey")] [InlineData("taskName")]
    [InlineData("expectedRevision")] [InlineData("operationId")] [InlineData("reservedAt")]
    public async Task MissingRequiredFieldsRemainBlockedInsteadOfRecoveringDefaults(string field)
    {
        using var root = Root(); var store = Store(root); _ = await store.ReserveAsync(Id, Revision);
        var node = JsonNode.Parse(File.ReadAllText(Pending(root, store)))!.AsObject(); node.Remove(field);
        await AssertBlocked(root, store, Encoding.UTF8.GetBytes(node.ToJsonString()));
    }

    [Theory]
    [InlineData("schema")] [InlineData("root")] [InlineData("subject")] [InlineData("name")] [InlineData("name-control")]
    [InlineData("revision")] [InlineData("revision-case")] [InlineData("id")] [InlineData("id-case")] [InlineData("id-null")]
    [InlineData("time")] [InlineData("offset")] [InlineData("duplicate")] [InlineData("case")] [InlineData("unknown")]
    [InlineData("confirmation")] [InlineData("number")] [InlineData("array")] [InlineData("null")] [InlineData("utf8")]
    [InlineData("empty")] [InlineData("size")]
    public async Task MalformedOrForeignHintNeverInventsIdentityOrRewritesOriginalBytes(string defect)
    {
        using var root = Root(); var store = Store(root); _ = await store.ReserveAsync(Id, Revision);
        var original = File.ReadAllText(Pending(root, store)); var node = JsonNode.Parse(original)!.AsObject();
        switch (defect)
        {
            case "schema": node["schemaVersion"] = 2; break;
            case "root": node["rootId"] = new string('0', 64); break;
            case "subject": node["subjectKey"] = "foreign_subject"; break;
            case "name": node["taskName"] = "another"; break;
            case "name-control": node["taskName"] = "media\n"; break;
            case "revision": node["expectedRevision"] = "bad"; break;
            case "revision-case": node["expectedRevision"] = Revision.ToUpperInvariant(); break;
            case "id": node["operationId"] = Guid.NewGuid().ToString("N"); break;
            case "id-case": node["operationId"] = Id.ToUpperInvariant(); break;
            case "id-null": node["operationId"] = null; break;
            case "time": node["reservedAt"] = DateTimeOffset.MinValue; break;
            case "offset": node["reservedAt"] = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)); break;
            case "case": node["SchemaVersion"] = 1; break;
            case "unknown": node["fingerprint"] = new string('b', 64); break;
            case "confirmation": node["confirmed"] = true; break;
            case "number": node["taskName"] = 0; break;
        }
        var text = defect switch { "duplicate" => original[..^1] + ",\"operationId\":\"" + Id + "\"}", "array" => "[]", "null" => "null", "empty" => "", "size" => new string('x', RecoveryPendingStore.MaximumBytes + 1), _ => node.ToJsonString() };
        await AssertBlocked(root, store, defect == "utf8" ? new byte[] { 0xff, 0xfe, 0xfd } : Encoding.UTF8.GetBytes(text));
    }

    private static async Task AssertBlocked(TempDirectory root, RecoveryPendingStore store, byte[] bytes)
    {
        File.WriteAllBytes(Pending(root, store), bytes); var state = await store.ReadAsync();
        Assert.Equal(RecoveryPendingState.Unavailable, state.State); Assert.True(state.BlocksSubmission); Assert.Null(state.Operation);
        Assert.Equal("pending_invalid", state.Reason);
        Assert.Equal("pending_invalid", (await Assert.ThrowsAsync<RecoveryPendingStoreException>(() => store.ReserveAsync(AgentProtocol.NewOperationId(), Revision))).Code);
        Assert.Equal(bytes, File.ReadAllBytes(Pending(root, store))); Assert.Empty(Directory.GetFiles(root.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("changed")] [InlineData("whitespace")] [InlineData("corrupt")] [InlineData("deleted")] [InlineData("directory")]
    public async Task ReleaseCannotRemoveChangedOrUnreadableHintAndCannotRetryItsCapability(string defect)
    {
        using var root = Root(); var store = Store(root); var owner = await store.ReserveAsync(Id, Revision); var path = Pending(root, store);
        if (defect == "changed") { File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(owner.Operation with { OperationId = AgentProtocol.NewOperationId() }, AgentProtocol.JsonOptions)); }
        if (defect == "whitespace") { File.AppendAllText(path, " "); }
        if (defect == "corrupt") { File.WriteAllText(path, "corrupt-fixture"); }
        if (defect is "deleted" or "directory") { File.Delete(path); }
        if (defect == "directory") { Directory.CreateDirectory(path); }
        var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        Assert.Equal("pending_changed", (await Assert.ThrowsAsync<RecoveryPendingStoreException>(() => owner.ReleaseAfterKnownOutcomeAsync())).Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ReleaseAfterKnownOutcomeAsync());
        if (before is not null) { Assert.Equal(before, File.ReadAllBytes(path)); }
        else { Assert.Equal(defect == "directory", Directory.Exists(path)); }
    }

    [Fact]
    public async Task ConcurrentAliasesReserveExactlyOneIdButOtherNameRemainsUsable()
    {
        using var root = Root(); var stores = new[] { Store(root), Store(root, "MEDIA") };
        async Task<bool> Attempt(int index) { try { _ = await stores[index].ReserveAsync(AgentProtocol.NewOperationId(), Revision); return true; } catch (RecoveryPendingStoreException error) { Assert.Equal("pending_exists", error.Code); return false; } }
        var results = await Task.WhenAll(Attempt(0), Attempt(1)); Assert.Single(results, won => won);
        _ = await Store(root, "another").ReserveAsync(AgentProtocol.NewOperationId(), Revision);
        Assert.Equal(RecoveryPendingState.Present, (await Store(root).ReadAsync()).State);
        Assert.Equal(RecoveryPendingState.Present, (await Store(root, "another").ReadAsync()).State);
    }

    [Fact]
    public async Task CopiedRecordInAnotherRootIsUnavailableNotARecoveredOperation()
    {
        using var root = Root(); using var another = Root(); var original = Store(root); var foreign = Store(another);
        _ = await original.ReserveAsync(Id, Revision); var bytes = File.ReadAllBytes(Pending(root, original)); File.WriteAllBytes(Pending(another, foreign), bytes);
        var state = await foreign.ReadAsync(); Assert.Equal(RecoveryPendingState.Unavailable, state.State); Assert.Null(state.Operation); Assert.True(state.BlocksSubmission);
        Assert.Equal(bytes, File.ReadAllBytes(Pending(another, foreign)));
    }

    [Theory]
    [InlineData("before")] [InlineData("flushed")] [InlineData("published")]
    public async Task CancellationBeforeReturnNeverSuppliesClearCapabilityAndPreservesPublication(string stage)
    {
        using var root = Root(); using var cancel = new CancellationTokenSource();
        var store = Instrumented(root, checkpoint => { if (checkpoint == stage) { cancel.Cancel(); } });
        if (stage == "before") { cancel.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReserveAsync(Id, Revision, cancel.Token));
        var snapshot = await Store(root).ReadAsync(); Assert.Equal(stage == "published" ? RecoveryPendingState.Present : RecoveryPendingState.Missing, snapshot.State);
        if (stage == "published") { Assert.Equal(Id, snapshot.Operation!.OperationId); }
        Assert.Empty(Directory.GetFiles(root.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("flushed")] [InlineData("published")]
    public async Task InjectedIoFailureDoesNotSendOrRemoveDestination(string stage)
    {
        using var root = Root(); var store = Instrumented(root, checkpoint => { if (checkpoint == stage) { throw new IOException("owned test failure"); } });
        Assert.Equal("pending_storage_unavailable", (await Assert.ThrowsAsync<RecoveryPendingStoreException>(() => store.ReserveAsync(Id, Revision))).Code);
        Assert.Equal(stage == "published" ? RecoveryPendingState.Present : RecoveryPendingState.Missing, (await Store(root).ReadAsync()).State);
        Assert.Empty(Directory.GetFiles(root.Path, "*.tmp"));
    }

    [Fact]
    public async Task PublicationReadbackIsNotOptionalAndUnknownTempsAreNotCleaned()
    {
        using var root = Root(); var ordinary = Store(root); root.Write("unowned.tmp", "keep");
        var store = Instrumented(root, checkpoint => { if (checkpoint == "published") { File.WriteAllText(Pending(root, ordinary), "changed-after-rename"); } });
        Assert.Equal("pending_publication_unverified", (await Assert.ThrowsAsync<RecoveryPendingStoreException>(() => store.ReserveAsync(Id, Revision))).Code);
        Assert.Equal("changed-after-rename", File.ReadAllText(Pending(root, ordinary))); Assert.Equal("keep", File.ReadAllText(Path.Combine(root.Path, "unowned.tmp")));
        Assert.Equal(RecoveryPendingState.Unavailable, (await ordinary.ReadAsync()).State);
    }

    [Fact]
    public async Task PublicationMustVerifyExactBytesEvenForSemanticallyIdenticalValidJson()
    {
        using var root = Root(); var ordinary = Store(root);
        var store = Instrumented(root, checkpoint => { if (checkpoint == "published") { File.AppendAllText(Pending(root, ordinary), " \n"); } });
        Assert.Equal("pending_publication_unverified", (await Assert.ThrowsAsync<RecoveryPendingStoreException>(() => store.ReserveAsync(Id, Revision))).Code);
        Assert.Equal(RecoveryPendingState.Present, (await ordinary.ReadAsync()).State); Assert.EndsWith(" \n", File.ReadAllText(Pending(root, ordinary)));
        Assert.Empty(Directory.GetFiles(root.Path, "*.tmp"));
    }

    [Fact]
    public async Task ReadOnlyReleaseFailurePreservesBytesAndConsumesOnlyFreshCapability()
    {
        using var root = Root(); var store = Store(root); var owner = await store.ReserveAsync(Id, Revision); var path = Pending(root, store); var before = File.ReadAllBytes(path);
        var original = File.GetAttributes(path);
        try
        {
            File.SetAttributes(path, original | FileAttributes.ReadOnly);
            Assert.Equal("pending_storage_unavailable", (await Assert.ThrowsAsync<RecoveryPendingStoreException>(() => owner.ReleaseAfterKnownOutcomeAsync())).Code);
            Assert.Equal(before, File.ReadAllBytes(path)); Assert.True((await Store(root).ReadAsync()).BlocksSubmission);
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ReleaseAfterKnownOutcomeAsync());
        }
        finally { File.SetAttributes(path, original); }
    }

    [Fact]
    public async Task ActualConcurrentReadCannotPermanentlyBlockFreshDefinitiveRelease()
    {
        using var root = Root(); var ordinary = Store(root); var owner = await ordinary.ReserveAsync(Id, Revision);
        using var opened = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        var reader = Instrumented(root, checkpoint => { if (checkpoint == "read-opened") { opened.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Owned slow reader was not released."); } } });
        var reading = Task.Run(() => reader.ReadAsync());
        try
        {
            Assert.True(opened.Wait(TimeSpan.FromSeconds(5))); await owner.ReleaseAfterKnownOutcomeAsync();
            Assert.Equal(RecoveryPendingState.Missing, (await ordinary.ReadAsync()).State);
        }
        finally { resume.Set(); await reading; }
        Assert.Equal(owner.Operation, (await reading).Operation); Assert.Empty(Directory.GetFiles(root.Path, "*.cleared"));
    }

    [Fact]
    public async Task BusyLeaseHasBoundedAdmissionAndReadDoesNotNeedTheLease()
    {
        using var root = Root(); var store = Store(root); await using var held = await RootWriteLease.AcquireAsync(new(root.Path));
        Assert.Equal(RecoveryPendingState.Missing, (await store.ReadAsync()).State);
        Assert.Equal("pending_storage_unavailable", (await Assert.ThrowsAsync<RecoveryPendingStoreException>(() => store.ReserveAsync(Id, Revision, leaseTimeout: TimeSpan.FromMilliseconds(30)))).Code);
        Assert.False(File.Exists(Pending(root, store)));
    }

    [Fact]
    public async Task LongUnicodeNamesAndUnrelatedSelfHintArePreserved()
    {
        using var root = Root(); root.Write(SelfStartupPendingStore.FileName, "unrelated-self-hint"); var name = new string('界', 256); var store = Store(root, name);
        _ = await store.ReserveAsync(Id, Revision); Assert.Equal(name, (await store.ReadAsync()).Operation!.TaskName);
        Assert.InRange(new FileInfo(Pending(root, store)).Length, 1, RecoveryPendingStore.MaximumBytes);
        Assert.Equal("unrelated-self-hint", File.ReadAllText(Path.Combine(root.Path, SelfStartupPendingStore.FileName)));
    }

    [Fact]
    public async Task InvalidArgumentsDoNotResolveOrWriteRootAndMissingRootIsNotMissingHint()
    {
        using var root = new TempDirectory(); var store = Store(root);
        Assert.Throws<ArgumentException>(() => Store(root, "\n"));
        await Assert.ThrowsAsync<AgentCommandException>(() => store.ReserveAsync("bad", Revision));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReserveAsync(Id, "bad"));
        Assert.Equal(RecoveryPendingState.Unavailable, (await store.ReadAsync()).State); Assert.Empty(Directory.GetFileSystemEntries(root.Path));
    }

    private static RecoveryPendingStore Instrumented(TempDirectory root, Action<string> checkpoint) =>
        (RecoveryPendingStore)typeof(RecoveryPendingStore).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(ProjectPaths), typeof(string), typeof(Action<string>)], null)!.Invoke([new ProjectPaths(root.Path), "media", checkpoint]);
}
