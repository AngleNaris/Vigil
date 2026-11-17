using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;
using PackageFixture = SystemAutorun.IntegrationTests.SelfStartupTargetTests.PackageFixture;
using AdmissionFixture = SystemAutorun.IntegrationTests.StartupMutationAdapterTests.AdmissionFixture;
using NativeFixture = SystemAutorun.IntegrationTests.SelfStartupNativeReadTests.NativeFixture;
using Planning = SystemAutorun.Windows.Startup.StartupNativeSlotProbe.SelfStartupPlanningSession;
using Admission = SystemAutorun.Windows.Startup.StartupNativeSlotProbe.SelfStartupAdmissionLease;

namespace SystemAutorun.IntegrationTests;

public sealed class SelfStartupPlanningSessionTests
{
    [Theory]
    [InlineData(false, true, true, SelfStartupTransition.Create)]
    [InlineData(false, false, true, SelfStartupTransition.VerifyAbsent)]
    [InlineData(true, true, true, SelfStartupTransition.VerifyEnabled)]
    [InlineData(true, false, true, SelfStartupTransition.Delete)]
    [InlineData(false, true, false, SelfStartupTransition.Create)]
    [InlineData(false, false, false, SelfStartupTransition.VerifyAbsent)]
    public async Task RealPrimitivesComposeAcrossFourTransitionsWithoutAProductionWriter(bool before, bool enabled, bool keyPresent, SelfStartupTransition transition)
    {
        using var owned = new Fixture();
        if (!keyPresent) { owned.Native.DeleteRun(); }
        if (before) { owned.Native.Set(1, owned.TargetBytes); }
        using var session = await owned.Open(owned.Request(enabled));
        Assert.Equal(transition, session.Plan.Transition); Assert.Equal(keyPresent, session.Before.KeyPresent);
        Assert.Equal(owned.Context, session.Context); Assert.Equal(session.Before, session.RecheckPrecondition());
        await owned.AssertHeld();
        // The TEST, not the session, applies the plan in its independent GUID key.
        if (session.Plan.RequiresNativeWrite) { if (enabled) { owned.Native.Set(1, owned.TargetBytes); } else { owned.DeleteValue(); } }
        var post = session.CheckPostcondition(); Assert.True(post.Matches); Assert.Null(post.Reason);
        Assert.Equal(session.Plan.ExpectedAfter, post.Current.Registration);
        session.Dispose(); session.Dispose(); await owned.AssertReleased();
        Assert.Throws<ObjectDisposedException>(() => session.RecheckPrecondition());
        Assert.Throws<ObjectDisposedException>(() => session.CheckPostcondition());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task InvalidOrUnconfirmedRequestNeverCallsAnyFactory(int kind)
    {
        var request = new SelfStartupMutationRequest { SchemaVersion = 1, Enabled = true, Confirmed = true,
            TargetContextId = new string('a', 64), ExpectedRegistration = new(false) };
        request = kind switch { 0 => null!, 1 => request with { SchemaVersion = 2 }, 2 => request with { Confirmed = false },
            3 => request with { ExpectedRegistration = new(true) }, _ => request with { TargetContextId = "not-a-context" } };
        var calls = 0;
        var error = await Assert.ThrowsAsync<SelfStartupPlanningException>(() => Planning.OpenAsync(request,
            _ => { calls++; throw new InvalidOperationException(); }, _ => { calls++; throw new InvalidOperationException(); },
            _ => { calls++; throw new InvalidOperationException(); }));
        Assert.Equal(kind == 2 ? "authorization_required" : "invalid_argument", error.Reason); Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BusyOrCancelledCoordinatorReleasesAlreadyOpenedPackage(bool cancel)
    {
        using var owned = new Fixture(); using var held = await Admission.AcquireAsync(owned.Admission.Scope);
        using var stop = new CancellationTokenSource(); var reads = 0;
        if (cancel) { stop.CancelAfter(TimeSpan.FromMilliseconds(40)); }
        var operation = owned.Open(token: stop.Token, reader: ct => { reads++; return owned.Read(ct); },
            acquire: ct => Admission.AcquireAsync(owned.Admission.Scope, ct, TimeSpan.FromMilliseconds(100)));
        if (cancel) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); }
        else { await Assert.ThrowsAsync<SelfStartupAdmissionBusyException>(() => operation); }
        Assert.Equal(0, reads); owned.AssertPackageReleased();
        held.Dispose(); await owned.AssertReleased();
    }

