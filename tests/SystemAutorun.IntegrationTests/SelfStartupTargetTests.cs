using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Startup;

namespace SystemAutorun.IntegrationTests;

public sealed class SelfStartupTargetTests
{
    [Fact]
    public void AppAndAgentBindSamePrimaryActorRootAndEntireListedAssetSet()
    {
        using var fixture = new PackageFixture();
        using var agent = fixture.Open(); using var app = fixture.Open(app: true);
        Assert.Equal(agent.Context, app.Context); Assert.Equal(StartupHistoryProtection.Actor(), agent.Context.ActorSid);
        Assert.Equal(PhysicalDirectory.Resolve(fixture.Root), agent.Context.RootDirectory);
        Assert.Equal(fixture.Asset("Vigil.exe"), agent.Context.Executable);
        Assert.Equal(UserStartupRegistration.BuildCommand(agent.Context.Executable, fixture.Root), agent.Context.Command);
        Assert.Matches("^[a-f0-9]{64}$", agent.Context.ContextId); agent.VerifyCurrent(); app.VerifyCurrent();
        var request = new SelfStartupMutationRequest { SchemaVersion = 1, Enabled = true, Confirmed = true,
            TargetContextId = agent.Context.ContextId, ExpectedRegistration = new(false) };
        Assert.Equal(SelfStartupTransition.Create, SelfStartupMutationPolicy.Evaluate(request, agent.Context.ContextId, agent.Context.Command, new(false)).Plan!.Transition);
    }

    [Fact]
    public void AmbientEntryRefusesTesthostInsteadOfDevelopmentOrEnvironmentFallback()
    {
        using var fixture = new PackageFixture();
        AssertRefusal<InvalidOperationException>(() => StartupNativeSlotProbe.OpenSelfStartupTarget(new(fixture.Root)));
    }

