using SystemAutorun.Core.Models;

namespace SystemAutorun.Windows.Startup;

internal static partial class StartupNativeSlotProbe
{
    // Owns target pins + the cooperating actor/slot lock through owner disposal.
    // Integrity/planning only: no publisher authorization, native call, Journal,
    // object identity/CAS, or evidence that any native call actually occurred.
    internal sealed class SelfStartupPlanningSession : IDisposable
    {
        private readonly object _sync = new();
        private readonly SelfStartupTargetLease _target;
        private readonly SelfStartupAdmissionLease _admission;
        private readonly Func<CancellationToken, SelfStartupRawObservation> _read;
        private readonly SelfStartupMutationRequest _request;
        private bool _disposed, _failed, _nativeScope, _disposeRequested;
        internal SelfStartupTargetContext Context { get; }
        internal SelfStartupMutationPlan Plan { get; }
        internal SelfStartupRawObservation Before { get; }

        private SelfStartupPlanningSession(SelfStartupTargetLease target, SelfStartupAdmissionLease admission,
            Func<CancellationToken, SelfStartupRawObservation> read, SelfStartupMutationRequest request,
            SelfStartupRawObservation before, SelfStartupMutationPlan plan)
        { _target = target; _admission = admission; _read = read; _request = request; Before = before; Plan = plan; Context = target.Context; }

        // INTERNAL composition seam: tests supply the real synthetic-package,
        // isolated cooperation and fixed-GUID native primitives, not fake leases.
        internal static async Task<SelfStartupPlanningSession> OpenAsync(SelfStartupMutationRequest request,
            Func<CancellationToken, SelfStartupTargetLease> openTarget,
            Func<CancellationToken, Task<SelfStartupAdmissionLease>> acquire,
            Func<CancellationToken, SelfStartupRawObservation> read, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (SelfStartupMutationPolicy.ValidateRequest(request) is { } error) { throw new SelfStartupPlanningException(error); }
            ArgumentNullException.ThrowIfNull(openTarget); ArgumentNullException.ThrowIfNull(acquire); ArgumentNullException.ThrowIfNull(read);
            var captured = request with { ExpectedRegistration = request.ExpectedRegistration! with { } };
            var target = openTarget(token) ?? throw new InvalidOperationException("Missing self target lease."); SelfStartupAdmissionLease? admission = null;
            try
            {
                StartupHistoryProtection.RequireActor(target.Context.ActorSid); target.VerifyCurrent(token);
                admission = await acquire(token).ConfigureAwait(false) ?? throw new InvalidOperationException("Missing self cooperation lease.");
                // Never use a registration observation taken before the await.
                var before = Observe(target, admission, read, token);
                var decision = SelfStartupMutationPolicy.Evaluate(captured, target.Context.ContextId, target.Context.Command, before.Registration);
                var plan = decision.Plan ?? throw new SelfStartupPlanningException(decision.ErrorCode ?? "self_registration_invalid");
                return new(target, admission, read, captured, before, plan);
            }
            catch
            {
                // Busy, cancellation, arbitrary factory/read exceptions all release
                // returned leases. No exception type here classifies an old write.
                try { admission?.Dispose(); } finally { target.Dispose(); }
                throw;
            }
        }

        // Production native entry requires provenance of the ambient target factory,
        // never a matching context string or an explicit synthetic environment seam.
        internal void RequireCurrentPackage(CancellationToken token)
        {
            lock (_sync)
            {
                RequireUsable(); _target.VerifyCurrent(token);
                if (!_target.IsCurrentPackage) { _failed = true; throw new SelfStartupPlanningException("self_target_unavailable"); }
            }
        }

        internal SelfStartupRawObservation RecheckPrecondition(CancellationToken token = default) => RecheckPreconditionCore(token, null);

        internal SelfStartupRawObservation RecheckAfterOwnedKeyCreate(SelfStartupNativeAttempt attempt, CancellationToken token = default) =>
            RecheckPreconditionCore(token, attempt ?? throw new ArgumentNullException(nameof(attempt)));

        private SelfStartupRawObservation RecheckPreconditionCore(CancellationToken token, SelfStartupNativeAttempt? created)
        {
            lock (_sync)
            {
                RequireUsable();
                try
                {
                    var expectedKey = Before.KeyPresent;
                    if (created is not null)
                    {
                        if (Before.KeyPresent || Plan.Transition != SelfStartupTransition.Create || !created.IsOwnedCreateFor(this))
                        { throw new SelfStartupPlanningException("self_registry_context_changed"); }
                        expectedKey = true;
                    }
                    var current = Observe(_target, _admission, _read, token);
                    if (current.KeyPresent != expectedKey) { throw new SelfStartupPlanningException("self_registry_context_changed"); }
                    var decision = SelfStartupMutationPolicy.Evaluate(_request, Context.ContextId, Context.Command, current.Registration);
                    var fresh = decision.Plan;
                    if (fresh is null || fresh.TargetContextId != Plan.TargetContextId || fresh.Transition != Plan.Transition
                        || fresh.ExpectedBefore != Plan.ExpectedBefore || fresh.ExpectedAfter != Plan.ExpectedAfter)
                    { throw new SelfStartupPlanningException(decision.ErrorCode ?? "self_context_changed"); }
                    return current;
                }
                catch { _failed = true; throw; }
            }
        }