    [Fact]
    public async Task TargetActorMustBeActualPrimaryActorBeforeCoordinatorOrNativeRead()
    {
        using var owned = new Fixture(); var calls = 0;
        await Assert.ThrowsAsync<CryptographicException>(() => owned.Open(
            open: ct => owned.Package.Open(actor: () => "S-1-0-0", token: ct),
            acquire: _ => { calls++; throw new InvalidOperationException(); }, reader: _ => { calls++; throw new InvalidOperationException(); }));
        Assert.Equal(0, calls); Assert.Empty(Directory.GetFileSystemEntries(owned.Admission.Root)); await owned.AssertReleased();
    }

    [Fact]
    public async Task InvalidPackageCannotMaterializeCoordinationScaffolding()
    {
        using var owned = new Fixture(); var calls = 0;
        File.WriteAllText(owned.Package.ManifestPath, "{}");
        await Assert.ThrowsAsync<SelfStartupTargetException>(() => owned.Open(acquire: _ => { calls++; throw new InvalidOperationException(); }));
        Assert.Equal(0, calls); Assert.Empty(Directory.GetFileSystemEntries(owned.Admission.Root)); owned.AssertPackageReleased();
    }

    [Theory]
    [InlineData(0, "self_context_changed")]
    [InlineData(1, "self_registration_invalid")]
    [InlineData(2, "self_context_changed")]
    [InlineData(3, "self_registration_changed")]
    [InlineData(4, "self_registration_conflict")]
    public async Task ActorMetadataContextExpectedStateAndForeignValueRefuseAndRelease(int kind, string reason)
    {
        using var owned = new Fixture();
        if (kind == 4) { owned.Native.Set(3, [9]); }
        var request = owned.Request();
        if (kind == 2) { request = request with { TargetContextId = new string('b', 64) }; }
        if (kind == 3) { request = request with { ExpectedRegistration = SelfStartupNativeState.FromNativeBytes(3, [8]) }; }
        var error = await Assert.ThrowsAsync<SelfStartupPlanningException>(() => owned.Open(request, reader: ct =>
        {
            var current = owned.Read(ct);
            return kind switch { 0 => current with { ActorSid = "S-1-0-0" },
                1 => current with { KeyPresent = false, Registration = SelfStartupNativeState.FromNativeBytes(1, [1]) }, _ => current };
        }));
        Assert.Equal(reason, error.Reason); await owned.AssertReleased();
        Assert.Equal(kind == 4, owned.Native.Read().Registration.Registered);
    }

