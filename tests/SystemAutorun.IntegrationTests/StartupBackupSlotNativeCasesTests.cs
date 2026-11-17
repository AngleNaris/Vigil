using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Fact]
    public async Task BackupSlotAclDeniedIsAnActualDeniedErrorNotMissingAndOriginalAclIsRestored()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned); await File.WriteAllBytesAsync(record.BackupPath!, [7]);
        VerifyEvidenceOwnedPath(owned, record.BackupPath!);
        var file = new FileInfo(record.BackupPath!); var original = file.GetAccessControl(); var originalBytes = original.GetSecurityDescriptorBinaryForm();
        var denied = file.GetAccessControl(); using var identity = WindowsIdentity.GetCurrent();
        denied.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ReadData, AccessControlType.Deny));
        try
        {
            file.SetAccessControl(denied);
            using var manager = owned.Manager(); var result = await manager.ObserveBackupSlotAsync(record.Id);
            Assert.Equal("unknown", result.Observation.State); Assert.Equal("archive_permission_denied", result.Observation.Reason);
            Assert.Equal(5, result.Observation.NativeErrorCode); Assert.NotNull(result.Observation.DirectoryIdentity);
            Assert.Null(result.Observation.FileIdentity); Assert.False(result.NativeWriteReady);
        }
        finally { original.SetSecurityDescriptorBinaryForm(originalBytes, AccessControlSections.Access); file.SetAccessControl(original); }
        Assert.Equal(new byte[] { 7 }, await File.ReadAllBytesAsync(record.BackupPath!));
        Assert.Equal(original.GetSecurityDescriptorSddlForm(AccessControlSections.Access), file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    [Fact]
    public async Task BackupSlotParentAclDeniedCannotSupplyAnIdentityOrMissingEvidence()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned);
        var directory = new DirectoryInfo(Path.GetDirectoryName(record.BackupPath!)!); VerifyEvidenceOwnedPath(owned, directory.FullName);
        var original = directory.GetAccessControl(); var originalBytes = original.GetSecurityDescriptorBinaryForm(); var denied = directory.GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        denied.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        try
        {
            directory.SetAccessControl(denied);
            using var manager = owned.Manager(); var result = await manager.ObserveBackupSlotAsync(record.Id);
            Assert.Equal("unknown", result.Observation.State); Assert.Equal("archive_permission_denied", result.Observation.Reason);
            Assert.Equal(5, result.Observation.NativeErrorCode); Assert.Null(result.Observation.DirectoryIdentity); Assert.False(result.NativeWriteReady);
        }
        finally { original.SetSecurityDescriptorBinaryForm(originalBytes, AccessControlSections.Access); directory.SetAccessControl(original); }
        Assert.Equal(original.GetSecurityDescriptorSddlForm(AccessControlSections.Access), directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    [Fact]
    public async Task BackupSlotUppercaseGuidAndExtensionArePresentNotMissingOnAnInsensitiveParent()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned);
        var upper = Path.Combine(Path.GetDirectoryName(record.BackupPath!)!, record.Id.ToUpperInvariant() + ".LNK");
        await File.WriteAllBytesAsync(upper, [7]); using var manager = owned.Manager();
        var result = await manager.ObserveBackupSlotAsync(record.Id);
        Assert.Equal("present", result.Observation.State); Assert.Equal("insensitive", result.Observation.CaseSensitivity);
        Assert.NotNull(result.Observation.FileIdentity); Assert.False(result.NativeWriteReady);
    }

    [Fact]
    public async Task BackupSlotHardLinkIdentityAndShareArbitrationApplyToTheSameNativeObject()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned); await File.WriteAllBytesAsync(record.BackupPath!, [7]);
        var alias = Path.Combine(Path.GetDirectoryName(record.BackupPath!)!, Guid.NewGuid().ToString("N") + ".lnk");
        VerifyEvidenceOwnedPath(owned, alias);
        if (!EvidenceNativeCases.CreateHardLinkW(alias, record.BackupPath!, IntPtr.Zero)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var first = StartupArchiveSlotProbe.Observe(record.BackupPath!, beforeSeal: () =>
        { Assert.Throws<IOException>(() => { using var writer = new FileStream(alias, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }); });
        var second = StartupArchiveSlotProbe.Observe(alias);
        Assert.Equal("present", first.State); Assert.Equal(2u, first.LinkCount); Assert.Equal(first.FileIdentity, second.FileIdentity);
        using var manager = owned.Manager(); Assert.False((await manager.ObserveBackupSlotAsync(record.Id)).NativeWriteReady);
    }

    [Fact]
    public async Task BackupSlotCaseSensitiveParentNeverCertifiesAnAbsentSpellingAsMissing()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned);
        var directory = Path.GetDirectoryName(record.BackupPath!)!; VerifyEvidenceOwnedPath(owned, directory);
        var upper = Path.Combine(directory, record.Id.ToUpperInvariant() + ".LNK"); VerifyEvidenceOwnedPath(owned, upper);
        SetEvidenceDirectoryCaseSensitivity(directory, true);
        try
        {
            await File.WriteAllBytesAsync(upper, [7]); using var manager = owned.Manager();
            var variant = await manager.ObserveBackupSlotAsync(record.Id);
            Assert.NotEqual("missing", variant.Observation.State); Assert.Equal("sensitive", variant.Observation.CaseSensitivity);
            File.Delete(upper);
            VerifyEvidenceOwnedPath(owned, record.BackupPath!); await File.WriteAllBytesAsync(record.BackupPath!, [8]);
            var exact = await manager.ObserveBackupSlotAsync(record.Id);
            Assert.Equal("present", exact.Observation.State); Assert.Equal("sensitive", exact.Observation.CaseSensitivity);
            File.Delete(record.BackupPath!);
            var empty = await manager.ObserveBackupSlotAsync(record.Id);
            Assert.Equal("unknown", empty.Observation.State); Assert.Equal("archive_case_semantics_unavailable", empty.Observation.Reason);
            Assert.Equal("sensitive", empty.Observation.CaseSensitivity); Assert.False(empty.NativeWriteReady);
        }
        finally
        {
            if (File.Exists(upper)) { File.Delete(upper); }
            SetEvidenceDirectoryCaseSensitivity(directory, false);
        }
    }

    [Fact]
    public async Task BackupSlotCaseModeChangedDuringLookupCannotBecomeMissingEvidence()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned);
        var directory = Path.GetDirectoryName(record.BackupPath!)!; VerifyEvidenceOwnedPath(owned, directory);
        SetEvidenceDirectoryCaseSensitivity(directory, true);
        try
        {
            var result = StartupArchiveSlotProbe.Observe(record.BackupPath!, beforeSeal: () => SetEvidenceDirectoryCaseSensitivity(directory, false));
            Assert.Equal("unknown", result.State); Assert.Equal("archive_case_semantics_changed", result.Reason);
            Assert.Equal("insensitive", result.CaseSensitivity);
        }
        finally { SetEvidenceDirectoryCaseSensitivity(directory, false); }
    }

    private static void SetEvidenceDirectoryCaseSensitivity(string directory, bool sensitive)
    {
        using var handle = EvidenceNativeCases.CreateFileW(directory, 0x40000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var flags = sensitive ? 1u : 0u;
        if (!EvidenceNativeCases.SetFileInformationByHandle(handle, 23, ref flags, 4)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }

    private static class EvidenceNativeCases
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateHardLinkW(string newPath, string existingPath, IntPtr security);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref uint info, uint size);
    }
}
