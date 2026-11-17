using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SystemAutorun.Windows.Startup;
using Xunit;
using Admission = SystemAutorun.Windows.Startup.StartupNativeSlotProbe.SelfStartupAdmissionLease;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Fact]
    public async Task SelfAdmissionUsesProtectedFixedEmptyObjectAndCrossThreadRelease()
    {
        using var owned = new AdmissionFixture(); using var lease = await Admission.AcquireAsync(owned.Scope);
        var path = lease.LockPath; var identity = lease.FileIdentity; lease.VerifyCurrent();
        Assert.Equal(Admission.LockName, Path.GetFileName(path)); Assert.Equal(0, new FileInfo(path).Length);
        var security = new FileInfo(path).GetAccessControl(); Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(StartupHistoryProtection.Actor(), security.GetOwner(typeof(SecurityIdentifier))?.Value);
        Assert.Throws<IOException>(() => { using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
        Assert.Throws<IOException>(() => Directory.Move(owned.Root, owned.Root + ".moved"));
        await Task.Run(lease.Dispose); lease.Dispose(); Assert.Throws<ObjectDisposedException>(() => lease.VerifyCurrent());
        Assert.True(File.Exists(path)); using var again = await Admission.AcquireAsync(owned.Scope); Assert.Equal(identity, again.FileIdentity);
    }

    [Fact]
    public async Task SelfAdmissionBusyAndCancelledWaitersDoNotStealOrReplaceObject()
    {
        using var owned = new AdmissionFixture(); using var owner = await Admission.AcquireAsync(owned.Scope); var id = owner.FileIdentity;
        var busy = await Assert.ThrowsAsync<SelfStartupAdmissionBusyException>(() => Admission.AcquireAsync(owned.Scope, timeout: TimeSpan.FromMilliseconds(80)));
        Assert.True(Assert.IsType<Win32Exception>(busy.InnerException).NativeErrorCode is 32 or 33);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Admission.AcquireAsync(owned.Scope, cancelled.Token));
        owner.VerifyCurrent(); Assert.Equal(id, owner.FileIdentity);
    }

    [Fact]
    public async Task SelfAdmissionAlreadyCancelledDoesNotResolveOrCreateAnything()
    {
        using var owned = new AdmissionFixture(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var called = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Admission.AcquireAsync(() => { called = true; return owned.Scope(); }, cancelled.Token));
        Assert.False(called); Assert.Empty(Directory.GetFileSystemEntries(owned.Root));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(60001)]
    public async Task SelfAdmissionTimeoutBoundsRefuseBeforeStorage(int milliseconds)
    {
        using var owned = new AdmissionFixture();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Admission.AcquireAsync(owned.Scope, timeout: TimeSpan.FromMilliseconds(milliseconds)));
        Assert.Empty(Directory.GetFileSystemEntries(owned.Root));
    }

    [Fact]
    public async Task SelfAdmissionRejectsActorSpoofBeforeCreationAndMappingDriftBeforeUse()
    {
        using var owned = new AdmissionFixture(); using var other = new AdmissionFixture();
        await Assert.ThrowsAsync<SelfStartupAdmissionException>(() => Admission.AcquireAsync(() => new("S-1-5-21-1-2-3-1001", owned.Root)));
        Assert.Empty(Directory.GetFileSystemEntries(owned.Root)); var scope = owned.Scope();
        using var lease = await Admission.AcquireAsync(() => scope); scope = scope with { LocalAppData = other.Root };
        var refusal = Assert.Throws<SelfStartupAdmissionException>(() => lease.VerifyCurrent());
        Assert.IsType<InvalidOperationException>(refusal.InnerException); Assert.Empty(Directory.GetFileSystemEntries(other.Root));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task SelfAdmissionRejectsWeakFileOrActorDirectoryAclWithoutRepair(int target)
    {
        using var owned = new AdmissionFixture(); string path; var directory = target != 0;
        using (var lease = await Admission.AcquireAsync(owned.Scope)) { path = target == 2 ? owned.Root : directory ? Path.GetDirectoryName(lease.LockPath)! : lease.LockPath; }
        FileSystemInfo item = directory ? new DirectoryInfo(path) : new FileInfo(path);
        FileSystemSecurity Read() => directory ? ((DirectoryInfo)item).GetAccessControl() : ((FileInfo)item).GetAccessControl();
        void Set(FileSystemSecurity value) { if (directory) { ((DirectoryInfo)item).SetAccessControl((DirectorySecurity)value); } else { ((FileInfo)item).SetAccessControl((FileSecurity)value); } }
        // SetAccessControl canonicalizes freshly created P to PAI on this NTFS.
        // Establish the setter's stable form BEFORE recording the rollback image.
        FileSystemSecurity seed = directory ? new DirectorySecurity() : new FileSecurity();
        seed.SetSecurityDescriptorBinaryForm(Read().GetSecurityDescriptorBinaryForm(), AccessControlSections.Access); Set(seed);
        var original = Read(); var binary = original.GetSecurityDescriptorBinaryForm(); var sddl = original.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var changed = Read(); changed.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"), FileSystemRights.WriteData, AccessControlType.Allow));
        try
        {
            Set(changed); var weak = Read().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            await Assert.ThrowsAsync<SelfStartupAdmissionException>(() => Admission.AcquireAsync(owned.Scope));
            Assert.Equal(weak, Read().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
        finally
        {
            FileSystemSecurity restore = directory ? new DirectorySecurity() : new FileSecurity();
            restore.SetSecurityDescriptorBinaryForm(binary, AccessControlSections.Access); Set(restore);
            Assert.Equal(sddl, Read().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
    }

    [Fact]
    public async Task SelfAdmissionNeverTruncatesMalformedExistingLock()
    {
        using var owned = new AdmissionFixture(); string path;
        using (var lease = await Admission.AcquireAsync(owned.Scope)) { path = lease.LockPath; }
        File.WriteAllBytes(path, [7, 8, 9]); await Assert.ThrowsAsync<SelfStartupAdmissionException>(() => Admission.AcquireAsync(owned.Scope));
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SelfAdmissionRejectsHardlinkAndReparseLockLeaves(bool reparse)
    {
        using var owned = new AdmissionFixture(); string path;
        using (var lease = await Admission.AcquireAsync(owned.Scope)) { path = lease.LockPath; }
        var alias = Path.Combine(owned.Root, "alias.lock");
        if (reparse)
        {
            File.Delete(path); File.WriteAllBytes(alias, []);
            if (!AdmissionNative.CreateSymbolicLinkW(path, alias, 2)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        }
        else if (!AdmissionNative.CreateHardLinkW(alias, path, IntPtr.Zero)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try { await Assert.ThrowsAsync<SelfStartupAdmissionException>(() => Admission.AcquireAsync(owned.Scope)); }
        finally { File.Delete(reparse ? path : alias); }
    }

    [Fact]
    public async Task SelfAdmissionRejectsReparseParentWithoutCreatingTargetState()
    {
        using var owned = new AdmissionFixture(); using var other = new AdmissionFixture(); var parent = Path.Combine(owned.Root, "Vigil");
        if (!AdmissionNative.CreateSymbolicLinkW(parent, other.Root, 3)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try { await Assert.ThrowsAsync<SelfStartupAdmissionException>(() => Admission.AcquireAsync(owned.Scope)); Assert.Empty(Directory.GetFileSystemEntries(other.Root)); }
        finally { Directory.Delete(parent); }
    }

    [Fact]
    public async Task SelfAdmissionTwoRootProcessesContendThenAbruptExitReleasesSameSlot()
    {
        using var first = new AdmissionFixture(); using var second = new AdmissionFixture();
        File.WriteAllText(Path.Combine(first.Root, "self-admission-base.txt"), first.Root);
        File.WriteAllText(Path.Combine(second.Root, "self-admission-base.txt"), first.Root);
        var firstBundle = new CrashHostBundle(first.Root); var secondBundle = new CrashHostBundle(second.Root);
        await using var owner = firstBundle.Start("self-admission-hold"); await owner.WaitForCheckpointAsync("self_admission_held");
        string fixedPath;
        await using (var contender = secondBundle.Start("self-admission-probe"))
        {
            using var report = await contender.ReadReportAsync(); var row = report.RootElement;
            Assert.Equal("busy", row.GetProperty("state").GetString()); Assert.Equal(second.Root, row.GetProperty("root").GetString());
            Assert.NotEqual(owner.Id, row.GetProperty("pid").GetInt32()); fixedPath = row.GetProperty("lockPath").GetString()!;
        }
        await owner.TerminateAsync(); Assert.NotEqual(0, owner.ExitCode);
        await using var fresh = secondBundle.Start("self-admission-probe"); using var next = await fresh.ReadReportAsync();
        Assert.Equal("acquired", next.RootElement.GetProperty("state").GetString()); Assert.Equal(fixedPath, next.RootElement.GetProperty("lockPath").GetString());
        Assert.StartsWith(first.Root + Path.DirectorySeparatorChar, fixedPath, StringComparison.Ordinal);
        Assert.Equal(Admission.LockName, Path.GetFileName(fixedPath)); Assert.Equal(0, new FileInfo(fixedPath).Length);
    }

    [Fact]
    public async Task SelfAdmissionKnownFolderProbeIgnoresWorkerEnvironmentOverride()
    {
        using var owned = new AdmissionFixture(); var bundle = new CrashHostBundle(owned.Root); await using var worker = bundle.Start("self-admission-scope");
        using var report = await worker.ReadReportAsync(); var row = report.RootElement;
        Assert.Equal(StartupHistoryProtection.Actor(), row.GetProperty("actor").GetString());
        Assert.NotEqual(owned.Root, row.GetProperty("local").GetString()); Assert.False(Directory.Exists(Path.Combine(owned.Root, "Vigil")));
    }

    private static async Task<int> RunSelfAdmissionWorkerAsync(string root, string mode)
    {
        root = RequireCrashRoot(root); var image = Environment.ProcessPath ?? throw new IOException("No owned worker image."); VerifyCrashImage(image, root);
        Console.OutputEncoding = new UTF8Encoding(false);
        if (mode == "self-admission-scope")
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", root); var native = Admission.CurrentScope();
            Console.WriteLine(JsonSerializer.Serialize(new { pid = Environment.ProcessId, root, image, actor = native.ActorSid, local = native.LocalAppData })); return 0;
        }
        var bytes = ReadCrashInput(root, "self-admission-base.txt", 4096); var actorBase = RequireCrashRoot(Encoding.UTF8.GetString(bytes));
        SelfStartupAdmissionScope Scope() => new(StartupHistoryProtection.Actor(), actorBase);
        try
        {
            using var lease = await Admission.AcquireAsync(Scope, timeout: TimeSpan.FromMilliseconds(150));
            if (mode == "self-admission-hold")
            {
                var marker = Path.Combine(root, "history-crash.ready.json"); var staging = marker + ".staging";
                File.WriteAllText(staging, JsonSerializer.Serialize(new { pid = Environment.ProcessId, root, stage = "self_admission_held" })); File.Move(staging, marker);
                using var forever = new ManualResetEventSlim(false); forever.Wait(); throw new InvalidOperationException("Owned holder must be terminated.");
            }
            Console.WriteLine(JsonSerializer.Serialize(new { pid = Environment.ProcessId, root, image, state = "acquired", lockPath = lease.LockPath })); return 0;
        }
        catch (SelfStartupAdmissionBusyException)
        {
            var actorKey = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(StartupHistoryProtection.Actor())));
            var path = Path.Combine(actorBase, "Vigil", "self-startup-v1-" + actorKey, Admission.LockName);
            Console.WriteLine(JsonSerializer.Serialize(new { pid = Environment.ProcessId, root, image, state = "busy", lockPath = path })); return 0;
        }
    }

    internal sealed class AdmissionFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "SystemAutorun-startup-mutation-" + Guid.NewGuid().ToString("N"));
        internal AdmissionFixture()
        {
            // A private fake known-folder base, not an inherited writable temp ACL.
            var actor = StartupHistoryProtection.Actor(); var security = new DirectorySecurity();
            security.SetOwner(new SecurityIdentifier(actor)); security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { actor, "S-1-5-18", "S-1-5-32-544" }.Distinct(StringComparer.Ordinal))
            { security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow)); }
            Directory.CreateDirectory(Root); new DirectoryInfo(Root).SetAccessControl(security);
        }
        internal SelfStartupAdmissionScope Scope() => new(StartupHistoryProtection.Actor(), Root);
        public void Dispose() { var full = RequireCrashRoot(Root); Directory.Delete(full, recursive: true); }
    }

    private static class AdmissionNative
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateHardLinkW(string name, string existing, IntPtr security);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateSymbolicLinkW(string name, string target, int flags);
    }
}
