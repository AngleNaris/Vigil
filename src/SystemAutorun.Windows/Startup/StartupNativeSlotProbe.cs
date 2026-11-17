using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SystemAutorun.Core.Models;
using SystemAutorun.Windows.Health;

namespace SystemAutorun.Windows.Startup;

// Only layout-validated, SERVER-DERIVED slots may call these shared primitives. The
// observation entrypoints never read link bytes. The separate opt-in capture partial
// holds bounded current bytes; neither observation nor capture authenticates history
// or provides a general inspector, mutation permission or native CAS.
internal static partial class StartupNativeSlotProbe
{
    internal static StartupNativeSlotCapture Observe(string derivedPath, CancellationToken cancellationToken,
        Action? beforeSeal = null)
    {
        var directory = Path.GetDirectoryName(derivedPath)!;
        var leafName = Path.GetFileName(derivedPath);
        cancellationToken.ThrowIfCancellationRequested();
        StartupNativeFileIdentity? parentIdentity = null;
        StartupNativeSlotCapture Result(string state, string? reason = null, int? error = null, string casing = "unknown",
            StartupNativeFileIdentity? file = null, long? length = null, uint? links = null) =>
            new(state, reason, error, directory, casing, parentIdentity, file, length, links, DateTimeOffset.UtcNow);
        try
        {
            // Pin the entire local ancestry, rejecting reparses and directory deletion.
            // Resolve the leaf RELATIVE to the held parent, never through a second absolute walk.
            using var chain = SshInputFileLease.OpenDirectory(directory);
            using var parent = OpenParent(directory);
            parentIdentity = Identity(parent);
            var initialCasing = CaseSensitivity(parent);
            cancellationToken.ThrowIfCancellationRequested();
            using var leaf = OpenRelative(parent, leafName, out var openError);
            StartupNativeFileIdentity? fileIdentity = null;
            long? length = null; uint? links = null;
            string? rejected = null;
            if (openError == 0)
            {
                if (GetFileType(leaf) != 1 || !GetFileInformationByHandle(leaf, out var info))
                { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                if ((info.Attributes & 0x410) != 0) { rejected = "not_regular"; }
                else
                {
                    fileIdentity = Identity(leaf);
                    length = ((long)info.SizeHigh << 32) | info.SizeLow;
                    links = info.Links;
                    if (length < 0 || length > 64 * 1024) { rejected = "too_large"; }
                }
            }
            beforeSeal?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            // Reopen the CURRENT namespace, not just the same held handle. Best-effort
            // drift detection only: sequential observations are not a combined CAS.
            using var currentParent = OpenParent(directory);
            if (Identity(currentParent) != parentIdentity) { return Result("unknown", "context_changed"); }
            var casing = CaseSensitivity(parent);
            if (rejected is not null) { return Result("unknown", rejected, casing: casing); }
            if (openError == 0) { return Result("present", casing: casing, file: fileIdentity, length: length, links: links); }
            if (openError == 2 && initialCasing != casing) { return Result("unknown", "case_semantics_changed", 2, casing); }
            if (openError == 2 && initialCasing == "insensitive" && casing == "insensitive") { return Result("missing", error: 2, casing: casing); }
            if (openError == 2) { return Result("unknown", "case_semantics_unavailable", 2, casing); }
            return Result("unknown", ErrorReason(openError), openError, casing);
        }
        catch (UnauthorizedAccessException) { return Result("unknown", "permission_denied", 5); }
        catch (Win32Exception error) { return Result("unknown", ErrorReason(error.NativeErrorCode), error.NativeErrorCode); }
        catch (IOException error)
        {
            var code = (error.InnerException as Win32Exception)?.NativeErrorCode;
            return Result("unknown", code is 2 or 3 ? "directory_missing" : code is not null ? ErrorReason(code.Value) : "directory_unavailable", code);
        }
    }

    // Fixed-source directory only; never opens a leaf or reads directory contents.
    // The callback rechecks the actor/known-folder mapping while the ancestry is held.
    internal static StartupNativeDirectoryCapture ObserveDirectory(string directory, CancellationToken cancellationToken,
        Func<bool> contextUnchanged)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartupNativeDirectoryCapture Unknown(string reason, int? error = null) =>
            new("unknown", reason, error, "unknown", null, DateTimeOffset.UtcNow);
        try
        {
            using var chain = SshInputFileLease.OpenDirectory(directory);
            using var parent = OpenParent(directory);
            var identity = Identity(parent);
            var casing = CaseSensitivity(parent);
            if (casing == "unknown") { return Unknown("case_semantics_unavailable"); }
            cancellationToken.ThrowIfCancellationRequested();
            var unchanged = contextUnchanged();
            cancellationToken.ThrowIfCancellationRequested();
            if (!unchanged) { return Unknown("context_changed"); }
            using var currentParent = OpenParent(directory);
            if (Identity(currentParent) != identity) { return Unknown("context_changed"); }
            if (CaseSensitivity(currentParent) != casing || CaseSensitivity(parent) != casing)
            { return Unknown("case_semantics_changed"); }
            return new("observed", null, null, casing, identity, DateTimeOffset.UtcNow);
        }
        catch (UnauthorizedAccessException) { return Unknown("permission_denied", 5); }
        catch (Win32Exception error)
        { return Unknown(error.NativeErrorCode is 2 or 3 ? "directory_missing" : ErrorReason(error.NativeErrorCode), error.NativeErrorCode); }
        catch (IOException error)
        {
            var code = (error.InnerException as Win32Exception)?.NativeErrorCode;
            return Unknown(code is 2 or 3 ? "directory_missing" : code is not null ? ErrorReason(code.Value) : "directory_unavailable", code);
        }
    }

