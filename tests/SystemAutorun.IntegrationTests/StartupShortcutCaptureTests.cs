using System.ComponentModel;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup", 0)] [InlineData("Startup", 3)] [InlineData("Startup", 65536)]
    [InlineData("Common Startup", 0)] [InlineData("Common Startup", 3)] [InlineData("Common Startup", 65536)]
    public void ShortcutCaptureBindsActualScopeObjectAndExactBytesWithoutParsingOrWriting(string source, int length)
    {
        using var owned = new Fixture(); using var manager = owned.Manager();
        var path = Path.Combine(owned.Native.GetShortcutDirectory(source), "owned.lnk"); VerifyEvidenceOwnedPath(owned, path);
        var bytes = Enumerable.Range(0, length).Select(value => (byte)(value % 251)).ToArray(); File.WriteAllBytes(path, bytes);
        using (var capture = manager.CaptureShortcutForDisable(source, "owned"))
        {
            Assert.Equal("observed", capture.Scope.State); Assert.Equal(source, capture.Scope.Source); Assert.Equal("owned", capture.Name);
            Assert.Equal(path, capture.Input.Origin); Assert.Equal(capture.Scope.DirectoryIdentity, capture.Input.DirectoryIdentity);
            Assert.Equal(capture.Scope.CaseSensitivity, capture.Input.CaseSensitivity);
            Assert.Equal(length, capture.Input.Length); Assert.Equal(1u, capture.Input.LinkCount);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), capture.Input.Sha256);
            Assert.Equal(capture.Input.DirectoryIdentity.VolumeSerialNumber, capture.Input.FileIdentity.VolumeSerialNumber);
            Assert.Matches("^[0-9a-f]{32}$", capture.Input.FileIdentity.FileId);
            Assert.Equal(bytes, capture.CopyOriginalBytes()); var copy = capture.CopyOriginalBytes();
            if (copy.Length != 0) { copy[0] ^= 1; Assert.Equal(bytes, capture.CopyOriginalBytes()); }
            capture.VerifyCurrent(); AssertScopeNoWrite(capture.Scope, owned);
        }
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.False(File.Exists(owned.Paths.StartupBackupPath));
        Assert.Empty(owned.Native.AccessedKeys); Assert.Empty(owned.Operations.Calls);
    }

    [Fact]
    public void ShortcutCaptureHoldsLeafAndParentUntilDisposedThenReleasesThem()
    {
        using var owned = new Fixture(); using var manager = owned.Manager();
        var directory = owned.Native.GetShortcutDirectory("Startup"); var path = Path.Combine(directory, "owned.lnk");
        var moved = Path.Combine(directory, "moved.lnk"); var movedDirectory = directory + ".moved";
        VerifyEvidenceOwnedPath(owned, path); VerifyEvidenceOwnedPath(owned, moved); VerifyEvidenceOwnedPath(owned, movedDirectory);
        File.WriteAllBytes(path, [7, 8]); var capture = manager.CaptureShortcutForDisable("Startup", "owned");
        try
        {
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, [9]));
            Assert.Throws<IOException>(() => File.Move(path, moved));
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.Throws<IOException>(() => Directory.Move(directory, movedDirectory));
            capture.VerifyCurrent(); Assert.Equal(new byte[] { 7, 8 }, capture.CopyOriginalBytes());
        }
        finally { capture.Dispose(); capture.Dispose(); }
        Assert.Throws<ObjectDisposedException>(() => capture.CopyOriginalBytes());
        Assert.Throws<ObjectDisposedException>(() => capture.VerifyCurrent());
        File.WriteAllBytes(path, [9]); File.Move(path, moved); Directory.Move(directory, movedDirectory);
        Assert.Empty(owned.Operations.Calls);
    }

    [Fact]
    public void ShortcutCaptureNewObjectAtSamePathAndSameBytesIsNotOldIdentity()
    {
        using var owned = new Fixture(); using var manager = owned.Manager();
        var path = Path.Combine(owned.Native.GetShortcutDirectory("Startup"), "owned.lnk"); var saved = path + ".saved";
        VerifyEvidenceOwnedPath(owned, path); VerifyEvidenceOwnedPath(owned, saved); File.WriteAllBytes(path, [1, 2, 3]);
        StartupShortcutInputSnapshot first;
        using (var capture = manager.CaptureShortcutForDisable("Startup", "owned")) { first = capture.Input; }
        File.Move(path, saved); File.Copy(saved, path);
        using var second = manager.CaptureShortcutForDisable("Startup", "owned");
        Assert.Equal(first.Sha256, second.Input.Sha256); Assert.NotEqual(first.FileIdentity, second.Input.FileIdentity);
        Assert.Empty(owned.Operations.Calls);
    }

    [Theory]
    [InlineData("missing")] [InlineData("directory")] [InlineData("reparse")]
    [InlineData("hardlink")] [InlineData("oversize")] [InlineData("occupied")]
    public void ShortcutCaptureRejectsNonCaptureableLeavesAndDoesNotFollowThem(string mode)
    {
        using var owned = new Fixture(); using var manager = owned.Manager();
        var path = Path.Combine(owned.Native.GetShortcutDirectory("Startup"), "owned.lnk"); VerifyEvidenceOwnedPath(owned, path);
        var target = Path.Combine(owned.Root, "never-parse-or-run.bin"); File.WriteAllBytes(target, [9]);
        if (mode == "directory") { Directory.CreateDirectory(path); }
        else if (mode == "reparse") { File.CreateSymbolicLink(path, target); }
        else if (mode == "hardlink") { Assert.True(EvidenceNativeCases.CreateHardLinkW(path, target, IntPtr.Zero)); }
        else if (mode == "oversize") { File.WriteAllBytes(path, new byte[65537]); }
        else if (mode == "occupied") { File.WriteAllBytes(path, [1]); }
        if (mode == "occupied")
        {
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            { Assert.Equal(32, Assert.Throws<Win32Exception>(() => manager.CaptureShortcutForDisable("Startup", "owned")).NativeErrorCode); }
            using var released = manager.CaptureShortcutForDisable("Startup", "owned"); released.VerifyCurrent();
        }
        else if (mode is "directory" or "reparse" or "hardlink" or "oversize")
        { Assert.Throws<InvalidOperationException>(() => manager.CaptureShortcutForDisable("Startup", "owned")); }
        else { Assert.Throws<Win32Exception>(() => manager.CaptureShortcutForDisable("Startup", "owned")); }
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(target)); Assert.Equal(0, owned.Native.WritableOpens);
        Assert.Empty(owned.Operations.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ShortcutCaptureAclDeniedReadOrDeleteAccessFailsAndDaclIsRestored(bool delete)
    {
        using var owned = new Fixture(); using var manager = owned.Manager();
        var path = Path.Combine(owned.Native.GetShortcutDirectory("Startup"), "owned.lnk"); VerifyEvidenceOwnedPath(owned, path);
        File.WriteAllBytes(path, [7]); var info = new FileInfo(path); var acl = info.GetAccessControl();
        var binary = acl.GetSecurityDescriptorBinaryForm(); var sddl = acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); var parentAcl = parent.GetAccessControl();
        var parentBinary = parentAcl.GetSecurityDescriptorBinaryForm(); var parentSddl = parentAcl.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        try
        {
            // Windows can grant DELETE via the parent's DELETE_CHILD. Deny both
            // routes in this owned fixture rather than claiming a leaf-only deny.
            if (delete) { parentAcl.AddAccessRule(new(identity.User!, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny)); parent.SetAccessControl(parentAcl); }
            acl.AddAccessRule(new(identity.User!, delete ? FileSystemRights.Delete : FileSystemRights.ReadData, AccessControlType.Deny));
            info.SetAccessControl(acl);
            Assert.Equal(5, Assert.Throws<Win32Exception>(() => manager.CaptureShortcutForDisable("Startup", "owned")).NativeErrorCode);
        }
        finally
        {
            try { acl.SetSecurityDescriptorBinaryForm(binary, AccessControlSections.Access); info.SetAccessControl(acl); }
            finally { parentAcl.SetSecurityDescriptorBinaryForm(parentBinary, AccessControlSections.Access); parent.SetAccessControl(parentAcl); }
        }
        Assert.Equal(sddl, info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.Equal(parentSddl, parent.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        using var restored = manager.CaptureShortcutForDisable("Startup", "owned"); Assert.Equal(new byte[] { 7 }, restored.CopyOriginalBytes());
    }

    [Theory]
    [InlineData("mapping")] [InlineData("cancel")] [InlineData("callback")]
    public void ShortcutCaptureFailedSealReleasesTheHeldLeafAndDoesNotPublish(string failure)
    {
        using var owned = new Fixture(); using var cancellation = new CancellationTokenSource();
        var directory = owned.Native.GetShortcutDirectory("Startup"); var mapped = directory;
        var path = Path.Combine(directory, "owned.lnk"); var moved = path + ".moved";
        VerifyEvidenceOwnedPath(owned, path); VerifyEvidenceOwnedPath(owned, moved); File.WriteAllBytes(path, [1]);
        var other = Path.Combine(owned.Root, "other-source"); Directory.CreateDirectory(other);
        var native = new ScopeOnlyAccess(() => mapped);
        void Seal()
        {
            if (failure == "mapping") { mapped = other; }
            else if (failure == "cancel") { cancellation.Cancel(); }
            else { throw new IOException("Owned injected seal failure."); }
        }
        if (failure == "mapping") { Assert.Throws<InvalidOperationException>(() => StartupShortcutCapture.Open("Startup", "owned", native, cancellation.Token, Seal)); }
        else if (failure == "cancel") { Assert.ThrowsAny<OperationCanceledException>(() => StartupShortcutCapture.Open("Startup", "owned", native, cancellation.Token, Seal)); }
        else { Assert.Throws<IOException>(() => StartupShortcutCapture.Open("Startup", "owned", native, cancellation.Token, Seal)); }
        File.Move(path, moved); Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(moved));
        Assert.False(File.Exists(owned.Paths.StartupBackupPath)); Assert.Empty(owned.Operations.Calls);
    }

    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData(@"..\outside")] [InlineData(@"C:\outside")]
    [InlineData("bad%TOKEN%")] [InlineData("CON")] [InlineData("bad/leaf")]
    public void ShortcutCaptureNeverAcceptsARecordPathAsTheSelectedBasename(string name)
    {
        using var owned = new Fixture(); using var manager = owned.Manager();
        Assert.ThrowsAny<ArgumentException>(() => manager.CaptureShortcutForDisable("Startup", name));
        Assert.Empty(owned.Native.AccessedKeys); Assert.Empty(owned.Operations.Calls);
    }

    [Fact]
    public void ShortcutCaptureContinuousScopeRejectsRealImpersonationAndAcceptsExactSensitiveName()
    {
        using var owned = new Fixture(); using var manager = owned.Manager(); using var identity = WindowsIdentity.GetCurrent();
        var directory = owned.Native.GetShortcutDirectory("Startup"); var path = Path.Combine(directory, "owned.lnk");
        VerifyEvidenceOwnedPath(owned, path); VerifyEvidenceOwnedPath(owned, directory);
        // This Windows host rejects toggling a nonempty directory (ERROR_DIR_NOT_EMPTY).
        // Set it while empty; do not pretend a failed flag mutation tested drift.
        SetEvidenceDirectoryCaseSensitivity(directory, true);
        try
        {
            File.WriteAllBytes(path, [7]);
            using (var capture = manager.CaptureShortcutForDisable("Startup", "owned"))
            {
                Assert.Equal("sensitive", capture.Scope.CaseSensitivity); Assert.Equal("sensitive", capture.Input.CaseSensitivity);
                WindowsIdentity.RunImpersonated(identity.AccessToken, () => Assert.Throws<InvalidOperationException>(() => capture.VerifyCurrent()));
                capture.VerifyCurrent(); Assert.Equal(new byte[] { 7 }, capture.CopyOriginalBytes());
            }
            Assert.Equal(2, Assert.Throws<Win32Exception>(() => manager.CaptureShortcutForDisable("Startup", "OWNED")).NativeErrorCode);
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
            SetEvidenceDirectoryCaseSensitivity(directory, false);
        }
        Assert.Empty(owned.Operations.Calls);
    }

    [Fact]
    public void ShortcutCapturePreCanceledAndUnsupportedSourceAreRejectedWithoutWriting()
    {
        using var owned = new Fixture(); using var manager = owned.Manager(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => manager.CaptureShortcutForDisable("Startup", "owned", canceled.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => manager.CaptureShortcutForDisable("HKCU", "owned"));
        Assert.Empty(owned.Native.AccessedKeys); Assert.Empty(owned.Operations.Calls);
    }
}
