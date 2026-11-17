using System.ComponentModel;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;
using Fixture = SystemAutorun.IntegrationTests.SelfStartupPlanningSessionTests.Fixture;

namespace SystemAutorun.IntegrationTests;

public sealed class SelfStartupNativeMutationTests
{
    [Theory]
    [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)] [InlineData(false, false)]
    public async Task ExistingKeySetDeleteAndNoopsPreserveOtherValuesAndUseExactPostcondition(bool before, bool enabled)
    {
        using var owned = new Fixture();
        if (before) { owned.Native.Set(1, owned.TargetBytes); }
        using (var key = Registry.CurrentUser.OpenSubKey(owned.Native.RunPath, true)!) { key.SetValue("OwnedOtherValue", new byte[] { 7, 8 }, RegistryValueKind.Binary); }
        using var session = await owned.Open(owned.Request(enabled)); var attempt = new SelfStartupNativeAttempt(); var callbacks = 0;
        var post = StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => callbacks++);
        Assert.True(post.Matches); Assert.Equal(session.Plan.ExpectedAfter, post.Current.Registration);
        Assert.Equal(before != enabled, attempt.MutationCallStarted); Assert.Equal(before != enabled ? 1 : 0, callbacks);
        if (before != enabled) { Assert.Equal(0, attempt.NativeStatus); Assert.Equal(session.Plan.ExpectedAfter, attempt.HeldAfter); Assert.True(attempt.FreshAfter!.Matches); }
        else { Assert.Null(attempt.NativeStatus); }
        Assert.Null(attempt.PostcheckError);
        using (var key = Registry.CurrentUser.OpenSubKey(owned.Native.RunPath)!)
        { Assert.Equal(new byte[] { 7, 8 }, Assert.IsType<byte[]>(key.GetValue("OwnedOtherValue"))); Assert.Equal(0, key.SubKeyCount); }
        Assert.Throws<InvalidOperationException>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => callbacks++));
        Assert.Equal(before != enabled ? 1 : 0, callbacks); session.Dispose(); await owned.AssertReleased();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingKeyCreateNowComposesWhileAbsentNoopRemainsReadonly(bool enabled)
    {
        using var owned = new Fixture(); owned.Native.DeleteRun(); using var session = await owned.Open(owned.Request(enabled));
        var attempt = new SelfStartupNativeAttempt(); var calls = 0;
        Assert.True(StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => calls++).Matches);
        Assert.Equal(enabled, attempt.MutationCallStarted); Assert.Equal(enabled ? 1 : 0, calls);
        Assert.Equal(enabled, owned.Native.Read().KeyPresent); Assert.Equal(enabled ? 2 : 0, attempt.NativeCallCount);
        if (enabled) { Assert.Equal(SelfStartupNativeCallKind.CreateKey, attempt.Calls[0].Kind); Assert.Equal(1u, attempt.Calls[0].Disposition); }
        session.Dispose(); await owned.AssertReleased();
    }

    [Fact]
    public async Task EmptyGuidCannotSelectOrConsumeAnAttempt()
    {
        using var owned = new Fixture(); using var session = await owned.Open(owned.Request(false)); var attempt = new SelfStartupNativeAttempt();
        Assert.Throws<ArgumentException>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(Guid.Empty, session, attempt, () => throw new InvalidOperationException()));
        Assert.True(StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => throw new InvalidOperationException()).Matches);
        Assert.False(attempt.MutationCallStarted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SameIoFaultIsPhaseNeutralAndActualCallFlagDistinguishesBeforeFromAfter(bool after)
    {
        using var owned = new Fixture(); using var session = await owned.Open(); var attempt = new SelfStartupNativeAttempt(); var calls = 0;
        var sentinel = new IOException("Owned phase fault.");
        var error = Assert.Throws<IOException>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt,
            () => { calls++; if (!after) { throw sentinel; } }, afterNative: () => throw sentinel));
        Assert.Same(sentinel, error); Assert.Equal(1, calls); Assert.Equal(after, attempt.MutationCallStarted);
        if (after) { Assert.Equal(0, attempt.NativeStatus); Assert.Same(sentinel, attempt.PostcheckError); Assert.True(attempt.FreshAfter!.Matches); Assert.Equal(session.Plan.ExpectedAfter, attempt.HeldAfter); }
        else { Assert.Null(attempt.NativeStatus); Assert.Null(attempt.FreshAfter); }
        Assert.Equal(after ? session.Plan.ExpectedAfter : session.Plan.ExpectedBefore, owned.Native.Read().Registration);
        Assert.Throws<InvalidOperationException>(() => session.RecheckPrecondition());
        await owned.AssertHeld(); session.Dispose(); await owned.AssertReleased();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationBeforeOrAfterCallNeverRollsBackAndCannotReuseSession(bool after)
    {
        using var owned = new Fixture(); using var stop = new CancellationTokenSource(); using var session = await owned.Open(); var attempt = new SelfStartupNativeAttempt();
        Assert.ThrowsAny<OperationCanceledException>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt,
            () => { if (!after) { stop.Cancel(); } }, stop.Token, () => { if (after) { stop.Cancel(); } }));
        Assert.Equal(after, attempt.MutationCallStarted); Assert.Equal(after ? session.Plan.ExpectedAfter : session.Plan.ExpectedBefore, owned.Native.Read().Registration);
        Assert.Throws<InvalidOperationException>(() => session.RecheckPrecondition()); session.Dispose(); await owned.AssertReleased();
    }

    [Fact]
    public async Task PreCancellationNeverNotifiesOrDispatches()
    {
        using var owned = new Fixture(); using var session = await owned.Open(); using var stop = new CancellationTokenSource(); stop.Cancel();
        var attempt = new SelfStartupNativeAttempt(); var calls = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => calls++, stop.Token));
        Assert.False(attempt.MutationCallStarted); Assert.Equal(0, calls); Assert.False(owned.Native.Read().Registration.Registered);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SameValueDriftReasonBeforeAndAfterHasDifferentNativeEvidence(bool after)
    {
        using var owned = new Fixture(); using var session = await owned.Open(); var attempt = new SelfStartupNativeAttempt();
        void Drift() => owned.Native.Set(3, [4]);
        var error = Assert.Throws<SelfStartupPlanningException>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt,
            () => { if (!after) { Drift(); } }, afterNative: () => { if (after) { Drift(); } }));
        Assert.Equal("self_registration_changed", error.Reason); Assert.Equal(after, attempt.MutationCallStarted);
        Assert.Equal(SelfStartupNativeState.FromNativeBytes(3, [4]), owned.Native.Read().Registration);
        Assert.Throws<InvalidOperationException>(() => session.RecheckPrecondition());
    }

    [Fact]
    public async Task RecreatedNamespaceWithEqualAbsentDataCannotRedirectTheHeldWriterKey()
    {
        using var owned = new Fixture(); using var session = await owned.Open(); var attempt = new SelfStartupNativeAttempt();
        var error = Assert.Throws<Win32Exception>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () =>
        { owned.Native.DeleteRun(); owned.Native.Set(3, [1]); owned.DeleteValue(); }));
        Assert.Equal(1018, error.NativeErrorCode); Assert.False(attempt.MutationCallStarted);
        Assert.True(owned.Native.Read().KeyPresent); Assert.False(owned.Native.Read().Registration.Registered);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealNativeErrorRetainsStatusAndFreshPostObservationWithoutRollback(bool enabled)
    {
        using var owned = new Fixture(); if (!enabled) { owned.Native.Set(1, owned.TargetBytes); }
        using var session = await owned.Open(owned.Request(enabled)); var attempt = new SelfStartupNativeAttempt();
        var error = Assert.Throws<Win32Exception>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt,
            () => { }, dispatchRace: owned.Native.DeleteRun));
        Assert.Equal(1018, error.NativeErrorCode); Assert.True(attempt.MutationCallStarted);
        Assert.NotNull(attempt.NativeStatus); Assert.NotEqual(0, attempt.NativeStatus); Assert.Null(attempt.HeldAfter);
        Assert.Equal(1018, Assert.IsType<Win32Exception>(attempt.PostcheckError).NativeErrorCode);
        Assert.False(attempt.FreshAfter!.Matches); Assert.False(attempt.FreshAfter.Current.KeyPresent);
        Assert.Equal("self_registry_context_changed", attempt.FreshAfter.Reason);
        Assert.False(owned.Native.Read().KeyPresent); Assert.Throws<InvalidOperationException>(() => session.RecheckPrecondition());
        session.Dispose(); await owned.AssertReleased();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SetValueDenyAllowsReadonlyNoopButBlocksMutationBeforeNotification(bool enabled)
    {
        using var owned = new Fixture(); using var session = await owned.Open(owned.Request(enabled));
        using var key = Registry.CurrentUser.OpenSubKey(owned.Native.RunPath, RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ReadPermissions | RegistryRights.ChangePermissions)!;
        var stable = new RegistrySecurity(); stable.SetSecurityDescriptorBinaryForm(key.GetAccessControl().GetSecurityDescriptorBinaryForm(), AccessControlSections.Access); key.SetAccessControl(stable);
        var original = key.GetAccessControl(); var bytes = original.GetSecurityDescriptorBinaryForm(); var sddl = original.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var attempt = new SelfStartupNativeAttempt(); var calls = 0;
        try
        {
            var denied = new RegistrySecurity(); denied.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access);
            denied.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(StartupHistoryProtection.Actor()), RegistryRights.SetValue, AccessControlType.Deny)); key.SetAccessControl(denied);
            if (enabled)
            {
                var error = Assert.Throws<Win32Exception>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => calls++));
                Assert.Equal(5, error.NativeErrorCode);
            }
            else { Assert.True(StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => calls++).Matches); }
            Assert.False(attempt.MutationCallStarted); Assert.Equal(0, calls);
        }
        finally
        {
            var restore = new RegistrySecurity(); restore.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access); key.SetAccessControl(restore);
            Assert.Equal(sddl, key.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
        session.Dispose(); await owned.AssertReleased();
    }

    [Fact]
    public async Task OwnerDisposeCannotReleaseProtectionWhileSynchronousNativeScopeIsActive()
    {
        using var owned = new Fixture(); using var session = await owned.Open(); var attempt = new SelfStartupNativeAttempt();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var disposing = new ManualResetEventSlim();
        var mutation = Task.Run(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () =>
        { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Owned callback gate timed out."); } }));
        Task? dispose = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            dispose = Task.Run(() => { disposing.Set(); session.Dispose(); }); Assert.True(disposing.Wait(TimeSpan.FromSeconds(5)));
            await owned.AssertHeld(); Assert.False(dispose.IsCompleted);
        }
        finally { release.Set(); await mutation; if (dispose is not null) { await dispose; } }
        Assert.True(attempt.MutationCallStarted); await owned.AssertReleased();
    }

    [Fact]
    public async Task ConcurrentApplicationsCannotDispatchTwiceFromOneInitialPlan()
    {
        using var owned = new Fixture(); using var session = await owned.Open(); var attempts = new[] { new SelfStartupNativeAttempt(), new SelfStartupNativeAttempt() };
        var results = await Task.WhenAll(attempts.Select(attempt => Task.Run(() =>
        {
            try { return StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => { }).Matches; }
            catch (SelfStartupPlanningException error) { Assert.Equal("self_registration_changed", error.Reason); return false; }
        })));
        Assert.Single(results, value => value); Assert.Single(attempts, attempt => attempt.MutationCallStarted);
        Assert.Equal(session.Plan.ExpectedAfter, owned.Native.Read().Registration); session.Dispose(); await owned.AssertReleased();
    }
}
