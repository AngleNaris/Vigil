using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SystemAutorun.Windows.Startup;

internal static partial class StartupNativeSlotProbe
{
    private static void VerifySelfCreationParent(SafeRegistryHandle hive, SafeRegistryHandle parent, string relative,
        string expectedName, string actor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); StartupHistoryProtection.RequireActor(actor);
        VerifySelfRegistryName(hive, @"\REGISTRY\USER\" + actor); VerifySelfRegistryName(parent, expectedName);
        using var fresh = OpenSelfRegistryKey(hive, relative, out var error);
        RequireSelfRegistryOpen(fresh, error); VerifySelfRegistryName(fresh, expectedName); VerifySelfRegistryName(parent, expectedName);
        StartupHistoryProtection.RequireActor(actor); token.ThrowIfCancellationRequested();
    }

    // Exactly one literal child of an EXISTING verified parent. No slash/path
    // supplied by request, recursive creation, explicit security, link, privilege,
    // volatile option, disposition takeover or cleanup compensation.
    private static SafeRegistryHandle CreateSelfRegistryRun(SafeRegistryHandle parent, SelfStartupNativeAttempt attempt,
        Action? dispatchRace, out int status, out uint disposition)
    {
        var text = Marshal.StringToHGlobalUni("Run"); var pointer = IntPtr.Zero;
        try
        {
            pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeName>());
            Marshal.StructureToPtr(new UnicodeName { Length = 6, MaximumLength = 8, Buffer = text }, pointer, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(),
                Name = pointer, Attributes = 0x1040 }; // OBJ_DONT_REPARSE | CASE_INSENSITIVE.
            dispatchRace?.Invoke(); // GUID-only deterministic pre-dispatch race.
            attempt.MarkNativeCall(SelfStartupNativeCallKind.CreateKey);
            status = CreateSelfRegistryKeyNative(out var key, 0x103, ref attributes, 0, IntPtr.Zero, 0, out disposition);
            return key;
        }
        finally { GC.KeepAlive(parent); Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(text); }
    }

    [DllImport("ntdll.dll", EntryPoint = "NtCreateKey", ExactSpelling = true)]
    private static extern int CreateSelfRegistryKeyNative(out SafeRegistryHandle key, uint access, ref ObjectAttributes attributes,
        uint title, IntPtr className, uint options, out uint disposition);
}
