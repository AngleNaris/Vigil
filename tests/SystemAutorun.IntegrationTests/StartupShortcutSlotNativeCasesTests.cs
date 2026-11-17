using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Fact]
    public async Task ShortcutSlotsSensitiveOriginalAndModeDriftNeverProduceMissing()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned);
        var source = Path.GetDirectoryName(record.Origin)!; VerifyEvidenceOwnedPath(owned, source);
        var upper = Path.Combine(source, "OWNED.LNK"); VerifyEvidenceOwnedPath(owned, upper);
        SetEvidenceDirectoryCaseSensitivity(source, true);
        try
        {
            await File.WriteAllBytesAsync(upper, [2]); using var manager = owned.Manager();
            var both = await manager.ObserveShortcutSlotsAsync(record.Id);
            Assert.Equal("missing", both.Archive.State); Assert.NotEqual("missing", both.Original.State);
            Assert.Equal("sensitive", both.Original.CaseSensitivity);
            File.Delete(upper); await File.WriteAllBytesAsync(record.Origin, [3]);
            Assert.Equal("present", (await manager.ObserveShortcutSlotsAsync(record.Id)).Original.State);
            VerifyEvidenceOwnedPath(owned, record.Origin); File.Delete(record.Origin);
            Assert.Equal("unknown", (await manager.ObserveShortcutSlotsAsync(record.Id)).Original.State);
            var changed = StartupOriginalSlotProbe.Observe(record.Origin, beforeSeal: () => SetEvidenceDirectoryCaseSensitivity(source, false));
            Assert.Equal("unknown", changed.State); Assert.Equal("original_case_semantics_changed", changed.Reason);
        }
        finally
        {
            if (File.Exists(upper)) { File.Delete(upper); }
            SetEvidenceDirectoryCaseSensitivity(source, false);
        }
    }

    [Fact]
    public async Task ShortcutSlotsHardLinkedArchiveAndOriginalAreTwoNamesNotAnAuthorization()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned); await File.WriteAllBytesAsync(record.BackupPath!, [1]);
        VerifyEvidenceOwnedPath(owned, record.Origin);
        if (!EvidenceNativeCases.CreateHardLinkW(record.Origin, record.BackupPath!, IntPtr.Zero)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        using var manager = owned.Manager(); var both = await manager.ObserveShortcutSlotsAsync(record.Id);
        Assert.Equal("present", both.Archive.State); Assert.Equal("present", both.Original.State);
        Assert.Equal(both.Archive.FileIdentity, both.Original.FileIdentity); Assert.Equal(2u, both.Original.LinkCount);
        Assert.False(both.NativeWriteReady); Assert.False(both.NativeWritePerformed); Assert.False(both.ExecutionRequested);
        var moved = record.BackupPath + ".moved"; VerifyEvidenceOwnedPath(owned, record.BackupPath!); VerifyEvidenceOwnedPath(owned, moved);
        File.Move(record.BackupPath!, moved); await File.WriteAllBytesAsync(record.Origin, [9]);
        Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(moved));
    }

    [Theory]
    [InlineData("窗口 名称")] [InlineData("named.lnk")] [InlineData("name.")] [InlineData("name ")]
    public async Task ShortcutSlotsValidateTheFinalLeafWithoutInventingOrAppendingAnotherExtension(string name)
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned);
        var origin = Path.Combine(owned.Native.GetShortcutDirectory("Startup"), name + ".LNK"); VerifyEvidenceOwnedPath(owned, origin);
        owned.RewriteBackup(row => { row["name"] = name; row["origin"] = origin; });
        await File.WriteAllBytesAsync(origin, [2]); using var manager = owned.Manager();
        var both = await manager.ObserveShortcutSlotsAsync(record.Id);
        Assert.Equal(name, both.Name); Assert.Equal("present", both.Original.State); Assert.Equal(1, both.Original.Length);
        Assert.Equal("missing", both.Archive.State); Assert.False(both.NativeWriteReady);
    }

    [Fact]
    public async Task ShortcutSlotsInvalidFinalComponentsAreRejectedBeforeDeniedSourceDirectoryIo()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned);
        var directory = new DirectoryInfo(Path.GetDirectoryName(record.Origin)!); VerifyEvidenceOwnedPath(owned, directory.FullName);
        var acl = directory.GetAccessControl(); var original = acl.GetSecurityDescriptorBinaryForm(); var sddl = acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        try
        {
            acl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny)); directory.SetAccessControl(acl);
            using var manager = owned.Manager();
            foreach (var name in new[] { new string('a', 300), "a:b", "CON", "NUL", "..\\escape" })
            {
                owned.RewriteBackup(row => { row["name"] = name; row["origin"] = Path.Combine(directory.FullName, name + ".lnk"); });
                var both = await manager.ObserveShortcutSlotsAsync(record.Id);
                Assert.Equal("original_context_invalid", both.Original.Reason); Assert.Equal("archive_context_invalid", both.Archive.Reason);
                Assert.Null(both.Original.SourceDirectory); Assert.Null(both.Archive.ArchiveDirectory); Assert.False(both.NativeWriteReady);
            }
        }
        finally { acl.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access); directory.SetAccessControl(acl); }
        Assert.Equal(sddl, directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    [Fact]
    public async Task ShortcutSlotsUseFixedSourceSpellingNotTheEditableCaseAliasParent()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned);
        var parent = Path.Combine(owned.Root, "owned-case-source-parent"); VerifyEvidenceOwnedPath(owned, parent); Directory.CreateDirectory(parent);
        SetEvidenceDirectoryCaseSensitivity(parent, true);
        var source = Path.Combine(parent, "source"); var alias = Path.Combine(parent, "SOURCE");
        var aliasFile = Path.Combine(alias, "owned.lnk");
        VerifyEvidenceOwnedPath(owned, source); VerifyEvidenceOwnedPath(owned, alias); VerifyEvidenceOwnedPath(owned, aliasFile);
        try
        {
            Directory.CreateDirectory(source); Directory.CreateDirectory(alias); SetEvidenceDirectoryCaseSensitivity(source, false);
            await File.WriteAllBytesAsync(aliasFile, [9]);
            owned.RewriteBackup(row => row["origin"] = aliasFile);
            using var manager = new StartupManager(owned.Paths, new EvidenceFixedSourceAccess(owned.Native, source), operations: owned.Operations);
            var both = await manager.ObserveShortcutSlotsAsync(record.Id);
            Assert.Equal("missing", both.Original.State); Assert.Equal(source, both.Original.SourceDirectory);
            Assert.NotNull(both.Original.DirectoryIdentity); Assert.False(both.NativeWriteReady);
            Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(aliasFile));
        }
        finally
        {
            if (File.Exists(aliasFile)) { File.Delete(aliasFile); }
            if (Directory.Exists(source)) { Directory.Delete(source); }
            if (Directory.Exists(alias)) { Directory.Delete(alias); }
            SetEvidenceDirectoryCaseSensitivity(parent, false);
        }
    }

    private sealed class EvidenceFixedSourceAccess(IStartupNativeAccess inner, string source) : IStartupNativeAccess
    {
        public bool IsAdministrator => inner.IsAdministrator;
        public string GetShortcutDirectory(string requested) => requested == "Startup" ? source : inner.GetShortcutDirectory(requested);
        public RegistryKey? OpenRunKey(string requested, bool writable, bool create) => inner.OpenRunKey(requested, writable, create);
    }
}