    [Theory]
    [InlineData("host")] [InlineData("base")] [InlineData("module")] [InlineData("agent-layout")]
    public void FixedHostAndLoadedModuleLayoutCannotBeSelectedFreely(string defect)
    {
        using var fixture = new PackageFixture();
        var host = fixture.Asset("runtime/Vigil.Agent.exe"); var basePath = Path.GetDirectoryName(host)!; var module = fixture.Asset("runtime/SystemAutorun.Windows.dll");
        if (defect == "host") { host = fixture.Asset("cli/vigil.exe"); }
        if (defect == "base") { basePath = fixture.Package; }
        if (defect == "module") { module = fixture.Asset("SystemAutorun.Windows.dll"); }
        if (defect == "agent-layout") { host = fixture.Asset("Vigil.Agent.exe"); basePath = fixture.Package; module = fixture.Asset("SystemAutorun.Windows.dll"); }
        AssertRefusal<InvalidOperationException>(() => StartupNativeSlotProbe.SelfStartupTargetLease.Open(fixture.Root, host, basePath, module, StartupHistoryProtection.Actor));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SizeOrHashMismatchRejectsAndReleasesAllPins(bool sameLength)
    {
        using var fixture = new PackageFixture(); var path = fixture.Asset("SystemAutorun.Core.dll"); var bytes = File.ReadAllBytes(path);
        if (sameLength) { bytes[0] ^= 1; } else { bytes = [1]; }
        File.WriteAllBytes(path, bytes); AssertRefusal<InvalidDataException>(() => fixture.Open()); fixture.RenamePackageAfterFailure();
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    public void MalformedOrIncompleteManifestRefusesBeforeAnyNativeWrite(int defect)
    {
        using var fixture = new PackageFixture(); var json = fixture.Manifest(); var files = json["files"]!.AsArray(); var entry = files[0]!.AsObject();
        switch (defect)
        {
            case 0: json["schema_version"] = 2; break;
            case 1: json["self_contained"] = false; break;
            case 2: json["runtime"] = "linux-x64"; break;
            case 3: files.RemoveAt(0); break;
            case 4: entry["size"] = -1; break;
            case 5: entry["size"] = SelfStartupPackageManifest.MaximumFileBytes + 1; break;
            case 6: entry["sha256"] = new string('A', 64); break;
            case 7: json["verified"] = true; break;
            case 8: entry["path"] = "publish-manifest.json"; break;
            case 9: json["created_utc"] = "2026-11-03T10:00:00+01:00"; break;
            case 10: var alias = entry.DeepClone(); alias["path"] = entry["path"]!.GetValue<string>().ToUpperInvariant(); files.Add(alias); break;
            case 11: json["schema_version"] = "1"; break;
        }
        fixture.WriteManifest(json); AssertRefusal<InvalidDataException>(() => fixture.Open()); fixture.RenamePackageAfterFailure();
    }

    [Theory]
    [InlineData("../outside.dll")] [InlineData("C:/outside.dll")] [InlineData("//offline.invalid/x.dll")]
    [InlineData("runtime\\escape.dll")] [InlineData("runtime//escape.dll")] [InlineData("runtime/./escape.dll")]
    [InlineData("runtime/../escape.dll")] [InlineData("runtime/escape.dll:stream")]
    [InlineData("runtime/NUL.dll")] [InlineData("runtime/COM1.dll")] [InlineData("runtime/escape.dll.")]
    [InlineData("runtime/escape.dll ")] [InlineData("runtime/\u202eescape.dll")]
    [InlineData("runtime/CONIN$.dll")] [InlineData("runtime/COM¹.dll")] [InlineData("runtime/NUL .txt")] [InlineData("runtime/%TEMP%.dll")]
    public void UnsafeManifestPathsAreNeverOpened(string path)
    {
        using var fixture = new PackageFixture(); var json = fixture.Manifest(); var extra = json["files"]![0]!.DeepClone(); extra["path"] = path;
        json["files"]!.AsArray().Add(extra); fixture.WriteManifest(json);
        Assert.Throws<InvalidDataException>(() => SelfStartupPackageManifest.Read(Encoding.UTF8.GetBytes(json.ToJsonString())));
        AssertRefusal<InvalidDataException>(() => fixture.Open());
    }

    [Fact]
    public void ManifestDuplicatesInvalidUtf8AndBudgetsFailClosed()
    {
        using var fixture = new PackageFixture(); var original = File.ReadAllText(fixture.ManifestPath);
        foreach (var text in new[] { original.Replace("\"schema_version\":1", "\"schema_version\":1,\"schema_version\":1"), "null", "[]" })
        { File.WriteAllText(fixture.ManifestPath, text); AssertRefusal<InvalidDataException>(() => fixture.Open()); }
        File.WriteAllBytes(fixture.ManifestPath, [0xff]); AssertRefusal<InvalidDataException>(() => fixture.Open());
        File.WriteAllBytes(fixture.ManifestPath, new byte[SelfStartupPackageManifest.MaximumBytes + 1]); AssertRefusal<InvalidDataException>(() => fixture.Open());
        Assert.Throws<InvalidDataException>(() => SelfStartupPackageManifest.Read([]));
        var many = JsonNode.Parse(original)!.AsObject(); var files = many["files"]!.AsArray();
        while (files.Count <= SelfStartupPackageManifest.MaximumFiles) { files.Add(files[0]!.DeepClone()); }
        Assert.Throws<InvalidDataException>(() => SelfStartupPackageManifest.Read(Encoding.UTF8.GetBytes(many.ToJsonString())));
    }

    [Theory]
    [InlineData("machine")] [InlineData("subsystem")] [InlineData("dll")] [InlineData("magic")]
    public void MatchingHashesDoNotReplacePeEntryContractValidation(string defect)
    {
        using var fixture = new PackageFixture(); var path = fixture.Asset("Vigil.exe"); var bytes = File.ReadAllBytes(path);
        if (defect == "machine") { BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(132), 0x014c); }
        if (defect == "subsystem") { BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(220), 3); }
        if (defect == "dll") { BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(150), 0x2022); }
        if (defect == "magic") { bytes[0] = 0; }
        File.WriteAllBytes(path, bytes); fixture.RefreshManifest();
        var error = Assert.Throws<SelfStartupTargetException>(() => fixture.Open()).InnerException; Assert.NotNull(error);
        Assert.True(error is InvalidDataException or BadImageFormatException, error.GetType().Name); fixture.RenamePackageAfterFailure();
    }

    [Theory]
    [InlineData("global")] [InlineData("missing")] [InlineData("oversized")]
    public void SelfContainedFlagAloneDoesNotProveBundledRuntime(string defect)
    {
        using var fixture = new PackageFixture(); var path = fixture.Asset("Vigil.runtimeconfig.json");
        File.WriteAllText(path, defect switch
        { "global" => "{\"runtimeOptions\":{\"framework\":{\"name\":\"Microsoft.NETCore.App\"},\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\"}]}}",
            "missing" => "{\"runtimeOptions\":{}}", _ => new string(' ', 65537) });
        fixture.RefreshManifest(); AssertRefusal<InvalidDataException>(() => fixture.Open());
    }

    [Theory]
    [InlineData("{}")] [InlineData("{\"runtimeOptions\":[]}")]
    [InlineData("{\"runtimeOptions\":{\"includedFrameworks\":[{}]}}")]
    [InlineData("{\"runtimeOptions\":{},\"runtimeOptions\":{}}")]
    public void RuntimeShapeFailuresHaveOneAdmissionException(string json)
    {
        using var fixture = new PackageFixture(); File.WriteAllText(fixture.Asset("Vigil.runtimeconfig.json"), json); fixture.RefreshManifest();
        var error = Assert.Throws<SelfStartupTargetException>(() => fixture.Open());
        Assert.True(error.InnerException is KeyNotFoundException or InvalidOperationException or JsonException);
        fixture.RenamePackageAfterFailure();
    }

    [Theory]
    [InlineData("")] [InlineData("not-a-sid")]
    public void MalformedActorSeamFailsBeforeAcquiringNamespacePins(string actor)
    {
        using var fixture = new PackageFixture(); Assert.Throws<SelfStartupTargetException>(() => fixture.Open(actor: () => actor)); fixture.RenamePackageAfterFailure();
    }

    [Fact]
    public void ActorDriftAfterOpenUsesSameAdmissionException()
    {
        using var fixture = new PackageFixture(); var actor = StartupHistoryProtection.Actor(); using var lease = fixture.Open(actor: () => actor);
        actor = "S-1-5-21-1-2-3-1001"; AssertRefusal<InvalidOperationException>(() => lease.VerifyCurrent());
    }

    [Fact]
    public void ReadPinsDenyAssetWritesAndRootRenamesUntilDisposed()
    {
        using var fixture = new PackageFixture(); var lease = fixture.Open();
        try
        {
            Assert.Throws<IOException>(() => File.WriteAllBytes(fixture.Asset("Vigil.exe"), [1]));
            var moved = fixture.Owned("root-moved"); fixture.VerifyOwned(fixture.Root);
            Assert.Throws<IOException>(() => Directory.Move(fixture.Root, moved)); lease.VerifyCurrent();
        }
        finally { lease.Dispose(); }
        Assert.Throws<ObjectDisposedException>(() => lease.VerifyCurrent());
        File.WriteAllBytes(fixture.Asset("Vigil.exe"), [1]);
    }

    [Theory]
    [InlineData("leaf-reparse")] [InlineData("parent-reparse")] [InlineData("root-reparse")] [InlineData("hardlink")]
    public void ReparseAndAliasedAssetNamespacesRefuse(string defect)
    {
        using var fixture = new PackageFixture(); var path = fixture.Asset("Vigil.exe"); var saved = fixture.Owned("saved-exe");
        if (defect is "leaf-reparse" or "hardlink")
        {
            fixture.VerifyOwned(path); File.Move(path, saved);
            if (defect == "leaf-reparse") { File.CreateSymbolicLink(path, saved); }
            else { File.Copy(saved, path); Assert.True(CreateHardLinkW(fixture.Owned("asset-alias"), path, IntPtr.Zero), new Win32Exception(Marshal.GetLastWin32Error()).Message); }
        }
        else
        {
            var directory = defect == "root-reparse" ? fixture.Root : fixture.Owned("package/runtime");
            var moved = fixture.Owned("saved-dir"); fixture.VerifyOwned(directory); Directory.Move(directory, moved); Directory.CreateSymbolicLink(directory, moved);
        }
        var error = Assert.Throws<SelfStartupTargetException>(() => fixture.Open()).InnerException; Assert.NotNull(error);
        if (defect is "leaf-reparse" or "hardlink") { Assert.IsType<InvalidDataException>(error); }
        else { Assert.True(error is IOException or Win32Exception or InvalidOperationException, error.GetType().Name); }
    }

    [Fact]
    public void RootActorManifestAndAssetIdentityChangesInvalidatePreviewContext()
    {
        using var fixture = new PackageFixture(); string first;
        using (var lease = fixture.Open()) { first = lease.Context.ContextId; }
        var secondRoot = fixture.Owned("root-2"); Directory.CreateDirectory(secondRoot);
        using (var lease = fixture.Open(root: secondRoot)) { Assert.NotEqual(first, lease.Context.ContextId); }
        using (var lease = fixture.Open(actor: () => "S-1-5-21-1-2-3-1001")) { Assert.NotEqual(first, lease.Context.ContextId); }
        var json = fixture.Manifest(); json["created_utc"] = "2026-11-03T01:02:04Z"; fixture.WriteManifest(json);
        using (var lease = fixture.Open()) { Assert.NotEqual(first, lease.Context.ContextId); first = lease.Context.ContextId; }
        var path = fixture.Asset("Vigil.exe"); var saved = fixture.Owned("replaced-exe"); var bytes = File.ReadAllBytes(path);
        fixture.VerifyOwned(path); File.Move(path, saved); File.WriteAllBytes(path, bytes);
        using (var lease = fixture.Open()) { Assert.NotEqual(first, lease.Context.ContextId); }
    }

    [Fact]
    public void ActorChangeAndCancellationAtSealReleaseEveryInput()
    {
        using var fixture = new PackageFixture(); var actor = StartupHistoryProtection.Actor();
        AssertRefusal<InvalidOperationException>(() => fixture.Open(actor: () => actor, beforeSeal: () => actor = "S-1-5-21-1-2-3-1001"));
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => fixture.Open(token: cancellation.Token, beforeSeal: cancellation.Cancel));
        Assert.Throws<OperationCanceledException>(() => fixture.Open(token: cancellation.Token)); fixture.RenamePackageAfterFailure();
    }

    [Theory]
    [InlineData("publish-manifest.json")] [InlineData("Vigil.exe")]
    public void MissingPackageMaterialIsNotAnAbsentNativeRegistration(string relative)
    {
        using var fixture = new PackageFixture(); var path = fixture.Asset(relative); fixture.VerifyOwned(path); File.Delete(path);
        var error = AssertRefusal<Win32Exception>(() => fixture.Open()); Assert.Equal(2, error.NativeErrorCode); fixture.RenamePackageAfterFailure();
    }

    [Fact]
    public void AnExistingAssetWriterPreventsReadProofAdmission()
    {
        using var fixture = new PackageFixture();
        using (var writer = new FileStream(fixture.Asset("SystemAutorun.Core.dll"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        { var error = AssertRefusal<Win32Exception>(() => fixture.Open()); Assert.Contains(error.NativeErrorCode, new[] { 32, 33 }); }
        fixture.RenamePackageAfterFailure();
    }

    [Fact]
    public void LexicalCaseAliasDoesNotBecomePhysicalRootIdentity()
    {
        using var fixture = new PackageFixture(); Assert.NotEqual(fixture.Root, fixture.Root.ToUpperInvariant());
        AssertRefusal<InvalidOperationException>(() => fixture.Open(root: fixture.Root.ToUpperInvariant()));
    }

    [Fact]
    public void SpacesAndUnicodeRemainPartOfTheExactCanonicalTarget()
    {
        using var fixture = new PackageFixture(); var root = fixture.Owned("服务 root 空间"); Directory.CreateDirectory(root);
        using var lease = fixture.Open(root: root); Assert.Equal(root, lease.Context.RootDirectory);
        Assert.Equal(root, StartupRegistrationContract.ParseSelfRoot(lease.Context.Command)); lease.VerifyCurrent();
    }

    [Fact]
    public void ManifestAggregateBudgetAndNestedDuplicatesRejectWithoutAssetIo()
    {
        using var fixture = new PackageFixture(); var json = fixture.Manifest();
        foreach (var file in json["files"]!.AsArray()) { file!["size"] = 64L * 1024 * 1024; }
        Assert.Throws<InvalidDataException>(() => SelfStartupPackageManifest.Read(Encoding.UTF8.GetBytes(json.ToJsonString())));
        var text = File.ReadAllText(fixture.ManifestPath).Replace("\"size\":512", "\"size\":512,\"size\":512");
        Assert.NotEqual(File.ReadAllText(fixture.ManifestPath), text);
        Assert.Throws<InvalidDataException>(() => SelfStartupPackageManifest.Read(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void UnlistedUserDataIsNotLockedHashedOrClaimedAsPublishedAsset()
    {
        using var fixture = new PackageFixture(); File.WriteAllText(fixture.Asset("autorun.yaml"), "user data");
        using var lease = fixture.Open(); File.WriteAllText(fixture.Asset("autorun.yaml"), "edited user data"); lease.VerifyCurrent();
    }

    private static T AssertRefusal<T>(Action action) where T : Exception
    {
        var failure = Assert.Throws<SelfStartupTargetException>(action);
        Assert.Equal("The self startup target context could not be verified.", failure.Message);
        return Assert.IsType<T>(failure.InnerException);
    }

    internal sealed class PackageFixture : IDisposable
    {
        private readonly string _id = Guid.NewGuid().ToString("N");
        private readonly string _base;
        internal string Package { get; }
        internal string Root { get; }
        internal string ManifestPath => Asset("publish-manifest.json");
        internal PackageFixture()
        {
            _base = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SystemAutorun-self-target-" + _id));
            Package = Owned("package"); Root = Owned("root"); Directory.CreateDirectory(Package); Directory.CreateDirectory(Root);
            try
            {
                foreach (var path in SelfStartupPackageManifest.RequiredFiles)
                {
                    var file = Asset(path); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    if (path.EndsWith(".exe")) { File.WriteAllBytes(file, Pe(path.StartsWith("cli/") ? (ushort)3 : (ushort)2)); }
                    else { File.WriteAllText(file, path.EndsWith(".runtimeconfig.json") ? "{\"runtimeOptions\":{\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}]}}" : "synthetic-not-loadable:" + path); }
                }
                RefreshManifest();
            }
            catch { Dispose(); throw; }
        }
        internal string Owned(string relative) { var value = Path.GetFullPath(Path.Combine(_base, relative.Replace('/', '\\'))); VerifyOwned(value); return value; }
        internal string Asset(string relative) => Owned("package/" + relative);
        internal void VerifyOwned(string path)
        { if (!Path.GetFullPath(path).StartsWith(_base + Path.DirectorySeparatorChar, StringComparison.Ordinal)) { throw new InvalidOperationException("Fixture path escaped its owned root."); } }
        internal JsonObject Manifest() => JsonNode.Parse(File.ReadAllText(ManifestPath))!.AsObject();
        internal void WriteManifest(JsonObject value) => File.WriteAllText(ManifestPath, value.ToJsonString());
        internal void RefreshManifest()
        {
            var files = SelfStartupPackageManifest.RequiredFiles.Select(path => { var bytes = File.ReadAllBytes(Asset(path));
                return new { path, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), size = bytes.LongLength }; }).ToArray();
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(new { schema_version = 1, runtime = "win-x64", self_contained = true,
                created_utc = "2026-11-03T01:02:03Z", files }));
        }
        internal StartupNativeSlotProbe.SelfStartupTargetLease Open(bool app = false, string? root = null, Func<string>? actor = null,
            CancellationToken token = default, Action? beforeSeal = null)
        {
            var directory = app ? Package : Owned("package/runtime");
            return StartupNativeSlotProbe.SelfStartupTargetLease.Open(root ?? Root, Path.Combine(directory, app ? "Vigil.exe" : "Vigil.Agent.exe"),
                directory, Path.Combine(directory, "SystemAutorun.Windows.dll"), actor ?? StartupHistoryProtection.Actor, token, beforeSeal);
        }
        internal void RenamePackageAfterFailure()
        { VerifyOwned(Package); Directory.Move(Package, Owned("package-released")); }
        public void Dispose()
        {
            if (_base != Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SystemAutorun-self-target-" + _id))) { throw new InvalidOperationException("Unexpected fixture root."); }
            if (!Directory.Exists(_base)) { return; }
            if ((File.GetAttributes(_base) & FileAttributes.ReparsePoint) != 0) { throw new InvalidOperationException("Refuse redirected fixture cleanup."); }
            Directory.Delete(_base, recursive: true);
        }
        private static byte[] Pe(ushort subsystem)
        {
            var bytes = new byte[512]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0x5a4d); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3c), 128);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(128), 0x4550); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(132), 0x8664);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(148), 240); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(150), 0x22);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(152), 0x20b); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(220), subsystem); return bytes;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newPath, string existingPath, IntPtr security);
}
