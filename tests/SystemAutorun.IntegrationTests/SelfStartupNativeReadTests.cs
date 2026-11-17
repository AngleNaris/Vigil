using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;
using Xunit.Abstractions;

namespace SystemAutorun.IntegrationTests;

public sealed class SelfStartupNativeReadTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0u)] [InlineData(1u)] [InlineData(2u)] [InlineData(3u)] [InlineData(4u)] [InlineData(7u)] [InlineData(11u)]
    public void RawTypesAndBytesAreNotDecodedExpandedTrimmedOrExecuted(uint type)
    {
        using var owned = new NativeFixture(); byte[] bytes = [0x25, 0, 0, 0, 0, 0, 0x00, 0xd8, 0x41];
        owned.Set(type, bytes); var result = owned.Read();
        Assert.True(result.KeyPresent); Assert.Equal(StartupHistoryProtection.Actor(), result.ActorSid);
        Assert.Equal(SelfStartupNativeState.FromNativeBytes(type, bytes), result.Registration);
        Assert.Equal(result, owned.Read());
    }

    [Theory]
    [InlineData(1u, 0)] [InlineData(1u, 1)] [InlineData(1u, 2)] [InlineData(2u, 1)] [InlineData(3u, 1)]
    public void CompleteRawFingerprintFeedsSharedPolicyWithoutLosingTerminalBytes(uint type, int nulCount)
    {
        using var owned = new NativeFixture();
        const string command = "\"C:\\owned data\\Vigil.exe\" --background --root \"C:\\owned data\\根\"";
        var bytes = Encoding.Unicode.GetBytes(command + new string('\0', nulCount)); owned.Set(type, bytes);
        var state = owned.Read().Registration; Assert.Equal(SelfStartupNativeState.FromNativeBytes(type, bytes), state);
        var context = new string('a', 64); var decision = SelfStartupMutationPolicy.Evaluate(new SelfStartupMutationRequest
        { SchemaVersion = 1, Enabled = true, Confirmed = true, TargetContextId = context, ExpectedRegistration = state }, context, command, state);
        if (type == 1 && nulCount == 1) { Assert.Equal(SelfStartupTransition.VerifyEnabled, decision.Plan?.Transition); Assert.False(decision.Plan!.RequiresNativeWrite); }
        else { Assert.Null(decision.Plan); Assert.Equal("self_registration_conflict", decision.ErrorCode); }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(-1)] [InlineData(-2)]
    public void ExactNativeByteBoundsAreNeverTruncated(int size)
    {
        using var owned = new NativeFixture(); var length = size < 0 ? SelfStartupMutationPolicy.MaximumNativeDataBytes + size + 1 : size;
        var bytes = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray(); owned.Set(3, bytes);
        Assert.Equal(SelfStartupNativeState.FromNativeBytes(3, bytes), owned.Read().Registration);
    }

    [Fact]
    public void OverBudgetValueRefusesAndIsNotModified()
    {
        using var owned = new NativeFixture(); var bytes = new byte[SelfStartupMutationPolicy.MaximumNativeDataBytes + 1]; owned.Set(3, bytes);
        Assert.Equal("self_registration_too_large", Assert.Throws<SelfStartupNativeReadException>(() => owned.Read()).Reason);
        using var key = Registry.CurrentUser.OpenSubKey(owned.RunPath)!; Assert.Equal(bytes, Assert.IsType<byte[]>(key.GetValue(UserStartupRegistration.ValueName)));
    }

    [Fact]
    public void MissingKeyAndMissingValueRemainDistinctAndReadCreatesNothing()
    {
        var id = Guid.NewGuid(); var path = NativeFixture.PathFor(id);
        Assert.Null(Registry.CurrentUser.OpenSubKey(path)); var missing = StartupNativeSlotProbe.ReadSelfNativeFixture(id);
        Assert.False(missing.KeyPresent); Assert.Equal(new SelfStartupNativeState(false), missing.Registration); Assert.Null(Registry.CurrentUser.OpenSubKey(path));
        using var owned = new NativeFixture(); var empty = owned.Read(); Assert.True(empty.KeyPresent); Assert.Equal(missing.Registration, empty.Registration);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void ValueAndKeyDriftRefuseInsteadOfPublishingAbsentOrAStableSnapshot(int change)
    {
        using var owned = new NativeFixture();
        if (change != 3) { owned.Set(3, [1, 2]); }
        if (change == 6) { owned.DeleteRun(); }
        var error = Assert.Throws<SelfStartupNativeReadException>(() => owned.Read(() =>
        {
            switch (change)
            {
                case 0: owned.Set(3, [1, 3]); break;
                case 1: owned.Set(0, [1, 2]); break;
                case 2: using (var key = Registry.CurrentUser.OpenSubKey(owned.RunPath, true)!) { key.DeleteValue(UserStartupRegistration.ValueName); } break;
                case 3: owned.Set(3, [1, 2]); break;
                case 4: owned.DeleteRun(); break;
                case 5: using (var root = Registry.CurrentUser.OpenSubKey(owned.Path, true)!) { Native.Check(Native.RegRenameKey(root.Handle, @"Parent\Run", "Moved")); } break;
                case 6: using (Registry.CurrentUser.CreateSubKey(owned.RunPath)) { } break;
                case 7: owned.Set(3, new byte[SelfStartupMutationPolicy.MaximumNativeDataBytes + 1]); break;
            }
        }));
        output.WriteLine($"Drift={change}; Reason={error.Reason}; Native={(error.InnerException as Win32Exception)?.NativeErrorCode}");
        if (change == 4)
        {
            Assert.Equal("self_registry_unavailable", error.Reason);
            Assert.Equal(1018, Assert.IsType<Win32Exception>(error.InnerException).NativeErrorCode);
        }
        else { Assert.Equal(change switch { 5 => "self_registry_context_changed", 7 => "self_registration_too_large", _ => "self_registration_changed" }, error.Reason); }
    }

    [Fact]
    public void CancellationAndInvalidFixtureDoNotCreateAKey()
    {
        var id = Guid.NewGuid(); using var token = new CancellationTokenSource(); token.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => StartupNativeSlotProbe.ReadSelfNativeFixture(id, token.Token));
        Assert.Throws<ArgumentException>(() => StartupNativeSlotProbe.ReadSelfNativeFixture(Guid.Empty)); Assert.Null(Registry.CurrentUser.OpenSubKey(NativeFixture.PathFor(id)));
        using var owned = new NativeFixture(); using var later = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => StartupNativeSlotProbe.ReadSelfNativeFixture(owned.Id, later.Token, later.Cancel));
    }

    [Fact]
    public void EvenSameSidImpersonationCannotBorrowPrimaryActorAuthority()
    {
        using var owned = new NativeFixture(); using var identity = WindowsIdentity.GetCurrent();
        WindowsIdentity.RunImpersonated(identity.AccessToken, () =>
        {
            var error = Assert.Throws<SelfStartupNativeReadException>(() => owned.Read()); Assert.IsType<CryptographicException>(error.InnerException);
        });
        Assert.True(owned.Read().KeyPresent);
    }

    [Fact]
    public void QueryAccessDeniedIsNotAbsenceAndDaclIsRestoredExactly()
    {
        using var owned = new NativeFixture(); using var key = Registry.CurrentUser.OpenSubKey(owned.RunPath, RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ReadKey | RegistryRights.WriteKey | RegistryRights.ChangePermissions)!;
        // Prime the equivalent setter form before recording the rollback image:
        // Windows may add the auto-inherited control bit on the first DACL set.
        var stable = new RegistrySecurity(); stable.SetSecurityDescriptorBinaryForm(key.GetAccessControl().GetSecurityDescriptorBinaryForm(), AccessControlSections.Access); key.SetAccessControl(stable);
        var original = key.GetAccessControl(); var binary = original.GetSecurityDescriptorBinaryForm(); var sddl = original.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var changed = key.GetAccessControl(); changed.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(StartupHistoryProtection.Actor()), RegistryRights.QueryValues, AccessControlType.Deny));
        try
        {
            key.SetAccessControl(changed); var weak = key.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            var error = Assert.Throws<SelfStartupNativeReadException>(() => owned.Read()); Assert.Equal(5, Assert.IsType<Win32Exception>(error.InnerException).NativeErrorCode);
            Assert.Equal(weak, key.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
        finally
        {
            var restore = new RegistrySecurity(); restore.SetSecurityDescriptorBinaryForm(binary, AccessControlSections.Access); key.SetAccessControl(restore);
            Assert.Equal(sddl, key.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RegistryLinkAtParentOrFinalKeyCannotRedirectTheFixedSlot(bool parent)
    {
        using var owned = new NativeFixture(); var link = parent ? "Parent" : @"Parent\Run"; var target = parent ? @"Target\Run" : "Target";
        using (var key = Registry.CurrentUser.CreateSubKey(owned.Path + "\\" + target)) { Native.Check(Native.RegSetValueExW(key.Handle, UserStartupRegistration.ValueName, 0, 3, [9], 1)); }
        using var root = Registry.CurrentUser.OpenSubKey(owned.Path, true)!; root.DeleteSubKeyTree(link);
        var destination = @"\Registry\User\" + StartupHistoryProtection.Actor() + "\\" + owned.Path + @"\Target";
        SafeRegistryHandle? symbolic = null;
        try
        {
            var code = Native.RegCreateKeyExW(root.Handle, link, 0, null, 2, 0x10103, IntPtr.Zero, out var created, out var disposition); symbolic = created;
            Native.Check(code); Assert.Equal(1u, disposition);
            var data = Encoding.Unicode.GetBytes(destination); Native.Check(Native.RegSetValueExW(symbolic, "SymbolicLinkValue", 0, 6, data, (uint)data.Length));
            var error = Assert.Throws<SelfStartupNativeReadException>(() => owned.Read());
            output.WriteLine($"LinkParent={parent}; Reason={error.Reason}; Native={(error.InnerException as Win32Exception)?.NativeErrorCode}");
            Assert.Equal("self_registry_unavailable", error.Reason);
            Assert.Equal(4395, Assert.IsType<Win32Exception>(error.InnerException).NativeErrorCode);
            using var key = Registry.CurrentUser.OpenSubKey(owned.Path + "\\" + target)!; Assert.Equal(new byte[] { 9 }, Assert.IsType<byte[]>(key.GetValue(UserStartupRegistration.ValueName)));
        }
        finally
        {
            // Delete the exact CREATED link handle, not a name API that can follow
            // the link and leave a dangling key or remove its (owned) target.
            if (symbolic is not null) { try { if (!symbolic.IsInvalid) { Assert.Equal(0, Native.NtDeleteKey(symbolic)); } } finally { symbolic.Dispose(); } }
        }
    }

    internal sealed class NativeFixture : IDisposable
    {
        private readonly string _actor = StartupHistoryProtection.Actor();
        private bool _created;
        internal Guid Id { get; } = Guid.NewGuid();
        internal string Path => PathFor(Id);
        private string Leaf => "SystemAutorun.SelfRaw.Tests." + Id.ToString("N");
        private string NativePath => @"\REGISTRY\USER\" + _actor + "\\" + Path;
        internal string RunPath => Path + @"\Parent\Run";
        internal static string PathFor(Guid id) => @"Software\SystemAutorun.SelfRaw.Tests." + id.ToString("N");
        internal NativeFixture()
        {
            try
            {
                using var software = Registry.Users.OpenSubKey(_actor + @"\Software", true)!;
                Native.RequireName(software.Handle, @"\REGISTRY\USER\" + _actor + @"\Software");
                using (var prior = software.OpenSubKey(Leaf)) { if (prior is not null) { throw new IOException("Fixture leaf already exists."); } }
                using var root = software.CreateSubKey(Leaf); Native.RequireName(root.Handle, NativePath); _created = true;
                using var key = root.CreateSubKey(@"Parent\Run"); Native.RequireName(key.Handle, NativePath + @"\Parent\Run");
            }
            catch { Dispose(); throw; }
        }
        internal SelfStartupRawObservation Read(Action? after = null) => StartupNativeSlotProbe.ReadSelfNativeFixture(Id, afterFirstRead: after);
        internal void Set(uint type, byte[] data) { using var key = Registry.CurrentUser.CreateSubKey(RunPath); Native.SetRaw(key.Handle, type, data); }
        internal void DeleteRun() { using var key = Registry.CurrentUser.OpenSubKey(Path, true)!; key.DeleteSubKeyTree(@"Parent\Run"); }
        public void Dispose()
        {
            if (!_created) { return; }
            StartupHistoryProtection.RequireActor(_actor);
            using var software = Registry.Users.OpenSubKey(_actor + @"\Software", true)!;
            Native.RequireName(software.Handle, @"\REGISTRY\USER\" + _actor + @"\Software");
            using (var root = software.OpenSubKey(Leaf)!) { Native.RequireName(root.Handle, NativePath); }
            software.DeleteSubKeyTree(Leaf, throwOnMissingSubKey: true);
            using var remaining = software.OpenSubKey(Leaf); Assert.Null(remaining); _created = false;
        }
    }

    internal static class Native
    {
        internal static void Check(int error) { if (error != 0) { throw new Win32Exception(error); } }
        internal static void SetRaw(SafeRegistryHandle key, uint type, byte[] data)
        {
            // RegSetValueExW may append a missing REG_SZ terminator. Seed actual
            // malformed native bytes, rather than testing a normalized fixture.
            var text = Marshal.StringToHGlobalUni(UserStartupRegistration.ValueName);
            try
            {
                var name = new NativeString { Length = (ushort)(UserStartupRegistration.ValueName.Length * 2), MaximumLength = (ushort)((UserStartupRegistration.ValueName.Length + 1) * 2), Buffer = text };
                Assert.Equal(0, NtSetValueKey(key, ref name, 0, type, data, (uint)data.Length));
            }
            finally { Marshal.FreeHGlobal(text); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int NtSetValueKey(SafeRegistryHandle key, ref NativeString name, uint title, uint type, [In] byte[] data, uint length);
        internal static void RequireName(SafeRegistryHandle key, string expected)
        {
            var bytes = new byte[4096]; Assert.Equal(0, NtQueryKey(key, 3, bytes, (uint)bytes.Length, out var length));
            Assert.InRange(length, 4u, (uint)bytes.Length); Assert.Equal(length - 4, BitConverter.ToUInt32(bytes, 0));
            Assert.True(string.Equals(expected, new UnicodeEncoding(false, false, true).GetString(bytes, 4, (int)length - 4), StringComparison.OrdinalIgnoreCase), "Wrong physical fixture key.");
        }
        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int NtQueryKey(SafeRegistryHandle key, int kind, [Out] byte[] data, uint length, out uint returned);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int RegSetValueExW(SafeRegistryHandle key, string name, uint reserved, uint type, [In] byte[] bytes, uint length);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int RegCreateKeyExW(SafeRegistryHandle key, string subkey, uint reserved, string? keyClass, uint options, uint access, IntPtr security, out SafeRegistryHandle result, out uint disposition);
        [DllImport("ntdll.dll", ExactSpelling = true)]
        internal static extern int NtDeleteKey(SafeRegistryHandle key);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int RegRenameKey(SafeRegistryHandle key, string subkey, string newName);
    }
}
