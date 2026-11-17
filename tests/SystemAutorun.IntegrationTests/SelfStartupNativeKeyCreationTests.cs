using System.ComponentModel;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Native = SystemAutorun.IntegrationTests.SelfStartupNativeReadTests.Native;
using Microsoft.Win32;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;
using Fixture = SystemAutorun.IntegrationTests.SelfStartupPlanningSessionTests.Fixture;

namespace SystemAutorun.IntegrationTests;

public sealed class SelfStartupNativeKeyCreationTests
{
    private static string Parent(Fixture owned) => owned.Native.RunPath[..^4];

    [Fact]
    public async Task OnlyLiteralChildAndFixedValueAreCreatedWithTwoRecordedStatuses()
    {
        using var owned = new Fixture(); owned.Native.DeleteRun();
        using (var parent = Registry.CurrentUser.OpenSubKey(Parent(owned), true)!)
        { parent.SetValue("OwnedParentValue", 23); using var sibling = parent.CreateSubKey("OwnedSibling"); sibling.SetValue("Keep", "yes"); }
        using var session = await owned.Open(); var attempt = new SelfStartupNativeAttempt(); var notifications = 0; var dispatches = 0;
        var post = StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => notifications++, dispatchRace: () => dispatches++);
        Assert.True(post.Matches); Assert.True(attempt.ProtocolCompleted); Assert.Null(attempt.Failure);
        Assert.True(attempt.CreatedKeyAfter!.KeyPresent); Assert.False(attempt.CreatedKeyAfter.Registration.Registered);
        Assert.Equal(1, notifications); Assert.Equal(2, dispatches); Assert.Equal(2, attempt.NativeCallCount);
        Assert.Equal(new[] { SelfStartupNativeCallKind.CreateKey, SelfStartupNativeCallKind.SetValue }, attempt.Calls.Select(call => call.Kind));
        Assert.All(attempt.Calls, call => Assert.Equal(0, call.Status)); Assert.Equal(1u, attempt.Calls[0].Disposition); Assert.Null(attempt.Calls[1].Disposition);
        Assert.Equal(session.Plan.ExpectedAfter, owned.Native.Read().Registration); Assert.Null(attempt.PostcheckError);
        using (var parent = Registry.CurrentUser.OpenSubKey(Parent(owned))!)
        { Assert.Equal(23, parent.GetValue("OwnedParentValue")); Assert.Equal(new[] { "OwnedSibling", "Run" }, parent.GetSubKeyNames().OrderBy(name => name)); }
        session.Dispose(); await owned.AssertReleased();
    }

    [Fact]
    public async Task MissingParentIsNotRecursivelyCreated()
    {
        using var owned = new Fixture(); Registry.CurrentUser.DeleteSubKeyTree(Parent(owned));
        using var session = await owned.Open(); var attempt = new SelfStartupNativeAttempt(); var notified = 0;
        var error = Assert.Throws<Win32Exception>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => notified++));
        Assert.Equal(2, error.NativeErrorCode); Assert.False(attempt.MutationCallStarted); Assert.Equal(0, notified);
        Assert.Null(Registry.CurrentUser.OpenSubKey(Parent(owned))); session.Dispose(); await owned.AssertReleased();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailureOrCancellationBeforeCreateLeavesNoKey(bool cancel)
    {
        using var owned = new Fixture(); owned.Native.DeleteRun(); using var stop = new CancellationTokenSource(); using var session = await owned.Open();
        var attempt = new SelfStartupNativeAttempt(); var sentinel = new IOException("Owned before-create fault.");
        void Before() { if (cancel) { stop.Cancel(); } else { throw sentinel; } }
        if (cancel) { Assert.ThrowsAny<OperationCanceledException>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, Before, stop.Token)); }
        else { Assert.Same(sentinel, Assert.Throws<IOException>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, Before))); }
        Assert.False(attempt.MutationCallStarted); Assert.Empty(attempt.Calls); Assert.False(owned.Native.Read().KeyPresent);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailureOrCancellationAfterCreateDoesNotRemoveThePartialKey(bool cancel)
    {
        using var owned = new Fixture(); owned.Native.DeleteRun(); using var stop = new CancellationTokenSource(); using var session = await owned.Open();
        var attempt = new SelfStartupNativeAttempt(); var sentinel = new IOException("Owned intermediate fault.");
        void AfterCreate() { if (cancel) { stop.Cancel(); } else { throw sentinel; } }
        if (cancel) { Assert.ThrowsAny<OperationCanceledException>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => { }, stop.Token, afterKeyCreate: AfterCreate)); }
        else { Assert.Same(sentinel, Assert.Throws<IOException>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => { }, afterKeyCreate: AfterCreate))); }
        var call = Assert.Single(attempt.Calls); Assert.Equal(SelfStartupNativeCallKind.CreateKey, call.Kind); Assert.Equal(0, call.Status); Assert.Equal(1u, call.Disposition);
        Assert.True(attempt.MutationCallStarted); Assert.True(owned.Native.Read().KeyPresent); Assert.False(owned.Native.Read().Registration.Registered);
        if (!cancel) { Assert.Equal(new(false), attempt.HeldAfter); Assert.False(attempt.FreshAfter!.Matches); Assert.Equal("self_registration_changed", attempt.FreshAfter.Reason); }
        Assert.Throws<InvalidOperationException>(() => session.RecheckPrecondition()); await owned.AssertHeld(); session.Dispose(); await owned.AssertReleased();
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task OpenedExistingDispositionNeverTakesOverEvenEmptyOrCanonicalRacedKey(int valueKind)
    {
        using var owned = new Fixture(); owned.Native.DeleteRun(); using var session = await owned.Open(); var attempt = new SelfStartupNativeAttempt();
        var error = Assert.Throws<SelfStartupPlanningException>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => { }, dispatchRace: () =>
        { if (valueKind == 2) { owned.Native.Set(1, owned.TargetBytes); } else { owned.Native.Set(3, [9]); if (valueKind == 0) { owned.DeleteValue(); } } }));
        Assert.Equal("self_registry_context_changed", error.Reason); var call = Assert.Single(attempt.Calls);
        Assert.Equal(SelfStartupNativeCallKind.CreateKey, call.Kind); Assert.Equal(0, call.Status); Assert.Equal(2u, call.Disposition);
        var expected = valueKind == 2 ? session.Plan.ExpectedAfter : valueKind == 1 ? SelfStartupNativeState.FromNativeBytes(3, [9]) : new(false);
        Assert.Equal(expected, owned.Native.Read().Registration); Assert.True(owned.Native.Read().KeyPresent); Assert.True(attempt.MutationCallStarted);
        Assert.False(attempt.ProtocolCompleted); Assert.Same(error, attempt.Failure); Assert.Null(attempt.CreatedKeyAfter);
        if (valueKind == 2) { Assert.Equal(expected, attempt.HeldAfter); Assert.True(attempt.FreshAfter!.Matches); } // State match is NOT ownership/success.
    }

    [Fact]
    public async Task ReplacedParentWithSameNameIsRejectedBeforeNativeCreation()
    {
        using var owned = new Fixture(); owned.Native.DeleteRun(); using var session = await owned.Open(); var attempt = new SelfStartupNativeAttempt();
        var error = Assert.Throws<Win32Exception>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () =>
        { Registry.CurrentUser.DeleteSubKeyTree(Parent(owned)); using var replacement = Registry.CurrentUser.CreateSubKey(Parent(owned)); }));
        Assert.Equal(1018, error.NativeErrorCode); Assert.False(attempt.MutationCallStarted); Assert.False(owned.Native.Read().KeyPresent);
    }

    [Fact]
    public async Task DeletedParentInLastRaceWindowProducesRealCreateErrorAndFreshEvidence()
    {
        using var owned = new Fixture(); owned.Native.DeleteRun(); using var session = await owned.Open(); var attempt = new SelfStartupNativeAttempt();
        var error = Assert.Throws<Win32Exception>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => { },
            dispatchRace: () => Registry.CurrentUser.DeleteSubKeyTree(Parent(owned))));
        Assert.Equal(1018, error.NativeErrorCode); var call = Assert.Single(attempt.Calls); Assert.Equal(SelfStartupNativeCallKind.CreateKey, call.Kind);
        Assert.NotEqual(0, call.Status); Assert.True(attempt.MutationCallStarted); Assert.False(attempt.FreshAfter!.Current.KeyPresent);
        Assert.False(owned.Native.Read().KeyPresent); Assert.Null(Registry.CurrentUser.OpenSubKey(Parent(owned)));
    }

    [Fact]
    public async Task SecondNativeErrorRetainsFirstCreateSuccessAndDoesNotRecreate()
    {
        using var owned = new Fixture(); owned.Native.DeleteRun(); using var session = await owned.Open(); var attempt = new SelfStartupNativeAttempt(); var dispatches = 0;
        var error = Assert.Throws<Win32Exception>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => { }, dispatchRace: () =>
        { if (++dispatches == 2) { owned.Native.DeleteRun(); } }));
        Assert.Equal(1018, error.NativeErrorCode); Assert.Equal(2, attempt.NativeCallCount);
        Assert.Equal(0, attempt.Calls[0].Status); Assert.Equal(1u, attempt.Calls[0].Disposition); Assert.NotEqual(0, attempt.Calls[1].Status);
        Assert.Equal(SelfStartupNativeCallKind.SetValue, attempt.Calls[1].Kind); Assert.False(attempt.FreshAfter!.Current.KeyPresent);
        Assert.False(owned.Native.Read().KeyPresent); Assert.Throws<InvalidOperationException>(() => session.RecheckPrecondition());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task IntermediateForeignValueOrEqualAbsentReplacementCannotReachSet(bool replaceKey)
    {
        using var owned = new Fixture(); owned.Native.DeleteRun(); using var session = await owned.Open(); var attempt = new SelfStartupNativeAttempt();
        void Drift() { if (replaceKey) { owned.Native.DeleteRun(); } owned.Native.Set(3, [6]); if (replaceKey) { owned.DeleteValue(); } }
        if (replaceKey)
        {
            var error = Assert.Throws<Win32Exception>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => { }, afterKeyCreate: Drift));
            Assert.Equal(1018, error.NativeErrorCode);
        }
        else
        {
            var error = Assert.Throws<SelfStartupPlanningException>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => { }, afterKeyCreate: Drift));
            Assert.Equal("self_registration_changed", error.Reason);
        }
        Assert.Single(attempt.Calls); Assert.True(owned.Native.Read().KeyPresent);
        Assert.Equal(replaceKey ? new(false) : SelfStartupNativeState.FromNativeBytes(3, [6]), owned.Native.Read().Registration);
        Assert.False(attempt.ProtocolCompleted); Assert.NotNull(attempt.Failure); Assert.False(attempt.FreshAfter!.Matches);
        Assert.Equal(owned.Native.Read().Registration, attempt.FreshAfter.Current.Registration);
        Assert.Throws<InvalidOperationException>(() => session.CheckPostconditionAfterNative(attempt));
    }

    [Fact]
    public async Task AnotherSessionsActualCreateEvidenceCannotAdvanceThisSession()
    {
        using var first = new Fixture(); using var second = new Fixture(); first.Native.DeleteRun(); second.Native.DeleteRun();
        using var one = await first.Open(); using var two = await second.Open(); var attempt = new SelfStartupNativeAttempt();
        Assert.Throws<IOException>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(first.Native.Id, one, attempt, () => { }, afterKeyCreate: () => throw new IOException("Owned stop after create.")));
        var error = Assert.Throws<SelfStartupPlanningException>(() => two.RecheckAfterOwnedKeyCreate(attempt));
        Assert.Equal("self_registry_context_changed", error.Reason); Assert.False(second.Native.Read().KeyPresent);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RacedRegistryLinkCannotRedirectCreateToExistingOrMissingOwnedTarget(bool targetExists)
    {
        using var owned = new Fixture(); owned.Native.DeleteRun(); using var session = await owned.Open(); var attempt = new SelfStartupNativeAttempt();
        var targetPath = owned.Native.Path + @"\OwnedLinkTarget"; var nativeRoot = @"\Registry\User\" + StartupHistoryProtection.Actor() + "\\" + owned.Native.Path;
        if (targetExists) { using var target = Registry.CurrentUser.CreateSubKey(targetPath); Native.SetRaw(target.Handle, 3, [27]); }
        SafeRegistryHandle? symbolic = null;
        try
        {
            var error = Assert.Throws<Win32Exception>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => { }, dispatchRace: () =>
            {
                using var parent = Registry.CurrentUser.OpenSubKey(Parent(owned), true)!;
                var code = Native.RegCreateKeyExW(parent.Handle, "Run", 0, null, 2, 0x10103, IntPtr.Zero, out var created, out var disposition); symbolic = created;
                Native.Check(code); Assert.Equal(1u, disposition); var data = Encoding.Unicode.GetBytes(nativeRoot + @"\OwnedLinkTarget");
                Native.Check(Native.RegSetValueExW(symbolic, "SymbolicLinkValue", 0, 6, data, (uint)data.Length));
            }));
            Assert.Equal(4395, error.NativeErrorCode); var call = Assert.Single(attempt.Calls); Assert.Equal(SelfStartupNativeCallKind.CreateKey, call.Kind);
            Assert.NotEqual(0, call.Status); Assert.True(attempt.MutationCallStarted);
        }
        finally
        {
            // Delete the exact CREATED LINK HANDLE before any recursive fixture
            // cleanup; never recurse while the link can redirect the test root.
            if (symbolic is not null) { try { if (!symbolic.IsInvalid) { Assert.Equal(0, Native.NtDeleteKey(symbolic)); } } finally { symbolic.Dispose(); } }
            using var root = Registry.CurrentUser.OpenSubKey(owned.Native.Path)!; Native.RequireName(root.Handle, nativeRoot);
        }
        using var remaining = Registry.CurrentUser.OpenSubKey(targetPath); Assert.Equal(targetExists, remaining is not null);
        if (remaining is not null) { Assert.Equal(new byte[] { 27 }, Assert.IsType<byte[]>(remaining.GetValue(UserStartupRegistration.ValueName))); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ParentCreateDenyDoesNotBroadenReadonlyNoopRights(bool enabled)
    {
        using var owned = new Fixture(); owned.Native.DeleteRun(); using var session = await owned.Open(owned.Request(enabled));
        using var parent = Registry.CurrentUser.OpenSubKey(Parent(owned), RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadPermissions | RegistryRights.ChangePermissions)!;
        var stable = new RegistrySecurity(); stable.SetSecurityDescriptorBinaryForm(parent.GetAccessControl().GetSecurityDescriptorBinaryForm(), AccessControlSections.Access); parent.SetAccessControl(stable);
        var bytes = parent.GetAccessControl().GetSecurityDescriptorBinaryForm(); var sddl = parent.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var attempt = new SelfStartupNativeAttempt(); var notified = 0;
        try
        {
            var denied = new RegistrySecurity(); denied.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access);
            denied.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(StartupHistoryProtection.Actor()), RegistryRights.CreateSubKey, AccessControlType.Deny)); parent.SetAccessControl(denied);
            if (enabled)
            {
                var error = Assert.Throws<Win32Exception>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => notified++)); Assert.Equal(5, error.NativeErrorCode);
            }
            else { Assert.True(StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => notified++).Matches); }
            Assert.False(attempt.MutationCallStarted); Assert.Equal(0, notified); Assert.False(owned.Native.Read().KeyPresent);
        }
        finally
        {
            var restore = new RegistrySecurity(); restore.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access); parent.SetAccessControl(restore);
            Assert.Equal(sddl, parent.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SameThreadDisposeDefersProtectionReleaseUntilNativeScopeUnwinds(bool afterCreate)
    {
        using var owned = new Fixture(); owned.Native.DeleteRun(); using var session = await owned.Open(); var attempt = new SelfStartupNativeAttempt(); var calls = 0;
        void DisposeInside() { calls++; session.Dispose(); owned.AssertHeld().GetAwaiter().GetResult(); }
        var error = await Task.Run(() => Assert.Throws<InvalidOperationException>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt,
            () => { if (!afterCreate) { DisposeInside(); } }, afterKeyCreate: () => { if (afterCreate) { DisposeInside(); } })));
        Assert.Equal(1, calls); Assert.Equal(afterCreate ? 1 : 0, attempt.NativeCallCount); Assert.Equal(afterCreate, owned.Native.Read().KeyPresent);
        Assert.False(attempt.ProtocolCompleted); Assert.Same(error, attempt.Failure); Assert.Throws<ObjectDisposedException>(() => session.RecheckPrecondition());
        if (afterCreate) { Assert.NotNull(attempt.CreatedKeyAfter); Assert.True(attempt.FreshAfter!.Current.KeyPresent); }
        await owned.AssertReleased();
    }

    [Fact]
    public async Task ReentrantDriverCannotDispatchOrReviveTheOuterScope()
    {
        using var owned = new Fixture(); owned.Native.DeleteRun(); using var session = await owned.Open(); var outer = new SelfStartupNativeAttempt(); var inner = new SelfStartupNativeAttempt();
        Assert.Throws<InvalidOperationException>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, outer, () =>
        { Assert.Throws<InvalidOperationException>(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, inner, () => { })); }));
        Assert.Empty(outer.Calls); Assert.Empty(inner.Calls); Assert.NotNull(outer.Failure); Assert.NotNull(inner.Failure); Assert.False(owned.Native.Read().KeyPresent);
        await owned.AssertHeld(); session.Dispose(); await owned.AssertReleased();
    }

    [Fact]
    public async Task DisposeStillWaitsAcrossTheIntermediateCreatedKeyWindow()
    {
        using var owned = new Fixture(); owned.Native.DeleteRun(); using var session = await owned.Open(); var attempt = new SelfStartupNativeAttempt();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var disposing = new ManualResetEventSlim();
        var mutation = Task.Run(() => StartupNativeSlotProbe.ApplySelfNativeFixture(owned.Native.Id, session, attempt, () => { }, afterKeyCreate: () =>
        { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Owned intermediate gate timed out."); } }));
        Task? dispose = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); Assert.Equal(1, attempt.NativeCallCount); Assert.False(owned.Native.Read().Registration.Registered);
            Assert.True(attempt.CreatedKeyAfter!.KeyPresent); Assert.False(attempt.CreatedKeyAfter.Registration.Registered);
            dispose = Task.Run(() => { disposing.Set(); session.Dispose(); }); Assert.True(disposing.Wait(TimeSpan.FromSeconds(5)));
            await owned.AssertHeld(); Assert.False(dispose.IsCompleted);
        }
        finally { release.Set(); await mutation; if (dispose is not null) { await dispose; } }
        Assert.Equal(2, attempt.NativeCallCount); await owned.AssertReleased();
    }
}
