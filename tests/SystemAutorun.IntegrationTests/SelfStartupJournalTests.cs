using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;
using Xunit;
using Fixture = SystemAutorun.IntegrationTests.SelfStartupPlanningSessionTests.Fixture;

namespace SystemAutorun.IntegrationTests;

public sealed class SelfStartupJournalTests
{
    [Theory]
    [InlineData("create", 1)] [InlineData("create-missing", 2)] [InlineData("delete", 1)]
    [InlineData("enabled", 0)] [InlineData("absent", 0)] [InlineData("absent-missing", 0)]
    public async Task DurableAdmissionPrecedesDispatchAndFinalReceiptReplaysWithoutReopeningInputs(string mode, int nativeCalls)
    {
        using var owned = new Fixture();
        if (mode is "create-missing" or "absent-missing") { owned.Native.DeleteRun(); }
        if (mode is "enabled" or "delete") { owned.Native.Set(1, owned.TargetBytes); }
        var paths = Paths(owned); var journal = Journal(owned); var enabled = mode is "create" or "create-missing" or "enabled";
        var envelope = Envelope(owned.Request(enabled)); var stages = new List<string>();
        var observer = new Assertions(stage =>
        {
            stages.Add(stage);
            if (stage is "planned" or "receipt_readback" or "receipt_verified") { owned.AssertHeld().GetAwaiter().GetResult(); }
            if (stage == "before_native")
            {
                var pending = ReadReceipt(paths, envelope.OperationId!); Assert.Equal(OperationState.Running, pending.State); Assert.Null(pending.Response);
                using var root = RootWriteLease.AcquireAsync(paths).GetAwaiter().GetResult(); // Journal callback does not own it.
            }
        });
        var first = await Run(owned, journal, envelope, observer.Visit); observer.Throw();
        Assert.True(first.Ok); Assert.False(first.Replayed); var data = Data(first); Assert.True(data.ProtocolCompleted);
        Assert.Equal(nativeCalls, data.Calls.Count); Assert.Equal(nativeCalls != 0, data.DriverDispatched); Assert.Null(data.FailureCode);
        Assert.Contains("receipt_readback", stages); Assert.Equal(nativeCalls == 0 ? 0 : 1, stages.Count(value => value == "before_native"));
        Assert.Equal(enabled ? SelfStartupNativeState.FromNativeBytes(1, owned.TargetBytes) : new(false), owned.Native.Read().Registration);
        Assert.Equal(OperationState.Completed, ReadReceipt(paths, envelope.OperationId!).State); await owned.AssertReleased();
        var before = Images(paths); var replayOnly = new SelfStartupJournal(owned.Native.Id, paths,
            _ => throw new InvalidOperationException("Replay reopened target."), _ => throw new InvalidOperationException("Replay acquired actor."));
        var replay = await replayOnly.RunFixtureAsync(envelope with { Id = "replay" });
        Assert.True(replay.Ok); Assert.True(replay.Replayed); Assert.Equal("replay", replay.Id); Assert.Equal(data.Calls, Data(replay).Calls);
        AssertImages(before, Images(paths)); Assert.Equal(enabled, owned.Native.Read().Registration.Registered);
    }

