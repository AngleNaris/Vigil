using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed partial class StartupMutationAdapterTests
{
    [Theory]
    [InlineData("Startup", false)] [InlineData("Startup", true)]
    [InlineData("Common Startup", false)] [InlineData("Common Startup", true)]
    public void SourceScopeUsesRealActorAndDirectoryNotElevationOrLeafContents(string source, bool administrator)
    {
        using var owned = new Fixture(administrator); using var manager = owned.Manager();
        var directory = owned.Native.GetShortcutDirectory(source);
        var leaf = Path.Combine(directory, "never-read.lnk"); VerifyEvidenceOwnedPath(owned, leaf);
        File.WriteAllBytes(leaf, [0, 1, 2]);
        using var locked = new FileStream(leaf, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var scope = manager.ObserveShortcutSourceScope(source);
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal("observed", scope.State); Assert.Null(scope.Reason); Assert.Null(scope.NativeErrorCode);
        Assert.Equal(1, scope.SchemaVersion); Assert.Equal(source, scope.Source);
        Assert.Equal(source == "Startup" ? "user" : "machine", scope.SourceKind);
        Assert.Equal(source == "Startup" ? "Startup" : "CommonStartup", scope.KnownFolder);
        Assert.Equal(identity.User!.Value, scope.ObserverSid);
        Assert.Equal(source == "Startup" ? identity.User.Value : null, scope.SourceUserSid);
        Assert.Equal(directory, scope.DirectoryPath); Assert.Equal("insensitive", scope.CaseSensitivity);
        var independent = StartupOriginalSlotProbe.Observe(Path.Combine(directory, "not-present.lnk"));
        Assert.Equal(scope.DirectoryIdentity, independent.DirectoryIdentity);
        Assert.Matches("^[0-9a-f]{16}$", scope.DirectoryIdentity!.VolumeSerialNumber);
        Assert.Matches("^[0-9a-f]{32}$", scope.DirectoryIdentity.FileId);
        Assert.InRange(scope.ObservedAt, DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow);
        Assert.Equal(scope, JsonSerializer.Deserialize<StartupSourceScopeObservation>(JsonSerializer.Serialize(scope, AgentProtocol.JsonOptions), AgentProtocol.JsonOptions));
        AssertScopeNoWrite(scope, owned); Assert.False(File.Exists(owned.Paths.StartupBackupPath));
        Assert.Empty(owned.Native.AccessedKeys);
    }

    [Fact]
    public void SourceScopeDetectsSamePathReplacementAndReleasesEveryHandle()
    {
        using var owned = new Fixture(); using var manager = owned.Manager();
        var source = owned.Native.GetShortcutDirectory("Startup"); var saved = source + ".saved";
        VerifyEvidenceOwnedPath(owned, source); VerifyEvidenceOwnedPath(owned, saved);
        var first = manager.ObserveShortcutSourceScope("Startup"); Assert.Equal("observed", first.State);
        Directory.Move(source, saved); Directory.CreateDirectory(source);
        var second = manager.ObserveShortcutSourceScope("Startup"); Assert.Equal("observed", second.State);
        Assert.Equal(first.DirectoryPath, second.DirectoryPath); Assert.NotEqual(first.DirectoryIdentity, second.DirectoryIdentity);
        Assert.Equal(first.ObserverSid, second.ObserverSid); AssertScopeNoWrite(second, owned);
        var held = StartupSourceScopeProbe.Observe("Startup", owned.Native, default,
            beforeSeal: () => Assert.Throws<IOException>(() => Directory.Move(source, source + ".blocked")));
        Assert.Equal("observed", held.State); Assert.False(Directory.Exists(source + ".blocked"));
        VerifyEvidenceOwnedPath(owned, source + ".released"); Directory.Move(source, source + ".released");
    }

    [Fact]
    public void SourceScopeRealDenyAclIsUnknownAndOriginalDaclIsRestored()
    {
        using var owned = new Fixture(); using var manager = owned.Manager();
        var directory = owned.Native.GetShortcutDirectory("Startup"); VerifyEvidenceOwnedPath(owned, directory);
        var info = new DirectoryInfo(directory); var acl = info.GetAccessControl();
        var bytes = acl.GetSecurityDescriptorBinaryForm(); var sddl = acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        try
        {
            acl.AddAccessRule(new(identity.User!, FileSystemRights.ReadData, AccessControlType.Deny)); info.SetAccessControl(acl);
            var scope = manager.ObserveShortcutSourceScope("Startup"); AssertUnknownScope(scope, owned);
            Assert.Equal("source_scope_permission_denied", scope.Reason); Assert.Equal(5, scope.NativeErrorCode);
        }
        finally { acl.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access); info.SetAccessControl(acl); }
        Assert.Equal(sddl, info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.Equal("observed", manager.ObserveShortcutSourceScope("Startup").State);
    }

    [Theory]
    [InlineData("missing")] [InlineData("reparse")] [InlineData("occupied")]
    public void SourceScopeNeverCreatesOrFollowsUnavailableDirectories(string mode)
    {
        using var owned = new Fixture(); using var manager = owned.Manager();
        var directory = owned.Native.GetShortcutDirectory("Startup"); VerifyEvidenceOwnedPath(owned, directory);
        if (mode == "missing")
        {
            Directory.Delete(directory); var result = manager.ObserveShortcutSourceScope("Startup");
            AssertUnknownScope(result, owned); Assert.Equal("source_scope_directory_missing", result.Reason);
            Assert.False(Directory.Exists(directory));
        }
        else if (mode == "reparse")
        {
            var other = Path.Combine(owned.Root, "owned-scope-reparse-target"); Directory.CreateDirectory(other);
            Directory.Delete(directory); Directory.CreateSymbolicLink(directory, other);
            try { AssertUnknownScope(manager.ObserveShortcutSourceScope("Startup"), owned); Assert.Empty(Directory.GetFileSystemEntries(other)); }
            finally { VerifyEvidenceOwnedPath(owned, directory); Directory.Delete(directory); Directory.CreateDirectory(directory); }
        }
        else
        {
            using (var held = EvidenceNativeCases.CreateFileW(directory, 0x81, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                Assert.False(held.IsInvalid); var result = manager.ObserveShortcutSourceScope("Startup");
                AssertUnknownScope(result, owned); Assert.Equal("source_scope_occupied", result.Reason); Assert.Equal(32, result.NativeErrorCode);
            }
            Assert.Equal("observed", manager.ObserveShortcutSourceScope("Startup").State);
        }
    }

    [Theory]
    [InlineData("sid")] [InlineData("missing-sid")] [InlineData("mapping")] [InlineData("case-only")] [InlineData("network-after")]
    public void SourceScopeControlledActorAndMappingDriftCannotBeSealed(string mode)
    {
        using var owned = new Fixture(); using var identity = WindowsIdentity.GetCurrent();
        string? sid = identity.User!.Value; var directory = owned.Native.GetShortcutDirectory("Startup");
        var mapped = directory; var native = new ScopeOnlyAccess(() => mapped);
        var result = StartupSourceScopeProbe.Observe("Startup", native, default, () => sid, () =>
        {
            if (mode == "sid") { sid = "S-1-5-21-1-2-3-1101"; }
            else if (mode == "missing-sid") { sid = null; }
            else if (mode == "mapping") { mapped = Path.Combine(owned.Root, "not-created-or-opened"); }
            else if (mode == "case-only") { mapped = directory[..directory.LastIndexOf('\\')] + "\\USER-STARTUP"; }
            else { mapped = @"\\offline.invalid\never-follow-scope"; }
        });
        AssertUnknownScope(result, owned);
        Assert.Equal(mode == "network-after" ? "source_scope_context_invalid" : "source_scope_context_changed", result.Reason);
        Assert.False(Directory.Exists(Path.Combine(owned.Root, "not-created-or-opened")));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("not-a-sid")]
    public void SourceScopeUnavailableActorDoesNotResolveAnyDirectory(string? sid)
    {
        using var owned = new Fixture(); var native = new ScopeOnlyAccess(() => throw new Exception("No directory resolution is allowed."));
        var result = StartupSourceScopeProbe.Observe("Startup", native, default, () => sid);
        AssertUnknownScope(result, owned); Assert.Equal("source_scope_identity_unavailable", result.Reason); Assert.Equal(0, native.DirectoryReads);
    }

    [Theory]
    [InlineData(@"\\offline.invalid\never-follow")] [InlineData(@"\\?\C:\never-follow")]
    [InlineData("relative")] [InlineData(@"C:\%TEMP%\never-expand")] [InlineData(@"C:\scope\..\never-follow")]
    public void SourceScopeRejectsInvalidFixedMappingWithoutProbingIt(string directory)
    {
        using var owned = new Fixture(); var native = new ScopeOnlyAccess(() => directory);
        var result = StartupSourceScopeProbe.Observe("Startup", native, default);
        AssertUnknownScope(result, owned); Assert.Equal("source_scope_context_invalid", result.Reason); Assert.Equal(1, native.DirectoryReads);
    }

    [Theory]
    [InlineData("HKCU")] [InlineData("HKLM")] [InlineData("startup")] [InlineData("Startup\\..")]
    [InlineData("")] [InlineData(null)]
    public void SourceScopeRejectsUnsupportedSourcesBeforeIdentityOrDirectoryReads(string? source)
    {
        using var owned = new Fixture(); var native = new ScopeOnlyAccess(() => throw new Exception("Must not resolve."));
        Assert.Throws<ArgumentOutOfRangeException>(() => StartupSourceScopeProbe.Observe(source!, native, default,
            () => throw new Exception("Must not read identity.")));
        Assert.Equal(0, native.DirectoryReads); Assert.Empty(owned.Native.AccessedKeys); Assert.Empty(owned.Operations.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SourceScopeCancellationIncludingSimultaneousDriftCannotProduceAnObservation(bool drift)
    {
        using var owned = new Fixture(); using var cancellation = new CancellationTokenSource();
        using var identity = WindowsIdentity.GetCurrent(); string? sid = identity.User!.Value;
        Assert.ThrowsAny<OperationCanceledException>(() => StartupSourceScopeProbe.Observe("Startup", owned.Native, cancellation.Token,
            () => sid, () => { if (drift) { sid = null; } cancellation.Cancel(); }));
        using var manager = owned.Manager();
        Assert.ThrowsAny<OperationCanceledException>(() => manager.ObserveShortcutSourceScope("Startup", cancellation.Token));
        Assert.Equal("observed", manager.ObserveShortcutSourceScope("Startup").State); Assert.Empty(owned.Operations.Calls);
    }

    [Fact]
    public void SourceScopeRealSameUserImpersonationIsRejectedAndReverted()
    {
        using var owned = new Fixture(); using var manager = owned.Manager(); using var identity = WindowsIdentity.GetCurrent();
        var scope = WindowsIdentity.RunImpersonated(identity.AccessToken, () => manager.ObserveShortcutSourceScope("Startup"));
        AssertUnknownScope(scope, owned); Assert.Equal("source_scope_identity_unavailable", scope.Reason);
        Assert.Equal("observed", manager.ObserveShortcutSourceScope("Startup").State);
    }

    [Fact]
    public void SourceScopeCaseModeIsObservedAndChangesInvalidateTheCapture()
    {
        using var owned = new Fixture(); var directory = owned.Native.GetShortcutDirectory("Startup"); VerifyEvidenceOwnedPath(owned, directory);
        SetEvidenceDirectoryCaseSensitivity(directory, true);
        try
        {
            using var manager = owned.Manager(); var exact = manager.ObserveShortcutSourceScope("Startup");
            Assert.Equal("observed", exact.State); Assert.Equal("sensitive", exact.CaseSensitivity); AssertScopeNoWrite(exact, owned);
            var drift = StartupSourceScopeProbe.Observe("Startup", owned.Native, default,
                beforeSeal: () => SetEvidenceDirectoryCaseSensitivity(directory, false));
            AssertUnknownScope(drift, owned); Assert.Equal("source_scope_case_semantics_changed", drift.Reason);
        }
        finally { SetEvidenceDirectoryCaseSensitivity(directory, false); }
    }

    [Fact]
    public async Task SourceScopeObservedWithForgedLegacyRecordAndBothMissingStillGrantsNoNativeAction()
    {
        using var owned = new Fixture(); var record = await WriteEvidenceRecord(owned); using var manager = owned.Manager();
        var before = await File.ReadAllBytesAsync(owned.Paths.StartupBackupPath);
        var scope = manager.ObserveShortcutSourceScope(record.Source); Assert.Equal("observed", scope.State);
        var slots = await manager.ObserveShortcutSlotsAsync(record.Id);
        Assert.Equal("missing", slots.Archive.State); Assert.Equal("missing", slots.Original.State);
        var entry = Assert.Single((await manager.GetSnapshotAsync()).Entries, row => row.BackupId == record.Id);
        Assert.All(StartupInspectionPolicy.Describe(entry), action => Assert.NotEqual("reviewable", action.State));
        Assert.False(slots.NativeWriteReady); Assert.False(slots.NativeWritePerformed); Assert.False(slots.ExecutionRequested);
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(slots, AgentProtocol.JsonOptions));
        Assert.Equal(1, wire.RootElement.GetProperty("schemaVersion").GetInt32()); Assert.False(wire.RootElement.TryGetProperty("sourceScope", out _));
        Assert.Equal(before, await File.ReadAllBytesAsync(owned.Paths.StartupBackupPath)); AssertScopeNoWrite(scope, owned);
        await File.WriteAllTextAsync(owned.Paths.StartupBackupPath, "bad-json-not-read-by-source-scope");
        await File.WriteAllTextAsync(owned.Paths.StartupBackupPath + ".transaction", "unresolved-not-authorized-by-source-scope");
        Assert.Equal("observed", manager.ObserveShortcutSourceScope(record.Source).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ObserveShortcutSlotsAsync(record.Id));
        Assert.Equal("bad-json-not-read-by-source-scope", await File.ReadAllTextAsync(owned.Paths.StartupBackupPath));
        Assert.Empty(owned.Operations.Calls);
    }

    private static void AssertScopeNoWrite(StartupSourceScopeObservation scope, Fixture owned)
    {
        Assert.False(scope.NativeWriteReady); Assert.False(scope.NativeWritePerformed); Assert.False(scope.ExecutionRequested);
        Assert.Equal(0, owned.Native.WritableOpens); Assert.Empty(owned.Operations.Calls);
    }

    private static void AssertUnknownScope(StartupSourceScopeObservation scope, Fixture owned)
    {
        Assert.Equal("unknown", scope.State); Assert.NotNull(scope.Reason); Assert.Null(scope.DirectoryIdentity);
        Assert.Null(scope.DirectoryPath); Assert.Null(scope.ObserverSid); Assert.Null(scope.SourceUserSid);
        Assert.Equal("unknown", scope.CaseSensitivity); AssertScopeNoWrite(scope, owned);
    }

    private sealed class ScopeOnlyAccess(Func<string> directory) : IStartupNativeAccess
    {
        internal int DirectoryReads { get; private set; }
        public bool IsAdministrator => throw new Exception("Scope must not infer ownership from elevation.");
        public string GetShortcutDirectory(string source) { DirectoryReads++; return directory(); }
        public Microsoft.Win32.RegistryKey? OpenRunKey(string source, bool writable, bool create) => throw new Exception("Scope must not open registry keys.");
    }
}
