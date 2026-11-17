using System.Security.Cryptography;
using System.Text;

namespace SystemAutorun.Windows.Startup;

internal static partial class StartupHistoryProtection
{
    internal const int MaximumProtectedIndexBytes = 512 * 1024;
    private static readonly byte[] IndexMagic = "SALST001"u8.ToArray();
    private static readonly byte[] IndexEntropy = Encoding.UTF8.GetBytes("SystemAutorun.StartupHistory.LatestIndex.CurrentUser.v1");

    // Codec only, not a publisher or native-success issuer. No business writer uses it.
    internal static byte[] ProtectIndex(StartupHistoryIndex index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var actor = Actor();
        if (index is null || index.ActorSid != actor) { throw new CryptographicException("History index actor context is unavailable."); }
        var json = StartupHistoryIndexFormat.Write(index); byte[] frame = []; byte[]? result = null;
        try
        {
            frame = new byte[HeaderBytes + json.Length]; IndexMagic.CopyTo(frame, 0);
            SHA256.HashData(json).CopyTo(frame, 8); json.CopyTo(frame, HeaderBytes);
            result = Transform(frame, protect: true, latestIndex: true);
            cancellationToken.ThrowIfCancellationRequested(); RequireActor(actor); return result;
        }
        catch { if (result is not null) { CryptographicOperations.ZeroMemory(result); } throw; }
        finally { CryptographicOperations.ZeroMemory(json); CryptographicOperations.ZeroMemory(frame); }
    }

    internal static StartupHistoryIndex UnprotectIndex(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var actor = Actor();
        if (bytes.Length is 0 or > MaximumProtectedIndexBytes) { throw new CryptographicException("History protected index byte limit is invalid."); }
        var input = bytes.ToArray(); byte[] frame = [];
        try
        {
            frame = Transform(input, protect: false, latestIndex: true);
            if (frame.Length <= HeaderBytes || !frame.AsSpan(0, 8).SequenceEqual(IndexMagic)
                || !CryptographicOperations.FixedTimeEquals(frame.AsSpan(8, 32), SHA256.HashData(frame.AsSpan(HeaderBytes))))
            { throw new CryptographicException("History protected index frame is invalid."); }
            var index = StartupHistoryIndexFormat.Read(frame.AsSpan(HeaderBytes));
            cancellationToken.ThrowIfCancellationRequested(); RequireActor(actor);
            if (index.ActorSid != actor) { throw new CryptographicException("History index actor binding changed."); }
            return index;
        }
        finally { CryptographicOperations.ZeroMemory(input); CryptographicOperations.ZeroMemory(frame); }
    }
}