    [Theory]
    [InlineData("accepted", false)] [InlineData("planned", false)] [InlineData("before_native", false)] [InlineData("key_created", false)]
    [InlineData("native_returned", false)] [InlineData("native_verified", false)] [InlineData("callback_returning", false)]
    [InlineData("accepted", true)] [InlineData("planned", true)] [InlineData("before_native", true)] [InlineData("key_created", true)]
    [InlineData("native_returned", true)] [InlineData("native_verified", true)] [InlineData("callback_returning", true)]
    public async Task SameFaultOrCancellationIsClassifiedByActualDispatchNotExceptionType(string failAt, bool cancel)
    {
        using var owned = new Fixture(); owned.Native.DeleteRun(); using var stop = new CancellationTokenSource();
        var paths = Paths(owned); var journal = Journal(owned); var envelope = Envelope(owned.Request());
        var response = await Run(owned, journal, envelope, stage =>
        { if (stage == failAt) { if (cancel) { stop.Cancel(); stop.Token.ThrowIfCancellationRequested(); } throw new IOException("Owned phase fault."); } }, stop.Token);
        var calls = failAt is "accepted" or "planned" or "before_native" ? 0 : failAt == "key_created" ? 1 : 2;
        var data = Data(response); Assert.False(response.Ok); Assert.Equal(calls, data.Calls.Count); Assert.Equal(calls != 0, data.DriverDispatched);
        Assert.Equal(calls == 0 ? "not_executed" : "outcome_unknown", response.ErrorCode);
        Assert.Equal(cancel ? "cancelled" : "execution_failed", data.FailureCode);
        Assert.Equal(failAt is "native_verified" or "callback_returning", data.ProtocolCompleted);
        Assert.Equal(calls != 0, owned.Native.Read().KeyPresent); Assert.Equal(calls == 2, owned.Native.Read().Registration.Registered);
        if (calls != 0) { Assert.True(data.CreatedKeyAfter!.KeyPresent); Assert.False(data.CreatedKeyAfter.Registration.Registered); }
        var receipt = ReadReceipt(paths, envelope.OperationId!); Assert.Equal(calls == 0 ? OperationState.Completed : OperationState.Unknown, receipt.State);
        Assert.Equal(data.FailureCode, Data(receipt.Response!).FailureCode); await owned.AssertReleased();
        var before = Images(paths); var replay = await Run(owned, Journal(owned), envelope, _ => throw new InvalidOperationException("Replayed callback."));
        Assert.True(replay.Replayed); Assert.Equal(response.ErrorCode, replay.ErrorCode); AssertImages(before, Images(paths));
        Assert.Equal(calls == 2, owned.Native.Read().Registration.Registered); // No compensation, recovery or replay.
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AGloballyWhitelistedErrorCannotHideAPreviousCreateDispatch(bool afterCreate)
    {
        using var owned = new Fixture(); owned.Native.DeleteRun(); var envelope = Envelope(owned.Request()); var paths = Paths(owned);
        var result = await Run(owned, Journal(owned), envelope, stage =>
        { if (stage == (afterCreate ? "key_created" : "before_native")) { throw new SelfStartupPlanningException("invalid_argument"); } });
        Assert.Equal(afterCreate ? "outcome_unknown" : "not_executed", result.ErrorCode); Assert.Equal("invalid_argument", Data(result).FailureCode);
        Assert.Equal(afterCreate ? OperationState.Unknown : OperationState.Completed, ReadReceipt(paths, envelope.OperationId!).State);
        Assert.Equal(afterCreate ? 1 : 0, Data(result).Calls.Count); Assert.False(owned.Native.Read().Registration.Registered); await owned.AssertReleased();
    }

    [Theory]
    [InlineData("unknown")] [InlineData("duplicate")] [InlineData("confirmation")] [InlineData("schema")]
    [InlineData("name")] [InlineData("revision")] [InlineData("command")] [InlineData("protocol")] [InlineData("timeout")] [InlineData("root")]
    public async Task StrictFrozenEnvelopeRefusesBeforeTargetIo(string variant)
    {
        using var owned = new Fixture(); var envelope = Envelope(owned.Request()); var payload = envelope.Payload!.Value.GetRawText();
        envelope = variant switch
        {
            "unknown" => envelope with { Payload = JsonDocument.Parse(payload[..^1] + ",\"extra\":1}").RootElement.Clone() },
            "duplicate" => envelope with { Payload = JsonDocument.Parse(payload[..^1] + ",\"enabled\":true}").RootElement.Clone() },
            "confirmation" => Envelope(owned.Request() with { Confirmed = false }),
            "schema" => Envelope(owned.Request() with { SchemaVersion = 2 }),
            "name" => envelope with { Name = "not-a-task" },
            "revision" => envelope with { ExpectedRevision = new string('a', 64) },
            "command" => envelope with { Command = "startup-self-set" },
            "protocol" => envelope with { ProtocolVersion = 1 },
            "timeout" => envelope with { TimeoutMilliseconds = 123 },
            "root" => envelope with { RootId = "not-authority" },
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        var paths = Paths(owned); var opened = 0;
        var coordinator = new SelfStartupJournal(owned.Native.Id, paths,
            _ => { opened++; throw new IOException("Must not open assets."); }, _ => throw new IOException("Must not acquire actor."));
        if (variant is not ("unknown" or "duplicate" or "confirmation" or "schema"))
        {
            var error = await Assert.ThrowsAsync<AgentCommandException>(() => coordinator.RunFixtureAsync(envelope));
            Assert.Equal("invalid_argument", error.Code); Assert.Equal(0, opened); Assert.False(File.Exists(ReceiptPath(paths, envelope.OperationId!))); return;
        }
        var response = await coordinator.RunFixtureAsync(envelope);
        Assert.False(response.Ok); Assert.Equal("not_executed", response.ErrorCode); Assert.Equal(0, opened); Assert.Empty(Data(response).Calls);
        Assert.Equal(variant == "confirmation" ? "authorization_required" : "invalid_argument", Data(response).FailureCode);
        Assert.Equal(OperationState.Completed, ReadReceipt(paths, envelope.OperationId!).State); Assert.False(owned.Native.Read().Registration.Registered);
    }

    [Fact]
    public async Task CallerDocumentMayEndAfterAdmissionAndFingerprintConflictNeverReexecutes()
    {
        using var owned = new Fixture(); var paths = Paths(owned); var journal = Journal(owned);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(owned.Request(), AgentProtocol.JsonOptions));
        var envelope = Envelope(owned.Request()) with { Payload = document.RootElement };
        var result = await Run(owned, journal, envelope, stage => { if (stage == "accepted") { document.Dispose(); } });
        Assert.True(result.Ok); Assert.True(owned.Native.Read().Registration.Registered);
        var replayable = envelope with { Payload = JsonSerializer.SerializeToElement(owned.Request() with { ExpectedRegistration = new(false) }, AgentProtocol.JsonOptions) };
        var replay = await Run(owned, journal, replayable); Assert.True(replay.Replayed); Assert.True(replay.Ok);
        var conflict = await Assert.ThrowsAsync<AgentCommandException>(() => Run(owned, journal,
            replayable with { Payload = JsonSerializer.SerializeToElement(owned.Request(false), AgentProtocol.JsonOptions) }));
        Assert.Equal("operation_id_conflict", conflict.Code); await owned.AssertReleased();
    }

    [Fact]
    public async Task TargetAndJournalPhysicalRootsMustBothMatch()
    {
        using var owned = new Fixture(); using var other = new Fixture(); var paths = Paths(owned);
        var coordinator = new SelfStartupJournal(owned.Native.Id, paths,
            ct => other.Package.Open(token: ct), ct => StartupNativeSlotProbe.SelfStartupAdmissionLease.AcquireAsync(owned.Admission.Scope, ct));
        var response = await coordinator.RunFixtureAsync(Envelope(other.Request()));
        Assert.Equal("not_executed", response.ErrorCode); Assert.Equal("self_context_changed", Data(response).FailureCode); Assert.Empty(Data(response).Calls);
        Assert.False(owned.Native.Read().Registration.Registered); await owned.AssertReleased(); await other.AssertReleased();
        var mismatchedJournal = await Run(owned, Journal(owned, paths: Paths(other)), Envelope(owned.Request()));
        Assert.Equal("not_executed", mismatchedJournal.ErrorCode); Assert.Empty(Data(mismatchedJournal).Calls); Assert.Equal(owned.Context.ContextId, Data(mismatchedJournal).TargetContextId);
    }

    [Fact]
    public async Task NativeProtectionSurvivesCallbackEndWhileReceiptCommitWaitsForRootLease()
    {
        using var owned = new Fixture(); var paths = Paths(owned); using var returning = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var envelope = Envelope(owned.Request()); OperationExecutionContext? context = null;
        var assertions = new Assertions(stage => { if (stage == "receipt_verified") { owned.AssertHeld().GetAwaiter().GetResult(); } });
        var work = Task.Run(() => Journal(owned).RunFixtureAsync(envelope, (stage, current) =>
        {
            assertions.Visit(stage);
            if (stage == "callback_returning") { context = current; returning.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Owned commit gate."); } }
        }));
        try
        {
            Assert.True(returning.Wait(TimeSpan.FromSeconds(5))); Assert.NotNull(context);
            await using (var root = await RootWriteLease.AcquireAsync(paths))
            {
                release.Set(); using var end = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (context.IsActive) { await Task.Delay(10, end.Token); } // Deterministic callback End, NOT a timing guess.
                Assert.False(work.IsCompleted); await owned.AssertHeld(); Assert.Equal(OperationState.Running, ReadReceipt(paths, envelope.OperationId!).State);
            }
            var response = await work; assertions.Throw(); Assert.True(response.Ok); Assert.Equal(OperationState.Completed, ReadReceipt(paths, envelope.OperationId!).State);
        }
        finally { release.Set(); await work; }
        await owned.AssertReleased();
    }

    [Fact]
    public async Task PublicationFailureAfterMutationLeavesRunningUnknownAndNeverReplays()
    {
        using var owned = new Fixture(); owned.Native.DeleteRun(); var paths = Paths(owned); var envelope = Envelope(owned.Request()); FileStream? blocker = null;
        try
        {
            await Assert.ThrowsAsync<AgentOutcomeUnknownException>(() => Run(owned, Journal(owned), envelope, stage =>
            { if (stage == "callback_returning") { blocker = File.Open(ReceiptPath(paths, envelope.OperationId!), FileMode.Open, FileAccess.Read, FileShare.Read); } }));
        }
        finally { blocker?.Dispose(); }
        var receipt = ReadReceipt(paths, envelope.OperationId!); Assert.Equal(OperationState.Running, receipt.State); Assert.Null(receipt.Response);
        Assert.True(owned.Native.Read().Registration.Registered); await owned.AssertReleased();
        var replay = await Run(owned, Journal(owned), envelope, _ => throw new IOException("Must not execute."));
        Assert.True(replay.Replayed); Assert.Equal("outcome_unknown", replay.ErrorCode); Assert.True(owned.Native.Read().Registration.Registered);
    }

    [Fact]
    public async Task ReadbackFaultNeverReturnsSuccessAndDoesNotUndoAlreadyCommittedNativeResult()
    {
        using var owned = new Fixture(); var paths = Paths(owned); var envelope = Envelope(owned.Request()); var sentinel = new IOException("Owned receipt read fault.");
        var error = await Assert.ThrowsAsync<AgentOutcomeUnknownException>(() => Run(owned, Journal(owned), envelope, stage => { if (stage == "receipt_readback") { throw sentinel; } }));
        Assert.Same(sentinel, error.InnerException); Assert.True(owned.Native.Read().Registration.Registered); await owned.AssertReleased();
        Assert.Equal(OperationState.Completed, ReadReceipt(paths, envelope.OperationId!).State);
        var replay = await Run(owned, Journal(owned), envelope, _ => throw new IOException("Must not reexecute.")); Assert.True(replay.Ok); Assert.True(replay.Replayed);
    }

    [Theory]
    [InlineData("case")] [InlineData("protocol")] [InlineData("root")] [InlineData("timeout")] [InlineData("name")] [InlineData("revision")]
    public async Task StrictEnvelopeIsCheckedBeforeTheSameIdCanReplay(string header)
    {
        using var owned = new Fixture(); var paths = Paths(owned); var coordinator = Journal(owned); var envelope = Envelope(owned.Request());
        Assert.True((await Run(owned, coordinator, envelope)).Ok); var before = Images(paths); var native = owned.Native.Read();
        var changed = header switch
        {
            "case" => envelope with { Command = envelope.Command.ToUpperInvariant() },
            "protocol" => envelope with { ProtocolVersion = 1 },
            "root" => envelope with { RootId = "not-authority" },
            "timeout" => envelope with { TimeoutMilliseconds = 123 },
            "name" => envelope with { Name = "not-a-task" },
            "revision" => envelope with { ExpectedRevision = new string('a', 64) },
            _ => throw new ArgumentOutOfRangeException(nameof(header))
        };
        var error = await Assert.ThrowsAsync<AgentCommandException>(() => Run(owned, coordinator, changed)); Assert.Equal("invalid_argument", error.Code);
        AssertImages(before, Images(paths)); Assert.Equal(native, owned.Native.Read()); Assert.True((await Run(owned, coordinator, envelope)).Replayed);
    }

    [Theory]
    [InlineData("response")] [InlineData("owner")] [InlineData("fingerprint")] [InlineData("state")] [InlineData("missing")] [InlineData("io")]
    public async Task ActualReadbackRejectsMismatchedMissingOrUnreadableReceipt(string fault)
    {
        using var owned = new Fixture(); var paths = Paths(owned); var envelope = Envelope(owned.Request());
        var path = ReceiptPath(paths, envelope.OperationId!); byte[]? original = null; FileStream? blocker = null;
        try
        {
            await Assert.ThrowsAsync<AgentOutcomeUnknownException>(() => Run(owned, Journal(owned), envelope, stage =>
            {
                if (stage != "receipt_readback") { return; }
                original = File.ReadAllBytes(path); owned.Package.VerifyOwned(path);
                if (fault == "io") { blocker = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None); return; }
                if (fault == "missing") { if (path != Path.GetFullPath(path)) { throw new InvalidOperationException("Noncanonical fixture receipt."); } File.Delete(path); return; }
                var receipt = JsonSerializer.Deserialize<OperationReceipt>(original, AgentProtocol.JsonOptions)!;
                var changed = fault switch
                {
                    "response" => receipt with { Response = receipt.Response! with { Data = JsonSerializer.SerializeToElement(new { forged = true }) } },
                    "owner" => receipt with { OwnerInstanceId = "not-the-owning-journal" },
                    "fingerprint" => receipt with { Fingerprint = (receipt.Fingerprint[0] == 'a' ? "b" : "a") + receipt.Fingerprint[1..] },
                    "state" => receipt with { State = OperationState.Unknown },
                    _ => throw new ArgumentOutOfRangeException(nameof(fault))
                };
                File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(changed, AgentProtocol.JsonOptions));
            }));
        }
        finally { blocker?.Dispose(); if (original is not null) { File.WriteAllBytes(path, original); } }
        Assert.NotNull(original); Assert.Equal(original, File.ReadAllBytes(path)); Assert.Equal(OperationState.Completed, ReadReceipt(paths, envelope.OperationId!).State);
        Assert.True(owned.Native.Read().Registration.Registered); await owned.AssertReleased();
        var replay = await Run(owned, Journal(owned), envelope, _ => throw new IOException("No replay execution.")); Assert.True(replay.Ok); Assert.True(replay.Replayed);
    }

