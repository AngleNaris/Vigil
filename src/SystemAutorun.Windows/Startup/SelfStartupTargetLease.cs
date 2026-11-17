using System.ComponentModel;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SystemAutorun.Core.Infrastructure;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Windows.Startup;

internal static partial class StartupNativeSlotProbe
{
    // Ambient process facts ONLY; no caller-selected executable, SID, hive or package.
    internal static SelfStartupTargetLease OpenSelfStartupTarget(ProjectPaths paths, CancellationToken token = default) =>
        SelfStartupTargetLease.OpenCurrent(paths, token);

    // Holds read-only file/namespace pins, NOT the future cross-root native writer lease.
    // Manifest agreement is integrity of listed assets, not publisher trust or authorization.
    internal sealed class SelfStartupTargetLease : IDisposable
    {
        private readonly Dictionary<string, SelfTargetDirectory> _directories = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SelfTargetFile> _files = new(StringComparer.Ordinal);
        private readonly Func<string> _actor;
        private bool _disposed;
        internal SelfStartupTargetContext Context { get; private set; } = null!;
        internal bool IsCurrentPackage { get; private set; }
        private SelfStartupTargetLease(Func<string> actor) { _actor = actor; }

        internal static SelfStartupTargetLease OpenCurrent(ProjectPaths paths, CancellationToken token)
        {
            var lease = Open(paths.RootDirectory, Environment.ProcessPath ?? "", AppContext.BaseDirectory,
                typeof(SelfStartupTargetLease).Assembly.Location, StartupHistoryProtection.Actor, token);
            // Only this ambient path stamps provenance; Open's explicit environment
            // seam deliberately cannot mint it, even with identical Context fields.
            lease.IsCurrentPackage = true; return lease;
        }

        // Explicit environment seam is INTERNAL and used by isolated synthetic-package tests.
        internal static SelfStartupTargetLease Open(string root, string processPath, string baseDirectory, string modulePath,
            Func<string> actor, CancellationToken token = default, Action? beforeSeal = null)
        {
            try { return OpenCore(root, processPath, baseDirectory, modulePath, actor, token, beforeSeal); }
            catch (Exception error) when (VerificationFailure(error)) { throw new SelfStartupTargetException(error); }
        }

        private static SelfStartupTargetLease OpenCore(string root, string processPath, string baseDirectory, string modulePath,
            Func<string> actor, CancellationToken token, Action? beforeSeal)
        {
            token.ThrowIfCancellationRequested(); var sid = actor();
            if (string.IsNullOrEmpty(sid) || new SecurityIdentifier(sid).Value != sid) { throw new InvalidOperationException("Primary actor identity is unavailable."); }
            var host = StartupShortcutPathLease.LocalPath(processPath);
            var hostDirectory = Path.GetDirectoryName(host)!;
            var package = Path.GetFileName(host) switch
            {
                "Vigil.exe" => hostDirectory,
                "Vigil.Agent.exe" when Path.GetFileName(hostDirectory) == "runtime" => Path.GetDirectoryName(hostDirectory)!,
                _ => throw new InvalidOperationException("Self startup requires a fixed published App or Agent host."),
            };
            if (StartupShortcutPathLease.LocalPath(baseDirectory) != hostDirectory
                || StartupShortcutPathLease.LocalPath(modulePath) != Path.Combine(hostDirectory, "SystemAutorun.Windows.dll"))
            { throw new InvalidOperationException("The current component is outside the published layout."); }
            var lease = new SelfStartupTargetLease(actor);
            try
            {
                var rootPin = lease.PinDirectory(StartupShortcutPathLease.LocalPath(root));
                var packagePin = lease.PinDirectory(package);
                var gui = Path.Combine(package, "Vigil.exe");
                var command = UserStartupRegistration.BuildCommand(gui, rootPin.Path);
                if (StartupRegistrationContract.ParseSelfRoot(command) != rootPin.Path) { throw new InvalidOperationException("The current root cannot form a canonical self target."); }
                var manifest = lease.PinFile(package, "publish-manifest.json", SelfStartupPackageManifest.MaximumBytes, token);
                var entries = SelfStartupPackageManifest.Read(manifest.ReadSmall(SelfStartupPackageManifest.MaximumBytes, token));
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested(); var file = lease.PinFile(package, entry.Path, SelfStartupPackageManifest.MaximumFileBytes, token);
                    if (file.Length != entry.Size || file.Digest != entry.Sha256) { throw new InvalidDataException("Published asset bytes differ from the manifest."); }
                }
                foreach (var (path, subsystem) in new[] { ("Vigil.exe", Subsystem.WindowsGui), ("cli/vigil.exe", Subsystem.WindowsCui),
                    ("runtime/Vigil.Agent.exe", Subsystem.WindowsGui), ("runtime/Vigil.Elevated.exe", Subsystem.WindowsGui) })
                {
                    var runtimePath = Path.ChangeExtension(path, ".runtimeconfig.json").Replace('\\', '/');
                    if (!lease._files.TryGetValue(path, out var file) || !lease._files.TryGetValue(runtimePath, out var runtime))
                    { throw new InvalidDataException("Published entry assets are incomplete."); }
                    file.Stream.Position = 0;
                    using var pe = new PEReader(file.Stream, PEStreamOptions.LeaveOpen);
                    var headers = pe.PEHeaders; var optional = headers.PEHeader;
                    if (headers.CoffHeader.Machine != Machine.Amd64 || optional is null || optional.Magic != PEMagic.PE32Plus || optional.Subsystem != subsystem
                        || (headers.CoffHeader.Characteristics & Characteristics.ExecutableImage) == 0 || (headers.CoffHeader.Characteristics & Characteristics.Dll) != 0)
                    { throw new InvalidDataException("Published entry point has an unsupported PE contract."); }
                    RequireBundledRuntime(runtime.ReadSmall(64 * 1024, token));
                }
                // Host and loaded Windows component must be among the pinned, checked assets.
                if (!lease._files.ContainsKey(Path.GetRelativePath(package, host).Replace('\\', '/'))
                    || !lease._files.ContainsKey(Path.GetRelativePath(package, modulePath).Replace('\\', '/')))
                { throw new InvalidDataException("Current host assets are not covered by the package."); }
                var material = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = 1, registration = StartupRegistrationContract.Id("self"), actorSid = sid,
                    root = new { rootPin.Path, rootPin.FileIdentity, rootPin.Casing },
                    package = new { packagePin.Path, packagePin.FileIdentity, packagePin.Casing },
                    manifest = new { manifest.FileIdentity, manifest.Digest }, command,
                    assets = entries.Select(entry => new { entry.Path, lease._files[entry.Path].FileIdentity, entry.Size, entry.Sha256 }).ToArray(),
                });
                var contextId = Convert.ToHexStringLower(SHA256.HashData(material));
                lease.Context = new(sid, rootPin.Path, package, gui, command, contextId);
                beforeSeal?.Invoke(); lease.VerifyCurrent(token); return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        internal void VerifyCurrent(CancellationToken token = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
            try
            {
                if (_actor() != Context.ActorSid) { throw new InvalidOperationException("Self target actor context changed."); }
                foreach (var directory in _directories.Values) { token.ThrowIfCancellationRequested(); directory.VerifyCurrent(); }
                foreach (var file in _files.Values) { file.VerifyCurrent(token); }
                if (_actor() != Context.ActorSid) { throw new InvalidOperationException("Self target actor context changed."); }
            }
            catch (Exception error) when (VerificationFailure(error)) { throw new SelfStartupTargetException(error); }
        }

