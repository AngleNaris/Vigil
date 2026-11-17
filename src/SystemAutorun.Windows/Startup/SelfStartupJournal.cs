using System.ComponentModel;
using System.Text.Json;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using TargetLease = SystemAutorun.Windows.Startup.StartupNativeSlotProbe.SelfStartupTargetLease;
using AdmissionLease = SystemAutorun.Windows.Startup.StartupNativeSlotProbe.SelfStartupAdmissionLease;
using SelfStartupPlanningSession = SystemAutorun.Windows.Startup.StartupNativeSlotProbe.SelfStartupPlanningSession;

namespace SystemAutorun.Windows.Startup;

internal sealed class SelfStartupJournal
{
    internal const string FixtureCommand = "self-native-fixture";
    private readonly Guid? _fixtureId;
    internal bool IsFixture => _fixtureId is not null;
    internal string InstanceId => _journal.InstanceId;
    private readonly ProjectPaths _paths;
    private readonly OperationJournal _journal;
    private readonly Func<CancellationToken, TargetLease> _openTarget;
    private readonly Func<CancellationToken, Task<AdmissionLease>> _acquireAdmission;

    // Bind the journal namespace, fixed native slot and server factories ONCE.
    // No independently supplied journal/root or per-request target selection.
    internal SelfStartupJournal(Guid id, ProjectPaths paths, Func<CancellationToken, TargetLease> openTarget,
        Func<CancellationToken, Task<AdmissionLease>> acquireAdmission, OperationJournalLimits? limits = null)
        : this((Guid?)id, new OperationJournal(paths, limits: limits), openTarget, acquireAdmission) { }

    internal SelfStartupJournal(Guid id, OperationJournal journal, Func<CancellationToken, TargetLease> openTarget,
        Func<CancellationToken, Task<AdmissionLease>> acquireAdmission)
        : this((Guid?)id, journal, openTarget, acquireAdmission) { }

    private SelfStartupJournal(Guid? fixtureId, OperationJournal journal, Func<CancellationToken, TargetLease> openTarget,
        Func<CancellationToken, Task<AdmissionLease>> acquireAdmission)
    {
        if (fixtureId == Guid.Empty) { throw new ArgumentException("An owned GUID fixture is required.", nameof(fixtureId)); }
        ArgumentNullException.ThrowIfNull(journal); ArgumentNullException.ThrowIfNull(openTarget); ArgumentNullException.ThrowIfNull(acquireAdmission);
        _fixtureId = fixtureId; _paths = journal.Paths; _openTarget = openTarget; _acquireAdmission = acquireAdmission; _journal = journal;
    }

    // G1-self-package-admission-r1: current running published App/Agent package
    // plus explicit per-operation confirmation. Integrity, NOT publisher attestation.
    // This private production constructor has no target/native/storage override.
    internal static SelfStartupJournal ForCurrentPackage(ProjectPaths paths) => ForCurrentPackage(new OperationJournal(paths));
    internal static SelfStartupJournal ForCurrentPackage(OperationJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return new(null, journal, ct => StartupNativeSlotProbe.OpenSelfStartupTarget(journal.Paths, ct), ct => AdmissionLease.AcquireAsync(ct));
    }

    private SelfStartupRawObservation ReadNative(CancellationToken token) => _fixtureId is { } id
        ? StartupNativeSlotProbe.ReadSelfNativeFixture(id, token) : StartupNativeSlotProbe.ReadSelfNativeState(token);

