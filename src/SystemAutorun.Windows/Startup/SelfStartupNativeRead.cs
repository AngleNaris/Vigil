using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SystemAutorun.Core.Models;

namespace SystemAutorun.Windows.Startup;

internal static partial class StartupNativeSlotProbe
{
    // Fixed current-primary-user 64-bit Run item only. No caller key/hive/name/SID,
    // HKCU cached-handle dependency, environment expansion, decoding or trimming.
    // A bounded observation, NOT a key identity token, CAS, lock or write authority.
    internal static SelfStartupRawObservation ReadSelfNativeState(CancellationToken token = default) =>
        ReadSelfNativeCore(@"Software\Microsoft\Windows\CurrentVersion\Run", token, null);

    // The only alternate locator is a fixed GUID-owned private fixture namespace.
    // This is internal, has no request field, and cannot select the real Run path.
    internal static SelfStartupRawObservation ReadSelfNativeFixture(Guid id, CancellationToken token = default, Action? afterFirstRead = null)
    {
        token.ThrowIfCancellationRequested();
        if (id == Guid.Empty) { throw new ArgumentException("A unique owned fixture is required.", nameof(id)); }
        return ReadSelfNativeCore(@"Software\SystemAutorun.SelfRaw.Tests." + id.ToString("N") + @"\Parent\Run", token, afterFirstRead);
    }