    [Fact]
    public async Task OmittedDetailedResultRetainsReceiptAndCannotCauseReexecution()
    {
        using var owned = new Fixture(); var paths = Paths(owned); var envelope = Envelope(owned.Request()); var journal = Journal(owned, limits: new(MaximumResultBytes: 1));
        var first = await Run(owned, journal, envelope); Assert.True(first.Ok); Assert.NotNull(first.Data);
        var receipt = ReadReceipt(paths, envelope.OperationId!); Assert.True(receipt.ResultOmitted); Assert.Null(receipt.Response!.Data);
        var replay = await Run(owned, Journal(owned), envelope); Assert.True(replay.Replayed); Assert.Equal("operation_result_unavailable", replay.ErrorCode);
        Assert.True(owned.Native.Read().Registration.Registered); await owned.AssertReleased();
    }

    internal static ProjectPaths Paths(Fixture owned) => new(owned.Package.Root);
    internal static AgentRequest Envelope(SelfStartupMutationRequest request) => new(Guid.NewGuid().ToString("N"), SelfStartupJournal.FixtureCommand,
        Payload: JsonSerializer.SerializeToElement(request, AgentProtocol.JsonOptions), OperationId: Guid.CreateVersion7().ToString("N"));
    internal static SelfStartupJournal Journal(Fixture owned, ProjectPaths? paths = null, OperationJournalLimits? limits = null) =>
        new(owned.Native.Id, paths ?? Paths(owned), ct => owned.Package.Open(token: ct), ct => StartupNativeSlotProbe.SelfStartupAdmissionLease.AcquireAsync(owned.Admission.Scope, ct), limits);
    internal static Task<AgentResponse> Run(Fixture owned, SelfStartupJournal journal, AgentRequest envelope, Action<string>? checkpoint = null, CancellationToken token = default) =>
        journal.RunFixtureAsync(envelope, checkpoint is null ? null : (stage, _) => checkpoint(stage), token);
    internal static SelfJournalEvidence Data(AgentResponse response) => ((JsonElement)response.Data!).Deserialize<SelfJournalEvidence>(AgentProtocol.JsonOptions)!;
    internal static string ReceiptPath(ProjectPaths paths, string id) => Path.Combine(paths.RootDirectory, ".autorun-state", CurrentUserIdentity.Key, "operations", id + ".operation.json");
    internal static OperationReceipt ReadReceipt(ProjectPaths paths, string id) => JsonSerializer.Deserialize<OperationReceipt>(File.ReadAllBytes(ReceiptPath(paths, id)), AgentProtocol.JsonOptions)!;
    internal static Dictionary<string, byte[]> Images(ProjectPaths paths) => Directory.GetFiles(paths.RootDirectory, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
    internal static void AssertImages(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    { Assert.Equal(before.Keys.Order(), after.Keys.Order()); foreach (var row in before) { Assert.Equal(row.Value, after[row.Key]); } }
    private sealed class Assertions(Action<string> action)
    {
        private Exception? _failure;
        internal void Visit(string stage) { try { action(stage); } catch (Exception error) { _failure = error; throw; } }
        internal void Throw() { if (_failure is not null) { ExceptionDispatchInfo.Capture(_failure).Throw(); } }
    }
}