    // Pure read-only preparation: no admission lease, Journal or metadata writes.
    // Hypothetical confirmation is ONLY an eligibility test; return never confirms.
    internal Task<SelfStartupMutationRequest> PrepareAsync(bool enabled, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested(); using var target = _openTarget(token);
        RequireTargetRoot(target.Context.RootDirectory); target.VerifyCurrent(token);
        var raw = ReadNative(token);
        if (!string.Equals(raw.ActorSid, target.Context.ActorSid, StringComparison.Ordinal)) { throw new SelfStartupPlanningException("self_context_changed"); }
        StartupHistoryProtection.RequireActor(raw.ActorSid); target.VerifyCurrent(token); token.ThrowIfCancellationRequested();
        var request = new SelfStartupMutationRequest { SchemaVersion = SelfStartupMutationContract.SchemaVersion, Enabled = enabled, Confirmed = false,
            TargetContextId = target.Context.ContextId, ExpectedRegistration = raw.Registration };
        var decision = SelfStartupMutationPolicy.Evaluate(request with { Confirmed = true }, target.Context.ContextId, target.Context.Command, raw.Registration);
        if (decision.Plan is null) { throw new SelfStartupPlanningException(decision.ErrorCode ?? "self_registration_invalid"); }
        return request;
    }, token);

    private void RequireTargetRoot(string root)
    {
        if (!string.Equals(root, PhysicalDirectory.Resolve(_paths.RootDirectory), StringComparison.Ordinal))
        { throw new SelfStartupPlanningException("self_context_changed"); }
    }

    internal Task<AgentResponse> RunFixtureAsync(AgentRequest envelope, Action<string, OperationExecutionContext>? checkpoint = null, CancellationToken token = default)
    {
        if (_fixtureId is null) { throw new InvalidOperationException("A production coordinator is not a fixture."); }
        return RunAsync(envelope, checkpoint, token);
    }

    // Factories/checkpoint are trusted server seams, NOT request callbacks. Running
    // is only the existing durable admission fence, not a per-native-stage log.
    internal async Task<AgentResponse> RunAsync(AgentRequest envelope,
        Action<string, OperationExecutionContext>? checkpoint = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        // The shared fingerprint excludes transport headers and normalizes command
        // case. Validate the fixed envelope BEFORE even a cached replay.
        if (envelope.Command != (_fixtureId is null ? SelfStartupRegistrationService.Command : FixtureCommand) || envelope.Name is not null || envelope.ExpectedRevision is not null
            || envelope.ProtocolVersion != AgentProtocol.Version || envelope.RootId is not null || envelope.TimeoutMilliseconds is not null)
        { throw new AgentCommandException("invalid_argument", "Unsupported self registration envelope."); }
        SelfStartupPlanningSession? owner = null; OperationExecutionContext? accepted = null;
        var attempt = new SelfStartupNativeAttempt(); var paths = _paths; var journal = _journal;
        try
        {
            var response = await journal.RunWithContextAsync(envelope, async (context, ct) =>
            {
                accepted = context;
                try
                {
                    context.RequireActiveFor(paths); checkpoint?.Invoke("accepted", context);
                    var input = context.Request;
                    var request = SelfStartupMutationContract.ReadRequest(input.Payload?.GetRawText() ?? "null");
                    var refusal = SelfStartupMutationPolicy.ValidateRequest(request);
                    if (refusal is not null) { throw new SelfStartupPlanningException(refusal); }
                    context.RequireActiveFor(paths);
                    owner = await SelfStartupPlanningSession.OpenAsync(request, _openTarget, _acquireAdmission, ReadNative, ct).ConfigureAwait(false);
                    context.RequireActiveFor(paths); RequireTargetRoot(owner.Context.RootDirectory);
                    checkpoint?.Invoke("planned", context);
                    void BeforeNative() { context.RequireActiveFor(paths); checkpoint?.Invoke("before_native", context); }
                    void AfterNative() { context.RequireActiveFor(paths); checkpoint?.Invoke("native_returned", context); }
                    void AfterKeyCreate() { context.RequireActiveFor(paths); checkpoint?.Invoke("key_created", context); }
                    if (_fixtureId is { } id) { StartupNativeSlotProbe.ApplySelfNativeFixture(id, owner, attempt, BeforeNative, ct, afterNative: AfterNative, afterKeyCreate: AfterKeyCreate); }
                    else { StartupNativeSlotProbe.ApplySelfNativeCurrent(context, paths, owner, attempt, BeforeNative, ct, afterNative: AfterNative, afterKeyCreate: AfterKeyCreate); }
                    context.RequireActiveFor(paths); checkpoint?.Invoke("native_verified", context);
                    var data = SelfJournalData(owner, attempt, null); checkpoint?.Invoke("callback_returning", context);
                    return new(input.Id, true, data);
                }
                catch (Exception error)
                {
                    // Same error/code can precede or follow a native call. Never
                    // extend OperationJournal's global no-write whitelist.
                    return new(context.Request.Id, false, SelfJournalData(owner, attempt, error),
                        Error: attempt.MutationCallStarted ? "Self operation may have changed Windows state; inspect, do not replay." : "Self driver did not dispatch a native mutation.",
                        ErrorCode: attempt.MutationCallStarted ? "outcome_unknown" : "not_executed");
                }
            }, token).ConfigureAwait(false);
            if (accepted is not null)
            {
                // Owner survives callback End, final publication AND readback.
                // No native scope/sync monitor is held while acquiring root lease.
                try
                {
                    checkpoint?.Invoke("receipt_readback", accepted);
                    var receipt = await journal.GetAsync(accepted.OperationId, CancellationToken.None).ConfigureAwait(false);
                    var expectedState = response.Ok || response.ErrorCode == "not_executed" ? OperationState.Completed : OperationState.Unknown;
                    var expected = receipt?.ResultOmitted == true ? response with { Data = null } : response;
                    if (receipt is null || receipt.OwnerInstanceId != accepted.OwnerInstanceId || receipt.Fingerprint != accepted.Fingerprint
                        || receipt.State != expectedState || receipt.Response is null
                        || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected, AgentProtocol.JsonOptions), JsonSerializer.SerializeToElement(receipt.Response, AgentProtocol.JsonOptions)))
                    { throw new IOException("Self operation receipt readback did not match the published result."); }
                    checkpoint?.Invoke("receipt_verified", accepted);
                }
                catch (Exception error) { throw new AgentOutcomeUnknownException(accepted.OperationId, error, envelope.Id); }
            }
            return response;
        }
        finally { owner?.Dispose(); }
    }

    private static JsonElement SelfJournalData(SelfStartupPlanningSession? owner, SelfStartupNativeAttempt attempt, Exception? failure)
    {
        static SelfJournalState? State(SelfStartupRawObservation? value) => value is null ? null : new(value.KeyPresent, value.Registration);
        var data = new SelfJournalEvidence(owner?.Context.ContextId, owner?.Plan.Transition, attempt.ProtocolCompleted,
            attempt.MutationCallStarted, attempt.Calls, State(attempt.CreatedKeyAfter), attempt.HeldAfter,
            State(attempt.FreshAfter?.Current), attempt.FreshAfter?.Reason, SelfJournalFailure(failure), (failure as Win32Exception)?.NativeErrorCode);
        return JsonSerializer.SerializeToElement(data, AgentProtocol.JsonOptions);
    }
    private static string? SelfJournalFailure(Exception? error) => error switch
    {
        null => null,
        SelfStartupPlanningException refusal => refusal.Reason,
        JsonException => "invalid_argument",
        OperationCanceledException => "cancelled",
        SelfStartupTargetException => "self_target_unavailable",
        SelfStartupAdmissionBusyException => "configuration_busy",
        SelfStartupAdmissionException => "self_coordinator_unavailable",
        SelfStartupNativeReadException => "self_registry_unavailable",
        Win32Exception => "native_error",
        _ => "execution_failed"
    };
}

// Internal bounded receipt DATA, not a public request/authority schema. No raw
// command, package path, actor SID, exception text or exception whitelist.
internal sealed record SelfJournalState(bool KeyPresent, SelfStartupNativeState Registration);
internal sealed record SelfJournalEvidence(string? TargetContextId, SelfStartupTransition? Transition, bool ProtocolCompleted,
    bool DriverDispatched, IReadOnlyList<SelfStartupNativeCall> Calls, SelfJournalState? CreatedKeyAfter,
    SelfStartupNativeState? HeldAfter, SelfJournalState? FreshAfter, string? PostconditionReason, string? FailureCode, int? NativeErrorCode);