    private static SelfStartupRawObservation ReadSelfNativeCore(string relative, CancellationToken token, Action? afterFirstRead)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var actor = StartupHistoryProtection.Actor(); var hiveName = @"\REGISTRY\USER\" + actor; var keyName = hiveName + "\\" + relative;
            // A missing/unreadable actor hive is NOT an absent startup value.
            using var hive = OpenSelfRegistryKey(null, hiveName, out var hiveError);
            RequireSelfRegistryOpen(hive, hiveError); VerifySelfRegistryName(hive, hiveName);
            StartupHistoryProtection.RequireActor(actor); token.ThrowIfCancellationRequested();
            using var first = OpenSelfRegistryKey(hive, relative, out var firstError);
            var firstPresent = SelfRegistryPresent(first, firstError);
            if (firstPresent) { VerifySelfRegistryName(first, keyName); }
            var before = firstPresent ? ReadSelfRegistryValue(first) : new SelfStartupNativeState(false);
            afterFirstRead?.Invoke(); token.ThrowIfCancellationRequested(); StartupHistoryProtection.RequireActor(actor);
            VerifySelfRegistryName(hive, hiveName);
            if (firstPresent) { VerifySelfRegistryName(first, keyName); }
            // Reopen the CURRENT namespace without dropping the held first key.
            // Deleted/renamed keys fail; equal name/data is not claimed to fence ABA.
            using var current = OpenSelfRegistryKey(hive, relative, out var currentError);
            var currentPresent = SelfRegistryPresent(current, currentError);
            if (firstPresent != currentPresent) { throw new SelfStartupNativeReadException("self_registration_changed"); }
            if (currentPresent) { VerifySelfRegistryName(current, keyName); }
            var after = currentPresent ? ReadSelfRegistryValue(current) : new SelfStartupNativeState(false);
            if (before != after) { throw new SelfStartupNativeReadException("self_registration_changed"); }
            if (firstPresent)
            {
                // Never turn ERROR_KEY_DELETED on the held key into absent. Recheck
                // both data and namespace after the fresh-key read as well.
                if (ReadSelfRegistryValue(first) != after) { throw new SelfStartupNativeReadException("self_registration_changed"); }
                VerifySelfRegistryName(first, keyName); VerifySelfRegistryName(current, keyName);
            }
            token.ThrowIfCancellationRequested(); VerifySelfRegistryName(hive, hiveName); StartupHistoryProtection.RequireActor(actor);
            return new(actor, currentPresent, after);
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException
            or InvalidDataException or CryptographicException or ArgumentException or InvalidOperationException)
        { throw new SelfStartupNativeReadException("self_registry_unavailable", error); }
    }

    private static SelfStartupNativeState ReadSelfRegistryValue(SafeRegistryHandle key)
    {
        // Fixed, non-growing allocation. All native uint types and arbitrary bytes
        // are fingerprinted verbatim, including malformed UTF-16 and trailing NULs.
        // RegQueryValueExW can insert a terminator into a malformed odd-length
        // string's returned buffer. NtQueryValueKey supplies the actual stored bytes.
        const int header = 12; // KEY_VALUE_PARTIAL_INFORMATION, not its Align64 variant.
        var buffer = new byte[header + SelfStartupMutationPolicy.MaximumNativeDataBytes];
        var name = UserStartupRegistration.ValueName; var text = Marshal.StringToHGlobalUni(name);
        try
        {
            var value = new UnicodeName { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)((name.Length + 1) * 2)), Buffer = text };
            var status = QuerySelfRegistryValueNative(key, ref value, 2, buffer, (uint)buffer.Length, out var returned);
            var code = status == 0 ? 0 : unchecked((int)RtlNtStatusToDosError(status));
            if (code == 2) { return new(false); }
            if (code is 122 or 234) { throw new SelfStartupNativeReadException("self_registration_too_large"); }
            if (status != 0) { throw new Win32Exception(code, "Self registry value query failed."); }
            if (returned < header || returned > buffer.Length) { throw new SelfStartupNativeReadException("self_registry_invalid"); }
            var type = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4)); var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8));
            if (count > SelfStartupMutationPolicy.MaximumNativeDataBytes) { throw new SelfStartupNativeReadException("self_registration_too_large"); }
            if (count != returned - header) { throw new SelfStartupNativeReadException("self_registry_invalid"); }
            return SelfStartupNativeState.FromNativeBytes(type, buffer.AsSpan(header, checked((int)count)));
        }
        finally { Marshal.FreeHGlobal(text); }
    }

    private static bool SelfRegistryPresent(SafeRegistryHandle key, int error)
    {
        if (error is 2 or 3) { return false; }
        RequireSelfRegistryOpen(key, error); return true;
    }

    private static void RequireSelfRegistryOpen(SafeRegistryHandle key, int error)
    {
        if (error != 0 || key.IsInvalid) { throw new Win32Exception(error == 0 ? 6 : error, "Self registry key could not be opened."); }
    }

    private enum SelfRegistryAccess : uint { Query = 0x101, ValueMutation = 0x103, ChildCreation = 0x105 }

    private static SafeRegistryHandle OpenSelfRegistryKey(SafeRegistryHandle? parent, string name, out int error, SelfRegistryAccess access = SelfRegistryAccess.Query)
    {
        var text = Marshal.StringToHGlobalUni(name); var pointer = IntPtr.Zero;
        try
        {
            pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeName>());
            Marshal.StructureToPtr(new UnicodeName { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)((name.Length + 1) * 2)), Buffer = text }, pointer, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent?.DangerousGetHandle() ?? IntPtr.Zero,
                Name = pointer, Attributes = 0x1040 }; // OBJ_DONT_REPARSE | OBJ_CASE_INSENSITIVE; registry names are case-insensitive.
            // Three bounded rights sets: query (default), value mutation, or
            // existing parent child-creation. Never backup/restore or OPEN_LINK.
            if (access is not (SelfRegistryAccess.Query or SelfRegistryAccess.ValueMutation or SelfRegistryAccess.ChildCreation)) { throw new ArgumentOutOfRangeException(nameof(access)); }
            var status = OpenSelfRegistryKeyNative(out var key, (uint)access, ref attributes, 0);
            error = status == 0 ? 0 : unchecked((int)RtlNtStatusToDosError(status)); return key;
        }
        finally { GC.KeepAlive(parent); Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(text); }
    }

    private static void VerifySelfRegistryName(SafeRegistryHandle key, string expected)
    {
        // KeyNameInformation, never a grow-until-success loop or last-write-time ID.
        var buffer = new byte[4096]; var status = QuerySelfRegistryKeyNative(key, 3, buffer, (uint)buffer.Length, out var returned);
        if (status != 0) { throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)), "Self registry key name query failed."); }
        if (returned < 4 || returned > buffer.Length) { throw new SelfStartupNativeReadException("self_registry_invalid"); }
        var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if ((length & 1) != 0 || length != returned - 4) { throw new SelfStartupNativeReadException("self_registry_invalid"); }
        var actual = new UnicodeEncoding(false, false, true).GetString(buffer, 4, checked((int)length));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        { throw new SelfStartupNativeReadException("self_registry_context_changed"); }
    }

    [DllImport("ntdll.dll", EntryPoint = "NtOpenKeyEx", ExactSpelling = true)]
    private static extern int OpenSelfRegistryKeyNative(out SafeRegistryHandle key, uint access, ref ObjectAttributes attributes, uint options);
    [DllImport("ntdll.dll", EntryPoint = "NtQueryKey", ExactSpelling = true)]
    private static extern int QuerySelfRegistryKeyNative(SafeRegistryHandle key, int infoClass, [Out] byte[] data, uint length, out uint returned);
    [DllImport("ntdll.dll", EntryPoint = "NtQueryValueKey", ExactSpelling = true)]
    private static extern int QuerySelfRegistryValueNative(SafeRegistryHandle key, ref UnicodeName name, int infoClass, [Out] byte[] data, uint length, out uint returned);
}

internal sealed record SelfStartupRawObservation(string ActorSid, bool KeyPresent, SelfStartupNativeState Registration);
// An observation refusal is not a classification of an earlier native write.
internal sealed class SelfStartupNativeReadException(string reason, Exception? inner = null) : Exception("Self startup native state could not be verified.", inner)
{ internal string Reason { get; } = reason; }
