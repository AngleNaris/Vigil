using System.IO;
using System.Text;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;
using Xunit;
using Fixture = SystemAutorun.IntegrationTests.SelfStartupPlanningSessionTests.Fixture;

namespace SystemAutorun.IntegrationTests;

public sealed class SelfStartupRegistrationServiceTests
{
    [Theory]
    [InlineData("empty", true)] [InlineData("empty", false)] [InlineData("missing", true)] [InlineData("missing", false)]
    [InlineData("same", true)] [InlineData("same", false)]
    public async Task PreparationIsUnconfirmedReadOnlyAndDoesNotAcquireAdmissionOrJournal(string state, bool enabled)
    {
        using var owned = new Fixture(); SetState(owned, state); var opens = 0; var admissions = 0;
        var coordinator = new SelfStartupJournal(owned.Native.Id, Paths(owned), ct => { opens++; return owned.Package.Open(token: ct); },
            _ => { admissions++; throw new IOException("Preparation acquired admission."); });
        var service = new SelfStartupRegistrationService(coordinator); var before = Storage(owned); var native = owned.Native.Read();
        var request = await service.PrepareAsync(enabled); var second = await service.PrepareAsync(enabled);
        Assert.Equal(2, opens); Assert.Equal(0, admissions); Assert.False(request.Confirmed); Assert.Equal(enabled, request.Enabled);
        Assert.Equal(SelfStartupMutationContract.SchemaVersion, request.SchemaVersion); Assert.Equal(owned.Context.ContextId, request.TargetContextId);
        Assert.Equal(native.Registration, request.ExpectedRegistration); Assert.Equal(request, second);
        Assert.Equal("authorization_required", SelfStartupMutationPolicy.ValidateRequest(request));
        Assert.Equal(native, owned.Native.Read()); AssertStorage(before, Storage(owned)); owned.AssertPackageReleased();
    }

    [Theory]
    [InlineData(1u, "foreign")] [InlineData(2u, "expand")] [InlineData(3u, "binary")] [InlineData(uint.MaxValue, "unknown")]
    public async Task PreparationRejectsForeignAndNontextNativeStateWithoutWriting(uint type, string value)
    {
        using var owned = new Fixture(); owned.Native.Set(type, Encoding.Unicode.GetBytes(value + '\0'));
        var service = Service(owned); var before = Storage(owned); var native = owned.Native.Read();
        var error = await Assert.ThrowsAsync<AgentCommandException>(() => service.PrepareAsync(true));
        Assert.Equal("self_registration_conflict", error.Code); AssertStorage(before, Storage(owned)); Assert.Equal(native, owned.Native.Read());
        owned.AssertPackageReleased();
    }

    [Fact]
    public async Task PreparationIgnoresHeldMutationRootAndActorLeasesAndCreatesNoOtherStorage()
    {
        using var owned = new Fixture(); var paths = Paths(owned);
        await using var root = await RootWriteLease.AcquireAsync(paths);
        using var actor = await StartupNativeSlotProbe.SelfStartupAdmissionLease.AcquireAsync(owned.Admission.Scope);
        var before = Storage(owned); using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = await Service(owned).PrepareAsync(true, limit.Token);
        Assert.False(request.Confirmed); AssertStorage(before, Storage(owned)); owned.AssertPackageReleased();
    }