        private static bool VerificationFailure(Exception error) => error is IOException or InvalidDataException or Win32Exception
            or UnauthorizedAccessException or CryptographicException or System.Security.SecurityException or ArgumentException
            or InvalidOperationException or KeyNotFoundException or BadImageFormatException or JsonException or FormatException or OverflowException;

        private SelfTargetDirectory PinDirectory(string path)
        {
            if (_directories.TryGetValue(path, out var existing)) { return existing; }
            var directory = SelfTargetDirectory.Open(path); _directories.Add(path, directory); return directory;
        }
        private SelfTargetFile PinFile(string package, string relative, long maximum, CancellationToken token)
        {
            if (_files.ContainsKey(relative)) { throw new InvalidDataException("Duplicate package asset."); }
            var path = Path.Combine(package, relative.Replace('/', '\\'));
            var file = SelfTargetFile.Open(PinDirectory(Path.GetDirectoryName(path)!), Path.GetFileName(path), maximum, token);
            _files.Add(relative, file); return file;
        }
        private static void RequireBundledRuntime(byte[] bytes)
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 32 });
            var options = json.RootElement.GetProperty("runtimeOptions");
            if (options.TryGetProperty("framework", out _) || options.TryGetProperty("frameworks", out _)
                || !options.TryGetProperty("includedFrameworks", out var frameworks) || frameworks.ValueKind != JsonValueKind.Array
                || !frameworks.EnumerateArray().Any(value => value.GetProperty("name").GetString() == "Microsoft.NETCore.App"))
            { throw new InvalidDataException("Published entry points require a bundled runtime."); }
        }
        public void Dispose()
        {
            if (_disposed) { return; } _disposed = true;
            try { foreach (var file in _files.Values.Reverse()) { file.Dispose(); } }
            finally { foreach (var directory in _directories.Values.Reverse()) { directory.Dispose(); } }
        }
    }

    private sealed class SelfTargetDirectory : IDisposable
    {
        private readonly SshInputFileLease _chain;
        internal SafeFileHandle Handle { get; }
        internal string Path { get; }
        internal StartupNativeFileIdentity FileIdentity { get; }
        internal string Casing { get; }
        private SelfTargetDirectory(string path, SshInputFileLease chain, SafeFileHandle handle)
        { Path = path; _chain = chain; Handle = handle; FileIdentity = Identity(handle); Casing = CaseSensitivity(handle); }
        internal static SelfTargetDirectory Open(string path)
        {
            var chain = SshInputFileLease.OpenDirectory(path); SafeFileHandle? handle = null;
            try
            {
                handle = OpenParent(path); var pin = new SelfTargetDirectory(path, chain, handle);
                if (pin.Casing == "unknown") { throw new InvalidOperationException("Directory case semantics are unavailable."); }
                if (PhysicalDirectory.Resolve(path) != path) { throw new InvalidOperationException("A canonical physical directory is required."); }
                return pin;
            }
            catch { handle?.Dispose(); chain.Dispose(); throw; }
        }
        internal void VerifyCurrent()
        {
            using var chain = SshInputFileLease.OpenDirectory(Path); using var current = OpenParent(Path);
            if (Identity(current) != FileIdentity || Identity(Handle) != FileIdentity || CaseSensitivity(current) != Casing
                || CaseSensitivity(Handle) != Casing || PhysicalDirectory.Resolve(Path) != Path)
            { throw new InvalidOperationException("Pinned target directory context changed."); }
        }
        public void Dispose() { try { Handle.Dispose(); } finally { _chain.Dispose(); } }
    }

    private sealed class SelfTargetFile : IDisposable
    {
        private readonly SelfTargetDirectory _parent;
        private readonly string _leaf;
        private readonly long _maximum;
        internal FileStream Stream { get; }
        internal StartupNativeFileIdentity FileIdentity { get; }
        internal long Length { get; }
        internal string Digest { get; }
        private SelfTargetFile(SelfTargetDirectory parent, string leaf, FileStream stream, long maximum, CancellationToken token)
        {
            _parent = parent; _leaf = leaf; Stream = stream; _maximum = maximum;
            (FileIdentity, Length) = Metadata(_maximum); Digest = Hash(token);
        }
        internal static SelfTargetFile Open(SelfTargetDirectory parent, string leaf, long maximum, CancellationToken token)
        {
            var handle = OpenRelative(parent.Handle, leaf, out var error, caseSensitive: true); FileStream? stream = null;
            try
            {
                if (error != 0) { throw new Win32Exception(error); }
                stream = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: false);
                return new SelfTargetFile(parent, leaf, stream, maximum, token);
            }
            catch { stream?.Dispose(); handle.Dispose(); throw; }
        }
        private (StartupNativeFileIdentity Identity, long Length) Metadata(long maximum)
        {
            if (GetFileType(Stream.SafeFileHandle) != 1 || !GetFileInformationByHandle(Stream.SafeFileHandle, out var info)) { throw new IOException("A local ordinary package asset is required."); }
            var length = ((long)info.SizeHigh << 32) | info.SizeLow; var identity = Identity(Stream.SafeFileHandle);
            if ((info.Attributes & 0x410) != 0 || info.Links != 1 || length < 0 || length > maximum || identity.VolumeSerialNumber != _parent.FileIdentity.VolumeSerialNumber)
            { throw new InvalidDataException("Package asset type, bounds or identity are unsupported."); }
            return (identity, length);
        }
        private string Hash(CancellationToken token)
        {
            Stream.Position = 0; var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long read = 0;
                while (read < Length)
                {
                    token.ThrowIfCancellationRequested(); var count = Stream.Read(buffer, 0, (int)Math.Min(buffer.Length, Length - read));
                    if (count == 0) { throw new IOException("Package asset length changed."); }
                    hash.AppendData(buffer, 0, count); read += count;
                }
                token.ThrowIfCancellationRequested(); if (Stream.ReadByte() != -1) { throw new IOException("Package asset length changed."); }
                return Convert.ToHexStringLower(hash.GetHashAndReset());
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        }
        internal byte[] ReadSmall(int maximum, CancellationToken token)
        {
            if (Length > maximum) { throw new InvalidDataException("Package metadata exceeds its read budget."); }
            var bytes = new byte[(int)Length]; Stream.Position = 0; token.ThrowIfCancellationRequested(); Stream.ReadExactly(bytes); token.ThrowIfCancellationRequested();
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != Digest) { throw new InvalidDataException("Package metadata bytes changed before parsing."); }
            return bytes;
        }
        internal void VerifyCurrent(CancellationToken token)
        {
            using var current = OpenRelative(_parent.Handle, _leaf, out var error, caseSensitive: true);
            if (error != 0) { throw new Win32Exception(error); }
            // Current namespace and held-object metadata/bytes are separate checks.
            // Share restrictions are not a CAS against every non-cooperating writer.
            var metadata = Metadata(_maximum);
            if (Identity(current) != FileIdentity || metadata.Identity != FileIdentity || metadata.Length != Length || Hash(token) != Digest)
            { throw new InvalidOperationException("Pinned package asset changed."); }
        }
        public void Dispose() => Stream.Dispose();
    }
}

internal sealed record SelfStartupTargetContext(string ActorSid, string RootDirectory, string PackageDirectory,
    string Executable, string Command, string ContextId);

// Proof failure only. A future executor MUST classify operation outcome by phase;
// this exception never proves whether a prior native write happened. Cancellation
// and disposed-owner misuse are not converted to an admission refusal.
internal sealed class SelfStartupTargetException(Exception inner)
    : Exception("The self startup target context could not be verified.", inner);
