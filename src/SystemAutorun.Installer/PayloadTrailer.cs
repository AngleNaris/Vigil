using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace SystemAutorun.Installer;

/// <summary>
/// Reads and locates the appended payload archive at the end of Vigil-Setup.exe:
/// [0..baseExeLength): base single-file executable (copied cleanly as Vigil-Uninstall.exe)
/// [baseExeLength..baseExeLength+payloadLength): compressed zip payload
/// [last 24 bytes]: [int64 baseExeLength][int64 payloadLength]["VIGILPK1"]
/// </summary>
internal static class PayloadTrailer
{
    internal const string Magic = "VIGILPK1";
    internal const int FooterSize = 24;

    private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes(Magic);

    internal static bool TryReadTrailer(string executablePath, out long baseExeLength, out long payloadLength)
    {
        baseExeLength = 0;
        payloadLength = 0;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return false;
        }

        using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length < FooterSize)
        {
            return false;
        }

        Span<byte> footer = stackalloc byte[FooterSize];
        stream.Seek(-FooterSize, SeekOrigin.End);
        stream.ReadExactly(footer);

        if (!footer[16..FooterSize].SequenceEqual(MagicBytes))
        {
            return false;
        }

        var exeLen = BinaryPrimitives.ReadInt64LittleEndian(footer[..8]);
        var zipLen = BinaryPrimitives.ReadInt64LittleEndian(footer[8..16]);
        if (exeLen <= 0 || zipLen <= 0 || exeLen + zipLen + FooterSize != stream.Length)
        {
            return false;
        }

        baseExeLength = exeLen;
        payloadLength = zipLen;
        return true;
    }

    internal static void CopyBaseExecutable(string sourceExecutablePath, string targetUninstallerPath, long baseExeLength)
    {
        using var input = new FileStream(sourceExecutablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new FileStream(targetUninstallerPath, FileMode.Create, FileAccess.Write, FileShare.None);
        CopyExactBytes(input, output, baseExeLength);
    }

    internal static void CopyExactBytes(Stream source, Stream destination, long count)
    {
        var buffer = new byte[81920];
        var remaining = count;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(buffer.Length, remaining);
            var read = source.Read(buffer, 0, chunk);
            if (read <= 0)
            {
                throw new EndOfStreamException("Unexpected end of embedded installer payload.");
            }
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}
