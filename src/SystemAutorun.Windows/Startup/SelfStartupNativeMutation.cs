using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Ipc;

namespace SystemAutorun.Windows.Startup;

internal static partial class StartupNativeSlotProbe
{
    // Fixed production locator, guarded by ambient package provenance before ANY
    // native registry open. No caller-selected path/SID/hive or race callbacks.
    internal static SelfStartupPostconditionObservation ApplySelfNativeCurrent(OperationExecutionContext context, ProjectPaths paths, SelfStartupPlanningSession session,
        SelfStartupNativeAttempt attempt, Action beforeNative, CancellationToken token = default, Action? afterNative = null, Action? afterKeyCreate = null)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(beforeNative);
        // The Core-minted LIVE context proves only Journal admission, not native
        // authority. Require its frozen confirmation intent, bound root AND the
        // ambient package session below; neither context ID nor plan is a grant.
        void RequireIntent()
        {
            context.RequireActiveFor(paths); var envelope = context.Request;
            if (envelope.Command != SelfStartupRegistrationService.Command || envelope.Name is not null || envelope.ExpectedRevision is not null
                || envelope.ProtocolVersion != AgentProtocol.Version || envelope.RootId is not null || envelope.TimeoutMilliseconds is not null)
            { throw new SelfStartupPlanningException("invalid_argument"); }
            var request = SelfStartupMutationContract.ReadRequest(envelope.Payload?.GetRawText() ?? "null");
            var decision = SelfStartupMutationPolicy.Evaluate(request, session.Context.ContextId, session.Context.Command, session.Before.Registration);
            var plan = decision.Plan;
            if (plan is null) { throw new SelfStartupPlanningException(decision.ErrorCode ?? "self_context_changed"); }
            if (session.Context.RootDirectory != PhysicalDirectory.Resolve(paths.RootDirectory) || plan.Transition != session.Plan.Transition
                || plan.ExpectedBefore != session.Plan.ExpectedBefore || plan.ExpectedAfter != session.Plan.ExpectedAfter)
            { throw new SelfStartupPlanningException("self_context_changed"); }
        }
        RequireIntent();
        return ApplySelfNativeFixed(@"Software\Microsoft\Windows\CurrentVersion\Run", true, session, attempt,
            () => { RequireIntent(); beforeNative(); RequireIntent(); }, token,
            afterNative: () => { RequireIntent(); afterNative?.Invoke(); }, dispatchRace: null,
            afterKeyCreate: () => { RequireIntent(); afterKeyCreate?.Invoke(); RequireIntent(); });
    }

    internal static SelfStartupPostconditionObservation ApplySelfNativeFixture(Guid id, SelfStartupPlanningSession session,
        SelfStartupNativeAttempt attempt, Action beforeNative, CancellationToken token = default, Action? afterNative = null,
        Action? dispatchRace = null, Action? afterKeyCreate = null)
    {
        if (id == Guid.Empty) { throw new ArgumentException("An owned GUID fixture is required.", nameof(id)); }
        return ApplySelfNativeFixed(@"Software\SystemAutorun.SelfRaw.Tests." + id.ToString("N") + @"\Parent\Run", false,
            session, attempt, beforeNative, token, afterNative, dispatchRace, afterKeyCreate);
    }

    private static SelfStartupPostconditionObservation ApplySelfNativeFixed(string relative, bool currentPackage, SelfStartupPlanningSession session,
        SelfStartupNativeAttempt attempt, Action beforeNative, CancellationToken token, Action? afterNative, Action? dispatchRace, Action? afterKeyCreate)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(attempt); ArgumentNullException.ThrowIfNull(beforeNative);
        attempt.Claim(session);
        try
        {
            var post = session.RunNativeCheckScope(() =>
            {
                if (currentPackage) { session.RequireCurrentPackage(token); }
                return ApplySelfNativeMutation(relative, session, attempt, beforeNative, token, afterNative, dispatchRace, afterKeyCreate);
            });
            attempt.RecordCompletion(); return post;
        }
        catch (Exception error) { attempt.RecordFailure(error); throw; }
    }

    private static SelfStartupPostconditionObservation ApplySelfNativeMutation(string relative, SelfStartupPlanningSession session,
        SelfStartupNativeAttempt attempt, Action beforeNative, CancellationToken token, Action? afterNative, Action? dispatchRace, Action? afterKeyCreate)
    {
        var before = session.RecheckPrecondition(token); var plan = session.Plan; var actor = session.Context.ActorSid;
        StartupHistoryProtection.RequireActor(actor); var hiveName = @"\REGISTRY\USER\" + actor; var keyName = hiveName + "\\" + relative;
        using var hive = OpenSelfRegistryKey(null, hiveName, out var hiveError);
        RequireSelfRegistryOpen(hive, hiveError); VerifySelfRegistryName(hive, hiveName);
        SafeRegistryHandle? key = null, parent = null;
        try
        {
            Exception? nativeError = null;
            try
            {
                key = OpenSelfRegistryKey(hive, relative, out var keyError, plan.RequiresNativeWrite ? SelfRegistryAccess.ValueMutation : SelfRegistryAccess.Query);
                var present = SelfRegistryPresent(key, keyError);
                if (present != before.KeyPresent) { throw new SelfStartupPlanningException("self_registry_context_changed"); }
                if (present) { VerifySelfHeldValue(key, keyName, plan.ExpectedBefore, actor, token); }
                if (!plan.RequiresNativeWrite)
                {
                    var noop = session.CheckPostcondition(token); if (!noop.Matches) { throw new SelfStartupPlanningException(noop.Reason!); }
                    return noop;
                }
                var bytes = plan.Transition == SelfStartupTransition.Create ? Encoding.Unicode.GetBytes(session.Context.Command + '\0') : [];
                if (plan.Transition == SelfStartupTransition.Create && SelfStartupNativeState.FromNativeBytes(1, bytes) != plan.ExpectedAfter)
                { throw new SelfStartupPlanningException("self_target_unavailable"); }
                if (!present)
                {
                    if (plan.Transition != SelfStartupTransition.Create || !relative.EndsWith(@"\Run", StringComparison.Ordinal))
                    { throw new SelfStartupPlanningException("self_registry_context_changed"); }
                    var parentRelative = relative[..^4]; var parentName = hiveName + "\\" + parentRelative;
                    parent = OpenSelfRegistryKey(hive, parentRelative, out var parentError, SelfRegistryAccess.ChildCreation);
                    RequireSelfRegistryOpen(parent, parentError);
                    VerifySelfCreationParent(hive, parent, parentRelative, parentName, actor, token);
                    // Trusted synchronous callbacks must be bounded and capture the
                    // owner's token. Dispose waits rather than dropping protection.
                    beforeNative(); session.RecheckPrecondition(token);
                    VerifySelfCreationParent(hive, parent, parentRelative, parentName, actor, token);
                    key.Dispose(); key = CreateSelfRegistryRun(parent, attempt, dispatchRace, out var status, out var disposition);
                    attempt.RecordStatus(status, disposition);
                    if (status != 0) { throw SelfNativeCallError(status); }
                    if (disposition != 1) { throw new SelfStartupPlanningException("self_registry_context_changed"); }
                    RequireSelfRegistryOpen(key, 0); VerifySelfCreationParent(hive, parent, parentRelative, parentName, actor, token);
                    VerifySelfHeldValue(key, keyName, plan.ExpectedBefore, actor, token);
                    attempt.RecordCreatedKey(session.RecheckAfterOwnedKeyCreate(attempt, token));
                    afterKeyCreate?.Invoke();
                    session.RecheckAfterOwnedKeyCreate(attempt, token);
                }
                else { beforeNative(); session.RecheckPrecondition(token); }
                VerifySelfRegistryName(hive, hiveName); VerifySelfHeldValue(key, keyName, plan.ExpectedBefore, actor, token);
                dispatchRace?.Invoke(); // GUID-only race seam, once per dispatch.
                WriteSelfRegistryValue(key, session, attempt, bytes);
                afterNative?.Invoke();
            }
            catch (Exception error)
            {
                if (!attempt.MutationCallStarted) { throw; }
                nativeError = error;
            }
            return CollectSelfNativeAfter(hive, key, keyName, session, attempt, nativeError, token);
        }
        finally { try { key?.Dispose(); } finally { parent?.Dispose(); } }
    }

    private static void WriteSelfRegistryValue(SafeRegistryHandle key, SelfStartupPlanningSession session, SelfStartupNativeAttempt attempt, byte[] bytes)
    {
        var text = Marshal.StringToHGlobalUni(UserStartupRegistration.ValueName);
        try
        {
            var name = new UnicodeName { Length = checked((ushort)(UserStartupRegistration.ValueName.Length * 2)),
                MaximumLength = checked((ushort)((UserStartupRegistration.ValueName.Length + 1) * 2)), Buffer = text };
            var create = session.Plan.Transition == SelfStartupTransition.Create;
            attempt.MarkNativeCall(create ? SelfStartupNativeCallKind.SetValue : SelfStartupNativeCallKind.DeleteValue);
            var status = create ? SetSelfRegistryValueNative(key, ref name, 0, 1, bytes, (uint)bytes.Length) : DeleteSelfRegistryValueNative(key, ref name);
            attempt.RecordStatus(status); if (status != 0) { throw SelfNativeCallError(status); }
        }
        finally { Marshal.FreeHGlobal(text); }
    }

    private static SelfStartupPostconditionObservation CollectSelfNativeAfter(SafeRegistryHandle hive, SafeRegistryHandle? key, string keyName,
        SelfStartupPlanningSession session, SelfStartupNativeAttempt attempt, Exception? nativeError, CancellationToken token)
    {
        // Each available AFTER is sampled independently, also on native errors or
        // partial key creation. Never overwrite the originating failure with it.
        Exception? postError = null; SelfStartupNativeState? held = null; SelfStartupPostconditionObservation? post = null;
        try
        {
            token.ThrowIfCancellationRequested(); StartupHistoryProtection.RequireActor(session.Context.ActorSid);
            VerifySelfRegistryName(hive, @"\REGISTRY\USER\" + session.Context.ActorSid);
            if (key is null || key.IsClosed || key.IsInvalid) { throw new SelfStartupPlanningException("self_registry_unavailable"); }
            VerifySelfRegistryName(key, keyName); var observed = ReadSelfRegistryValue(key);
            VerifySelfRegistryName(key, keyName); StartupHistoryProtection.RequireActor(session.Context.ActorSid); token.ThrowIfCancellationRequested(); held = observed;
        }
        catch (Exception error) { postError = error; }
        try { post = session.CheckPostconditionAfterNative(attempt, token); } catch (Exception error) { postError ??= error; }
        if (held is not null && held != session.Plan.ExpectedAfter) { postError ??= new SelfStartupPlanningException("self_registration_changed"); }
        if (post is { Matches: false }) { postError ??= new SelfStartupPlanningException(post.Reason!); }
        // Keep the available post-read cause; the originating failure is also
        // retained separately by the adapter and always wins the thrown result.
        attempt.RecordAfter(held, post, postError ?? nativeError);
        if (nativeError is not null) { ExceptionDispatchInfo.Capture(nativeError).Throw(); }
        if (postError is not null) { ExceptionDispatchInfo.Capture(postError).Throw(); }
        return post!;
    }

    private static Win32Exception SelfNativeCallError(int status) => new(unchecked((int)RtlNtStatusToDosError(status)), "Self native call failed.");
    private static void VerifySelfHeldValue(SafeRegistryHandle key, string keyName, SelfStartupNativeState expected, string actor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); StartupHistoryProtection.RequireActor(actor); VerifySelfRegistryName(key, keyName);
        if (ReadSelfRegistryValue(key) != expected) { throw new SelfStartupPlanningException("self_registration_changed"); }
        VerifySelfRegistryName(key, keyName); StartupHistoryProtection.RequireActor(actor); token.ThrowIfCancellationRequested();
    }

    [DllImport("ntdll.dll", EntryPoint = "NtSetValueKey", ExactSpelling = true)]
    private static extern int SetSelfRegistryValueNative(SafeRegistryHandle key, ref UnicodeName name, uint title, uint type, [In] byte[] data, uint length);
    [DllImport("ntdll.dll", EntryPoint = "NtDeleteValueKey", ExactSpelling = true)]
    private static extern int DeleteSelfRegistryValueNative(SafeRegistryHandle key, ref UnicodeName name);
}