    [Fact]
    public async Task PreparationCancellationCallsNoFactoryAndReleasesNothingItDoesNotOwn()
    {
        using var owned = new Fixture(); var calls = 0; var before = Storage(owned); using var stop = new CancellationTokenSource(); stop.Cancel();
        var coordinator = new SelfStartupJournal(owned.Native.Id, Paths(owned), _ => { calls++; throw new IOException("No target IO."); },
            _ => { calls++; throw new IOException("No admission IO."); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SelfStartupRegistrationService(coordinator).PrepareAsync(true, stop.Token));
        Assert.Equal(0, calls); AssertStorage(before, Storage(owned));
    }

    [Fact]
    public async Task PreparationRejectsPhysicalRootMismatchBeforeReadingNativeState()
    {
        using var owned = new Fixture(); using var other = new Fixture(); var before = Storage(owned); var native = owned.Native.Read();
        var coordinator = new SelfStartupJournal(owned.Native.Id, Paths(owned), ct => other.Package.Open(token: ct),
            _ => throw new IOException("Preparation must not acquire."));
        var error = await Assert.ThrowsAsync<AgentCommandException>(() => new SelfStartupRegistrationService(coordinator).PrepareAsync(true));
        Assert.Equal("self_context_changed", error.Code); AssertStorage(before, Storage(owned)); Assert.Equal(native, owned.Native.Read()); other.AssertPackageReleased();
    }

    [Theory]
    [InlineData("empty", true, 1)] [InlineData("empty", false, 0)] [InlineData("missing", true, 2)] [InlineData("missing", false, 0)]
    [InlineData("same", true, 0)] [InlineData("same", false, 1)]
    public async Task ExplicitConfirmationRoundTripsThroughTheSameServiceAndReplaysWithoutReexecution(string state, bool enabled, int calls)
    {
        using var owned = new Fixture(); SetState(owned, state); var service = Service(owned); var request = await service.PrepareAsync(enabled);
        Assert.False(request.Confirmed); var envelope = Envelope(request with { Confirmed = true });
        var response = await service.ExecuteAsync(envelope); Assert.True(response.Ok); Assert.False(response.Replayed);
        var data = SelfStartupJournalTests.Data(response); Assert.Equal(calls, data.Calls.Count); Assert.Equal(calls != 0, data.DriverDispatched);
        Assert.Equal(enabled ? SelfStartupNativeState.FromNativeBytes(1, owned.TargetBytes) : new(false), owned.Native.Read().Registration);
        await owned.AssertReleased(); var before = Storage(owned); var native = owned.Native.Read();
        var replayOnly = new SelfStartupRegistrationService(new SelfStartupJournal(owned.Native.Id, Paths(owned),
            _ => throw new IOException("Cached result reopened package."), _ => throw new IOException("Cached result acquired actor.")));
        var replay = await replayOnly.ExecuteAsync(envelope with { Id = "cached" });
        Assert.True(replay.Ok); Assert.True(replay.Replayed); Assert.Equal("cached", replay.Id); AssertStorage(before, Storage(owned)); Assert.Equal(native, owned.Native.Read());
    }

    [Fact]
    public async Task PreparedRequestDoesNotConfirmItselfAndChangedPayloadCannotReuseAdmittedId()
    {
        using var owned = new Fixture(); var service = Service(owned); var request = await service.PrepareAsync(true); var envelope = Envelope(request);
        var response = await service.ExecuteAsync(envelope); Assert.False(response.Ok); Assert.Equal("not_executed", response.ErrorCode);
        Assert.Equal("authorization_required", SelfStartupJournalTests.Data(response).FailureCode); Assert.Empty(SelfStartupJournalTests.Data(response).Calls);
        var conflict = await Assert.ThrowsAsync<AgentCommandException>(() => service.ExecuteAsync(envelope with { Payload = JsonSerializer.SerializeToElement(request with { Confirmed = true }, AgentProtocol.JsonOptions) }));
        Assert.Equal("operation_id_conflict", conflict.Code); Assert.False(owned.Native.Read().Registration.Registered);
    }

    [Theory]
    [InlineData("registration")] [InlineData("context")] [InlineData("asset")]
    public async Task PrepareDoesNotFenceAChangedNativeOrPackageContext(string change)
    {
        using var owned = new Fixture(); var service = Service(owned); var request = (await service.PrepareAsync(true)) with { Confirmed = true };
        if (change == "registration") { owned.Native.Set(1, owned.TargetBytes); }
        if (change == "context") { request = request with { TargetContextId = new string('0', 64) }; }
        if (change == "asset") { File.AppendAllText(owned.Package.Asset("Vigil.exe"), "changed"); }
        var native = owned.Native.Read(); var result = await service.ExecuteAsync(Envelope(request)); Assert.Equal("not_executed", result.ErrorCode);
        var data = SelfStartupJournalTests.Data(result); Assert.Empty(data.Calls); Assert.False(data.DriverDispatched);
        Assert.Equal(change switch { "registration" => "self_registration_changed", "context" => "self_context_changed", _ => "self_target_unavailable" }, data.FailureCode);
        Assert.Equal(native, owned.Native.Read()); owned.AssertPackageReleased();
    }

    [Theory]
    [InlineData("case")] [InlineData("fixture")] [InlineData("root")] [InlineData("version")]
    public async Task PublicEnvelopeRejectsAliasesBeforeEvenCachedReplay(string header)
    {
        using var owned = new Fixture(); var service = Service(owned); var envelope = Envelope((await service.PrepareAsync(true)) with { Confirmed = true });
        Assert.True((await service.ExecuteAsync(envelope)).Ok); var before = Storage(owned); var native = owned.Native.Read();
        var changed = header switch
        {
            "case" => envelope with { Command = SelfStartupRegistrationService.Command.ToUpperInvariant() },
            "fixture" => envelope with { Command = SelfStartupJournal.FixtureCommand },
            "root" => envelope with { RootId = "not-authority" },
            _ => envelope with { ProtocolVersion = 1 }
        };
        var error = await Assert.ThrowsAsync<AgentCommandException>(() => service.ExecuteAsync(changed)); Assert.Equal("invalid_argument", error.Code);
        AssertStorage(before, Storage(owned)); Assert.Equal(native, owned.Native.Read());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AmbientProductionHostRefusesTesthostBeforeAnyNativeMutation(bool enabled)
    {
        using var owned = new Fixture(); var paths = Paths(owned); var service = new SelfStartupRegistrationService(paths);
        var before = Storage(owned); var error = await Assert.ThrowsAsync<AgentCommandException>(() => service.PrepareAsync(enabled));
        Assert.Equal("self_target_unavailable", error.Code); AssertStorage(before, Storage(owned));
        var response = await service.ExecuteAsync(Envelope(owned.Request(enabled))); Assert.Equal("not_executed", response.ErrorCode);
        var data = SelfStartupJournalTests.Data(response); Assert.Equal("self_target_unavailable", data.FailureCode); Assert.False(data.DriverDispatched); Assert.Empty(data.Calls);
        Assert.Null(data.TargetContextId); Assert.True((await service.ExecuteAsync(Envelope(owned.Request(enabled)) with { OperationId = response.OperationId })).Replayed);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task ExplicitSyntheticTargetCannotMintProductionNativeAuthority(bool registered, bool enabled)
    {
        using var owned = new Fixture(); if (registered) { owned.Native.Set(1, owned.TargetBytes); }
        using (var target = owned.Package.Open()) { Assert.False(target.IsCurrentPackage); }
        using var owner = await owned.Open(owned.Request(enabled)); var attempt = new SelfStartupNativeAttempt(); var beforeNative = 0; var before = owned.Native.Read();
        await RunContext(Paths(owned), Envelope(owned.Request(enabled)), context =>
        {
            var error = Assert.Throws<SelfStartupPlanningException>(() => StartupNativeSlotProbe.ApplySelfNativeCurrent(context, Paths(owned), owner, attempt, () => beforeNative++));
            Assert.Equal("self_target_unavailable", error.Reason);
        });
        Assert.False(attempt.MutationCallStarted); Assert.Empty(attempt.Calls); Assert.False(attempt.ProtocolCompleted);
        Assert.Equal(0, beforeNative); Assert.Equal(before, owned.Native.Read());
    }

    [Fact]
    public void PublicServiceHasNoTargetSlotOrEnvironmentInjectionConstructor()
    {
        Assert.NotEqual(SelfStartupRegistrationService.Command, SelfStartupJournal.FixtureCommand);
        var constructors = typeof(SelfStartupRegistrationService).GetConstructors(); Assert.Equal(2, constructors.Length);
        Assert.All(constructors, constructor => Assert.Contains(Assert.Single(constructor.GetParameters()).ParameterType, new[] { typeof(ProjectPaths), typeof(OperationJournal) }));
        using var owned = new Fixture(); Assert.Throws<ArgumentException>(() => new SelfStartupRegistrationService(SelfStartupJournal.ForCurrentPackage(Paths(owned))));
        Assert.Throws<ArgumentException>(() => new SelfStartupJournal(Guid.Empty, Paths(owned), _ => throw new IOException(), _ => throw new IOException()));
    }

    [Theory]
    [InlineData("command")] [InlineData("confirmation")] [InlineData("context")] [InlineData("intent")] [InlineData("root")] [InlineData("expired")] [InlineData("missing")]
    public async Task ProductionNativeGateRequiresMatchingLiveJournalIntentNotJustAPreparedSession(string failure)
    {
        using var owned = new Fixture(); using var other = new Fixture(); var request = owned.Request(); using var owner = await owned.Open(request);
        var envelope = Envelope(request); var attempt = new SelfStartupNativeAttempt(); var native = owned.Native.Read();
        if (failure == "command") { envelope = envelope with { Command = SelfStartupJournal.FixtureCommand }; }
        if (failure == "confirmation") { envelope = Envelope(request with { Confirmed = false }); }
        if (failure == "context") { envelope = Envelope(request with { TargetContextId = new string('0', 64) }); }
        if (failure == "intent") { envelope = Envelope(request with { Enabled = false }); }
        void Verify(OperationExecutionContext context)
        {
            var error = Record.Exception(() => StartupNativeSlotProbe.ApplySelfNativeCurrent(context, Paths(owned), owner, attempt, () => throw new IOException("No dispatch callback.")));
            Assert.NotNull(error);
            if (failure == "root") { Assert.IsType<ArgumentException>(error); }
            else if (failure == "expired") { Assert.IsType<InvalidOperationException>(error); }
            else if (failure == "missing") { Assert.IsType<ArgumentNullException>(error); }
            else { Assert.Equal(failure switch { "command" => "invalid_argument", "confirmation" => "authorization_required", _ => "self_context_changed" }, Assert.IsType<SelfStartupPlanningException>(error).Reason); }
        }
        if (failure == "missing") { Verify(null!); }
        else if (failure == "expired")
        { OperationExecutionContext? historical = null; await RunContext(Paths(owned), envelope, context => historical = context); Assert.NotNull(historical); Verify(historical); }
        else { await RunContext(failure == "root" ? Paths(other) : Paths(owned), envelope, Verify); }
        Assert.Empty(attempt.Calls); Assert.False(attempt.MutationCallStarted); Assert.Equal(native, owned.Native.Read());
    }

    [Fact]
    public async Task BadPublicCommandFaultsTaskWithoutAdmissionOrSynchronousThrow()
    {
        using var owned = new Fixture(); var service = new SelfStartupRegistrationService(Paths(owned)); var before = Storage(owned);
        Task<AgentResponse>? work = null; var error = Record.Exception(() => { work = service.ExecuteAsync(Envelope(owned.Request()) with { Command = SelfStartupJournal.FixtureCommand }); });
        Assert.Null(error); Assert.NotNull(work); var rejected = await Assert.ThrowsAsync<AgentCommandException>(() => work);
        Assert.Equal("invalid_argument", rejected.Code); AssertStorage(before, Storage(owned));
    }

    [Fact]
    public async Task PreparePublishesBoundedPublicRegistryFailureCodeNotRawNativeReason()
    {
        using var owned = new Fixture(); owned.Native.Set(1, new byte[SelfStartupMutationPolicy.MaximumNativeDataBytes + 1]); var before = Storage(owned);
        var error = await Assert.ThrowsAsync<AgentCommandException>(() => Service(owned).PrepareAsync(true));
        Assert.Equal("self_registry_unavailable", error.Code); AssertStorage(before, Storage(owned)); owned.AssertPackageReleased();
    }

    private static async Task RunContext(ProjectPaths paths, AgentRequest envelope, Action<OperationExecutionContext> check)
    {
        Exception? failure = null;
        var result = await new OperationJournal(paths).RunWithContextAsync(envelope, (context, _) =>
        {
            try { check(context); } catch (Exception error) { failure = error; }
            return Task.FromResult(new AgentResponse(context.Request.Id, true));
        });
        if (failure is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
        Assert.True(result.Ok);
    }

    private static SelfStartupRegistrationService Service(Fixture owned) => new(SelfStartupJournalTests.Journal(owned));
    private static ProjectPaths Paths(Fixture owned) => SelfStartupJournalTests.Paths(owned);
    private static AgentRequest Envelope(SelfStartupMutationRequest request) => SelfStartupJournalTests.Envelope(request) with { Command = SelfStartupRegistrationService.Command };
    private static void SetState(Fixture owned, string state)
    { if (state == "missing") { owned.Native.DeleteRun(); } if (state == "same") { owned.Native.Set(1, owned.TargetBytes); } }
    private static Dictionary<string, byte[]> Storage(Fixture owned) => new[] { owned.Package.Root, owned.Admission.Root }
        .SelectMany(root => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        .ToDictionary(path => path, path => Directory.Exists(path) || new FileInfo(path).Length == 0 ? [] : File.ReadAllBytes(path), StringComparer.Ordinal);
    private static void AssertStorage(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    { Assert.Equal(before.Keys.Order(), after.Keys.Order()); foreach (var row in before) { Assert.Equal(row.Value, after[row.Key]); } }
}