    [Fact]
    public async Task ArbitraryReadFailureIsNotReclassifiedAndBothLeasesRelease()
    {
        using var owned = new Fixture(); var sentinel = new IOException("Owned native-read fault.");
        var actual = await Assert.ThrowsAsync<IOException>(() => owned.Open(reader: _ => throw sentinel));
        Assert.Same(sentinel, actual); await owned.AssertReleased();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DriftPoisonsPrecheckEvenIfPrivateStateIsRepaired(bool keyDrift)
    {
        using var owned = new Fixture(); using var session = await owned.Open();
        if (keyDrift) { owned.Native.DeleteRun(); } else { owned.Native.Set(3, [4]); }
        var error = Assert.Throws<SelfStartupPlanningException>(() => session.RecheckPrecondition());
        Assert.Equal(keyDrift ? "self_registry_context_changed" : "self_registration_changed", error.Reason);
        owned.Native.Set(3, [4]); owned.DeleteValue(); // restore absent VALUE, but no blind reuse.
        Assert.Throws<InvalidOperationException>(() => session.RecheckPrecondition());
        Assert.Throws<InvalidOperationException>(() => session.CheckPostcondition());
        await owned.AssertHeld(); session.Dispose(); await owned.AssertReleased();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedPostcheckReturnsObservationAndNeverRollsBackOrPermitsReuse(bool keyDrift)
    {
        using var owned = new Fixture();
        if (keyDrift) { owned.Native.Set(1, owned.TargetBytes); }
        using var session = await owned.Open(owned.Request(!keyDrift)); session.RecheckPrecondition();
        if (keyDrift) { owned.Native.DeleteRun(); } // absent value alone must not verify Delete.
        // Otherwise no apply was performed: create's expected postcondition is false.
        var post = session.CheckPostcondition(); Assert.False(post.Matches);
        Assert.Equal(keyDrift ? "self_registry_context_changed" : "self_registration_changed", post.Reason);
        Assert.Equal(owned.Native.Read(), post.Current);
        Assert.Throws<InvalidOperationException>(() => session.RecheckPrecondition());
        await owned.AssertHeld(); session.Dispose(); await owned.AssertReleased();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PostReadFaultOrCancellationStaysPhaseNeutral(bool cancel)
    {
        using var owned = new Fixture(); using var stop = new CancellationTokenSource(); var fail = false;
        var sentinel = new IOException("Owned post-read failure.");
        using var session = await owned.Open(reader: ct => fail ? throw sentinel : owned.Read(ct));
        session.RecheckPrecondition(); owned.Native.Set(1, owned.TargetBytes); fail = true;
        if (cancel) { stop.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => session.CheckPostcondition(stop.Token)); }
        else { Assert.Same(sentinel, Assert.Throws<IOException>(() => session.CheckPostcondition())); }
        Assert.Equal(session.Plan.ExpectedAfter, owned.Native.Read().Registration); // no rollback.
        Assert.Throws<InvalidOperationException>(() => session.RecheckPrecondition());
        session.Dispose(); await owned.AssertReleased();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TargetAndCoordinatorAreReverifiedAfterAwaitBeforeAnyNativeRead(bool scopeDrift)
    {
        using var owned = new Fixture(); var actor = StartupHistoryProtection.Actor(); var drift = false; var reads = 0;
        SelfStartupAdmissionScope Scope() => drift && scopeDrift ? owned.Admission.Scope() with { LocalAppData = owned.Admission.Root + "-not-created" } : owned.Admission.Scope();
        async Task<Admission> Acquire(CancellationToken ct)
        {
            var lease = await Admission.AcquireAsync(Scope, ct); drift = true;
            if (!scopeDrift) { actor = "S-1-0-0"; }
            return lease;
        }
        var operation = owned.Open(open: ct => owned.Package.Open(actor: () => actor, token: ct), acquire: Acquire,
            reader: ct => { reads++; return owned.Read(ct); });
        if (scopeDrift) { await Assert.ThrowsAsync<SelfStartupAdmissionException>(() => operation); }
        else { await Assert.ThrowsAsync<SelfStartupTargetException>(() => operation); }
        Assert.Equal(0, reads); await owned.AssertReleased();
    }

    [Fact]
    public async Task SuccessfulConcurrentRechecksSerializeRealPackageStreams()
    {
        using var owned = new Fixture(); using var session = await owned.Open();
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => session.RecheckPrecondition())));
        Assert.All(snapshots, current => Assert.Equal(session.Before, current)); session.Dispose(); await owned.AssertReleased();
    }