internal enum SelfStartupNativeCallKind { CreateKey, SetValue, DeleteValue }
internal sealed record SelfStartupNativeCall(SelfStartupNativeCallKind Kind, int? Status, uint? Disposition = null);
// In-memory evidence, consumed after return/throw, not a durable receipt or CAS.
internal sealed class SelfStartupNativeAttempt
{
    private int _used, _called, _returned, _status;
    private object? _owner;
    private readonly List<SelfStartupNativeCall> _calls = [];
    internal bool MutationCallStarted => Volatile.Read(ref _called) != 0;
    internal int NativeCallCount => Volatile.Read(ref _called);
    internal int? NativeStatus => Volatile.Read(ref _returned) == 0 ? null : _status;
    internal IReadOnlyList<SelfStartupNativeCall> Calls => _calls.ToArray();
    internal SelfStartupNativeState? HeldAfter { get; private set; }
    internal SelfStartupPostconditionObservation? FreshAfter { get; private set; }
    internal Exception? PostcheckError { get; private set; }
    internal SelfStartupRawObservation? CreatedKeyAfter { get; private set; }
    internal bool ProtocolCompleted { get; private set; }
    internal Exception? Failure { get; private set; }
    internal void RecordCreatedKey(SelfStartupRawObservation current) { CreatedKeyAfter = current; }
    internal void RecordCompletion() { ProtocolCompleted = true; }
    internal void RecordFailure(Exception error) { Failure = error; }
    internal bool IsFor(object owner) => ReferenceEquals(_owner, owner);
    internal void Claim(object owner) { if (Interlocked.Exchange(ref _used, 1) != 0) { throw new InvalidOperationException("A self native attempt cannot be reused."); } _owner = owner; }
    internal bool IsOwnedCreateFor(object owner) => ReferenceEquals(_owner, owner) && _calls.Count == 1
        && _calls[0] is { Kind: SelfStartupNativeCallKind.CreateKey, Status: 0, Disposition: 1 };
    internal void MarkNativeCall(SelfStartupNativeCallKind kind)
    {
        if (_calls.Count > 1 || (_calls.Count == 1 && (!IsOwnedCreateFor(_owner!) || kind != SelfStartupNativeCallKind.SetValue)))
        { throw new InvalidOperationException("Only create-key then set-value may dispatch twice."); }
        _calls.Add(new(kind, null)); Volatile.Write(ref _returned, 0); Volatile.Write(ref _called, _calls.Count);
    }
    internal void RecordStatus(int status, uint? disposition = null)
    { _calls[^1] = _calls[^1] with { Status = status, Disposition = disposition }; _status = status; Volatile.Write(ref _returned, 1); }
    internal void RecordAfter(SelfStartupNativeState? held, SelfStartupPostconditionObservation? fresh, Exception? error)
    { HeldAfter = held; FreshAfter = fresh; PostcheckError = error; }
}