    private static string ErrorReason(int error) => error switch
    {
        5 => "permission_denied", 32 or 33 => "occupied", 4395 => "reparse_rejected", _ => "unavailable",
    };

    private static SafeFileHandle OpenParent(string directory, bool readSecurity = false)
    {
        var handle = CreateFileW(directory, readSecurity ? 0x20081u : 0x81u, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { var code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code); }
        try
        {
            if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var info)
                || (info.Attributes & 0x410) != 0x10)
            { throw new IOException("Startup slot parent must remain an ordinary local directory."); }
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string leaf, out int error, bool reserveDeleteAccess = false, bool caseSensitive = false, uint share = 1)
    {
        var text = Marshal.StringToHGlobalUni(leaf);
        var namePointer = IntPtr.Zero;
        try
        {
            namePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeName>());
            Marshal.StructureToPtr(new UnicodeName { Length = checked((ushort)(leaf.Length * 2)), MaximumLength = checked((ushort)((leaf.Length + 1) * 2)), Buffer = text }, namePointer, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(), Name = namePointer,
                Attributes = 0x1000u | (caseSensitive ? 0u : 0x40u) }; // Exact spelling for sensitive captures; always OBJ_DONT_REPARSE.
            // Existing objects ONLY. No mutation, backup privilege or shell parsing.
            // Captures may reserve DELETE access for a future same-handle producer.
            var status = NtOpenFile(out var handle, reserveDeleteAccess ? 0x80110000u : 0x80100000u, ref attributes, out _, share, 0x00200020);
            error = status == 0 ? 0 : unchecked((int)RtlNtStatusToDosError(status));
            return handle;
        }
        finally { GC.KeepAlive(parent); Marshal.FreeHGlobal(namePointer); Marshal.FreeHGlobal(text); }
    }

    private static StartupNativeFileIdentity Identity(SafeFileHandle handle)
    {
        if (!GetIdentity(handle, 18, out var info, 24)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return new(info.Volume.ToString("x16", CultureInfo.InvariantCulture), Convert.ToHexStringLower(info.FileId));
    }

    private static string CaseSensitivity(SafeFileHandle parent)
    {
        if (!GetCaseInfo(parent, 23, out var flags, 4)) { return "unknown"; }
        return flags switch { 0 => "insensitive", 1 => "sensitive", _ => "unknown" };
    }

    [StructLayout(LayoutKind.Sequential)] private struct UnicodeName { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes
    { public int Length; public IntPtr RootDirectory; public IntPtr Name; public uint Attributes; public IntPtr Security; public IntPtr Quality; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { public IntPtr Status; public UIntPtr Information; }
    [StructLayout(LayoutKind.Sequential)] private struct FileIdentityInfo
    { public ulong Volume; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] FileId; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle file);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo info);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdentity(SafeFileHandle file, int infoClass, out FileIdentityInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCaseInfo(SafeFileHandle file, int infoClass, out uint info, uint size);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtOpenFile(out SafeFileHandle file, uint access, ref ObjectAttributes attributes, out IoStatus status, uint share, uint options);
    [DllImport("ntdll.dll", ExactSpelling = true)] private static extern uint RtlNtStatusToDosError(int status);
}

internal sealed record StartupNativeDirectoryCapture(string State, string? Reason, int? NativeErrorCode,
    string CaseSensitivity, StartupNativeFileIdentity? DirectoryIdentity, DateTimeOffset ObservedAt);

internal sealed record StartupNativeSlotCapture(string State, string? Reason, int? NativeErrorCode,
    string DirectoryPath, string CaseSensitivity, StartupNativeFileIdentity? DirectoryIdentity,
    StartupNativeFileIdentity? FileIdentity, long? Length, uint? LinkCount, DateTimeOffset ObservedAt)
{
    internal StartupArchiveSlotObservation ToArchive() => new(State, Reason is null ? null : "archive_" + Reason, NativeErrorCode,
        DirectoryPath, CaseSensitivity, DirectoryIdentity, FileIdentity, Length, LinkCount, ObservedAt);
    internal StartupOriginalSlotObservation ToOriginal() => new(State, Reason is null ? null : "original_" + Reason, NativeErrorCode,
        DirectoryPath, CaseSensitivity, DirectoryIdentity, FileIdentity, Length, LinkCount, ObservedAt);
}