    [Fact]
    public async Task PreCancelledSessionCallsNoFactory()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel(); var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Planning.OpenAsync(null!,
            _ => { calls++; throw new InvalidOperationException(); }, _ => { calls++; throw new InvalidOperationException(); },
            _ => { calls++; throw new InvalidOperationException(); }, stop.Token)); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task TwoRootSessionsReallyShareCoordinatorAndSecondCanProceedOnlyAfterRelease()
    {
        using var first = new Fixture(); using var second = new Fixture();
        using var one = await first.Open(); var secondReads = 0;
        var request = second.Request();
        await Assert.ThrowsAsync<SelfStartupAdmissionBusyException>(() => second.Open(request,
            acquire: ct => Admission.AcquireAsync(first.Admission.Scope, ct, TimeSpan.FromMilliseconds(40)),
            reader: ct => { secondReads++; return first.Read(ct); }));
        Assert.Equal(0, secondReads); second.AssertPackageReleased(); one.Dispose();
        using var two = await second.Open(request, acquire: ct => Admission.AcquireAsync(first.Admission.Scope, ct), reader: first.Read);
        Assert.Equal(second.Context.RootDirectory, two.Context.RootDirectory); two.RecheckPrecondition();
        two.Dispose(); await first.AssertReleased(); await second.AssertReleased();
    }

    internal sealed class Fixture : IDisposable
    {
        internal PackageFixture Package { get; }
        internal AdmissionFixture Admission { get; }
        internal NativeFixture Native { get; }
        internal SelfStartupTargetContext Context { get; }
        internal byte[] TargetBytes => Encoding.Unicode.GetBytes(Context.Command + '\0');
        internal Fixture()
        {
            PackageFixture? package = null; AdmissionFixture? admission = null; NativeFixture? native = null;
            try
            {
                Package = package = new(); Admission = admission = new(); Native = native = new();
                using var preview = Package.Open(); Context = preview.Context;
            }
            catch { try { native?.Dispose(); } finally { try { admission?.Dispose(); } finally { package?.Dispose(); } } throw; }
        }
        internal SelfStartupMutationRequest Request(bool enabled = true) => new() { SchemaVersion = 1, Enabled = enabled, Confirmed = true,
            TargetContextId = Context.ContextId, ExpectedRegistration = Native.Read().Registration };
        internal SelfStartupRawObservation Read(CancellationToken token) => StartupNativeSlotProbe.ReadSelfNativeFixture(Native.Id, token);
        internal Task<Planning> Open(SelfStartupMutationRequest? request = null, CancellationToken token = default,
            Func<CancellationToken, SelfStartupRawObservation>? reader = null,
            Func<CancellationToken, Task<Admission>>? acquire = null,
            Func<CancellationToken, StartupNativeSlotProbe.SelfStartupTargetLease>? open = null) =>
            Planning.OpenAsync(request ?? Request(), open ?? (ct => Package.Open(token: ct)),
                acquire ?? (ct => SystemAutorun.Windows.Startup.StartupNativeSlotProbe.SelfStartupAdmissionLease.AcquireAsync(Admission.Scope, ct)), reader ?? Read, token);
        internal void DeleteValue() { using var key = Registry.CurrentUser.OpenSubKey(Native.RunPath, true)!; key.DeleteValue(UserStartupRegistration.ValueName, false); }
        internal void AssertPackageReleased() { using var exclusive = File.Open(Package.Asset("Vigil.exe"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        internal async Task AssertReleased()
        {
            AssertPackageReleased(); using var lease = await SystemAutorun.Windows.Startup.StartupNativeSlotProbe.SelfStartupAdmissionLease.AcquireAsync(Admission.Scope, timeout: TimeSpan.FromMilliseconds(100));
        }
        internal async Task AssertHeld()
        {
            Assert.Throws<IOException>(() => { using var exclusive = File.Open(Package.Asset("Vigil.exe"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
            await Assert.ThrowsAsync<SelfStartupAdmissionBusyException>(() => SystemAutorun.Windows.Startup.StartupNativeSlotProbe.SelfStartupAdmissionLease.AcquireAsync(Admission.Scope, timeout: TimeSpan.FromMilliseconds(40)));
        }
        public void Dispose() { try { Native.Dispose(); } finally { try { Admission.Dispose(); } finally { Package.Dispose(); } } }
    }
}