        internal SelfStartupPostconditionObservation CheckPostcondition(CancellationToken token = default) => CheckPostconditionCore(token, null);

        // Read-only diagnostics after a crossed dispatch may run in the SAME live
        // native scope even when a precheck poisoned it. Never revive execution.
        internal SelfStartupPostconditionObservation CheckPostconditionAfterNative(SelfStartupNativeAttempt attempt, CancellationToken token = default) =>
            CheckPostconditionCore(token, attempt ?? throw new ArgumentNullException(nameof(attempt)));

        private SelfStartupPostconditionObservation CheckPostconditionCore(CancellationToken token, SelfStartupNativeAttempt? native)
        {
            lock (_sync)
            {
                if (native is null) { RequireUsable(); }
                else
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (!_nativeScope || !native.IsFor(this) || !native.MutationCallStarted) { throw new InvalidOperationException("Native diagnostics require the same live owner scope."); }
                }
                try
                {
                    var current = Observe(_target, _admission, _read, token);
                    // Delete preserves the key; Create may make a previously absent
                    // key. Matching absent value alone must not hide a removed key.
                    var expectedKey = Before.KeyPresent || Plan.ExpectedAfter.Registered == true;
                    var reason = current.KeyPresent != expectedKey ? "self_registry_context_changed"
                        : SelfStartupMutationPolicy.MatchesPostcondition(Plan, Context.ContextId, current.Registration) ? null : "self_registration_changed";
                    if (reason is not null) { _failed = true; }
                    return new(current, reason);
                }
                catch { _failed = true; throw; }
            }
        }

        internal SelfStartupPostconditionObservation RunNativeCheckScope(Func<SelfStartupPostconditionObservation> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            lock (_sync)
            {
                RequireUsable();
                if (_nativeScope) { _failed = true; throw new InvalidOperationException("A native owner scope cannot be reentered."); }
                _nativeScope = true;
                try { var result = action(); RequireUsable(); return result; }
                catch { _failed = true; throw; }
                finally { _nativeScope = false; if (_disposeRequested) { DisposeCore(); } }
            }
        }

        private static SelfStartupRawObservation Observe(SelfStartupTargetLease target, SelfStartupAdmissionLease admission,
            Func<CancellationToken, SelfStartupRawObservation> read, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); StartupHistoryProtection.RequireActor(target.Context.ActorSid);
            target.VerifyCurrent(token); admission.VerifyCurrent(token);
            if (!string.Equals(target.Context.ActorSid, admission.ActorSid, StringComparison.Ordinal)) { throw new SelfStartupPlanningException("self_context_changed"); }
            var current = read(token);
            if (current is null || !SelfStartupMutationPolicy.ValidState(current.Registration)
                || (!current.KeyPresent && current.Registration.Registered == true)) { throw new SelfStartupPlanningException("self_registration_invalid"); }
            if (!string.Equals(target.Context.ActorSid, current.ActorSid, StringComparison.Ordinal)) { throw new SelfStartupPlanningException("self_context_changed"); }
            target.VerifyCurrent(token); admission.VerifyCurrent(token); StartupHistoryProtection.RequireActor(current.ActorSid);
            token.ThrowIfCancellationRequested(); return current;
        }

        private void RequireUsable()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failed) { throw new InvalidOperationException("A failed self check session cannot be reused."); }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) { return; }
                // Monitor is reentrant: a callback on this thread requests teardown
                // but MUST NOT release protection until its native scope unwinds.
                if (_nativeScope) { _disposeRequested = true; _failed = true; return; }
                DisposeCore();
            }
        }

        private void DisposeCore()
        {
            if (_disposed) { return; } _disposed = true;
            try { _admission.Dispose(); } finally { _target.Dispose(); }
        }
    }
}

internal sealed record SelfStartupPostconditionObservation(SelfStartupRawObservation Current, string? Reason)
{ internal bool Matches => Reason is null; }
// Phase-neutral: the owner, not this type, must determine whether a native call
// was crossed. Never add this type wholesale to a known-no-write whitelist.
internal sealed class SelfStartupPlanningException(string reason) : Exception("Self startup checks refused the context or state.")
{ internal string Reason { get; } = reason; }
