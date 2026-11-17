using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SystemAutorun.Windows.Startup;

// CurrentUser DPAPI codec only. Encryption is not application attribution or proof of
// a native mutation. No files, latest-state authority, keys, migrations or issuer here.
internal static partial class StartupHistoryProtection
{
    internal const int MaximumProtectedBytes = 128 * 1024;
    private const int HeaderBytes = 40;
    private static readonly byte[] Magic = "SAHST001"u8.ToArray();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SystemAutorun.StartupHistory.CurrentUser.v1");
    private static readonly byte[] Zeroes = new byte[4096];

    internal static byte[] Protect(StartupHistoryPayload payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var actor = Actor();
        StartupHistoryFormat.Validate(payload);
        if (payload.Binding.ActorSid != actor) { throw new CryptographicException("History actor context is unavailable."); }
        var json = StartupHistoryFormat.Write(payload); byte[] frame = []; byte[]? result = null;
        try
        {
            frame = new byte[HeaderBytes + json.Length]; Magic.CopyTo(frame, 0);
            SHA256.HashData(json).CopyTo(frame, 8); json.CopyTo(frame, HeaderBytes);
            result = Transform(frame, protect: true);
            cancellationToken.ThrowIfCancellationRequested(); RequireActor(actor);
            return result;
        }
        catch { if (result is not null) { CryptographicOperations.ZeroMemory(result); } throw; }
        finally { CryptographicOperations.ZeroMemory(json); CryptographicOperations.ZeroMemory(frame); }
    }

    internal static StartupHistoryPayload Unprotect(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var actor = Actor();
        if (bytes.Length is 0 or > MaximumProtectedBytes) { throw new CryptographicException("History protected byte limit is invalid."); }
        var input = bytes.ToArray(); byte[] frame = [];
        try
        {
            frame = Transform(input, protect: false);
            if (frame.Length <= HeaderBytes || !frame.AsSpan(0, 8).SequenceEqual(Magic)
                || !CryptographicOperations.FixedTimeEquals(frame.AsSpan(8, 32), SHA256.HashData(frame.AsSpan(HeaderBytes))))
            { throw new CryptographicException("History protected frame is invalid."); }
            var payload = StartupHistoryFormat.Read(frame.AsSpan(HeaderBytes));
            cancellationToken.ThrowIfCancellationRequested(); RequireActor(actor);
            if (payload.Binding.ActorSid != actor) { throw new CryptographicException("History actor binding changed."); }
            return payload;
        }
        finally { CryptographicOperations.ZeroMemory(input); CryptographicOperations.ZeroMemory(frame); }
    }

    // Callers supply a CURRENT binding and an independently authenticated latest state.
    // A null/missing blob never creates material. Even a match remains write-ineligible.
    internal static StartupHistoryCheck Inspect(ReadOnlySpan<byte> bytes, StartupHistoryBinding current,
        StartupHistoryLatest? latest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartupHistoryFormat.ValidateBinding(current);
        if (bytes.IsEmpty) { return new("unverified", "history_missing"); }
        if (bytes.Length > MaximumProtectedBytes) { return new("unverified", "history_protection_invalid"); }
        if (latest is null) { return new("unverified", "history_latest_unavailable"); }
        if (!StartupHistoryFormat.ValidLatest(latest)) { return new("unverified", "history_latest_invalid"); }
        // Own a single bounded transport snapshot for digest + decrypt. A caller's
        // mutable backing array must not race those two steps, even in read-only code.
        var input = bytes.ToArray();
        try
        {
            if (Convert.ToHexStringLower(SHA256.HashData(input)) != latest.ProtectedDigest)
            { return new("unverified", "history_protected_bytes_changed"); }
            return StartupHistoryFormat.Match(Unprotect(input, cancellationToken), current, latest);
        }
        catch (CryptographicException) { return new("unverified", "history_protection_invalid"); }
        catch (JsonException) { return new("unverified", "history_payload_invalid"); }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    internal static string Actor()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        if (identity.ImpersonationLevel != TokenImpersonationLevel.None || identity.User is null)
        { throw new CryptographicException("History requires a primary current-user context."); }
        return identity.User.Value;
    }
    internal static void RequireActor(string actor)
    { if (Actor() != actor) { throw new CryptographicException("History actor context changed."); } }

    private static byte[] Transform(byte[] bytes, bool protect, bool latestIndex = false, bool operationLink = false)
    {
        if (latestIndex && operationLink) { throw new ArgumentException("History protection domains cannot be combined."); }
        // Native DPAPI allocates its output itself. Check input BEFORE the call and
        // output BEFORE any managed copy; this does not cap OS-internal allocation.
        var plainLimit = latestIndex ? StartupHistoryIndexFormat.MaximumBytes : operationLink ? StartupHistoryOperationFormat.MaximumBytes : StartupHistoryFormat.MaximumBytes;
        var cipherLimit = latestIndex ? MaximumProtectedIndexBytes : MaximumProtectedBytes;
        var domain = latestIndex ? IndexEntropy : operationLink ? OperationEntropy : Entropy;
        var maximumInput = protect ? plainLimit + HeaderBytes : cipherLimit;
        var maximumOutput = protect ? cipherLimit : plainLimit + HeaderBytes;
        if (bytes.Length is 0 || bytes.Length > maximumInput) { throw new CryptographicException("History DPAPI input limit is invalid."); }
        var pinnedInput = default(GCHandle); var pinnedEntropy = default(GCHandle); var output = default(Blob);
        try
        {
            pinnedInput = GCHandle.Alloc(bytes, GCHandleType.Pinned); pinnedEntropy = GCHandle.Alloc(domain, GCHandleType.Pinned);
            var input = new Blob { Length = bytes.Length, Data = pinnedInput.AddrOfPinnedObject() };
            var entropy = new Blob { Length = domain.Length, Data = pinnedEntropy.AddrOfPinnedObject() };
            // UI_FORBIDDEN only: never LOCAL_MACHINE, no prompt, description or audit.
            var succeeded = protect ? CryptProtectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!succeeded) { throw new CryptographicException(Marshal.GetLastWin32Error()); }
            if (output.Data == IntPtr.Zero || output.Length is <= 0 || output.Length > maximumOutput)
            { throw new CryptographicException("History DPAPI output limit is invalid."); }
            var result = new byte[output.Length];
            try { Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            catch { CryptographicOperations.ZeroMemory(result); throw; }
        }
        finally
        {
            try
            {
                if (output.Data != IntPtr.Zero)
                {
                    try
                    {
                        // Bound the scratch allocation, even if native output was too large.
                        for (var offset = 0; offset < output.Length;)
                        {
                            var count = Math.Min(Zeroes.Length, output.Length - offset);
                            Marshal.Copy(Zeroes, 0, IntPtr.Add(output.Data, offset), count); offset += count;
                        }
                    }
                    finally { _ = LocalFree(output.Data); }
                }
            }
            finally
            {
                if (pinnedEntropy.IsAllocated) { pinnedEntropy.Free(); }
                if (pinnedInput.IsAllocated) { pinnedInput.Free(); }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr LocalFree(IntPtr memory);
}
